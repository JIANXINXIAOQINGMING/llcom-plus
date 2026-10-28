[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use matching-bitness Windows PowerShell 5.1 -STA.' }
$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root "llcom plus\bin\$Platform\$Configuration"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-log-display-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null; $single = $null; $split = $null; $globalType = $null; $loggerType = $null
$exitCode = 0; $passed = 0
$oldContext = [Threading.SynchronizationContext]::Current
try {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -ReferencedAssemblies @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml') -TypeDefinition @'
using System;
using System.Reflection;
using System.Windows;
public static class LogDisplayObserver {
    public static int Errors, ConverterCalls;
    private static EventInfo pointEvent;
    private static Delegate pointHandler;
    public static void Install(string directory) {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            var name = new AssemblyName(args.Name).Name;
            return name == "System.Runtime.CompilerServices.Unsafe"
                ? Assembly.LoadFrom(System.IO.Path.Combine(directory, name + ".dll")) : null;
        };
    }
    public static void Watch(Application app, Assembly assembly) {
        app.DispatcherUnhandledException += (sender, e) => { Errors++; Console.Error.WriteLine(e.Exception); e.Handled = true; };
        pointEvent = assembly.GetType("llcom_plus.ScriptEnv.ScriptApis", true).GetEvent("LinePlotAdd");
        var point = pointEvent.EventHandlerType.GetGenericArguments()[0];
        pointHandler = Delegate.CreateDelegate(pointEvent.EventHandlerType,
            typeof(LogDisplayObserver).GetMethod("OnPoint").MakeGenericMethod(point));
        pointEvent.AddEventHandler(null, pointHandler);
    }
    public static void OnPoint<T>(object sender, T point) { ConverterCalls++; }
    public static void Unwatch() { if (pointEvent != null && pointHandler != null) pointEvent.RemoveEventHandler(null, pointHandler); }
}
'@
    [LogDisplayObserver]::Install($outputDir)
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object { try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { } }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
    $instance = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $static = [Reflection.BindingFlags]'Public,NonPublic,Static'
    function Invoke-Member($target, [string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        try { return $target.GetType().GetMethod($name, $instance).Invoke($target, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Invoke-Static([Type]$type, [string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        $methods = @($type.GetMethods($static) | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $arguments.Length })
        if ($methods.Count -ne 1) { throw "Expected one $name overload with $($arguments.Length) arguments." }
        try { return $methods[0].Invoke($null, $arguments) } catch { throw $_.Exception.GetBaseException() }
    }
    function Assert-Mode([bool]$condition, [string]$message) {
        if (-not $condition) { throw "FAIL $message" }
        $script:passed++; Write-Host "PASS $message"
    }
    function Box-Text($box) { return (New-Object Windows.Documents.TextRange($box.Document.ContentStart, $box.Document.ContentEnd)).Text }
    function Single-Text { return Box-Text ($single.FindName($(if ($settings.timeout -ge 0) { 'MainPackedTextBox' } else { 'MainTextBox' }))) }
    function Sample([bool]$sent) {
        [byte[]]$bytes = if ($sent) { $txBytes } else { $rxBytes }
        [void](Invoke-Static $loggerType 'ShowData' ([object[]]@($bytes, $sent, $null, $null)))
    }
    function Slot-Sample($slot, [bool]$sent) {
        [byte[]]$bytes = if ($sent) { $txBytes } else { $rxBytes }
        [void](Invoke-Member $slot 'WriteDataLog' ([object[]]@($bytes, $sent, $null, $false, $true)))
    }
    function Export-Single {
        $writer = New-Object IO.StringWriter
        try { [void](Invoke-Member $single 'WriteLogSnapshot' ([object[]]@($writer, ($settings.timeout -ge 0)))); return $writer.ToString() }
        finally { $writer.Dispose() }
    }
    function Read-SessionText([string]$path) {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    function Assert-Projection([string]$visible, [int]$mode, [int]$timeout, [string]$message) {
        $textExpected = $mode -ne 2
        $hexExpected = $mode -eq 2 -or ($mode -eq 0 -and $timeout -ge 0)
        Assert-Mode ($visible.Contains('TX_MODE_A') -eq $textExpected -and $visible.Contains('RX_MODE_B') -eq $textExpected -and
            $visible.Contains($txHex) -eq $hexExpected -and $visible.Contains($rxHex) -eq $hexExpected) $message
    }
    function New-Profile([int]$timeout = 50, [int]$mode = 1) {
        $profile = [Activator]::CreateInstance($profileType, $true)
        $profile.timeout = $timeout; $profile.showHexFormat = $mode; $profile.encoding = 65001
        $profile.recvScript = 'display_mode_probe'; $profile.showSend = $true; $profile.enableSymbol = $false
        return $profile
    }

    $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
    $globalType.GetField('ProfilePath').SetValue($null, $testRoot + '\')
    $settingsType = $assembly.GetType('llcom_plus.Model.Settings', $true)
    $profileType = $assembly.GetType('llcom_plus.Model.UartPortProfile', $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $globalType.GetField('setting').SetValue($null, $settings)
    $loggerType = $assembly.GetType('llcom_plus.Tools.Logger', $true)
    $singleType = $assembly.GetType('llcom_plus.Pages.DataShowPage', $true)
    $splitType = $assembly.GetType('llcom_plus.Pages.MultiPortPage', $true)
    $scriptDirectory = Join-Path $testRoot 'user_script_recv_convert'
    [void](New-Item -ItemType Directory -Path $scriptDirectory)
    Copy-Item -LiteralPath (Join-Path $root 'llcom plus\Resources\DefaultFiles\user_script_recv_convert\default.js') -Destination $scriptDirectory
    [IO.File]::WriteAllText((Join-Path $scriptDirectory 'display_mode_probe.js'), 'apiAddPoint(1, 0); return uartData;')
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri', $instance).SetValue($app, $null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    [LogDisplayObserver]::Watch($app, $assembly)
    [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext($app.Dispatcher)))
    [void](Invoke-Member $settings 'SetActiveUartProfile' ([object[]]@('COM98', $true)))
    $settings.showHexFormat = 1; $settings.encoding = 65001; $settings.recvScript = 'display_mode_probe'
    $settings.EnableSymbol = $false; $settings.showSend = $true; $settings.sessionLogEnabled = $false
    # Exercise the real receive converter once before a Page exists; a broken
    # isolated dependency fails here without invoking any application error UI.
    $args = New-Object Collections.ArrayList
    [void]$args.Add('uartData'); [void]$args.Add([Text.Encoding]::UTF8.GetBytes('PREFLIGHT'))
    [void](Invoke-Static ($assembly.GetType('llcom_plus.ScriptEnv.JavaScriptLoader', $true)) 'Run' ([object[]]@('display_mode_probe.js', $args, 'user_script_recv_convert/')))
    Assert-Mode ([LogDisplayObserver]::ConverterCalls -eq 1) 'Receive converter side effects are observed without opening hardware.'
    $txBytes = [Text.Encoding]::UTF8.GetBytes("TX_MODE_A`r`n")
    $rxBytes = [Text.Encoding]::UTF8.GetBytes("RX_MODE_B`r`n")
    $txHex = [BitConverter]::ToString($txBytes).Replace('-', ' ')
    $rxHex = [BitConverter]::ToString($rxBytes).Replace('-', ' ')
    $single = [Activator]::CreateInstance($singleType, $true)
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    foreach ($timeout in @(50, -1)) {
        foreach ($initialMode in @(1, 2, 0)) {
            $settings.timeout = $timeout; $settings.showHexFormat = $initialMode
            [void](Invoke-Static $loggerType 'ClearData')
            Sample $true; Sample $false
            $canonical = $single.GetLogTextSnapshot(); $export = Export-Single
            $calls = [LogDisplayObserver]::ConverterCalls
            foreach ($mode in @(2, 1, 0, 2, 1)) {
                $settings.showHexFormat = $mode
                Assert-Projection (Single-Text) $mode $timeout "Main timeout=$timeout initial=$initialMode immediately redraws historic TX/RX in mode=$mode without new traffic."
            }
            Assert-Mode ([LogDisplayObserver]::ConverterCalls -eq $calls) "Main timeout=$timeout initial=$initialMode never reruns the receive script while switching formats."
            Assert-Mode ($single.GetLogTextSnapshot() -ceq $canonical -and (Export-Single) -ceq $export) "Main timeout=$timeout initial=$initialMode preserves canonical snapshots and exports."
        }
    }
    $settings.timeout = 50; $settings.showHexFormat = 1
    [void](Invoke-Static $loggerType 'ClearData'); Sample $true; Sample $false
    $settings.showSend = $false; $settings.showHexFormat = 2
    Assert-Mode (-not (Single-Text).Contains($txHex) -and (Single-Text).Contains($rxHex)) 'Changing display mode retains the hidden-TX filter and redraws historic RX.'
    $settings.showSend = $true
    Assert-Projection (Single-Text) 2 50 'Restoring TX uses the current display format for previously hidden history.'
    $settings.showHexFormat = 1
    $snapshot = Invoke-Member $single 'GetLogSnapshot'
    [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))
    $settings.showHexFormat = 2
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Projection (Single-Text) 2 50 'Cached main page applies a format changed while unloaded.'
    [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))

    $split = [Activator]::CreateInstance($splitType, [object[]]@(2, $false, ''))
    [void](Invoke-Member $split 'Page_Loaded' ([object[]]@($null, $null)))
    $split.SetSlotPortName(1, 'COM98'); $split.SetSlotPortName(2, 'COM99')
    $first = Invoke-Member $split 'GetSlot' ([object[]]@(1)); $second = Invoke-Member $split 'GetSlot' ([object[]]@(2))
    $firstBox = $first.GetType().GetField('logTextBox', $instance).GetValue($first)
    $secondBox = $second.GetType().GetField('logTextBox', $instance).GetValue($second)
    foreach ($timeout in @(50, -1)) {
        foreach ($slot in @($first, $second)) {
            [void](Invoke-Member $slot 'ApplyProfile' ([object[]]@(New-Profile $timeout 1)))
            [void](Invoke-Member $slot 'ClearLog'); Slot-Sample $slot $true; Slot-Sample $slot $false
        }
        $canonical = Invoke-Member $first 'GetLogText'; $calls = [LogDisplayObserver]::ConverterCalls
        foreach ($mode in @(2, 0, 1, 2)) {
            $profile = Invoke-Member $first 'GetProfileSnapshot'; $profile.showHexFormat = $mode
            [void](Invoke-Member $first 'ApplyProcessingSettings' ([object[]]@($profile)))
            Assert-Projection (Box-Text $firstBox) $mode $timeout "Split timeout=$timeout redraws retained TX/RX in mode=$mode."
            Assert-Projection (Box-Text $secondBox) 1 $timeout "Split timeout=$timeout keeps the other COM format independent."
        }
        Assert-Mode ((Invoke-Member $first 'GetLogText') -ceq $canonical -and [LogDisplayObserver]::ConverterCalls -eq $calls) "Split timeout=$timeout preserves export and never reruns receive conversion."
    }
    [void](Invoke-Member $first 'ApplyProfile' ([object[]]@(New-Profile 50 2)))
    [void](Invoke-Member $first 'SetLogSnapshot' ([object[]]@($snapshot)))
    Assert-Projection (Box-Text $firstBox) 2 50 'Main-to-split snapshot follows destination display mode.'
    $roundtrip = Invoke-Member $first 'GetLogSnapshot'
    $settings.timeout = 50; $settings.showHexFormat = 0
    [void](Invoke-Member $single 'SetLogSnapshot' ([object[]]@($roundtrip)))
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Projection (Single-Text) 0 50 'Split-to-main snapshot retains both display projections.'
    [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))
    [void](Invoke-Member $first 'ApplyProfile' ([object[]]@(New-Profile 50 1)))
    [void](Invoke-Member $second 'ApplyProfile' ([object[]]@(New-Profile 50 1)))
    $split.SetActiveSlot(2)
    # Direct ApplyProfile above deliberately bypasses persisted COM settings.
    # Synchronize the active settings before selecting a different real value;
    # Fody suppresses a repeated setter of the already-stored value.
    $settings.showHexFormat = 1
    $settings.showHexFormat = 2
    Assert-Projection (Box-Text $secondBox) 2 50 'The active-COM settings notification redraws its complete retained history.'
    Assert-Projection (Box-Text $firstBox) 1 50 'Active-COM changes preserve another pane and its independent text display.'

    # Keep actual session writers open while changing formats, then compare the
    # files byte-for-byte. Rendering must not append records or rewrite content.
    $split.SetActiveSlot(1)
    $settings.showHexFormat = 1; $settings.recvScript = 'display_mode_probe'
    $settings.sessionLogEnabled = $true; $settings.sessionLogFolder = Join-Path $testRoot 'sessions'
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    [void](Invoke-Static $loggerType 'StartSessionLog' ([object[]]@('COM98')))
    Sample $true; Sample $false
    $stringPath = $loggerType.GetProperty('SessionStringLogFilePath', $static).GetValue($null, $null)
    $hexPath = $loggerType.GetProperty('SessionHexLogFilePath', $static).GetValue($null, $null)
    $stringBefore = Read-SessionText $stringPath; $hexBefore = Read-SessionText $hexPath
    $calls = [LogDisplayObserver]::ConverterCalls
    foreach ($mode in @(2, 0, 1)) { $settings.showHexFormat = $mode }
    Assert-Mode ((Read-SessionText $stringPath) -ceq $stringBefore -and (Read-SessionText $hexPath) -ceq $hexBefore -and
        $stringBefore.Contains('TX_MODE_A') -and $hexBefore.Contains($rxHex)) 'Main STRING/HEX files remain byte-identical while historic display formats change.'
    Assert-Mode ([LogDisplayObserver]::ConverterCalls -eq $calls) 'Settings notifications and shared snapshots never execute the converter again.'

    # Inspect giant records without rendering a multi-megabyte WPF document.
    # History clipping and cloning must preserve selectable tail projections.
    $itemType = $assembly.GetType('llcom_plus.Pages.DataShowPage+DataShow', $true)
    $paraType = $assembly.GetType('llcom_plus.Tools.DataShowPara', $true)
    $largeText = ("AB`r`n" * 90000) + 'TAIL'
    foreach ($initialMode in @(1, 2)) {
        $para = [Activator]::CreateInstance($paraType, $true)
        $para.data = [Text.Encoding]::UTF8.GetBytes($largeText); $para.send = $true
        $profile = New-Profile 50 $initialMode
        $original = [Activator]::CreateInstance($itemType, $instance, $null, [object[]]@($para, $profile, 'COM98'), $null)
        $canonical = Invoke-Member $original 'ToLogText'
        $copy = Invoke-Member $original 'Clone'
        foreach ($limit in @(1048576, 262144)) {
            $limited = Invoke-Member $copy 'LimitHistoryText' ([object[]]@($limit))
            $count = $itemType.GetProperty('RetainedCharacterCount', $instance).GetValue($limited, $null)
            Assert-Mode ($count -le $limit -and $itemType.GetProperty('IsSent', $instance).GetValue($limited, $null)) "Large initial=$initialMode cloned history stays bounded and retains TX identity at limit=$limit."
            foreach ($mode in @(2, 1, 0, 2)) {
                $projection = Invoke-Member $limited 'ForDisplayFormat' ([object[]]@($mode))
                $data = Invoke-Member $projection 'GetDisplayData' ([object[]]@($true))
                $expected = if ($mode -eq 2) { '54 41 49 4C' } else { 'TAIL' }
                $wholeBytes = $mode -ne 2 -or $data.TrimEnd() -cmatch '^(?:[0-9A-F]{2}(?: [0-9A-F]{2})*)?$'
                Assert-Mode ($data.Length -le $limit -and $data.TrimEnd().EndsWith($expected) -and $wholeBytes) "Clipped initial=$initialMode tail converts to mode=$mode at limit=$limit with complete HEX bytes."
            }
        }
        Assert-Mode ((Invoke-Member $original 'ToLogText') -ceq $canonical) "Cloning and clipping initial=$initialMode do not change canonical history."
    }
    Assert-Mode (-not $split.IsSlotOpen(1) -and -not $split.IsSlotOpen(2) -and -not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No physical COM connection was opened.'
    Assert-Mode ([LogDisplayObserver]::Errors -eq 0) 'No offscreen dispatcher exception occurred.'
    Write-Host "PASS All $passed log display mode checks ($Platform)."
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $single) { [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null))) }
    if ($null -ne $split) { [void](Invoke-Member $split 'Page_Unloaded' ([object[]]@($null, $null))) }
    if ($null -ne $loggerType) { [void](Invoke-Static $loggerType 'StopSessionLog'); [void](Invoke-Static $loggerType 'CloseUartLog') }
    if ($null -ne $globalType) {
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null, $true, $null)
        [void]$assembly.GetType('llcom_plus.Tools.QuickSendBackupService', $true).GetMethod('Shutdown', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $null)
    }
    if ('LogDisplayObserver' -as [Type]) { [LogDisplayObserver]::Unwatch() }
    if ($null -ne $app) { $app.Shutdown() }
    [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
    $resolved = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempBase -or [IO.Path]::GetFileName($resolved) -ne $testName -or
        $testName -notmatch '^llcom-log-display-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
exit $exitCode
