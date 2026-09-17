[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64',
    [switch]$RenderPreviews
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use matching-bitness Windows PowerShell 5.1 -STA.' }
$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root "llcom plus\bin\$Platform\$Configuration"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-log-prefix-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null; $single = $null; $split = $null; $globalType = $null; $assembly = $null; $loggerType = $null
$passed = 0; $exitCode = 0
$oldContext = [Threading.SynchronizationContext]::Current
try {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -ReferencedAssemblies @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml') -TypeDefinition @'
using System;
using System.Windows;
public static class PrefixUiObserver {
    public static int Count;
    public static void Install(string directory) {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            var name = new System.Reflection.AssemblyName(args.Name).Name;
            return name == "System.Runtime.CompilerServices.Unsafe" ? System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(directory, name + ".dll")) : null;
        };
    }
    public static void Watch(Application app) {
        app.DispatcherUnhandledException += (sender, e) => { Count++; Console.Error.WriteLine(e.Exception.ToString()); e.Handled = true; };
    }
}
'@
    [PrefixUiObserver]::Install($outputDir)
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object { try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { } }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
    $instanceFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $staticFlags = [Reflection.BindingFlags]'Public,NonPublic,Static'
    function Invoke-Member($target, [string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i] -and $arguments[$i] -isnot [Newtonsoft.Json.Linq.JToken]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        try { return $target.GetType().GetMethod($name, $instanceFlags).Invoke($target, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Invoke-Static([Type]$type, [string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i] -and $arguments[$i] -isnot [Newtonsoft.Json.Linq.JToken]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        $methods = @($type.GetMethods($staticFlags) | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $arguments.Length })
        if ($methods.Count -ne 1) { throw "Expected one $name overload with $($arguments.Length) parameters." }
        try { return $methods[0].Invoke($null, $arguments) } catch { throw $_.Exception.GetBaseException() }
    }
    function Assert-Prefix([bool]$condition, [string]$message) {
        if (-not $condition) { throw "FAIL $message" }
        $script:passed++; Write-Host "PASS $message"
    }
    function Box-Text($box) { return (New-Object Windows.Documents.TextRange($box.Document.ContentStart, $box.Document.ContentEnd)).Text }
    function Single-Text { return Box-Text ($single.FindName($(if ($settings.timeout -ge 0) { 'MainPackedTextBox' } else { 'MainTextBox' }))) }
    function New-Profile { return [Activator]::CreateInstance($profileType, $true) }
    function Prefix($profile, [bool]$sent = $true, [string]$port = 'COM98') {
        return Invoke-Static $settingsType 'FormatLogPrefix' ([object[]]@($stamp, $port, $sent, $profile))
    }
    function Send-Sample([string]$text) { [void](Invoke-Static $loggerType 'ShowData' ([object[]]@([Text.Encoding]::UTF8.GetBytes($text), $true, $null, $null))) }
    function Export-Single {
        $writer = New-Object IO.StringWriter
        try { [void](Invoke-Member $single 'WriteLogSnapshot' ([object[]]@($writer, ($settings.timeout -ge 0)))); return $writer.ToString() }
        finally { $writer.Dispose() }
    }
    $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
    $globalType.GetField('ProfilePath').SetValue($null, $testRoot + '\')
    $settingsType = $assembly.GetType('llcom_plus.Model.Settings', $true)
    $profileType = $assembly.GetType('llcom_plus.Model.UartPortProfile', $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $globalType.GetField('setting').SetValue($null, $settings)
    $loggerType = $assembly.GetType('llcom_plus.Tools.Logger', $true)
    $stamp = [datetime]'2026-09-17T12:34:56.789'
    $left = [string][char]0x2190; $right = [string][char]0x2192
    $profile = New-Profile
    Assert-Prefix ((Prefix $profile) -ceq ("[2026/09/17 12:34:56.789] $left ")) 'Default prefix exactly preserves the previous timestamp and TX arrow.'
    Assert-Prefix ((Prefix $profile $false) -ceq ("[2026/09/17 12:34:56.789] $right ")) 'Default RX arrow is unchanged.'
    $legacyProfile = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{"baudRate":115200}', $profileType)
    $legacySettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{}', $settingsType)
    Assert-Prefix ((Prefix $legacyProfile) -ceq (Prefix $profile) -and $legacySettings.LogShowDate -and $legacySettings.LogShowTime -and $legacySettings.LogShowMilliseconds) 'Old profile and settings JSON use the unchanged defaults.'
    $profile.logShowDate = $false; $profile.logShowMilliseconds = $false; $profile.logShowPort = $true; $profile.logTxLabel = 'TX'; $profile.logRxLabel = 'RX'
    Assert-Prefix ((Prefix $profile) -ceq '[12:34:56] [COM98] TX ') 'Time-only, no milliseconds, COM and custom TX label compose correctly.'
    $profile.logShowTime = $false; $profile.logShowDate = $true
    Assert-Prefix ((Prefix $profile $false) -ceq '[2026/09/17] [COM98] RX ') 'Date-only prefix and independent RX label compose correctly.'
    $profile.logShowDate = $false; $profile.logShowPort = $false; $profile.logTxLabel = ''
    Assert-Prefix ((Prefix $profile) -ceq '') 'Empty direction with all fields disabled hides the entire prefix.'
    $profile.logTxLabel = "TX`r`n`t" + [char]0x202E + ('X' * 40)
    $normalized = Invoke-Static $settingsType 'CreateNormalizedUartProfileSnapshot' ([object[]]@($profile))
    Assert-Prefix ($normalized.logTxLabel.Length -le 16 -and $normalized.logTxLabel -notmatch '[\r\n\t]' -and -not $normalized.logTxLabel.Contains([string][char]0x202E)) 'Labels are bounded single-line plain text without bidi controls.'
    [void](Invoke-Member $settings 'SaveUartProfileSnapshot' ([object[]]@('COM98', $normalized)))
    $loadedSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText((Join-Path $testRoot 'settings.json')), $settingsType)
    $loadedProfile = Invoke-Member $loadedSettings 'GetUartProfileSnapshot' ([object[]]@('COM98'))
    Assert-Prefix (-not $loadedProfile.logShowDate -and -not $loadedProfile.logShowTime -and -not $loadedProfile.logShowMilliseconds -and $loadedProfile.logTxLabel -ceq $normalized.logTxLabel) 'All prefix options survive per-COM settings file persistence.'
    $workspaceType = $assembly.GetType('llcom_plus.Tools.WorkspaceService', $true)
    $profileJson = [Newtonsoft.Json.Linq.JObject]::FromObject($normalized)
    [void](Invoke-Static $workspaceType 'ValidateProfile' ([object[]](,$profileJson)))
    $workspace = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Tools.WorkspaceSnapshot', $true), $true)
    $workspace.SerialProfiles.Add('COM98', $profileJson)
    $workspaceProfile = Invoke-Member $workspace 'GetPortProfile' ([object[]]@('COM98'))
    Assert-Prefix ($workspaceProfile.logTxLabel -ceq $normalized.logTxLabel -and -not $workspaceProfile.logShowDate -and -not $workspaceProfile.logShowMilliseconds) 'Workspace profile validation and restoration retain custom options.'
    $legacyJson = [Newtonsoft.Json.Linq.JObject]::Parse('{"baudRate":115200}')
    [void](Invoke-Static $workspaceType 'ValidateProfile' ([object[]](,$legacyJson)))
    $workspace.SerialProfiles['COM98'] = $legacyJson
    $legacyWorkspaceProfile = Invoke-Member $workspace 'GetPortProfile' ([object[]]@('COM98'))
    Assert-Prefix ((Prefix $legacyWorkspaceProfile) -ceq (Prefix (New-Profile))) 'A legacy workspace missing prefix fields restores the unchanged defaults.'
    $invalidJson = [Newtonsoft.Json.Linq.JObject]::Parse('{"logTxLabel":"BAD\nLABEL"}')
    $rejected = $false
    try { [void](Invoke-Static $workspaceType 'ValidateProfile' ([object[]](,$invalidJson))) } catch [IO.InvalidDataException] { $rejected = $true }
    Assert-Prefix $rejected 'Workspace import rejects injected multi-line direction labels.'
    # WPF is instantiated offscreen; normal startup, hardware opening and sending are never invoked.
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri', $instanceFlags).SetValue($app, $null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    [PrefixUiObserver]::Watch($app)
    [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext($app.Dispatcher)))
    $settings.showHexFormat = 1; $settings.timeout = 50; $settings.showSend = $true
    [void](Invoke-Member $settings 'SaveUartProfileSnapshot' ([object[]]@('COM98', (New-Profile))))
    [void](Invoke-Member $settings 'SetActiveUartProfile' ([object[]]@('COM98', $true)))
    $settings.showHexFormat = 1
    $single = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Pages.DataShowPage', $true), $true)
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    Send-Sample 'TX_PREFIX_SAMPLE'
    [void](Invoke-Static $loggerType 'ShowSerialData' ([object[]]@([Text.Encoding]::UTF8.GetBytes('ORIGINAL_PORT_SAMPLE'), $true, $null, $null, 'COM7')))
    $original = $single.GetLogTextSnapshot(); $originalExport = Export-Single
    Assert-Prefix ((Single-Text).Contains($left + ' TX_PREFIX_SAMPLE')) 'Main log initially renders the legacy prefix.'
    $settings.LogShowDate = $false; $settings.LogShowTime = $false; $settings.LogShowPort = $true; $settings.LogTxLabel = 'OUT'
    Assert-Prefix ((Single-Text).Contains('[COM98] OUT TX_PREFIX_SAMPLE')) 'Changing main prefix settings immediately re-renders retained history.'
    Assert-Prefix ((Single-Text).Contains('[COM7] OUT ORIGINAL_PORT_SAMPLE')) 'A packet keeps its captured source port instead of the currently selected profile.'
    Assert-Prefix ($single.GetLogTextSnapshot() -ceq $original -and (Export-Single) -ceq $originalExport) 'Manual export and canonical history do not inherit custom view prefixes.'
    $settings.showSend = $false; $settings.LogTxLabel = 'SEND'; $settings.showSend = $true
    Assert-Prefix ((Single-Text).Contains('[COM98] SEND TX_PREFIX_SAMPLE')) 'Restoring hidden TX applies the current prefix without losing history.'
    [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))
    $settings.LogTxLabel = 'RELOAD'
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Prefix ((Single-Text).Contains('[COM98] RELOAD TX_PREFIX_SAMPLE')) 'A cached page reload applies options changed while unloaded.'
    $settings.sessionLogEnabled = $true
    $settings.sessionLogFolder = Join-Path $testRoot 'session_logs'
    [void](Invoke-Static $loggerType 'StartSessionLog' ([object[]]@('COM98')))
    $stringPath = $loggerType.GetProperty('SessionStringLogFilePath', $staticFlags).GetValue($null, $null)
    $hexPath = $loggerType.GetProperty('SessionHexLogFilePath', $staticFlags).GetValue($null, $null)
    $settings.LogTxLabel = 'UI_ONLY_LABEL'
    Send-Sample 'PREFIX_FILE'
    [void](Invoke-Static $loggerType 'StopSessionLog')
    $stringLog = [IO.File]::ReadAllText($stringPath); $hexLog = [IO.File]::ReadAllText($hexPath)
    Assert-Prefix ($stringLog.Contains('PREFIX_FILE') -and -not $stringLog.Contains('UI_ONLY_LABEL') -and $hexLog.Contains('50 52 45 46 49 58 5F 46 49 4C 45') -and -not $hexLog.Contains('UI_ONLY_LABEL')) 'Automatic STRING and HEX files retain canonical payloads without view-only labels.'
    $settings.sessionLogEnabled = $false
    $settings.timeout = -1
    [void](Invoke-Static $loggerType 'ClearData')
    Send-Sample 'CONTINUOUS_TEXT'
    $continuous = Single-Text
    $settings.LogTxLabel = 'OTHER'
    Assert-Prefix ((Single-Text) -ceq $continuous -and -not $continuous.Contains('SEND')) 'Continuous text mode is not split up by added packet prefixes.'
    [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))
    $split = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Pages.MultiPortPage', $true), [object[]]@(2, $true, ''))
    [void](Invoke-Member $split 'Page_Loaded' ([object[]]@($null, $null)))
    $split.SetSlotPortName(1, 'COM98'); $split.SetSlotPortName(2, 'COM99')
    $first = Invoke-Member $split 'GetSlot' ([object[]]@(1)); $second = Invoke-Member $split 'GetSlot' ([object[]]@(2))
    foreach ($slot in @($first, $second)) {
        $p = New-Profile; $p.showHexFormat = 1
        [void](Invoke-Member $slot 'ApplyProfile' ([object[]]@($p)))
        [void](Invoke-Member $slot 'ClearLog')
        [void](Invoke-Member $slot 'WriteDataLog' ([object[]]@([Text.Encoding]::UTF8.GetBytes('SPLIT_SAMPLE'), $true, $null, $false, $false)))
    }
    $firstBox = $first.GetType().GetField('logTextBox', $instanceFlags).GetValue($first)
    $secondBox = $second.GetType().GetField('logTextBox', $instanceFlags).GetValue($second)
    $splitOriginal = Invoke-Member $first 'GetLogText'
    $p = Invoke-Member $first 'GetProfileSnapshot'
    $p.logShowDate = $false; $p.logShowTime = $false; $p.logShowPort = $true; $p.logTxLabel = 'SENT'
    [void](Invoke-Member $first 'ApplyProcessingSettings' ([object[]]@($p)))
    Assert-Prefix ((Box-Text $firstBox).Contains('[COM98] SENT SPLIT_SAMPLE') -and (Box-Text $secondBox).Contains($left + ' SPLIT_SAMPLE')) 'Per-COM prefix changes stay isolated between split panes.'
    Assert-Prefix ((Invoke-Member $first 'GetLogText') -ceq $splitOriginal) 'Split export retains the canonical full timestamp and direction.'
    $snapshot = Invoke-Member $first 'GetLogSnapshot'
    $settings.timeout = 50; $settings.LogShowDate = $false; $settings.LogShowTime = $false; $settings.LogShowPort = $true; $settings.LogTxLabel = 'BACK'
    [void](Invoke-Member $single 'Page_Loaded' ([object[]]@($null, $null)))
    [void](Invoke-Member $single 'SetLogSnapshot' ([object[]]@($snapshot)))
    Assert-Prefix ((Single-Text).Contains('[COM98] BACK SPLIT_SAMPLE')) 'Split-to-main history retains its original COM and timestamp metadata.'
    $settings.LogShowDate = $true; $settings.LogShowTime = $true; $settings.LogShowMilliseconds = $false
    Assert-Prefix ((Single-Text) -match '\[\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\] \[COM98\] BACK') 'Historical timestamps can be restored without waiting for fresh traffic.'
    $window = [Activator]::CreateInstance($assembly.GetType('llcom_plus.SettingWindow', $true), $true)
    $window.DataContext = $settings
    $app.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    $dateBox = $window.FindName('LogPrefixDateCheckBox')
    $dateBox.SetCurrentValue([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty, $false)
    $dateBox.GetBindingExpression([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).UpdateSource()
    Assert-Prefix (-not $settings.LogShowDate) 'The real settings checkbox updates the per-COM option.'
    $txBox = $window.FindName('LogPrefixTxTextBox')
    $txBox.SetCurrentValue([Windows.Controls.TextBox]::TextProperty, 'WRITE')
    $txBox.GetBindingExpression([Windows.Controls.TextBox]::TextProperty).UpdateSource()
    [void](Invoke-Member $window 'RefreshLogPrefixPreview')
    Assert-Prefix ($settings.LogTxLabel -ceq 'WRITE' -and $window.FindName('LogPrefixPreviewText').Text.Contains('WRITE AT+VER?')) 'Real direction binding and preview display the selected options.'
    if ($RenderPreviews) {
        $previewDir = Join-Path $root 'artifacts\log-prefix'
        [void](New-Item -ItemType Directory -Path $previewDir -Force)
        $expander = $window.FindName('LogPrefixExpander'); $expander.IsExpanded = $true
        $content = $expander.Content; $expander.Content = $null
        $border = New-Object Windows.Controls.Border
        $border.DataContext = $settings; $border.Padding = New-Object Windows.Thickness(12)
        $border.SetResourceReference([Windows.Documents.TextElement]::ForegroundProperty, 'AppGlassTextBrush')
        $border.Width = 420
        foreach ($theme in @('Light','Dark')) {
        [void](Invoke-Static $globalType 'ApplyTheme' ([object[]]@(($theme -eq 'Dark'))))
        $border.Background = $app.TryFindResource('AppWindowBackgroundBrush')
        $border.Child = $content
        $app.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
        $border.Measure((New-Object Windows.Size(420, [double]::PositiveInfinity)))
        $border.Arrange((New-Object Windows.Rect(0, 0, 420, $border.DesiredSize.Height))); $border.UpdateLayout()
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(420, ([int][Math]::Ceiling($border.ActualHeight)), 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($border)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $previewPath = Join-Path $previewDir "log-prefix-$Platform-$theme.png"
        $stream = [IO.File]::Create($previewPath)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        $border.Child = $null
        Write-Host "RENDER $previewPath"
        }
        $expander.Content = $content
    }
    [void](Invoke-Member $window 'ResetLogPrefixButton_Click' ([object[]]@($null, $null)))
    Assert-Prefix ($settings.LogShowDate -and $settings.LogShowTime -and $settings.LogShowMilliseconds -and -not $settings.LogShowPort -and $settings.LogTxLabel -ceq $left -and $settings.LogRxLabel -ceq $right) 'Restore defaults resets only the six log-prefix options.'
    Assert-Prefix (-not $split.IsSlotOpen(1) -and -not $split.IsSlotOpen(2) -and -not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No physical COM connection was opened.'
    Assert-Prefix ([PrefixUiObserver]::Count -eq 0) 'No offscreen UI exception occurred.'
    Write-Host "PASS All $passed log-prefix checks ($Platform)."
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $single) { [void](Invoke-Member $single 'DataShowPage_Unloaded' ([object[]]@($null, $null))) }
    if ($null -ne $split) { [void](Invoke-Member $split 'Page_Unloaded' ([object[]]@($null, $null))) }
    if ($null -ne $loggerType) { [void](Invoke-Static $loggerType 'StopSessionLog'); [void](Invoke-Static $loggerType 'CloseUartLog') }
    if ($null -ne $globalType) {
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null, $true, $null)
        $backupType = $assembly.GetType('llcom_plus.Tools.QuickSendBackupService', $true)
        [void]$backupType.GetMethod('Shutdown', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $null)
    }
    if ($null -ne $app) { $app.Shutdown() }
    [Threading.SynchronizationContext]::SetSynchronizationContext($oldContext)
    $resolved = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempBase -or [IO.Path]::GetFileName($resolved) -ne $testName -or $testName -notmatch '^llcom-log-prefix-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
exit $exitCode
