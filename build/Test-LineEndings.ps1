[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64',
    [switch]$RenderPreviews
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
$testName = 'llcom-line-endings-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null
$assembly = $null
$globalType = $null
$loggerType = $null
$single = $null
$split = $null
$exitCode = 0
$passed = 0
$previousContext = [Threading.SynchronizationContext]::Current
try {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -ReferencedAssemblies @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml') -TypeDefinition @'
using System;
using System.Windows;
public static class LineEndingsUiExceptionObserver
{
    public static int Count;
    public static void InstallDependencyRedirect(string directory)
    {
        // PowerShell does not load the application's .exe.config redirects.
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
    [LineEndingsUiExceptionObserver]::InstallDependencyRedirect($outputDir)
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
        $methods = @($type.GetMethods($staticFlags) | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $arguments.Length })
        if ($methods.Count -ne 1) { throw "Expected one $name overload with $($arguments.Length) parameters." }
        try { return $methods[0].Invoke($null, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Read-Field($target, [string]$name) { return $target.GetType().GetField($name, $instanceFlags).GetValue($target) }
    function Assert-Ending([bool]$condition, [string]$message) {
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
    function Format-Display([string]$text, [int]$codePage, [bool]$enableSymbol, [bool]$showLineEndings) {
        $bytes = [Text.Encoding]::GetEncoding($codePage).GetBytes($text)
        return Invoke-Static $globalType 'Byte2LogDisplay' ([object[]]@($bytes, -1, $codePage, $enableSymbol, $showLineEndings))
    }
    function Assert-Markers([string]$text, [string]$prefix, [bool]$visible, [string]$message) {
        $expected = @(($prefix + '_BEGIN\r\n'), ($prefix + '_CR\r'), ($prefix + '_LF\n'))
        foreach ($token in $expected) {
            if ($text.Contains($token) -ne $visible) { throw "FAIL $message ($token)" }
        }
        Assert-Ending ($text.Contains('LITERAL:\r\n')) "$message; literal backslashes are preserved."
        if (-not $visible) {
            Assert-Ending ($text.Contains($prefix + "_BEGIN`r`n")) "$message; actual line breaks are preserved."
        }
    }
    function Export-Single {
        $writer = New-Object IO.StringWriter
        try {
            [void](Invoke-Object $single 'WriteLogSnapshot' ([object[]]@($writer, ($settings.timeout -ge 0))))
            return $writer.ToString()
        }
        finally { $writer.Dispose() }
    }
    function Count-Literal([string]$text, [string]$value) {
        return [regex]::Matches($text, [regex]::Escape($value)).Count
    }
    function Pump-Ui {
        $app.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    }
    function Render-Options($page, [string]$name) {
        $popupName = if ($page -is [llcom_plus.Pages.DataShowPage]) { 'LogOptionsPopup' } else { 'ExternalOptionsPopup' }
        $popup = $page.FindName($popupName)
        $content = $popup.Child
        $popup.Child = $null
        $surface = New-Object Windows.Controls.Border
        $surface.Padding = New-Object Windows.Thickness(12)
        $surface.Background = $app.TryFindResource('AppWindowBackgroundBrush')
        $surface.Child = $content
        try {
            $surface.Measure((New-Object Windows.Size(434, [double]::PositiveInfinity)))
            $height = [int][Math]::Ceiling($surface.DesiredSize.Height)
            $surface.Arrange((New-Object Windows.Rect(0, 0, 434, $height)))
            $surface.UpdateLayout()
            Pump-Ui
            $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(434, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
            $bitmap.Render($surface)
            $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
            $previewDir = Join-Path $root 'artifacts\line-endings'
            [void](New-Item -ItemType Directory -Path $previewDir -Force)
            $path = Join-Path $previewDir ($name + '.png')
            $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
            try { $encoder.Save($stream) } finally { $stream.Dispose() }
            Write-Host "RENDER $path"
        }
        finally { $surface.Child = $null; $popup.Child = $content }
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
    Assert-Ending ($settings.ShowLineEndings -and ([Activator]::CreateInstance($profileType, $true)).showLineEndings) 'New settings and per-COM profiles default to showing CR/LF markers.'
    $legacySettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{}', $settingsType)
    $legacyProfile = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{"baudRate":115200}', $profileType)
    Assert-Ending ($legacySettings.ShowLineEndings -and $legacyProfile.showLineEndings) 'Old JSON without the new property keeps CR/LF markers enabled.'
    $savedFalse = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{"ShowLineEndings":false}', $settingsType)
    $profileFalse = [Newtonsoft.Json.JsonConvert]::DeserializeObject('{"showLineEndings":false}', $profileType)
    $profileAgain = [Newtonsoft.Json.JsonConvert]::DeserializeObject([Newtonsoft.Json.JsonConvert]::SerializeObject($profileFalse), $profileType)
    Assert-Ending (-not $savedFalse.ShowLineEndings -and -not $profileAgain.showLineEndings) 'Explicit false survives both settings loading and per-COM JSON roundtrip.'
    [void](Invoke-Object $settings 'SaveUartProfileSnapshot' ([object[]]@('COM97', $profileFalse)))
    $settings.ShowLineEndings = $false
    $savedSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText((Join-Path $testRoot 'settings.json')), $settingsType)
    $persistedProfile = Invoke-Object $savedSettings 'GetUartProfileSnapshot' ([object[]]@('COM97'))
    Assert-Ending (-not $savedSettings.ShowLineEndings -and -not $persistedProfile.showLineEndings) 'The actual settings file persists global and per-COM false values.'
    $settings.ShowLineEndings = $true

    # Exact formatter checks distinguish generated markers from actual controls
    # and literal backslash text. No WPF newline normalization is involved here.
    $unicodeWord = [string][char]0x6D4B + [char]0x8BD5
    $raw = $unicodeWord + "_A`r`nB`nC`rD"
    $marked = $unicodeWord + '_A\r\n' + "`r`n" + 'B\n' + "`n" + 'C\r' + "`r" + 'D'
    foreach ($codePage in @(65001, 936, 1200)) {
        foreach ($enableSymbol in @($false, $true)) {
            Assert-Ending ((Format-Display $raw $codePage $enableSymbol $true) -ceq $marked) "Codepage $codePage / symbols=$enableSymbol shows CRLF, LF and CR markers without changing their controls."
            Assert-Ending ((Format-Display $raw $codePage $enableSymbol $false) -ceq $raw) "Codepage $codePage / symbols=$enableSymbol hides only generated CR/LF markers."
            Assert-Ending ((Format-Display 'literal:\r\n\t' $codePage $enableSymbol $false) -ceq 'literal:\r\n\t') "Codepage $codePage / symbols=$enableSymbol never strips literal backslash text."
            $tabBytes = [Text.Encoding]::GetEncoding($codePage).GetBytes("A`tB")
            $legacyTab = Invoke-Static $globalType 'Byte2Readable' ([object[]]@($tabBytes, -1, $codePage, $enableSymbol))
            Assert-Ending ((Format-Display "A`tB" $codePage $enableSymbol $false) -ceq $legacyTab -and
                (Format-Display "A`tB" $codePage $enableSymbol $true) -ceq $legacyTab) "Codepage $codePage / symbols=$enableSymbol keeps tab behavior independent of CR/LF display."
        }
    }
    $rangeBytes = [Text.Encoding]::UTF8.GetBytes("A`r`nB")
    Assert-Ending ((Invoke-Static $globalType 'Byte2LogDisplay' ([object[]]@($rangeBytes, 1, 65001, $true, $true))) -ceq 'A') 'Explicit byte length is respected by the new formatter.'
    Assert-Ending ((Invoke-Static $globalType 'Byte2LogDisplay' ([object[]]@($null, -1, 65001, $true, $true))) -ceq '') 'Null byte input formats as empty text.'

    $scriptDirectory = Join-Path $testRoot 'user_script_recv_convert'
    [void](New-Item -ItemType Directory -Path $scriptDirectory)
    Copy-Item -LiteralPath (Join-Path $root 'llcom plus\Resources\DefaultFiles\user_script_recv_convert\default.js') -Destination $scriptDirectory
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri', $instanceFlags).SetValue($app, $null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    [LineEndingsUiExceptionObserver]::Install($app)
    [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext($app.Dispatcher)))
    $settings.showHexFormat = 1
    $settings.encoding = 65001
    $settings.recvScript = 'default'
    $settings.EnableSymbol = $false
    $settings.sessionLogEnabled = $false
    $settings.showSend = $true
    # Fail directly before a missing receive converter could open an error dialog.
    $scriptArgs = New-Object Collections.ArrayList
    [void]$scriptArgs.Add('uartData')
    [void]$scriptArgs.Add([Text.Encoding]::UTF8.GetBytes('PREFLIGHT'))
    $loaderType = $assembly.GetType('llcom_plus.ScriptEnv.JavaScriptLoader', $true)
    $converted = Invoke-Static $loaderType 'Run' ([object[]]@('default.js', $scriptArgs, 'user_script_recv_convert/'))
    Assert-Ending ([Text.Encoding]::UTF8.GetString([byte[]]$converted) -eq 'PREFLIGHT') 'Isolated receive converter dependencies are ready.'
    $single = [Activator]::CreateInstance($singleType, $true)
    $settings.ShowLineEndings = $false
    # Page lifecycle only: never construct/show the normal application window,
    # open a COM connection, or invoke a send-to-hardware method.
    [void](Invoke-Object $single 'Page_Loaded' ([object[]]@($null, $null)))
    $tx = "TX_BEGIN`r`nTX_CR`rTX_LF`n" + 'LITERAL:\r\n' + "`tTX_END"
    $rx = "RX_BEGIN`r`nRX_CR`rRX_LF`n" + 'LITERAL:\r\n' + "`tRX_END"
    Send-Sample $tx $true
    Assert-Markers (Single-Text) 'TX' $false 'Startup honors a saved disabled marker setting'
    $settings.ShowLineEndings = $true
    Assert-Markers (Single-Text) 'TX' $true 'First enable after a disabled startup refreshes existing history'
    foreach ($timeout in @(-1, 50)) {
        $settings.timeout = $timeout
        $settings.ShowLineEndings = $true
        [void](Invoke-Static $loggerType 'ClearData')
        Send-Sample $tx $true
        Send-Sample $rx $false
        Assert-Markers (Single-Text) 'TX' $true "Single timeout=$timeout displays TX markers by default"
        Assert-Markers (Single-Text) 'RX' $true "Single timeout=$timeout displays RX markers by default"
        $beforeSnapshot = $single.GetLogTextSnapshot()
        $beforeExport = Export-Single
        $settings.ShowLineEndings = $false
        Assert-Markers (Single-Text) 'TX' $false "Single timeout=$timeout hides historic TX markers immediately"
        Assert-Markers (Single-Text) 'RX' $false "Single timeout=$timeout hides historic RX markers immediately"
        Assert-Ending ($single.GetLogTextSnapshot() -ceq $beforeSnapshot -and (Export-Single) -ceq $beforeExport) "Single timeout=$timeout does not change snapshot/export text when hiding markers."
        $settings.ShowLineEndings = $true
        Assert-Markers (Single-Text) 'TX' $true "Single timeout=$timeout restores historic TX markers without new traffic"
        Assert-Markers (Single-Text) 'RX' $true "Single timeout=$timeout restores historic RX markers without new traffic"
        $settings.showSend = $false
        $settings.ShowLineEndings = $false
        $settings.showSend = $true
        Assert-Markers (Single-Text) 'TX' $false "Single timeout=$timeout restores hidden TX using the current marker setting"
    }
    $snapshot = Invoke-Object $single 'GetLogSnapshot'
    [void](Invoke-Object $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))
    $settings.ShowLineEndings = $true
    [void](Invoke-Object $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Markers (Single-Text) 'RX' $true 'Reload applies the marker setting changed while the cached page was unloaded'
    [void](Invoke-Object $single 'DataShowPage_Unloaded' ([object[]]@($null, $null)))

    $split = [Activator]::CreateInstance($splitType, [object[]]@(2, $false, ''))
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
            $profile.showSend = $true
            $profile.recvScript = 'default'
            $profile.enableSymbol = $false
            $profile.showLineEndings = $true
            [void](Invoke-Object $slot 'ApplyProfile' ([object[]]@($profile)))
            [void](Invoke-Object $slot 'ClearLog')
            Slot-Sample $slot $tx $true
            Slot-Sample $slot $rx $false
        }
        $before = Invoke-Object $first 'GetLogText'
        $firstProfile = Invoke-Object $first 'GetProfileSnapshot'
        $firstProfile.showLineEndings = $false
        [void](Invoke-Object $first 'ApplyProcessingSettings' ([object[]]@($firstProfile)))
        Assert-Markers (Visible-Text $firstBox) 'TX' $false "Split timeout=$timeout immediately hides historic TX markers"
        Assert-Markers (Visible-Text $firstBox) 'RX' $false "Split timeout=$timeout immediately hides historic RX markers"
        Assert-Markers (Visible-Text $secondBox) 'TX' $true "Split timeout=$timeout keeps the second COM setting independent"
        Assert-Ending ((Invoke-Object $first 'GetLogText') -ceq $before) "Split timeout=$timeout exports unchanged full log text."
        $firstProfile.showLineEndings = $true
        [void](Invoke-Object $first 'ApplyProcessingSettings' ([object[]]@($firstProfile)))
        Assert-Markers (Visible-Text $firstBox) 'RX' $true "Split timeout=$timeout restores historic markers immediately"
    }
    # Exercise the actual active-COM settings notification route, not only a
    # direct profile update. This is the route used by the send-options popup.
    $split.SetActiveSlot(2)
    $settings.ShowLineEndings = $false
    Assert-Markers (Visible-Text $secondBox) 'RX' $false 'The selected COM responds to the popup setting notification'
    Assert-Markers (Visible-Text $firstBox) 'RX' $true 'The nonselected COM retains its independent popup setting'
    [void](Invoke-Object $second 'SetLogSnapshot' ([object[]]@($snapshot)))
    Assert-Markers (Visible-Text $secondBox) 'TX' $false 'Single-to-split snapshots honor the destination marker setting'
    $settings.ShowLineEndings = $true
    Assert-Markers (Visible-Text $secondBox) 'TX' $true 'Single-to-split snapshots retain the data needed to restore markers'
    $roundtrip = Invoke-Object $second 'GetLogSnapshot'
    $settings.ShowLineEndings = $false
    [void](Invoke-Object $single 'SetLogSnapshot' ([object[]]@($roundtrip)))
    [void](Invoke-Object $single 'Page_Loaded' ([object[]]@($null, $null)))
    Assert-Markers (Single-Text) 'RX' $false 'Split-to-single snapshots honor the destination marker setting'
    $settings.ShowLineEndings = $true
    Assert-Markers (Single-Text) 'RX' $true 'Split-to-single snapshots retain marker-toggle history'

    foreach ($timeout in @(-1, 50)) {
        $settings.timeout = $timeout
        $settings.showHexFormat = 2
        $settings.ShowLineEndings = $true
        [void](Invoke-Static $loggerType 'ClearData')
        Send-Sample "HEX`r`n" $true
        $beforeHex = Single-Text
        $settings.ShowLineEndings = $false
        Assert-Ending ((Single-Text) -ceq $beforeHex -and $beforeHex.Contains('48 45 58 0D 0A')) "Main HEX view timeout=$timeout is unaffected by the CR/LF display setting."
        $hexProfile = Invoke-Object $first 'GetProfileSnapshot'
        $hexProfile.showHexFormat = 2
        $hexProfile.timeout = $timeout
        $hexProfile.showLineEndings = $true
        [void](Invoke-Object $first 'ApplyProcessingSettings' ([object[]]@($hexProfile)))
        [void](Invoke-Object $first 'ClearLog')
        Slot-Sample $first "HEX`r`n" $false
        $beforeHex = Visible-Text $firstBox
        $hexProfile.showLineEndings = $false
        [void](Invoke-Object $first 'ApplyProcessingSettings' ([object[]]@($hexProfile)))
        Assert-Ending ((Visible-Text $firstBox) -ceq $beforeHex -and $beforeHex.Contains('48 45 58 0D 0A')) "Split HEX view timeout=$timeout is unaffected by the CR/LF display setting."
    }

    $settings.showHexFormat = 1
    $settings.sessionLogEnabled = $true
    $settings.sessionLogFolder = Join-Path $testRoot 'session_logs'
    $fileText = "FILE_ENDINGS`r`n" + 'LITERAL:\r\n' + "`tEND"
    $fileHex = [BitConverter]::ToString([Text.Encoding]::UTF8.GetBytes($fileText)).Replace('-', ' ')
    [void](Invoke-Static $loggerType 'StartSessionLog' ([object[]]@('COM98')))
    $stringPath = $loggerType.GetProperty('SessionStringLogFilePath', $staticFlags).GetValue($null, $null)
    $hexPath = $loggerType.GetProperty('SessionHexLogFilePath', $staticFlags).GetValue($null, $null)
    foreach ($enabled in @($true, $false)) {
        $settings.ShowLineEndings = $enabled
        Send-Sample $fileText $true
    }
    [void](Invoke-Static $loggerType 'StopSessionLog')
    Assert-Ending ((Count-Literal ([IO.File]::ReadAllText($stringPath)) 'FILE_ENDINGS\r\n') -eq 2 -and
        (Count-Literal ([IO.File]::ReadAllText($hexPath)) $fileHex) -eq 2) 'Main STRING and HEX session files record identical payloads with markers on and off.'
    $slotStringPath = Join-Path $testRoot 'slot-line-endings-string.log'
    $slotHexPath = Join-Path $testRoot 'slot-line-endings-hex.log'
    $slotString = New-Object IO.StreamWriter($slotStringPath, $false, [Text.Encoding]::UTF8)
    $slotHex = New-Object IO.StreamWriter($slotHexPath, $false, [Text.Encoding]::UTF8)
    $second.GetType().GetField('sessionStringLogWriter', $instanceFlags).SetValue($second, $slotString)
    $second.GetType().GetField('sessionHexLogWriter', $instanceFlags).SetValue($second, $slotHex)
    foreach ($enabled in @($true, $false)) {
        $settings.ShowLineEndings = $enabled
        Slot-Sample $second $fileText $false
    }
    [void](Invoke-Object $second 'CloseSessionLog')
    Assert-Ending ((Count-Literal ([IO.File]::ReadAllText($slotStringPath)) 'FILE_ENDINGS\r\n') -eq 2 -and
        (Count-Literal ([IO.File]::ReadAllText($slotHexPath)) $fileHex) -eq 2) 'Split STRING and HEX session files record identical payloads with markers on and off.'

    # Charge marker expansion to the bounded history, even when the canonical
    # record is smaller. Keep these large-packet checks off the WPF renderer.
    $itemType = $assembly.GetType('llcom_plus.Pages.DataShowPage+DataShow', $true)
    $rawLarge = ("`n" * 900000) + 'RETAINED_END'
    $largeItem = Invoke-Static $itemType 'CreatePlain' ([object[]]@($rawLarge, $true))
    $itemType.GetField('dataWithLineEndings', $instanceFlags).SetValue($largeItem, (Format-Display $rawLarge 65001 $false $true))
    $itemType.GetField('dataWithoutLineEndings', $instanceFlags).SetValue($largeItem, $rawLarge)
    foreach ($limit in @(1048576, 262144)) {
        $limited = Invoke-Object $largeItem 'LimitHistoryText' ([object[]]@($limit))
        $count = $itemType.GetProperty('RetainedCharacterCount', $instanceFlags).GetValue($limited, $null)
        Assert-Ending ($count -le $limit -and (Invoke-Object $limited 'ToLogText').EndsWith('RETAINED_END')) "Canonical history and both marker projections respect the $limit character bound."
        foreach ($enabled in @($true, $false)) {
            $projection = Invoke-Object $limited 'GetDisplayData' ([object[]]@($enabled))
            Assert-Ending ($projection.Length -le $limit -and $projection.EndsWith('RETAINED_END')) "Bounded history preserves a nonempty tail with markers=$enabled at limit=$limit."
        }
    }

    # Exercise the actual two-way CheckBox binding rather than just changing
    # Settings directly. No popup HWND or application window is opened.
    foreach ($page in @($single, $split)) {
        $checkbox = $page.FindName('ShowLineEndingsCheckBox')
        Pump-Ui
        foreach ($enabled in @($true, $false)) {
            $checkbox.SetCurrentValue([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty, $enabled)
            $checkbox.GetBindingExpression([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).UpdateSource()
            Pump-Ui
            Assert-Ending ($settings.ShowLineEndings -eq $enabled) "Actual $($page.GetType().Name) checkbox updates settings to $enabled."
        }
        $settings.ShowLineEndings = $true
        Pump-Ui
        Assert-Ending ($checkbox.IsChecked -eq $true) "Actual $($page.GetType().Name) checkbox follows configuration changes."
    }
    if ($RenderPreviews) {
        foreach ($theme in @('Light', 'Dark')) {
            $settingsType.GetField('_darkMode', $instanceFlags).SetValue($settings, ($theme -eq 'Dark'))
            [void](Invoke-Static $globalType 'ApplyTheme' ([object[]]@(($theme -eq 'Dark'))))
            foreach ($enabled in @($true, $false)) {
                $settings.ShowLineEndings = $enabled
                Pump-Ui
                Render-Options $single "single-options-$theme-$enabled"
                Render-Options $split "split-options-$theme-$enabled"
            }
        }
    }
    Assert-Ending (-not $split.IsSlotOpen(1) -and -not $split.IsSlotOpen(2) -and -not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No physical COM connection was opened by this test.'
    Assert-Ending ([LineEndingsUiExceptionObserver]::Count -eq 0) 'No offline dispatcher exception occurred.'
    Write-Host "PASS All $passed line-ending display checks ($Platform)."
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
        $testName -notmatch '^llcom-line-endings-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
exit $exitCode
