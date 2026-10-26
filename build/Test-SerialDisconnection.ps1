[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use matching-bitness Windows PowerShell 5.1 -STA.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "llcom plus\bin\$Platform\$Configuration" }
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-disconnection-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null; $globalType = $null; $page = $null; $exitCode = 0
try {
    Add-Type -AssemblyName PresentationFramework
    Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $OutputDirectory 'llcom plus.exe'))
    $instance = [Reflection.BindingFlags]'NonPublic,Public,Instance'
    $globalType = $assembly.GetType('llcom_plus.Tools.Global',$true)
    $globalType.GetField('ProfilePath').SetValue($null,$testRoot + '\')
    $settings = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Model.Settings',$true),$true)
    $settings.language = 'zh-CN'
    $globalType.GetField('setting').SetValue($null,$settings)
    Add-Type -Path (Join-Path $PSScriptRoot 'SerialConnectionMonitorRegressionProbe.cs')
    [SerialConnectionMonitorRegressionProbe]::Run($assembly) | ForEach-Object { Write-Host "PASS $_" }
    Add-Type -Path (Join-Path $PSScriptRoot 'SerialReceiveIsolationRegressionProbe.cs')
    [SerialReceiveIsolationRegressionProbe]::Run($assembly) | ForEach-Object { Write-Host "PASS $_" }
    Add-Type -Path (Join-Path $PSScriptRoot 'LowPowerRegressionProbe.cs')
    [LowPowerRegressionProbe]::Run($assembly)
    Write-Host 'PASS Existing DTR wake, cancellation, and bounded serial-send queue regressions.'
    Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
public static class DisconnectionProbe
{
    public static int UiErrors;
    public static void Watch(System.Windows.Application app)
    {
        app.DispatcherUnhandledException += (sender, e) => { UiErrors++; Console.Error.WriteLine(e.Exception); e.Handled = true; };
    }
    public static object OnWorker(MethodInfo method, object target, object[] args)
    {
        return Task.Run(() => method.Invoke(target, args)).GetAwaiter().GetResult();
    }
    public static object Marker(Assembly assembly, string name)
    {
        return Activator.CreateInstance(assembly.GetType("llcom_plus.Tools.SerialConnectionMonitor", true),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new object[] { (Func<bool>)(() => true), (Action)(() => {}), (Func<string[]>)(() => new[] { name }),
                name, (Action<Exception>)(ex => {}), false }, null);
    }
    public static bool FreshWakeIgnoresRetiredCleanup(Assembly assembly,
        object oldController, object newController, object oldKey, long oldGeneration,
        object newKey, long newGeneration)
    {
        if (ReferenceEquals(oldController, newController)) return false;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var type = newController.GetType();
        var line = Activator.CreateInstance(type.GetNestedType("ProbeDtrLine", BindingFlags.NonPublic),
            flags, null, new object[] { false }, null);
        var profile = Activator.CreateInstance(assembly.GetType("llcom_plus.Model.UartPortProfile", true), true);
        profile.GetType().GetProperty("dtrWakeBeforeSend").SetValue(profile, true, null);
        profile.GetType().GetProperty("dtrWakeDelayMs").SetValue(profile, 0, null);
        profile.GetType().GetProperty("dtrWakeIdleMs").SetValue(profile, 60000, null);
        // Force old cleanup between the new BeginSend's revision capture and
        // driver-line access. No real SerialPort is opened or control line touched.
        Func<bool> current = () => {
            type.GetMethod("InvalidateConnectionCore", flags).Invoke(oldController,
                new object[] { oldKey, oldGeneration });
            return true;
        };
        var sends = 0;
        type.GetMethod("ExecuteWithWakeCore", flags).Invoke(newController,
            new object[] { newKey, newGeneration, current, profile, CancellationToken.None,
                (Action)(() => sends++), line, null });
        var asserted = (bool)line.GetType().GetProperty("Enabled").GetValue(line, null);
        type.GetMethod("InvalidateConnectionCore", flags).Invoke(newController,
            new object[] { newKey, newGeneration });
        return sends == 1 && asserted;
    }
}
'@ -ReferencedAssemblies @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml','System.Core')
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App',$true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    [DisconnectionProbe]::Watch($app)
    [void]$globalType.GetMethod('LoadLanguageFile').Invoke($null,@('zh-CN'))
    $page = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Pages.MultiPortPage',$true),[object[]]@(2,$false,$null))
    function Call-Disconnect($target,[string]$name,[object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        $method = $target.GetType().GetMethods($instance) | Where-Object { $_.Name -eq $name -and $_.GetParameters().Length -eq $arguments.Length } | Select-Object -First 1
        try { return $method.Invoke($target,$arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Assert-Disconnect([bool]$condition,[string]$message) {
        if (-not $condition) { throw "FAIL $message" }; Write-Host "PASS $message"
    }
    function Pump-Disconnect { $app.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Background) }
    [void](Call-Disconnect $page 'EnsureSlotsCreated')
    $slots = $page.GetType().GetField('slots',$instance).GetValue($page)
    $main = $slots[0]; $slot = $slots[1]; $slotType = $slot.GetType()
    [void](Call-Disconnect $main 'SetPortName' ([object[]]@('COM10',$true)))
    [void](Call-Disconnect $slot 'SetPortName' ([object[]]@('COM11',$true)))
    [void](Call-Disconnect $main 'SetLogTextSnapshot' ([object[]]@('main history')))
    [void](Call-Disconnect $slot 'SetLogTextSnapshot' ([object[]]@('split history')))
    $oldPort = $slotType.GetField('serial',$instance).GetValue($slot)
    # Opening normally applies the selected COM to the driver; model that without hardware.
    $oldPort.PortName = 'COM11'
    $generationField = $slotType.GetField('connectionGeneration',$instance)
    $oldGeneration = [long]$generationField.GetValue($slot)
    $wakeField = $slotType.GetField('dtrWakeController',$instance)
    $oldWake = $wakeField.GetValue($slot)
    $oldMainPort = $main.GetType().GetField('serial',$instance).GetValue($main)
    $slotType.GetField('connectionMonitor',$instance).SetValue($slot,[DisconnectionProbe]::Marker($assembly,'COM11'))
    [void](Call-Disconnect $slot 'QueueReceivedData' ([object[]]@([byte[]]@(65,66,67),$oldGeneration,(Call-Disconnect $slot 'GetProfileSnapshot'))))
    $fault = [UnauthorizedAccessException]::new('Access to the port is denied.')
    Assert-Disconnect ([bool](Call-Disconnect $slot 'HandleConnectionFailure' ([object[]]@($oldPort,$oldGeneration,$fault)))) 'A split-port access-denied fault is treated as a disconnect.'
    $newPort = $slotType.GetField('serial',$instance).GetValue($slot)
    Assert-Disconnect (-not [object]::ReferenceEquals($oldPort,$newPort) -and -not $slot.IsOpen -and
        [long]$generationField.GetValue($slot) -ne $oldGeneration) 'The failed driver object is replaced and its connection generation is invalidated immediately.'
    Assert-Disconnect ($slot.SelectedPortName -eq 'COM11' -and $newPort.PortName -eq 'COM11' -and $slot.LastErrorMessage.Length -gt 0) 'The disconnected COM assignment is retained for deliberate reopening.'
    Assert-Disconnect ($slotType.GetField('pendingReceiveData',$instance).GetValue($slot).Count -eq 0) 'Old aggregated bytes are not replayed on a later connection.'
    Assert-Disconnect ([DisconnectionProbe]::FreshWakeIgnoresRetiredCleanup($assembly,$oldWake,$wakeField.GetValue($slot),$oldPort,$oldGeneration,$newPort,[long]$generationField.GetValue($slot))) 'Late cleanup of a retired driver cannot cancel DTR wake or sending on its replacement.'
    $history = [string](Call-Disconnect $slot 'GetLogText')
    Assert-Disconnect ($history.Contains('split history') -and $history.Contains($slot.LastErrorMessage)) 'A disconnect keeps prior history and appends a connection notice.'
    Assert-Disconnect ([string](Call-Disconnect $main 'GetLogText') -eq 'main history' -and
        [object]::ReferenceEquals($oldMainPort,$main.GetType().GetField('serial',$instance).GetValue($main))) 'Another pane and its history are not changed by the failed port.'
    Assert-Disconnect (-not [bool](Call-Disconnect $slot 'HandleConnectionFailure' ([object[]]@($oldPort,$oldGeneration,$fault))) -and
        [string](Call-Disconnect $slot 'GetLogText') -eq $history) 'Repeated and stale error callbacks do not duplicate teardown or notices.'
    [void](Call-Disconnect $slot 'Serial_DataReceived' ([object[]]@($oldPort,$null)))
    Assert-Disconnect ([object]::ReferenceEquals($newPort,$slotType.GetField('serial',$instance).GetValue($slot))) 'A retired driver receive callback cannot replace a new connection.'
    [void](Call-Disconnect $slot 'RefreshPorts' ([object[]]@(,[string[]]@('COM10','COM12'))))
    Assert-Disconnect ($slot.SelectedPortName -eq 'COM11') 'A port refresh never substitutes another COM for the removed device.'
    $currentGeneration = [long]$generationField.GetValue($slot)
    $retire = $slotType.GetMethod('HandleConnectionFailure',$instance)
    [void][DisconnectionProbe]::OnWorker($retire,$slot,[object[]]@($newPort,$currentGeneration,$fault))
    # Model a new session before the old UI callback runs; no real Open is needed.
    $generationField.SetValue($slot,[long]($generationField.GetValue($slot) + 1))
    [void](Call-Disconnect $slot 'SetPortName' ([object[]]@('COM12',$true)))
    Pump-Disconnect
    Assert-Disconnect ($slot.SelectedPortName -eq 'COM12' -and [string](Call-Disconnect $slot 'GetLogText') -eq $history) 'Delayed UI work cannot alter the assignment or logs of a newer session.'
    $uart = $globalType.GetField('uart').GetValue($null)
    $uart.SetName('COM10')
    $uartType = $uart.GetType()
    $lease = Call-Disconnect $uart 'CaptureConnectionLease'
    $uartType.GetField('connectionMonitor',$instance).SetValue($uart,[DisconnectionProbe]::Marker($assembly,'COM10'))
    Assert-Disconnect ([bool](Call-Disconnect $uart 'RetireFailedConnection' ([object[]]@($lease,$fault)))) 'The main UART uses the same expected-disconnect path for access denial.'
    Assert-Disconnect (-not $uart.IsOpen() -and $uart.GetName() -eq 'COM10' -and -not [bool](Call-Disconnect $uart 'RetireFailedConnection' ([object[]]@($lease,$fault)))) 'Main teardown preserves COM identity and rejects the retired lease.'
    $lease = Call-Disconnect $uart 'CaptureConnectionLease'
    $marker = [DisconnectionProbe]::Marker($assembly,'COM10')
    $uartType.GetField('connectionMonitor',$instance).SetValue($uart,$marker)
    [void](Call-Disconnect $uart 'CheckDevicePresence' ([object[]]@($null)))
    [void](Call-Disconnect $uart 'CheckDevicePresence' ([object[]]@(,[string[]]@('COM10'))))
    Assert-Disconnect ([object]::ReferenceEquals($marker,$uartType.GetField('connectionMonitor',$instance).GetValue($uart)) -and
        -not [bool](Call-Disconnect $uart 'RetireFailedConnection' ([object[]]@($lease,[TimeoutException]::new())))) 'A failed scan, a present device, and a send timeout leave the current connection intact.'
    [void](Call-Disconnect $uart 'CheckDevicePresence' ([object[]]@(,[string[]]@('COM12'))))
    Assert-Disconnect ($null -eq $uartType.GetField('connectionMonitor',$instance).GetValue($uart) -and $uart.GetName() -eq 'COM10') 'Successful removal detection retires only the missing main COM.'
    Pump-Disconnect
    Assert-Disconnect ([DisconnectionProbe]::UiErrors -eq 0 -and -not $uart.IsOpen()) 'No expected unplug fault reaches the global UI exception handler; no hardware port was opened.'

    # Model a still-retiring handle without creating a real driver connection.
    # The actual split retirement path must retain its reservation while cleanup
    # waits on the retired port's lock. Both production Open entry points must
    # reject it before replacing/configuring their closed SerialPort objects.
    $coordinator=$assembly.GetType('llcom_plus.Tools.SerialPortAccessCoordinator',$true)
    $static=[Reflection.BindingFlags]'Public,NonPublic,Static'
    $acquire=$coordinator.GetMethod('Acquire',$static)
    $blockedPort='COM27011'
    $reservation=$acquire.Invoke($null,[object[]]@($blockedPort,'Synthetic retired handle'))
    [void](Call-Disconnect $slot 'SetPortName' ([object[]]@($blockedPort,$true)))
    [void](Call-Disconnect $slot 'SetBaudRate' ([object[]]@(115200)))
    $retiredPort=$slotType.GetField('serial',$instance).GetValue($slot)
    $retiredPort.PortName=$blockedPort
    $slotType.GetField('serialReservation',$instance).SetValue($slot,$reservation)
    $retiredGeneration=[long]$generationField.GetValue($slot)
    $cleanup=$null
    [Threading.Monitor]::Enter($retiredPort)
    try {
        $cleanup=[Threading.Tasks.Task](Call-Disconnect $slot 'RetireDirectConnection' ([object[]]@($retiredPort,$retiredGeneration,$false)))
        Assert-Disconnect ($null -ne $cleanup -and -not $cleanup.IsCompleted -and
            $reservation.IsReleasing -and -not $slot.IsOpen) `
            'A closed split replacement remains reserved while its retired driver cleanup is blocked.'
        $beforePort=$slotType.GetField('serial',$instance).GetValue($slot)
        $beforeName=$beforePort.PortName
        $beforeGeneration=[long]$generationField.GetValue($slot)
        $opened=[bool](Call-Disconnect $slot 'Open')
        Assert-Disconnect (-not $opened -and -not $slot.IsOpen -and
            [object]::ReferenceEquals($beforePort,$slotType.GetField('serial',$instance).GetValue($slot)) -and
            $beforePort.PortName -eq $beforeName -and
            [long]$generationField.GetValue($slot) -eq $beforeGeneration -and
            $slot.LastErrorMessage.Contains($blockedPort) -and
            $slot.LastErrorMessage.Contains(([string][char]0x91ca+[char]0x653e))) `
            'A split Open is rejected by the shared retirement gate before touching its driver or generation.'

        $uart.SetName($blockedPort)
        $uartPort=$uartType.GetField('serial',$instance).GetValue($uart)
        $uartGeneration=$uartType.GetField('connectionGeneration',$instance).GetValue($uart)
        $openFailure=$null
        try {$uart.Open()}catch {$openFailure=$_.Exception.GetBaseException()}
        Assert-Disconnect ($null -ne $openFailure -and
            $openFailure.GetType().FullName -eq 'llcom_plus.Tools.SerialPortReservationException' -and
            -not $uart.IsOpen() -and
            [object]::ReferenceEquals($uartPort,$uartType.GetField('serial',$instance).GetValue($uart)) -and
            $uartGeneration -eq $uartType.GetField('connectionGeneration',$instance).GetValue($uart)) `
            'A main Open is rejected by the same retirement gate before replacing or opening its driver.'
    }
    finally {
        [Threading.Monitor]::Exit($retiredPort)
        if($null -ne $cleanup){
            if(-not ([IAsyncResult]$cleanup).AsyncWaitHandle.WaitOne(5000)){throw 'Synthetic reservation cleanup timed out.'}
            $cleanup.GetAwaiter().GetResult()
        }else{$reservation.Dispose()}
    }
    $replacementReservation=$acquire.Invoke($null,[object[]]@($blockedPort,'Synthetic safe replacement'))
    $replacementReservation.Dispose()
    Assert-Disconnect (-not $reservation.IsReleaseFailed) 'The same COM becomes reservable only after the old split cleanup returns.'
    Write-Host "PASS All serial-disconnection checks ($Platform)."
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $page) { [void]$page.GetType().GetMethod('ReleaseAllPortsForLayoutChange').Invoke($page,$null) }
    if ($null -ne $globalType) {
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null,$true,$null)
        [void]$assembly.GetType('llcom_plus.Tools.QuickSendBackupService',$true).GetMethod('Shutdown',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,$null)
    }
    if ($null -ne $app) { $app.Shutdown() }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or $testName -notmatch '^llcom-disconnection-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
exit $exitCode
