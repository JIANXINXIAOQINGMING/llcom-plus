param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$VerifyInteractions,
    [switch]$VerifyHoverStates
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root "llcom plus\bin\x64\$Configuration"
$previewDir = Join-Path $root 'artifacts\serial-ui'
[void](New-Item -ItemType Directory -Force -Path $previewDir)
$profileDir = Join-Path $previewDir ('test-profile-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $profileDir)
Add-Type -AssemblyName PresentationFramework
Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
$globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
$settingsType = $assembly.GetType('llcom_plus.Model.Settings', $true)
$globalType.GetField('ProfilePath').SetValue($null, $profileDir + '\')
$settings = [Activator]::CreateInstance($settingsType, $true)
$globalType.GetField('setting').SetValue($null, $settings)
$flags = [Reflection.BindingFlags]'NonPublic,Instance'
$settingsType.GetField('_activeUartProfileName', $flags).SetValue($settings, 'COM7')
$settingsType.GetField('_dtrWakeBeforeSend', $flags).SetValue($settings, $true)
[Windows.Application]::ResourceAssembly = $assembly
$app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
$app.InitializeComponent()
# Pumping deferred focus work must not launch the app's normal startup window.
# .NET Framework's public setter rejects null even though the default is null.
[Windows.Application].GetField('_startupUri', $flags).SetValue($app, $null)
$app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$window = [Activator]::CreateInstance($assembly.GetType('llcom_plus.MainWindow', $true))
$flow = [Activator]::CreateInstance($assembly.GetType('llcom_plus.FlowControlWindow', $true))
$flow.DataContext = $settings
$quickContent = $window.FindName('QuickSendTab').Content
$window.FindName('QuickSendTab').Content = $null
$quickContent.Resources.MergedDictionaries.Add($window.Resources)
$list = $window.FindName('toSendList')
$windowType = $window.GetType()
$rows = $windowType.GetField('toSendListItems', $flags).GetValue($window)
$list.ItemsSource = $rows
$createRow = $windowType.GetMethod('CreateBlankQuickSendItem', $flags)
foreach ($command in @('AT', 'AT+CSQ', 'AT+GMR')) {
    $row = $createRow.Invoke($window, [object[]]@($rows.Count + 1))
    $row.text = $command
    $rows.Add($row)
}
$selector = $window.FindName('QuickListSelectComboBox')
$windowType.GetField('quickListSelectorRefreshing', $flags).SetValue($window, $true)
$selector.Tag = $true
$editor = $window.FindName('QuickSendItemSettingsEditor')
$setEditorItem = $editor.GetType().GetMethod('SetItem', $flags)
$rows[1].disableSuggestion = $true
$bar = $window.FindName('QuickSendCommandBar')
$barParent = $bar.Parent
$bar.Resources.MergedDictionaries.Add($quickContent.Resources)
$flowContent = $flow.Content
$flow.Content = $null
$flowContent.DataContext = $settings

function Render-Element($element, [int]$width, [string]$name, [int]$fixedHeight = 0, [switch]$KeepParent) {
    $surface = New-Object Windows.Controls.Border
    $surface.Padding = New-Object Windows.Thickness(10)
    $surface.Background = $app.TryFindResource('AppWindowBackgroundBrush')
    $surface.SetValue([Windows.Documents.TextElement]::FontFamilyProperty, (New-Object Windows.Media.FontFamily('Microsoft YaHei UI')))
    if ($KeepParent) { $surface = $element } else { $surface.Child = $element }
    $measureHeight = if ($fixedHeight -gt 0) { $fixedHeight } else { [double]::PositiveInfinity }
    $surface.Measure((New-Object Windows.Size($width, $measureHeight)))
    $height = if ($fixedHeight -gt 0) { $fixedHeight } else { [int][Math]::Ceiling($surface.DesiredSize.Height) }
    $surface.Arrange((New-Object Windows.Rect(0, 0, $width, $height)))
    $surface.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($surface)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $path = Join-Path $previewDir ($name + '.png')
    $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    if (-not $KeepParent) { $surface.Child = $null }
    Write-Host $path
}

function Test-InlineButtonHover([string]$language, [string]$theme) {
    # Set a visual-state property only on isolated, offscreen controls. This
    # exercises the real template triggers without moving the user's mouse.
    $hoverKey = [Windows.UIElement].GetField('IsMouseOverPropertyKey', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
    $sharedStyle = $app.TryFindResource('QuickSendInlineButtonStyle')
    foreach ($name in @('CloseButton')) {
        $button = $editor.FindName($name)
        [void]$button.ApplyTemplate()
        $chrome = $button.Template.FindName('Chrome', $button)
        if (-not [object]::ReferenceEquals($button.Style, $sharedStyle) -or
            -not [object]::ReferenceEquals($window.FindName('addSendListButton').Style, $sharedStyle)) {
            throw "FAIL $name does not share the Add command style"
        }
        if ($chrome.Background.Color.A -ne 0 -or $chrome.BorderBrush.Color.A -ne 0) {
            throw "FAIL $name does not start transparent"
        }
        $originalSize = $button.RenderSize
        try {
            $button.SetValue($hoverKey, $true)
            $editor.UpdateLayout()
            if ($chrome.Background.ToString() -ne $app.TryFindResource('AppGlassControlHoverBackground').ToString() -or
                $chrome.BorderBrush.ToString() -ne $app.TryFindResource('AppGlassBorderBrush').ToString() -or
                $chrome.BorderThickness.Left -le 0 -or $chrome.CornerRadius.TopLeft -le 0) {
                throw "FAIL $name hover background/rounded outline is missing ($language/$theme)"
            }
            if ($button.RenderSize -ne $originalSize) { throw "FAIL $name changes size on hover" }
            Render-Element $editor 350 "command-settings-hover-$name-$language-$theme" -KeepParent
        }
        finally {
            $button.ClearValue($hoverKey)
            $editor.UpdateLayout()
        }
        if ($chrome.Background.Color.A -ne 0 -or $chrome.BorderBrush.Color.A -ne 0) {
            throw "FAIL $name does not restore its idle appearance"
        }
        Write-Host "PASS Hover/leave $name ($language/$theme)"
    }
}

# No Window.Show / application startup: previews never enumerate or open COMs.
foreach ($language in @('zh-CN', 'en-US')) {
    $globalType.GetMethod('LoadLanguageFile').Invoke($null, @($language))
    $selector.ItemsSource = if ($language -eq 'zh-CN') {
        # ASCII script stays portable to Windows PowerShell's default encoding.
        [string[]]@(
            ('1. ' + (-join [char[]]@(0x57FA, 0x672C, 0x6307, 0x4EE4))),
            ('2. ' + (-join [char[]]@(0x7F51, 0x7EDC, 0x8C03, 0x8BD5))))
    } else {
        [string[]]@('1. Basic commands', '2. Network diagnostics')
    }
    $selector.SelectedIndex = 0
    $window.FindName('QuickListNameTextBox').Text = $selector.Items[0].Substring(3)
    foreach ($row in $rows) { $row.commit = $app.TryFindResource('QuickSendButton') }
    foreach ($dark in @($false, $true)) {
        $globalType.GetMethod('ApplyTheme').Invoke($null, @($dark))
        $theme = if ($dark) { 'dark' } else { 'light' }
        $barParent.Children.Remove($bar)
        Render-Element $bar 580 "toolbar-$language-$theme"
        [void]$barParent.Children.Add($bar)
        Render-Element $quickContent 650 "quicksend-$language-$theme" 340
        [void]$setEditorItem.Invoke($editor, [object[]]@($rows[1]))
        Render-Element $editor 350 "command-settings-$language-$theme" -KeepParent
        if ($VerifyHoverStates) { Test-InlineButtonHover $language $theme }
        [void]$setEditorItem.Invoke($editor, [object[]]@($null))
        [void]$selector.ApplyTemplate()
        $popup = $selector.Template.FindName('PART_Popup', $selector)
        Render-Element $popup.Child 280 "pages-$language-$theme" -KeepParent
        Render-Element $flowContent 440 "wake-$language-$theme"
    }
}
Write-Host 'Rendered both languages and themes using isolated test settings.'

if ($VerifyInteractions) {
    function Assert-Ui([bool]$condition, [string]$message) {
        if (-not $condition) { throw "FAIL $message" }
        Write-Host "PASS $message"
    }
    function Click-UiButton($button) {
        $event = New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent, $button)
        $button.RaiseEvent($event)
    }
    function Layout-QuickSend {
        $quickContent.Measure((New-Object Windows.Size(650, 340)))
        $quickContent.Arrange((New-Object Windows.Rect(0, 0, 650, 340)))
        $quickContent.UpdateLayout()
    }

    # Only test-model commands are changed. No window is shown, and no send
    # handler or hardware API is called by these interaction checks.
    $windowType.GetField('quickListSelectorRefreshing', $flags).SetValue($window, $false)
    $windowType.GetField('canSaveSendList', $flags).SetValue($window, $true)
    [void]$windowType.GetMethod('LoadQuickSendList', $flags).Invoke($window, $null)
    $initialPageCount = $settings.GetQuickSendListCount()
    $initialPageIndex = $settings.quickSendSelect
    $rows[0].text = 'UI regression sentinel - never sent'
    $window.SaveSendList($null, [EventArgs]::Empty)
    [void]$selector.ApplyTemplate()
    $newPageButton = $selector.Template.FindName('AddQuickSendPageButton', $selector)
    $deletePageButton = $selector.Template.FindName('DeleteQuickSendPageButton', $selector)
    Assert-Ui ($selector.Items.Count -eq $initialPageCount -and
        $null -ne $newPageButton -and $null -ne $deletePageButton -and
        -not $selector.Items.Contains($newPageButton) -and
        -not $selector.Items.Contains($deletePageButton)) 'Page commands are separate from selectable pages'
    Assert-Ui ($deletePageButton.IsEnabled -eq ($initialPageCount -gt 1)) 'Delete-page availability matches the number of pages'

    Click-UiButton $newPageButton
    $addedPageIndex = $settings.quickSendSelect
    Assert-Ui ($settings.GetQuickSendListCount() -eq $initialPageCount + 1 -and
        $selector.Items.Count -eq $initialPageCount + 1 -and
        $selector.SelectedIndex -eq $addedPageIndex -and
        $deletePageButton.IsEnabled) 'Dropdown New page creates and selects exactly one page'
    $nameBox = $window.FindName('QuickListNameTextBox')
    Assert-Ui ($nameBox.SelectionLength -eq $nameBox.Text.Length) 'New page name is selected for immediate renaming'

    $selector.SelectedIndex = $initialPageIndex
    Assert-Ui ($rows[0].text -eq 'UI regression sentinel - never sent') 'Page switching preserves edited commands'
    $selectPage = $windowType.GetMethod('SelectQuickSendPage', $flags)
    [void]$selectPage.Invoke($window, [object[]]@(-1))
    [void]$selectPage.Invoke($window, [object[]]@($settings.GetQuickSendListCount()))
    Assert-Ui ($settings.quickSendSelect -eq $initialPageIndex) 'Out-of-range selections cannot switch or create pages'

    $selector.SelectedIndex = $addedPageIndex
    Click-UiButton $deletePageButton # The newly created test page is empty.
    Assert-Ui ($settings.GetQuickSendListCount() -eq $initialPageCount -and
        $selector.Items.Count -eq $initialPageCount -and
        $deletePageButton.IsEnabled -eq ($initialPageCount -gt 1)) 'Deleting an empty test page refreshes selector and last-page protection'
    $selector.SelectedIndex = $initialPageIndex
    $initialRowCount = $rows.Count
    Layout-QuickSend
    Click-UiButton $window.FindName('addSendListButton')
    Layout-QuickSend
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Assert-Ui ($rows.Count -eq $initialRowCount + 1 -and $rows[$rows.Count - 1].appendCrlf) 'Add command appends one CRLF-enabled row'
    $getCell = $windowType.GetMethod('GetQuickSendNavigationElement', $flags)
    $newTextBox = $getCell.Invoke($window, [object[]]@(($rows.Count - 1), 0))
    Assert-Ui ($null -ne $newTextBox -and -not $newTextBox.IsReadOnly -and
        $windowType.GetField('quickSendExplicitEditMode', $flags).GetValue($window)) 'Added command is scrolled into view and enters editing mode'
    $rowContainer = $list.ItemContainerGenerator.ContainerFromIndex($rows.Count - 1)
    $settingsButton = $rowContainer.Template.FindName('QuickSendRowSettingsButton', $rowContainer)
    $columnMethod = $windowType.GetMethod('GetQuickSendNavigationColumnFromSource', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-Ui ($settingsButton.Focusable -and [int]$columnMethod.Invoke($null, [object[]]@($settingsButton)) -eq 2 -and
        $null -eq $rowContainer.Template.FindName('QuickSendRowHexCheckBox', $rowContainer)) 'Each row has one accessible settings entry instead of separate option columns'
    [void]$setEditorItem.Invoke($editor, [object[]]@($rows[$rows.Count - 1]))
    $windowType.GetField('quickActionsItem', $flags).SetValue($window, $rows[$rows.Count - 1])
    $radialAnchor = New-Object Windows.Controls.Button
    $radialAnchor.Tag = $rows[$rows.Count - 1]
    $windowType.GetField('quickActionsAnchor', $flags).SetValue($window, $radialAnchor)
    $window.FindName('QuickSendTab').IsSelected = $true
    $windowType.GetField('quickActionsPage', $flags).SetValue($window, $settings.quickSendSelect)
    $windowType.GetField('quickActionsGeneration', $flags).SetValue($window, $windowType.GetField('quickReorderGeneration', $flags).GetValue($window))
    Click-UiButton $window.FindName('QuickCommandActionsMenu').FindName('DeleteButton')
    Assert-Ui ($rows.Count -eq $initialRowCount -and
        $rows[0].text -eq 'UI regression sentinel - never sent') 'Delete in the radial menu affects only the selected test command'

    $first = $rows[0]
    $second = $rows[1]
    $secondHex = $second.hex
    $first.recvScriptPath = 'missing-ui-test-script'
    $first.recvScriptPara = 'original parameters'
    [void]$setEditorItem.Invoke($editor, [object[]]@($first))
    Assert-Ui ($first.recvScriptPath -eq 'missing-ui-test-script' -and $first.recvScriptPara -eq 'original parameters' -and
        $editor.FindName('ScriptComboBox').SelectedItem.Name -eq 'missing-ui-test-script') 'Opening settings preserves an unavailable script and its parameters'
    $hexBox = $editor.FindName('HexCheckBox')
    $hexBox.IsChecked = $true
    $hexBox.GetBindingExpression([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).UpdateSource()
    Assert-Ui ($first.hex -and $second.hex -eq $secondHex -and $first.HasCustomOptions) 'HEX changes only the bound command and updates its custom-options indicator'
    $crlfBox = $editor.FindName('CrlfCheckBox')
    $crlfBox.IsChecked = $false
    $crlfBox.GetBindingExpression([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).UpdateSource()
    Assert-Ui (-not $first.appendCrlf -and $second.appendCrlf) 'CRLF settings remain independent between commands'
    $excludeBox = $editor.FindName('ExcludeCheckBox')
    $excludeBox.IsChecked = $true
    $excludeBox.GetBindingExpression([Windows.Controls.Primitives.ToggleButton]::IsCheckedProperty).UpdateSource()
    Assert-Ui ($first.disableSuggestion -and -not $second.disableSuggestion) 'Suggestion exclusion applies only to this command'
    $editor.FindName('ScriptComboBox').SelectedIndex = 0
    Assert-Ui ($first.recvScriptPath -eq '' -and $first.recvScriptPara -eq 'original parameters') 'Choosing the global script explicitly clears the override without clearing parameters'
    [void]$setEditorItem.Invoke($editor, [object[]]@($second))
    $parameters = $editor.FindName('ScriptParameterTextBox')
    $parameters.Text = 'second command parameters'
    $parameters.GetBindingExpression([Windows.Controls.TextBox]::TextProperty).UpdateSource()
    Assert-Ui ($second.recvScriptPara -eq 'second command parameters' -and $first.recvScriptPara -eq 'original parameters') 'Switching the settings target cannot leak parameter edits to the previous command'
    [void]$setEditorItem.Invoke($editor, [object[]]@($null))
    $jsonType = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'Newtonsoft.Json.dll')).GetType('Newtonsoft.Json.JsonConvert', $true)
    $json = $jsonType.GetMethod('SerializeObject', [Type[]]@([object])).Invoke($null, [object[]]@($first))
    Assert-Ui (-not $json.Contains('HasCustomOptions')) 'UI-only indicators do not change the saved data format'

    $first.commit = 'A very long custom send button label'
    Layout-QuickSend
    $positions = @()
    foreach ($index in @(0, 1, 2)) {
        $button = $getCell.Invoke($window, [object[]]@($index, 2))
        $point = $button.TransformToAncestor($quickContent).Transform((New-Object Windows.Point(0, 0)))
        $positions += $point.X
    }
    Assert-Ui ($positions[0] -eq $positions[1] -and $positions[1] -eq $positions[2]) 'Long custom send labels cannot shift the row action columns'
    $clampColumn = $windowType.GetMethod('GetClampedQuickSendNavigationColumn', [Reflection.BindingFlags]'NonPublic,Static')
    Assert-Ui ([int]$clampColumn.Invoke($null, [object[]]@(2, 1)) -eq 2) 'Keyboard navigation ends at the settings button'
    $source = [IO.File]::ReadAllText((Join-Path $root 'llcom plus\UI\View\MainWindow.xaml.cs'))
    Assert-Ui ($source.Contains('if (toSendListItems.Any(HasQuickSendContent))')) 'Deletion confirmation also protects a page with one meaningful command'
    Write-Host 'All quick-send UI checks passed; only isolated test configuration was used.'
}
