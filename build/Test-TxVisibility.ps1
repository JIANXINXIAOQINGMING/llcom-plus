[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use matching-bitness Windows PowerShell 5.1 with -STA.'
}
$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root "llcom plus\bin\$Platform\$Configuration"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-tx-visibility-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null
$assembly = $null
$globalType = $null
$single = $null
$split = $null
$loggerType = $null
$exitCode = 0
$passed = 0
$previousContext = [Threading.SynchronizationContext]::Current
try {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -ReferencedAssemblies @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml') -TypeDefinition @'
using System;
using System.Windows;
public static class TxUiExceptionObserver
{
    public static int Count;
    public static void InstallDependencyRedirect(string directory)
    {
        // PowerShell does not load the application's .exe.config redirects.
        // Mirror only the bundled Unsafe redirect needed by Jint/System.Memory.
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            var name = new System.Reflection.AssemblyName(args.Name).Name;
            return name == "System.Runtime.CompilerServices.Unsafe"
                ? System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(directory, name + ".dll")) : null;
        };
    }
    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (sender, e) => {
            Count++;
            Console.Error.WriteLine(e.Exception.ToString());
            e.Handled = true;
        };
    }
}
'@
    [TxUiExceptionObserver]::InstallDependencyRedirect($outputDir)
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
    $instanceFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $staticFlags = [Reflection.BindingFlags]'Public,NonPublic,Static'
    function Invoke-Object($target, [string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        try { return $target.GetType().GetMethod($name, $instanceFlags).Invoke($target, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Invoke-Static([Type]$type, [string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        try { return $type.GetMethod($name, $staticFlags).Invoke($null, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Read-Field($target, [string]$name) { return $target.GetType().GetField($name, $instanceFlags).GetValue($target) }
    function Assert-Tx([bool]$condition, [string]$message) {
        if (-not $condition) { throw "FAIL $message" }
        $script:passed++
        Write-Host "PASS $message"
    }
    function Visible-Text($box) {
        return (New-Object Windows.Documents.TextRange($box.Document.ContentStart, $box.Document.ContentEnd)).Text
    }
    function Single-Text {
        $name = if ($settings.timeout -ge 0) { 'MainPackedTextBox' } else { 'MainTextBox' }
        return Visible-Text ($single.FindName($name))
    }
    function Send-Sample([string]$text, [bool]$sent) {
        [void](Invoke-Static $loggerType 'ShowData' ([object[]]@([Text.Encoding]::UTF8.GetBytes($text), $sent, $null, $null)))
    }
    function Slot-Sample($slot, [string]$text, [bool]$sent) {
        [void](Invoke-Object $slot 'WriteDataLog' ([object[]]@([Text.Encoding]::UTF8.GetBytes($text), $sent, $null, $false, $true)))
    }
    function Assert-Order([string]$text, [string[]]$tokens, [string]$message) {
        $previous = -1
        foreach ($token in $tokens) {
            $current = $text.IndexOf($token, [StringComparison]::Ordinal)
            if ($current -le $previous) { throw "FAIL $message ($token missing or reordered)" }
            $previous = $current
        }
        Assert-Tx $true $message
    }

    $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
    $globalType.GetField('ProfilePath').SetValue($null, $testRoot + '\')
    $settingsType = $assembly.GetType('llcom_plus.Model.Settings', $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $globalType.GetField('setting').SetValue($null, $settings)
    $loggerType = $assembly.GetType('llcom_plus.Tools.Logger', $true)
    $singleType = $assembly.GetType('llcom_plus.Pages.DataShowPage', $true)
    $splitType = $assembly.GetType('llcom_plus.Pages.MultiPortPage', $true)
    $profileType = $assembly.GetType('llcom_plus.Model.UartPortProfile', $true)
    $scriptDirectory = Join-Path $testRoot 'user_script_recv_convert'
    [void](New-Item -ItemType Directory -Path $scriptDirectory)
    Copy-Item -LiteralPath (Join-Path $root 'llcom plus\Resources\DefaultFiles\user_script_recv_convert\default.js') -Destination $scriptDirectory
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri', $instanceFlags).SetValue($app, $null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    [TxUiExceptionObserver]::Install($app)
    [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext($app.Dispatcher)))
    $settings.showHexFormat = 1
    $settings.encoding = 65001
    $settings.recvScript = 'default'
    $settings.EnableSymbol = $false
    $settings.sessionLogEnabled = $false
    # Fail directly if the isolated runtime is missing a converter dependency,
    # before the normal receive path can display an application error dialog.
    $scriptArgs = New-Object Collections.ArrayList
    [void]$scriptArgs.Add('uartData')
    [void]$scriptArgs.Add([Text.Encoding]::UTF8.GetBytes('PREFLIGHT'))
    $loaderType = $assembly.GetType('llcom_plus.ScriptEnv.JavaScriptLoader', $true)
    $converted = Invoke-Static $loaderType 'Run' ([object[]]@('default.js', $scriptArgs, 'user_script_recv_convert/'))
    Assert-Tx ([Text.Encoding]::UTF8.GetString([byte[]]$converted) -eq 'PREFLIGHT') 'Isolated receive converter is ready before testing the display pipeline.'
    $single = [Activator]::CreateInstance($singleType, $true)
    # Page lifecycle only. No Window is constructed or shown and no connection
    # opening or send-to-hardware method is called anywhere in this fixture.
    [void](Invoke-Object $single 'Page_Loaded' ([object[]]@($null, $null)))

    foreach ($timeout in @(-1, 50)) {
        $settings.timeout = $timeout
        $settings.showSend = $false
        [void](Invoke-Static $loggerType 'ClearData')
        Send-Sample 'TX_FIRST' $true
        Send-Sample 'RX_MIDDLE' $false
        Send-Sample 'TX_LAST' $true
        $view = Single-Text
        Assert-Tx (-not $view.Contains('TX_FIRST') -and -not $view.Contains('TX_LAST') -and $view.Contains('RX_MIDDLE')) "Single pane timeout=$timeout hides only TX in its displayed document."
        Assert-Order ($single.GetLogTextSnapshot()) @('TX_FIRST','RX_MIDDLE','TX_LAST') "Single pane timeout=$timeout retains all hidden TX in chronological history."
        $writer = New-Object IO.StringWriter
        [void](Invoke-Object $single 'WriteLogSnapshot' ([object[]]@($writer, ($timeout -ge 0))))
        Assert-Order ($writer.ToString()) @('TX_FIRST','RX_MIDDLE','TX_LAST') "Manual export timeout=$timeout includes hidden TX in order."
        $writer.Dispose()
        $settings.showSend = $true
        Assert-Order (Single-Text) @('TX_FIRST','RX_MIDDLE','TX_LAST') "Single pane timeout=$timeout restores TX immediately without new traffic."
        $settings.showSend = $false
        $settings.timeout = if ($timeout -ge 0) { -1 } else { 50 }
        Assert-Tx ((Single-Text).Contains('RX_MIDDLE') -and -not (Single-Text).Contains('TX_FIRST')) "Changing packed/plain mode preserves the filtered log."
        $settings.showSend = $true
        Assert-Order (Single-Text) @('TX_FIRST','RX_MIDDLE','TX_LAST') 'Mode changes do not clear direction-aware history.'
    }

    $snapshot = Invoke-Object $single 'GetLogSnapshot'
    $snapshotFlags = @($snapshot.Items | ForEach-Object { $_.GetType().GetProperty('IsSent', $instanceFlags).GetValue($_, $null) })
    Assert-Tx ($snapshot.Items.Count -eq 3 -and $snapshotFlags[0] -and -not $snapshotFlags[1] -and $snapshotFlags[2]) 'Snapshots preserve individual TX/RX identities.'
    [void](Invoke-Object $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))
    $settings.showSend = $false
    [void](Invoke-Object $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Tx (-not (Single-Text).Contains('TX_FIRST') -and (Single-Text).Contains('RX_MIDDLE')) 'Reload applies settings changed while the cached main page was unloaded.'
    [void](Invoke-Object $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))

    $split = [Activator]::CreateInstance($splitType, [object[]]@(2, $true, ''))
    [void](Invoke-Object $split 'Page_Loaded' ([object[]]@($null, $null)))
    $split.SetSlotPortName(1, 'COM98')
    $split.SetSlotPortName(2, 'COM99')
    $first = Invoke-Object $split 'GetSlot' ([object[]]@(1))
    $second = Invoke-Object $split 'GetSlot' ([object[]]@(2))
    $firstBox = Read-Field $first 'logTextBox'
    $secondBox = Read-Field $second 'logTextBox'
    foreach ($timeout in @(-1, 50)) {
        foreach ($slot in @($first, $second)) {
            $profile = [Activator]::CreateInstance($profileType, $true)
            $profile.timeout = $timeout
            $profile.showHexFormat = 1
            $profile.encoding = 65001
            $profile.showSend = $false
            $profile.recvScript = 'default'
            [void](Invoke-Object $slot 'ApplyProfile' ([object[]]@($profile)))
            [void](Invoke-Object $slot 'ClearLog')
            Slot-Sample $slot 'SLOT_TX_FIRST' $true
            Slot-Sample $slot 'SLOT_RX_MIDDLE' $false
            Slot-Sample $slot 'SLOT_TX_LAST' $true
        }
        Assert-Tx (-not (Visible-Text $firstBox).Contains('SLOT_TX_FIRST') -and (Visible-Text $firstBox).Contains('SLOT_RX_MIDDLE')) "Split timeout=$timeout hides TX but retains RX."
        Assert-Order (Invoke-Object $first 'GetLogText') @('SLOT_TX_FIRST','SLOT_RX_MIDDLE','SLOT_TX_LAST') "Split timeout=$timeout export/snapshot text includes hidden TX."
        $firstProfile = Invoke-Object $first 'GetProfileSnapshot'
        $firstProfile.showSend = $true
        [void](Invoke-Object $first 'ApplyProcessingSettings' ([object[]]@($firstProfile)))
        Assert-Order (Visible-Text $firstBox) @('SLOT_TX_FIRST','SLOT_RX_MIDDLE','SLOT_TX_LAST') "Split timeout=$timeout re-enables TX without waiting for new data."
        Assert-Tx (-not (Visible-Text $secondBox).Contains('SLOT_TX_FIRST')) 'Changing one COM display filter does not change the other pane.'
        $firstProfile.timeout = if ($timeout -ge 0) { -1 } else { 50 }
        [void](Invoke-Object $first 'ApplyProcessingSettings' ([object[]]@($firstProfile)))
        Slot-Sample $first 'AFTER_MODE_CHANGE' $false
        Assert-Order (Visible-Text $firstBox) @('SLOT_TX_FIRST','SLOT_RX_MIDDLE','SLOT_TX_LAST','AFTER_MODE_CHANGE') 'Split mode change preserves earlier mixed-format history.'
    }

    # Exercise the actual active-COM settings notification route, not just ApplyProfile.
    $split.SetActiveSlot(2)
    $settings.showSend = $true
    Assert-Tx ((Visible-Text $secondBox).Contains('SLOT_TX_FIRST')) 'More Settings updates the selected split COM through its change notification.'
    $settings.showSend = $false
    Assert-Tx (-not (Visible-Text $secondBox).Contains('SLOT_TX_FIRST') -and (Visible-Text $firstBox).Contains('SLOT_TX_FIRST')) 'Selected COM filtering stays isolated from the first pane.'

    [void](Invoke-Object $second 'SetLogSnapshot' ([object[]]@($snapshot)))
    Assert-Tx (-not (Visible-Text $secondBox).Contains('TX_FIRST') -and (Visible-Text $secondBox).Contains('RX_MIDDLE')) 'Single-to-split snapshot honors the destination COM filter.'
    $roundtrip = Invoke-Object $second 'GetLogSnapshot'
    $settings.showSend = $false
    [void](Invoke-Object $single 'SetLogSnapshot' ([object[]]@($roundtrip)))
    [void](Invoke-Object $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Tx (-not (Single-Text).Contains('TX_FIRST')) 'Split-to-single snapshot keeps TX hidden.'
    $settings.showSend = $true
    Assert-Order (Single-Text) @('TX_FIRST','RX_MIDDLE','TX_LAST') 'Single/split roundtrip can restore previously hidden TX in its original position.'

    $settings.sessionLogEnabled = $true
    $settings.sessionLogFolder = Join-Path $testRoot 'session_logs'
    $settings.showSend = $false
    [void](Invoke-Static $loggerType 'StartSessionLog' ([object[]]@('COM98')))
    $stringPath = $loggerType.GetProperty('SessionStringLogFilePath', $staticFlags).GetValue($null, $null)
    $hexPath = $loggerType.GetProperty('SessionHexLogFilePath', $staticFlags).GetValue($null, $null)
    Send-Sample 'HIDDEN_TX_BYTES' $true
    $settings.DisableLog = $true
    Send-Sample 'DISPLAY_DISABLED_BYTES' $true
    $settings.DisableLog = $false
    [void](Invoke-Static $loggerType 'StopSessionLog')
    $stringFile = [IO.File]::ReadAllText($stringPath)
    $hexFile = [IO.File]::ReadAllText($hexPath)
    Assert-Tx ($stringFile.Contains('HIDDEN_TX_BYTES') -and $stringFile.Contains('DISPLAY_DISABLED_BYTES') -and $hexFile.Contains('48 49 44 44 45 4E 5F 54 58 5F 42 59 54 45 53')) 'Main STRING and HEX session files record actual TX while TX/the display is hidden.'

    $slotStringPath = Join-Path $testRoot 'slot-hidden-string.log'
    $slotHexPath = Join-Path $testRoot 'slot-hidden-hex.log'
    $slotString = New-Object IO.StreamWriter($slotStringPath, $false, [Text.Encoding]::UTF8)
    $slotHex = New-Object IO.StreamWriter($slotHexPath, $false, [Text.Encoding]::UTF8)
    $second.GetType().GetField('sessionStringLogWriter', $instanceFlags).SetValue($second, $slotString)
    $second.GetType().GetField('sessionHexLogWriter', $instanceFlags).SetValue($second, $slotHex)
    $settings.showSend = $false
    Slot-Sample $second 'SPLIT_HIDDEN_BYTES' $true
    $settings.DisableLog = $true
    Slot-Sample $second 'SPLIT_DISABLED_BYTES' $true
    $settings.DisableLog = $false
    [void](Invoke-Object $second 'CloseSessionLog')
    Assert-Tx ([IO.File]::ReadAllText($slotStringPath).Contains('SPLIT_HIDDEN_BYTES') -and [IO.File]::ReadAllText($slotStringPath).Contains('SPLIT_DISABLED_BYTES') -and [IO.File]::ReadAllText($slotHexPath).Contains('53 50 4C 49 54 5F 48 49 44 44 45 4E 5F 42 59 54 45 53')) 'Split STRING and HEX session writers also record hidden TX without opening a serial port.'

    $settings.sessionLogEnabled = $false
    [void](Invoke-Static $loggerType 'ClearData')
    $settings.showSend = $false
    $itemType = $assembly.GetType('llcom_plus.Pages.DataShowPage+DataShow', $true)
    $largeText = ('x' * (1024 * 1024 + 128)) + 'LAST_RETAINED_TX'
    $largeItem = Invoke-Static $itemType 'CreatePlain' ([object[]]@($largeText, $true))
    [void](Invoke-Object $single 'AppendPackedLogItem' ([object[]]@($largeItem)))
    Assert-Tx ($single.GetLogTextSnapshot().Length -le 1024 * 1024 -and $single.GetLogTextSnapshot().EndsWith('LAST_RETAINED_TX') -and -not (Single-Text).Contains('LAST_RETAINED_TX')) 'Oversized hidden main TX retains a bounded tail instead of emptying all history.'
    $settings.showSend = $true
    Assert-Tx ((Single-Text).Contains('LAST_RETAINED_TX')) 'Oversized main TX retains its direction metadata for later restore.'
    [void](Invoke-Object $second 'ClearLog')
    [void](Invoke-Object $second 'AppendDataLog' ([object[]]@($largeItem)))
    $slotTail = Invoke-Object $second 'GetLogText'
    Assert-Tx ($slotTail.Length -le 256 * 1024 -and $slotTail.EndsWith('LAST_RETAINED_TX')) 'Split giant-packet history also remains non-empty and bounded.'
    [void](Invoke-Static $loggerType 'ClearData')
    Assert-Tx ($single.GetLogTextSnapshot().Length -eq 0 -and -not (Single-Text).Contains('LAST_RETAINED_TX')) 'Clear Log clears hidden and displayed history together.'
    Assert-Tx (-not $split.IsSlotOpen(1) -and -not $split.IsSlotOpen(2) -and -not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No physical COM connection was opened by this test.'
    Assert-Tx ([TxUiExceptionObserver]::Count -eq 0) 'No offline dispatcher exception occurred.'
    Write-Host "PASS All $passed TX visibility checks ($Platform)."
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $single) { [void](Invoke-Object $single 'DataShowPage_Unloaded' ([object[]]@($null, $null))) }
    if ($null -ne $split) { [void](Invoke-Object $split 'Page_Unloaded' ([object[]]@($null, $null))) }
    if ($null -ne $loggerType) {
        [void](Invoke-Static $loggerType 'StopSessionLog')
        [void](Invoke-Static $loggerType 'CloseUartLog')
    }
    if ($null -ne $globalType) {
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null, $true, $null)
        $backupType = $assembly.GetType('llcom_plus.Tools.QuickSendBackupService', $true)
        [void]$backupType.GetMethod('Shutdown', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $null)
    }
    if ($null -ne $app) { $app.Shutdown() }
    [Threading.SynchronizationContext]::SetSynchronizationContext($previousContext)
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or
        $testName -notmatch '^llcom-tx-visibility-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
exit $exitCode
