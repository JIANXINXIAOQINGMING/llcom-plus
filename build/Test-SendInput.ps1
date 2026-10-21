[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw 'Use matching-bitness Windows PowerShell 5.1 with -STA.'
}
$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root "llcom plus\bin\$Platform\$Configuration"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-send-input-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null
$globalType = $null
$exitCode = 0
try {
    Add-Type -AssemblyName PresentationFramework
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
    Add-Type -Path (Join-Path $PSScriptRoot 'SendInputHistoryRegressionProbe.cs')
    [SendInputHistoryRegressionProbe]::Run($assembly) | ForEach-Object { Write-Host "PASS $_" }
    Add-Type -TypeDefinition @'
using System;
using System.Reflection;
public static class OfflineSendInputTarget
{
    public static int Sends;
    public static void Install(Assembly assembly)
    {
        var global = assembly.GetType("llcom_plus.Tools.Global", true);
        var type = global.GetNestedType("MainSendTarget");
        var target = Activator.CreateInstance(type);
        type.GetProperty("Key").SetValue(target, "offline-input-probe", null);
        type.GetProperty("DisplayName").SetValue(target, "Offline input probe", null);
        type.GetProperty("Send").SetValue(target, (Func<byte[], bool>)(data => { Sends++; return true; }), null);
        global.GetMethod("SetMainSendTarget").Invoke(null, new[] { target });
    }
}
'@
    $flags = [Reflection.BindingFlags]'NonPublic,Instance'
    $globalType = $assembly.GetType('llcom_plus.Tools.Global',$true)
    $globalType.GetField('ProfilePath').SetValue($null,$testRoot + '\')
    $settingsType = $assembly.GetType('llcom_plus.Model.Settings',$true)
    $settings = [Activator]::CreateInstance($settingsType,$true)
    $globalType.GetField('setting').SetValue($null,$settings)
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App',$true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri',$flags).SetValue($app,$null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    $window = [Activator]::CreateInstance($assembly.GetType('llcom_plus.MainWindow',$true))
    $type = $window.GetType()
    $composer = $window.FindName('toSendDataTextBox')
    $composer.DataContext = $settings
    $popup = $window.FindName('sendSuggestPopup')
    $suggestions = $window.FindName('sendSuggestListBox')
    function Invoke-Input([string]$name,[object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        try { return $type.GetMethod($name,$flags).Invoke($window,$arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Assert-Input([bool]$condition,[string]$message) {
        if (-not $condition) { throw "FAIL $message" }; Write-Host "PASS $message"
    }
    function Layout-Input {
        $composer.Width = 300
        $composer.Measure((New-Object Windows.Size(300,120)))
        $composer.Arrange((New-Object Windows.Rect(0,0,300,120)))
        $composer.UpdateLayout()
    }
    function Set-Input([string]$text) {
        $composer.SetCurrentValue([Windows.Controls.TextBox]::TextProperty,$text)
        $composer.CaretIndex = $composer.Text.Length
        Layout-Input
    }
    function Press-Input([Windows.Input.Key]$key,[Windows.Input.ModifierKeys]$modifiers = [Windows.Input.ModifierKeys]::None) {
        return [bool](Invoke-Input 'HandleSendInputNavigation' ([object[]]@($key,$modifiers)))
    }
    # WPF defers Popup.IsOpen until its host is loaded. Inspect the requested
    # value and exercise candidate keys directly, without showing a native window.
    function Popup-Requested {
        return [object]::Equals($popup.ReadLocalValue([Windows.Controls.Primitives.Popup]::IsOpenProperty),$true)
    }
    function Press-Candidate([Windows.Input.Key]$key) {
        return [bool](Invoke-Input 'HandleSendSuggestionKey' ([object[]]@($key)))
    }
    Layout-Input
    $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Background)
    [OfflineSendInputTarget]::Install($assembly)
    [void](Invoke-Input 'UpdateMainSendTargetUi' ([object[]]@($true)))
    Assert-Input ([bool](Invoke-Input 'IsMainSendTargetSelected')) 'A fake send target avoids all hardware and network operations.'
    Set-Input 'AT'
    [void](Invoke-Input 'SendCurrentTextBoxData')
    Set-Input 'AT+VER?'
    [void](Invoke-Input 'SendCurrentTextBoxData')
    [void](Invoke-Input 'SendCurrentTextBoxData')
    $saved = @($settingsType.GetMethod('GetSendInputHistory',$flags).Invoke($settings,$null))
    Assert-Input ($saved.Count -eq 2 -and $saved[0] -eq 'AT' -and $saved[1] -eq 'AT+VER?' -and [OfflineSendInputTarget]::Sends -eq 3) "The real manual send entry records submitted input and collapses adjacent duplicates (entries=$($saved.Count), values=$($saved -join '|'), sends=$([OfflineSendInputTarget]::Sends))."
    $json = [IO.File]::ReadAllText((Join-Path $testRoot 'settings.json'))
    $restored = [Newtonsoft.Json.JsonConvert]::DeserializeObject($json,$settingsType)
    $restoredHistory = @($settingsType.GetMethod('GetSendInputHistory',$flags).Invoke($restored,$null))
    Assert-Input ($restoredHistory.Count -eq 2 -and $restoredHistory[1] -eq 'AT+VER?') 'History persists through an actual settings-file round trip.'
    $legacy = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{"dataToSend":"keep","sendInputHistory":null}',$settingsType)
    Assert-Input (@($settingsType.GetMethod('GetSendInputHistory',$flags).Invoke($legacy,$null)).Count -eq 0 -and $legacy.dataToSend -eq 'keep') 'Old and null history settings preserve the existing composer draft.'
    Set-Input 'scratch'
    $composer.Select(1,3)
    Assert-Input ((Press-Input Up) -and $composer.Text -eq 'AT+VER?' -and -not $popup.IsOpen) 'Up recalls the latest history without opening suggestions.'
    Assert-Input ((Press-Input Up) -and $composer.Text -eq 'AT') 'Another Up recalls the preceding entry.'
    Assert-Input (-not (Press-Input Up)) 'The oldest history entry does not wrap.'
    [void](Press-Input Down)
    Assert-Input ((Press-Input Down) -and $composer.Text -eq 'scratch' -and $composer.SelectionStart -eq 1 -and $composer.SelectionLength -eq 3) 'Returning past newest restores the entire draft and selection.'
    Set-Input 'AT+VER?'
    Assert-Input ((Press-Input Up) -and $composer.Text -eq 'AT') 'The first Up skips the submitted command already visible in the composer.'
    Set-Input 'edited history'
    [void](Press-Input Up)
    Assert-Input ((Press-Input Down) -and $composer.Text -eq 'edited history') 'Editing a recalled command starts a new draft without overwriting history.'
    Set-Input "first`r`nmiddle`r`nlast"
    $composer.CaretIndex = 9
    Assert-Input ($composer.LineCount -ge 3 -and -not (Press-Input Up) -and -not (Press-Input Down)) 'Arrow keys within a multiline input retain normal line movement.'
    $composer.CaretIndex = 0
    Assert-Input ((Press-Input Up) -and $composer.Text -eq 'AT+VER?') 'Only the top line moves into older history.'
    [void](Press-Input Down)
    Assert-Input ($composer.Text -eq "first`r`nmiddle`r`nlast") 'History restores multiline text without changing CRLF.'
    Set-Input ('x' * 120)
    Assert-Input ($composer.LineCount -gt 1 -and -not (Press-Input Up)) 'Wrapped visual lines also keep ordinary arrow movement.'
    Assert-Input (-not (Press-Input Up Shift) -and -not (Press-Input Tab Shift) -and -not (Press-Input ImeProcessed)) 'Shift navigation, reverse tab and IME processing are not captured.'
    $rows = $type.GetField('toSendListItems',$flags).GetValue($window)
    $rows.Clear()
    foreach ($text in @('AT+VER?','AT+CSQ','AT+FTPOPEN')) {
        $row = $type.GetMethod('CreateBlankQuickSendItem',$flags).Invoke($window,[object[]]@(1))
        $row.text = $text
        $rows.Add($row)
    }
    Set-Input 'AT+'
    Assert-Input (-not $popup.IsOpen) 'Typing and pasting do not automatically open completion.'
    Assert-Input ((Press-Input Tab) -and (Popup-Requested) -and $suggestions.Items.Count -ge 2) 'Tab explicitly requests matching quick-command suggestions.'
    $oldInput = $composer.Text
    [void](Press-Candidate Down)
    $selected = $suggestions.SelectedItem.SendText
    Assert-Input ($composer.Text -eq $oldInput -and [OfflineSendInputTarget]::Sends -eq 3) 'Candidate arrows choose a suggestion without recalling history or sending.'
    Assert-Input ((Press-Candidate Enter) -and $composer.Text -eq $selected -and -not (Popup-Requested) -and [OfflineSendInputTarget]::Sends -eq 3) 'Enter accepts the suggestion without sending it.'
    Assert-Input ($null -ne $composer.GetBindingExpression([Windows.Controls.TextBox]::TextProperty)) 'History and completion preserve the settings binding.'
    Set-Input 'AT'
    [void](Press-Input Tab)
    Assert-Input ((Press-Candidate Escape) -and -not (Popup-Requested)) 'Esc closes manually opened suggestions.'
    Set-Input 'AT+'
    Assert-Input (-not (Popup-Requested)) 'Typing after Esc does not reopen the popup.'
    [void](Press-Input Tab)
    $composer.CaretIndex = 0
    Assert-Input (-not (Popup-Requested)) 'Moving the caret invalidates old completion candidates.'
    Set-Input ''
    Assert-Input ((Press-Input Tab) -and (Popup-Requested)) 'Tab on an empty input shows available quick commands.'
    Assert-Input ((Press-Candidate Tab) -and -not (Popup-Requested) -and $composer.Text.Length -gt 0) 'A second Tab fills the selected command.'
    Set-Input 'no matching input'
    Assert-Input ((Press-Input Tab) -and -not (Popup-Requested) -and $composer.Text -eq 'no matching input') 'Tab without candidates leaves the text untouched.'
    Assert-Input (-not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No physical serial port was opened.'
    Write-Host "PASS All send-input checks ($Platform)."
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $globalType) {
        [void]$globalType.GetMethod('ClearMainSendTarget').Invoke($null,[object[]]@($null))
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null,$true,$null)
        [void]$assembly.GetType('llcom_plus.Tools.QuickSendBackupService',$true).GetMethod('Shutdown',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,$null)
    }
    if ($null -ne $app) { $app.Shutdown() }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or $testName -notmatch '^llcom-send-input-[0-9a-f]{32}$') {
        throw 'Refusing cleanup outside the isolated test directory.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
exit $exitCode
