param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'llcom plus'
$outputDir = Join-Path $projectDir "bin\$Platform\$Configuration"
$exePath = Join-Path $outputDir 'llcom plus.exe'
$failures = New-Object System.Collections.Generic.List[string]

function Test-Condition([bool]$condition, [string]$message) {
    if ($condition) {
        Write-Host "PASS  $message" -ForegroundColor Green
    } else {
        Write-Host "FAIL  $message" -ForegroundColor Red
        $failures.Add($message)
    }
}

function Invoke-Static([Reflection.MethodInfo]$method, [object[]]$arguments) {
    return $method.Invoke($null, $arguments)
}

Test-Condition (Test-Path -LiteralPath $exePath) "Application executable exists ($Configuration|$Platform)"
if (-not (Test-Path -LiteralPath $exePath)) {
    exit 1
}

Add-Type -AssemblyName System.Drawing
$appIcon = [Drawing.Icon]::ExtractAssociatedIcon($exePath)
$appIconBitmap = $appIcon.ToBitmap()
try {
    $minIconX = $appIconBitmap.Width
    $minIconY = $appIconBitmap.Height
    $maxIconX = -1
    $maxIconY = -1
    for ($iconY = 0; $iconY -lt $appIconBitmap.Height; $iconY++) {
        for ($iconX = 0; $iconX -lt $appIconBitmap.Width; $iconX++) {
            if ($appIconBitmap.GetPixel($iconX, $iconY).A -le 8) {
                continue
            }
            $minIconX = [Math]::Min($minIconX, $iconX)
            $minIconY = [Math]::Min($minIconY, $iconY)
            $maxIconX = [Math]::Max($maxIconX, $iconX)
            $maxIconY = [Math]::Max($maxIconY, $iconY)
        }
    }
    $iconContentWidth = $maxIconX - $minIconX + 1
    $iconContentHeight = $maxIconY - $minIconY + 1
    Test-Condition (
        $iconContentWidth -ge 30 -and
        $iconContentHeight -ge 30
    ) 'Application taskbar icon visibly fills the 32-pixel icon frame'
}
finally {
    $appIconBitmap.Dispose()
    $appIcon.Dispose()
}

$taskbarIconPath = Join-Path $outputDir 'llcom-plus-taskbar-v2.ico'
Test-Condition (Test-Path -LiteralPath $taskbarIconPath) 'Dedicated taskbar pin icon exists'
if (Test-Path -LiteralPath $taskbarIconPath) {
    $sourceIconPath = Join-Path $projectDir 'Resources\Assets\AppIcon.ico'
    Test-Condition (
        (Get-FileHash -LiteralPath $taskbarIconPath -Algorithm SHA256).Hash -eq
            (Get-FileHash -LiteralPath $sourceIconPath -Algorithm SHA256).Hash
    ) 'Dedicated taskbar pin icon matches the current application icon'
}

Push-Location $outputDir
try {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'Newtonsoft.Json.dll'))
    $assembly = [Reflection.Assembly]::LoadFrom($exePath)

    $taskbarType = $assembly.GetType('llcom_plus.Tools.TaskbarIntegration', $true)
    $taskbarFlags = [Reflection.BindingFlags]'NonPublic,Static'
    $appUserModelId = $taskbarType.GetField('AppUserModelId', $taskbarFlags).GetRawConstantValue()
    $taskbarIconFileName = $taskbarType.GetField('TaskbarIconFileName', $taskbarFlags).GetRawConstantValue()
    Test-Condition (
        $appUserModelId -eq 'LawrenceLiu.LlcomPlus' -and
        -not $appUserModelId.Contains(' ')
    ) 'Portable build exposes a stable version-independent AppUserModelID'
    Test-Condition (
        $taskbarIconFileName -eq 'llcom-plus-taskbar-v2.ico'
    ) 'Taskbar relaunch identity uses the cache-busting icon file'

    $buildRelaunchCommand = $taskbarType.GetMethod('BuildRelaunchCommand', $taskbarFlags)
    $relaunchCommandArguments = New-Object object[] 1
    $relaunchCommandArguments[0] = [string]$exePath
    $relaunchCommand = $buildRelaunchCommand.Invoke($null, $relaunchCommandArguments)
    Test-Condition (
        $relaunchCommand -eq ('"' + $exePath + '"')
    ) 'Pinned taskbar shortcut relaunches the current executable path'

    $buildRelaunchIcon = $taskbarType.GetMethod('BuildRelaunchIconResource', $taskbarFlags)
    $relaunchIconArguments = New-Object object[] 2
    $relaunchIconArguments[0] = [string]$outputDir
    $relaunchIconArguments[1] = [string]$exePath
    $relaunchIcon = $buildRelaunchIcon.Invoke($null, $relaunchIconArguments)
    Test-Condition (
        $relaunchIcon -eq ($taskbarIconPath + ',0')
    ) 'Pinned taskbar shortcut explicitly uses the current icon'

    $programSource = [IO.File]::ReadAllText((Join-Path $projectDir 'Program.cs'))
    Test-Condition (
        $programSource.IndexOf('TaskbarIntegration.InitializeProcessIdentity', [StringComparison]::Ordinal) -ge 0 -and
        $programSource.IndexOf('TaskbarIntegration.InitializeProcessIdentity', [StringComparison]::Ordinal) -lt
            $programSource.IndexOf('new App()', [StringComparison]::Ordinal)
    ) 'Taskbar process identity is assigned before WPF creates UI'

    $mainWindowSource = [IO.File]::ReadAllText((Join-Path $projectDir 'UI\View\MainWindow.xaml.cs'))
    Test-Condition (
        $mainWindowSource.Contains('TaskbarIntegration.ConfigureWindow(this)')
    ) 'Main window publishes taskbar relaunch and pinning properties'

    $calculatorType = $assembly.GetType('llcom_plus.Tools.DataCalcCalculator', $true)
    $calculate = $calculatorType.GetMethod('Calculate', [Reflection.BindingFlags]'Public,Static')
    [byte[]]$vector = [Text.Encoding]::ASCII.GetBytes('123456789')
    $calculateArguments = New-Object object[] 1
    $calculateArguments[0] = $vector
    $result = Invoke-Static $calculate $calculateArguments
    Test-Condition ($result.Crc16Modbus -eq '0x4B37') 'CRC16/MODBUS standard vector'
    Test-Condition ($result.Crc32 -eq '0xCBF43926') 'CRC32 standard vector'
    Test-Condition ($result.Md5 -eq '25F9E794323B453885F5181F1B624D0B') 'MD5 standard vector'

    $httpType = $assembly.GetType('llcom_plus.HttpTools.HttpRequestService', $true)
    $parse = $httpType.GetMethod(
        'ParseRawHttpResponse',
        [Reflection.BindingFlags]'NonPublic,Static',
        $null,
        [Type[]]@([byte[]], [string]),
        $null)

    $validRaw = "HTTP/1.1 100 Continue`r`n`r`nHTTP/1.1 200 OK`r`nTransfer-Encoding: chunked`r`nContent-Type: text/plain; charset=utf-8`r`n`r`n4`r`nWiki`r`n5`r`npedia`r`n0`r`n`r`n"
    $parseArguments = New-Object object[] 2
    $parseArguments[0] = [Text.Encoding]::ASCII.GetBytes($validRaw)
    $parseArguments[1] = 'GET'
    $parsed = Invoke-Static $parse $parseArguments
    Test-Condition (([int]$parsed.StatusCode -eq 200) -and ($parsed.Body -eq 'Wikipedia')) 'HTTPS parser accepts valid interim/chunked response'

    foreach ($invalid in @(
        'not an HTTP response',
        "HTTP/1.1 200 OK`r`nContent-Length: 10`r`n`r`nabc",
        "HTTP/1.1 200 OK`r`nTransfer-Encoding: chunked`r`n`r`n5`r`nab`r`n0`r`n`r`n"
    )) {
        $rejected = $false
        try {
            $invalidArguments = New-Object object[] 2
            $invalidArguments[0] = [Text.Encoding]::ASCII.GetBytes($invalid)
            $invalidArguments[1] = 'GET'
            [void](Invoke-Static $parse $invalidArguments)
        } catch {
            $rejected = $true
        }
        Test-Condition $rejected 'HTTPS parser rejects malformed or truncated response'
    }

    $settingsType = $assembly.GetType('llcom_plus.Model.Settings', $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $settingsType.GetProperty('tcpClientSslClientCertPassword').SetValue($settings, 'do-not-persist', $null)
    $jsonConvertType = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'Newtonsoft.Json.dll')).GetType('Newtonsoft.Json.JsonConvert', $true)
    $serialize = $jsonConvertType.GetMethod('SerializeObject', [Type[]]@([object]))
    $serializedSettings = $serialize.Invoke($null, [object[]]@($settings))
    Test-Condition (-not $serializedSettings.Contains('do-not-persist')) 'TLS certificate password value is not serialized'
    Test-Condition (-not $serializedSettings.Contains('tcpClientSslClientCertPassword')) 'TLS certificate password field is not serialized'
    Test-Condition ($settings.showSerialByteCounts) 'Serial byte counters remain visible by default'

    $migrationDir = Join-Path ([IO.Path]::GetTempPath()) ('llcom-settings-migration-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($migrationDir)
    try {
        $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
        $profilePathField = $globalType.GetField('ProfilePath', [Reflection.BindingFlags]'Public,Static')
        $profilePathField.SetValue($null, $migrationDir + [IO.Path]::DirectorySeparatorChar)
        $legacySettingsPath = Join-Path $migrationDir 'settings.json'
        [IO.File]::WriteAllText($legacySettingsPath, '{"tcpClientSslClientCertPassword":"legacy-audit-secret"}')

        $deserialize = $jsonConvertType.GetMethod('DeserializeObject', [Type[]]@([string], [Type]))
        $migratedSettings = $deserialize.Invoke($null, [object[]]@([IO.File]::ReadAllText($legacySettingsPath), $settingsType))
        $settingsType.GetMethod('EnsureRuntimeState', [Reflection.BindingFlags]'Public,Instance').Invoke($migratedSettings, $null)
        $settingsType.GetMethod('RemovePersistedTlsPassword', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($migratedSettings, $null)
        $migratedJson = [IO.File]::ReadAllText($legacySettingsPath)
        Test-Condition (-not $migratedJson.Contains('legacy-audit-secret')) 'Legacy TLS certificate password value is scrubbed during migration'
        Test-Condition (-not $migratedJson.Contains('tcpClientSslClientCertPassword')) 'Legacy TLS certificate password field is scrubbed during migration'
    }
    finally {
        if ($migrationDir.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $migrationDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $profilePathField.SetValue($null, $outputDir + [IO.Path]::DirectorySeparatorChar)

    $openSslType = $assembly.GetType('llcom_plus.Tools.OpenSslCli', $true)
    $optionsType = $assembly.GetType('llcom_plus.Tools.OpenSslClientOptions', $true)
    $options = [Activator]::CreateInstance($optionsType)
    $options.Host = 'localhost'
    $options.Port = 443
    $options.ClientCertPassword = 'do-not-expose'
    $buildArguments = $openSslType.GetMethod(
        'BuildSClientArguments',
        [Reflection.BindingFlags]'NonPublic,Static',
        $null,
        [Type[]]@($optionsType, [bool], [string]),
        $null)
    $argumentText = $buildArguments.Invoke($null, [object[]]@($options, $false, ''))
    Test-Condition (-not $argumentText.Contains('do-not-expose')) 'OpenSSL command line does not expose the certificate password'

    $createRoots = $openSslType.GetMethod('CreateWindowsRootCaBundle', [Reflection.BindingFlags]'NonPublic,Static')
    $rootBundlePath = $createRoots.Invoke($null, $null)
    try {
        $rootBundleText = [IO.File]::ReadAllText($rootBundlePath)
        Test-Condition ($rootBundleText.Contains('-----BEGIN CERTIFICATE-----')) 'Windows trusted roots can be exported for OpenSSL'
    }
    finally {
        Remove-Item -LiteralPath $rootBundlePath -Force -ErrorAction SilentlyContinue
    }

    $pinMonitorType = $assembly.GetType('llcom_plus.Tools.SerialPinMonitor', $true)
    $mapPinBits = $pinMonitorType.GetMethod(
        'AddChangedLines',
        [Reflection.BindingFlags]'NonPublic,Static',
        $null,
        [Type[]]@([Collections.Generic.ICollection[string]], [uint32]),
        $null)
    $pinNames = [Activator]::CreateInstance([Collections.Generic.List[string]])
    [void]$mapPinBits.Invoke($null, [object[]]@($pinNames, [uint32]0xF0))
    Test-Condition (($pinNames -join ',') -eq 'CTS,DSR,DCD,RI') 'Serial modem-status bits map to CTS/DSR/DCD/RI'

    Add-Type -AssemblyName PresentationFramework
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    [Windows.Application]::ResourceAssembly = $assembly
    $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
    $globalType.GetField('setting', [Reflection.BindingFlags]'Public,Static').SetValue($null, $settings)
    $appType = $assembly.GetType('llcom_plus.App', $true)
    $app = [Activator]::CreateInstance($appType)
    $window = $null
    try {
        [void]$appType.GetMethod('InitializeComponent').Invoke($app, $null)
        $windowType = $assembly.GetType('llcom_plus.MainWindow', $true)
        $window = [Activator]::CreateInstance($windowType)
        $checkUpdateButton = $window.FindName('CheckUpdateButton')
        $checkUpdateIcon = $window.FindName('CheckUpdateIcon')
        Test-Condition (
            $null -eq $window.FindName('AboutTab')
        ) 'The obsolete About tab is removed from the main navigation'
        Test-Condition (
            $null -ne $checkUpdateButton -and
            $null -ne $checkUpdateIcon -and
            $checkUpdateIcon.Icon.ToString() -eq 'Refresh'
        ) 'The top-right toolbar exposes a refresh-style update button'
        $topRightButtonPanel = $checkUpdateButton.Parent
        Test-Condition (
            $null -ne $topRightButtonPanel -and
            $topRightButtonPanel.Children.IndexOf($checkUpdateButton) -eq
                ($topRightButtonPanel.Children.Count - 1)
        ) 'The update button is the final top-right toolbar action'
        Test-Condition (
            $null -ne $assembly.GetType('llcom_plus.UpdateCheckController', $false) -and
            $null -ne $windowType.GetMethod(
                'CheckUpdateButton_Click',
                [Reflection.BindingFlags]'NonPublic,Instance')
        ) 'The top-right update button keeps the complete update workflow'
        $createDefaultQuickSendRows = $settingsType.GetMethod(
            'CreateDefaultQuickSendRows',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $defaultQuickSendRows = @($createDefaultQuickSendRows.Invoke($settings, $null))
        $isBlankQuickSendRow = $settingsType.GetMethod(
            'IsBlankQuickSendRow',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $allDefaultRowsAppendCrlf = $defaultQuickSendRows.Count -gt 0 -and
            @($defaultQuickSendRows | Where-Object { -not $_.appendCrlf }).Count -eq 0
        $defaultRowsRemainBlank = $defaultQuickSendRows.Count -gt 0 -and
            [bool]$isBlankQuickSendRow.Invoke(
                $settings,
                [object[]]@($defaultQuickSendRows[0]))
        Test-Condition (
            $allDefaultRowsAppendCrlf -and
            $defaultRowsRemainBlank
        ) 'New quick-send rows enable CRLF without treating untouched rows as content'

        $createBlankQuickSendItem = $windowType.GetMethod(
            'CreateBlankQuickSendItem',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $blankQuickSendItem = $createBlankQuickSendItem.Invoke(
            $window,
            [object[]]@(11))
        Test-Condition (
            $blankQuickSendItem.id -eq 11 -and
            $blankQuickSendItem.appendCrlf
        ) 'The add-row action creates a CRLF-enabled quick-send item'

        $wrapQuickSendRow = $windowType.GetMethod(
            'GetWrappedQuickSendRowIndex',
            [Reflection.BindingFlags]'NonPublic,Static')
        $wrapUpFromFirst = [int]$wrapQuickSendRow.Invoke(
            $null,
            [object[]]@(0, -1, 8))
        $wrapDownFromLast = [int]$wrapQuickSendRow.Invoke(
            $null,
            [object[]]@(7, 1, 8))
        $clampQuickSendColumn = $windowType.GetMethod(
            'GetClampedQuickSendNavigationColumn',
            [Reflection.BindingFlags]'NonPublic,Static')
        $leftColumnBoundary = [int]$clampQuickSendColumn.Invoke(
            $null,
            [object[]]@(0, -1))
        $rightColumnBoundary = [int]$clampQuickSendColumn.Invoke(
            $null,
            [object[]]@(4, 1))
        Test-Condition (
            $wrapUpFromFirst -eq 7 -and
            $wrapDownFromLast -eq 0 -and
            $leftColumnBoundary -eq 0 -and
            $rightColumnBoundary -eq 4
        ) 'Quick-send keyboard navigation wraps rows and bounds columns'

        $shouldNotifyBaudRateChange = $windowType.GetMethod(
            'ShouldNotifyBaudRateChange',
            [Reflection.BindingFlags]'NonPublic,Static')
        $notifyForOpenChangedPort = [bool]$shouldNotifyBaudRateChange.Invoke(
            $null,
            [object[]]@($true, 115200, 9600))
        $skipForClosedPort = -not [bool]$shouldNotifyBaudRateChange.Invoke(
            $null,
            [object[]]@($false, 115200, 9600))
        $skipForSameBaudRate = -not [bool]$shouldNotifyBaudRateChange.Invoke(
            $null,
            [object[]]@($true, 115200, 115200))
        Test-Condition (
            $notifyForOpenChangedPort -and
            $skipForClosedPort -and
            $skipForSameBaudRate
        ) 'Baud-rate notifications require an open port and an actual change'

        $shouldUseSerialSplitPage = $windowType.GetMethod(
            'ShouldUseSerialSplitPage',
            [Reflection.BindingFlags]'NonPublic,Static')
        Test-Condition (
            -not [bool]$shouldUseSerialSplitPage.Invoke($null, [object[]]@(1)) -and
            [bool]$shouldUseSerialSplitPage.Invoke($null, [object[]]@(2))
        ) 'A single remaining serial pane returns to the normal log page'

        $dataShowPageType = $assembly.GetType('llcom_plus.Pages.DataShowPage', $true)
        $controlLineHandler = $dataShowPageType.GetMethod(
            'ControlLineCheckBox_Click',
            [Reflection.BindingFlags]'NonPublic,Instance')
        Test-Condition ($null -ne $controlLineHandler) 'Manual RTS/DTR operations have a notification handler'
        $dataShowPage = [Activator]::CreateInstance($dataShowPageType)
        $logOptionsButton = $dataShowPage.FindName('LogOptionsButton')
        $logOptionsPopup = $dataShowPage.FindName('LogOptionsPopup')
        $showSerialByteCountsCheckBox = $dataShowPage.FindName('ShowSerialByteCountsCheckBox')
        $serialByteCountPanel = $window.FindName('SerialByteCountPanel')
        Test-Condition (
            $null -ne $showSerialByteCountsCheckBox -and
            $null -ne $serialByteCountPanel
        ) 'Send options expose the status-bar TX/RX byte counter toggle'
        $logOptionsButton.IsChecked = $true
        $closeLogOptionsOnDeactivate = $dataShowPageType.GetMethod(
            'OwnerWindow_Deactivated',
            [Reflection.BindingFlags]'NonPublic,Instance')
        [void]$closeLogOptionsOnDeactivate.Invoke(
            $dataShowPage,
            [object[]]@($null, [EventArgs]::Empty))
        Test-Condition (
            $logOptionsPopup.StaysOpen -and
            $logOptionsButton.IsChecked -ne $true
        ) 'Send and log options stay open for internal clicks and close when the application deactivates'

        $addSplitPaneButton = $window.FindName('AddSerialSplitPaneButton')
        Test-Condition ($null -ne $addSplitPaneButton) 'The main status bar exposes an add-split-pane button'

        $multiPortPageType = $assembly.GetType('llcom_plus.Pages.MultiPortPage', $true)
        $multiPortConstructor = $multiPortPageType.GetConstructor(
            [Type[]]@([int], [bool], [string], [string]))
        $multiPortPage = $multiPortConstructor.Invoke(
            [object[]]@(2, $false, $null, "preserved single-pane log"))
        $multiPortLoaded = $multiPortPageType.GetMethod(
            'Page_Loaded',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $multiPortUnloaded = $multiPortPageType.GetMethod(
            'Page_Unloaded',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $multiPortSlotsField = $multiPortPageType.GetField(
            'slots',
            [Reflection.BindingFlags]'NonPublic,Instance')
        try {
            [void]$multiPortLoaded.Invoke(
                $multiPortPage,
                [object[]]@($null, $null))
            $slots = $multiPortSlotsField.GetValue($multiPortPage)
            $firstSlot = $slots[0]
            $firstSlotType = $firstSlot.GetType()
            $getSlotLogText = $firstSlotType.GetMethod('GetLogText')
            $closeButtonField = $firstSlotType.GetField(
                'closeButton',
                [Reflection.BindingFlags]'NonPublic,Instance')
            $initialLogPreserved = $getSlotLogText.Invoke(
                $firstSlot,
                $null).Contains('preserved single-pane log')
            $getSlotLogTextSnapshot = $multiPortPageType.GetMethod('GetSlotLogTextSnapshot')
            $publicSlotLogSnapshotPreserved = $getSlotLogTextSnapshot.Invoke(
                $multiPortPage,
                [object[]]@(1)).Contains('preserved single-pane log')

            $addSplitSlot = $multiPortPageType.GetMethod('AddSlot')
            $removeSplitSlot = $multiPortPageType.GetMethod('RemoveSlot')
            $resizeSplitSlots = $multiPortPageType.GetMethod('ResizeSlotCount')
            $addedThirdSlot = [bool]$addSplitSlot.Invoke($multiPortPage, $null)
            $slots = $multiPortSlotsField.GetValue($multiPortPage)
            $thirdSlot = $slots[2]
            $threePaneLayoutPreserved =
                [Windows.Controls.Grid]::GetRow($thirdSlot.Root) -eq 1 -and
                [Windows.Controls.Grid]::GetColumn($thirdSlot.Root) -eq 0 -and
                [Windows.Controls.Grid]::GetColumnSpan($thirdSlot.Root) -eq 2
            $removedMiddleSlot = [bool]$removeSplitSlot.Invoke(
                $multiPortPage,
                [object[]]@(2))
            $slots = $multiPortSlotsField.GetValue($multiPortPage)
            $existingSlotsPreserved =
                [object]::ReferenceEquals($firstSlot, $slots[0]) -and
                [object]::ReferenceEquals($thirdSlot, $slots[1]) -and
                $slots[1].Index -eq 2 -and
                $getSlotLogText.Invoke($firstSlot, $null).Contains('preserved single-pane log')

            [void]$addSplitSlot.Invoke($multiPortPage, $null)
            [void]$addSplitSlot.Invoke($multiPortPage, $null)
            $rejectedFifthSlot = -not [bool]$addSplitSlot.Invoke($multiPortPage, $null)
            $maximumIsFour = $multiPortPage.SlotCount -eq 4
            $slots = $multiPortSlotsField.GetValue($multiPortPage)
            $fourPaneLayoutPreserved =
                [Windows.Controls.Grid]::GetRow($slots[0].Root) -eq 0 -and
                [Windows.Controls.Grid]::GetColumn($slots[0].Root) -eq 0 -and
                [Windows.Controls.Grid]::GetRow($slots[1].Root) -eq 0 -and
                [Windows.Controls.Grid]::GetColumn($slots[1].Root) -eq 1 -and
                [Windows.Controls.Grid]::GetRow($slots[2].Root) -eq 1 -and
                [Windows.Controls.Grid]::GetColumn($slots[2].Root) -eq 0 -and
                [Windows.Controls.Grid]::GetRow($slots[3].Root) -eq 1 -and
                [Windows.Controls.Grid]::GetColumn($slots[3].Root) -eq 1
            $closeButton = $closeButtonField.GetValue($firstSlot)
            $externalByteCountsCheckBox = $multiPortPage.FindName('ExternalShowSerialByteCountsCheckBox')
            $multiPaneCloseVisible = $closeButton.Visibility.ToString() -eq 'Visible'
            [void]$closeButton.ApplyTemplate()
            $closeGlyphPrimary = $closeButton.Template.FindName('CloseGlyphPrimary', $closeButton)
            $closeGlyphSecondary = $closeButton.Template.FindName('CloseGlyphSecondary', $closeButton)
            $compactSolidRedCloseButton =
                [string]$closeButton.Content -eq [string][char]0x00D7 -and
                $closeButton.Width -eq 16 -and
                $closeButton.BorderThickness.Left -eq 0 -and
                $closeButton.Background -is [Windows.Media.SolidColorBrush] -and
                $closeButton.Background.Color.A -eq 255 -and
                $closeButton.Foreground.ToString() -eq '#FFFFFFFF' -and
                $null -ne $closeGlyphPrimary -and
                $null -ne $closeGlyphSecondary -and
                $closeButton.HorizontalAlignment.ToString() -eq 'Right' -and
                $closeButton.VerticalAlignment.ToString() -eq 'Top' -and
                $closeButton.Margin.Top -lt 0 -and
                $closeButton.Margin.Right -lt 0 -and
                [Windows.Controls.Grid]::GetRowSpan($closeButton) -eq 4
            [void]$resizeSplitSlots.Invoke(
                $multiPortPage,
                [object[]]@(1))
            $singlePaneCloseHidden = $closeButton.Visibility.ToString() -eq 'Collapsed'
            $createSingleSerialLogPage = $windowType.GetMethod(
                'CreateSingleSerialLogPage',
                [Reflection.BindingFlags]'NonPublic,Static')
            $restoredSingleLogPage = $createSingleSerialLogPage.Invoke(
                $null,
                [object[]]@($multiPortPage))
            $singlePaneHistoryRestored = $restoredSingleLogPage.GetLogTextSnapshot().Contains(
                'preserved single-pane log')

            Test-Condition (
                $initialLogPreserved -and
                $publicSlotLogSnapshotPreserved -and
                $addedThirdSlot -and
                $removedMiddleSlot -and
                $existingSlotsPreserved -and
                $threePaneLayoutPreserved
            ) 'Adding and removing a selected split pane preserves all unaffected pane instances and logs'
            Test-Condition (
                $maximumIsFour -and
                $rejectedFifthSlot -and
                $fourPaneLayoutPreserved -and
                $multiPaneCloseVisible -and
                $compactSolidRedCloseButton -and
                $null -ne $externalByteCountsCheckBox -and
                $singlePaneCloseHidden -and
                $singlePaneHistoryRestored
            ) 'Split panes preserve the remaining history when returning to one pane'
        }
        finally {
            [void]$multiPortUnloaded.Invoke(
                $multiPortPage,
                [object[]]@($null, $null))
        }

        $serialSplitCountField = $settingsType.GetField(
            '_serialSplitScreenCount',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $applySerialSplitLayout = $windowType.GetMethod(
            'ApplySerialSplitLayout',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $dataShowFrame = $window.FindName('dataShowFrame')
        $mainSplitPortPageField = $windowType.GetField(
            'mainSplitPortPage',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $pendingSingleSerialLogPageField = $windowType.GetField(
            'pendingSingleSerialLogPage',
            [Reflection.BindingFlags]'NonPublic,Instance')
        $serialSplitCountField.SetValue($settings, 2)
        [void]$applySerialSplitLayout.Invoke($window, $null)
        $runtimeSplitPage = $mainSplitPortPageField.GetValue($window)
        $dataShowFrame = $windowType.GetField(
            'dataShowFrame',
            [Reflection.BindingFlags]'NonPublic,Instance').GetValue($window)
        [void]$multiPortLoaded.Invoke(
            $runtimeSplitPage,
            [object[]]@($null, $null))
        $runtimeSlots = $multiPortSlotsField.GetValue($runtimeSplitPage)
        $runtimeSlots[0].SetLogTextSnapshot('real frame transition history')
        [void]$runtimeSplitPage.RemoveSlot(2)
        # Reproduce the queued duplicate notification that used to replace the
        # first preserved page with a second blank page before navigation ended.
        [void]$applySerialSplitLayout.Invoke($window, $null)
        $runtimeSinglePage = $pendingSingleSerialLogPageField.GetValue($window)
        $realFrameTransitionPreservesHistory =
            $null -ne $runtimeSinglePage -and
            $runtimeSinglePage.GetLogTextSnapshot().Contains('real frame transition history')
        Test-Condition (
            $realFrameTransitionPreservesHistory
        ) 'Duplicate layout notifications preserve the pending single-pane history'
        $serialSplitCountField.SetValue($settings, 1)

        $circularSendPageType = $assembly.GetType('llcom_plus.Pages.CircularSendPage', $true)
        $circularSendPage = [Activator]::CreateInstance($circularSendPageType)
        $enableAllCheckBox = $circularSendPage.FindName('EnableAllCheckBox')
        $circularItems = $circularSendPage.Items
        $circularItems[0].IsSelected = $true
        $circularItems[1].IsSelected = $false
        $circularItems[2].IsSelected = $false
        $mixedSelectionShown = $enableAllCheckBox.IsChecked -eq $null
        $toggleCircularSelection = $circularSendPageType.GetMethod(
            'ToggleSelectionFromHeader',
            [Reflection.BindingFlags]'NonPublic,Instance')
        [void]$toggleCircularSelection.Invoke($circularSendPage, $null)
        $allCircularRowsSelected =
            $enableAllCheckBox.IsChecked -eq $true -and
            @($circularItems | Where-Object { -not $_.IsSelected }).Count -eq 0
        [void]$toggleCircularSelection.Invoke($circularSendPage, $null)
        $allCircularRowsCleared =
            $enableAllCheckBox.IsChecked -eq $false -and
            @($circularItems | Where-Object { $_.IsSelected }).Count -eq 0
        Test-Condition (
            $mixedSelectionShown -and
            $allCircularRowsSelected -and
            $allCircularRowsCleared
        ) 'Circular-send header checkbox shows mixed state and toggles all rows'

        $calculatePopupOffset = $windowType.GetMethod(
            'CalculateNotificationPopupOffset',
            [Reflection.BindingFlags]'NonPublic,Static')
        $popupOffset = [double]$calculatePopupOffset.Invoke(
            $null,
            [object[]]@([double]1160, [double]947, [double]430))
        Test-Condition (
            ([Math]::Abs((947 + $popupOffset + 430) - 1160) -lt 0.01)
        ) 'Notification popup right edge aligns with the application'
        $addNotification = $windowType.GetMethod('AddNotification', [Reflection.BindingFlags]'NonPublic,Instance')
        $notificationLevelType = $assembly.GetType('llcom_plus.Tools.AppNotificationLevel', $true)
        $notificationCategoryType = $assembly.GetType('llcom_plus.Tools.AppNotificationCategory', $true)
        $infoNotificationLevel = [Enum]::Parse($notificationLevelType, 'Info')
        $pinNotificationCategory = [Enum]::Parse($notificationCategoryType, 'SerialPin')
        [void]$addNotification.Invoke(
            $window,
            [object[]]@(
                [DateTime]::Now,
                'COM3 pin changed: RI',
                'CTS:1 DSR:0 DCD:0 RI:1',
                $infoNotificationLevel,
                $pinNotificationCategory))
        $badge = $window.FindName('NotificationBadge')
        $badgeText = $window.FindName('NotificationBadgeText')
        $notificationList = $window.FindName('NotificationListBox')
        $notificationPopup = $window.FindName('NotificationPopup')
        $notificationPopupRoot = $window.FindName('NotificationPopupRoot')
        $notificationPopupSurface = $window.FindName('NotificationPopupSurface')
        Test-Condition (
            $badge.Visibility.ToString() -eq 'Visible' -and
            $badgeText.Text -eq '1' -and
            $notificationList.Items.Count -eq 1
        ) 'Notification center shows an unread badge'
        Test-Condition (
            $notificationPopup.StaysOpen -and
            $notificationPopupSurface.CornerRadius.TopLeft -eq 16 -and
            $notificationPopupSurface.Effect -ne $null -and
            $notificationPopupSurface.Margin.Left -ge
                ($notificationPopupSurface.Effect.BlurRadius + [Math]::Abs($notificationPopupSurface.Effect.ShadowDepth))
        ) 'Notification popup has a padded floating shadow without clipped corners'

        $togglePopupState = $windowType.GetMethod(
            'GetNotificationPopupStateAfterButtonClick',
            [Reflection.BindingFlags]'NonPublic,Static')
        $openAfterClick = [bool]$togglePopupState.Invoke($null, [object[]]@($false))
        $closedAfterClick = -not [bool]$togglePopupState.Invoke($null, [object[]]@($true))
        Test-Condition ($openAfterClick -and $closedAfterClick) 'Notification button toggles the popup open and closed'

        $shouldClosePopup = $windowType.GetMethod(
            'ShouldCloseNotificationPopup',
            [Reflection.BindingFlags]'NonPublic,Static')
        $closeForOutsideClick = [bool]$shouldClosePopup.Invoke(
            $null,
            [object[]]@($true, $false, $false, $false))
        $keepForButtonClick = -not [bool]$shouldClosePopup.Invoke(
            $null,
            [object[]]@($true, $true, $false, $false))
        $keepForInsideClick = -not [bool]$shouldClosePopup.Invoke(
            $null,
            [object[]]@($true, $false, $true, $false))
        $keepForFilterClick = -not [bool]$shouldClosePopup.Invoke(
            $null,
            [object[]]@($true, $false, $false, $true))
        Test-Condition (
            $closeForOutsideClick -and
            $keepForButtonClick -and
            $keepForInsideClick -and
            $keepForFilterClick
        ) 'Only clicks outside the notification popup close it'

        $popupOpened = $windowType.GetMethod('NotificationPopup_Opened', [Reflection.BindingFlags]'NonPublic,Instance')
        [void]$popupOpened.Invoke($window, [object[]]@($null, [EventArgs]::Empty))
        Test-Condition (
            $badge.Visibility.ToString() -eq 'Collapsed' -and
            $notificationList.Items.Count -eq 1
        ) 'Opening the notification center marks messages as read'

        $clearNotifications = $windowType.GetMethod('NotificationClearButton_Click', [Reflection.BindingFlags]'NonPublic,Instance')
        [void]$clearNotifications.Invoke($window, [object[]]@($null, $null))
        Test-Condition ($notificationList.Items.Count -eq 0) 'Notification center clears message history'

        $successNotificationLevel = [Enum]::Parse($notificationLevelType, 'Success')
        $warningNotificationLevel = [Enum]::Parse($notificationLevelType, 'Warning')
        $errorNotificationLevel = [Enum]::Parse($notificationLevelType, 'Error')
        $connectionNotificationCategory = [Enum]::Parse($notificationCategoryType, 'Connection')
        $taskNotificationCategory = [Enum]::Parse($notificationCategoryType, 'Task')
        $updateNotificationCategory = [Enum]::Parse($notificationCategoryType, 'Update')
        $testNotifications = @(
            [pscustomobject]@{ Title = 'Socket connected'; Message = 'ready'; Level = $successNotificationLevel; Category = $connectionNotificationCategory },
            [pscustomobject]@{ Title = 'COM pin changed'; Message = 'RI:1'; Level = $infoNotificationLevel; Category = $pinNotificationCategory },
            [pscustomobject]@{ Title = 'File transfer failed'; Message = 'timeout'; Level = $errorNotificationLevel; Category = $taskNotificationCategory },
            [pscustomobject]@{ Title = 'Version available'; Message = '1.2.4'; Level = $warningNotificationLevel; Category = $updateNotificationCategory }
        )
        foreach ($notification in $testNotifications) {
            [void]$addNotification.Invoke(
                $window,
                [object[]]@(
                    [DateTime]::Now,
                    $notification.Title,
                    $notification.Message,
                    $notification.Level,
                    $notification.Category))
        }

        $notificationFilter = $window.FindName('NotificationFilterComboBox')
        $filterCounts = @()
        for ($filterIndex = 0; $filterIndex -lt 5; $filterIndex++) {
            $notificationFilter.SelectedIndex = $filterIndex
            $filterCounts += $notificationList.Items.Count
        }
        Test-Condition (
            ($filterCounts -join ',') -eq '4,1,1,1,1'
        ) 'Notification center filters messages by info, success, warning, and error colors'
        [void]$clearNotifications.Invoke($window, [object[]]@($null, $null))
    }
    finally {
        if ($window -ne $null) {
            try { $window.Close() } catch { }
        }
        try { $app.Shutdown() } catch { }
    }
}
finally {
    Pop-Location
}

$opensslDir = Join-Path $outputDir 'OpenSSL'
$opensslPath = Join-Path $opensslDir 'openssl.exe'
Test-Condition (Test-Path -LiteralPath $opensslPath) 'Bundled OpenSSL executable exists'
if (Test-Path -LiteralPath $opensslPath) {
    $version = & $opensslPath version 2>&1 | Select-Object -First 1
    Test-Condition ($version -match '^OpenSSL 3\.5\.7\b') 'Bundled OpenSSL is 3.5.7 LTS'
    $signature = Get-AuthenticodeSignature -LiteralPath $opensslPath
    Test-Condition ($signature.Status -eq 'Valid') 'Bundled OpenSSL Authenticode signature is valid'
    Test-Condition (Test-Path -LiteralPath (Join-Path $opensslDir 'openssl.cnf')) 'Bundled OpenSSL configuration exists'
    Test-Condition (Test-Path -LiteralPath (Join-Path $opensslDir 'ossl-modules\legacy.dll')) 'Matching OpenSSL provider module exists'
}

[xml]$zh = [IO.File]::ReadAllText((Join-Path $projectDir 'Resources\Languages\zh-CN.xaml'), [Text.Encoding]::UTF8)
[xml]$en = [IO.File]::ReadAllText((Join-Path $projectDir 'Resources\Languages\en-US.xaml'), [Text.Encoding]::UTF8)
$ns = New-Object Xml.XmlNamespaceManager($zh.NameTable)
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$zhKeys = @($zh.SelectNodes('//*[@x:Key]', $ns) | ForEach-Object { $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml') } | Sort-Object -Unique)
$enNs = New-Object Xml.XmlNamespaceManager($en.NameTable)
$enNs.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$enKeys = @($en.SelectNodes('//*[@x:Key]', $enNs) | ForEach-Object { $_.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml') } | Sort-Object -Unique)
Test-Condition (($zhKeys -join "`n") -eq ($enKeys -join "`n")) 'Chinese and English resource keys match'
$settingWindowXaml = [IO.File]::ReadAllText(
    (Join-Path $projectDir 'UI\View\SettingWindow.xaml'),
    [Text.Encoding]::UTF8)
Test-Condition (
    -not $settingWindowXaml.Contains('serialSplitScreenCount')
) 'Settings no longer exposes a split-pane count selector'

$largestAppxIcon = Get-ChildItem -LiteralPath (Join-Path $root 'WapProj\Images') -Filter '*.png' |
    Sort-Object Length -Descending |
    Select-Object -First 1
Test-Condition (
    $null -ne $largestAppxIcon -and $largestAppxIcon.Length -le 204800
) 'Generated MSIX icon assets stay within the manifest file-size limit'

if ($Configuration -eq 'Release') {
    $versionProps = [xml](Get-Content -LiteralPath (Join-Path $root 'Version.props'))
    $version = $versionProps.Project.PropertyGroup.AppVersion
    $zipPath = Join-Path $root "artifacts\release\llcom plus_${version}_${Platform}.zip"
    Test-Condition (Test-Path -LiteralPath $zipPath) 'Release ZIP exists'
    if (Test-Path -LiteralPath $zipPath) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            Test-Condition ($null -ne $zip.GetEntry('llcom plus.exe')) 'Release ZIP contains the application'
            Test-Condition ($null -ne $zip.GetEntry('llcom-plus-taskbar-v2.ico')) 'Release ZIP contains the dedicated taskbar icon'
            Test-Condition ($null -ne $zip.GetEntry('OpenSSL/openssl.exe')) 'Release ZIP contains OpenSSL'
            Test-Condition ($null -eq $zip.GetEntry('settings.json')) 'Release ZIP excludes settings.json'
            Test-Condition ($null -eq $zip.GetEntry('circular_send.json')) 'Release ZIP excludes circular_send.json'
        }
        finally {
            $zip.Dispose()
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "`n$($failures.Count) regression check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host "`nAll release regression checks passed." -ForegroundColor Green
