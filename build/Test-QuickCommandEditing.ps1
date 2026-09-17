[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86')][string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw 'Use matching-bitness Windows PowerShell 5.1 with -STA. This test does not build or open ports.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot "llcom plus\bin\$Platform\$Configuration"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-quick-edit-' + [Guid]::NewGuid().ToString('N')
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
    $flags = [Reflection.BindingFlags]'NonPublic,Instance'
    $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
    $globalType.GetField('ProfilePath').SetValue($null, $testRoot + '\')
    $settings = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Model.Settings', $true), $true)
    $globalType.GetField('setting').SetValue($null, $settings)
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri', $flags).SetValue($app, $null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    # Construct only: never call Show, start the normal application, or open a COM.
    $window = [Activator]::CreateInstance($assembly.GetType('llcom_plus.MainWindow', $true))
    $window.FindName('QuickSendTab').IsSelected = $true
    $type = $window.GetType()
    $rows = $type.GetField('toSendListItems', $flags).GetValue($window)
    $window.FindName('toSendList').ItemsSource = $rows
    $editor = $window.FindName('QuickSendItemSettingsEditor')
    $setEditorItem = $editor.GetType().GetMethod('SetItem', $flags)
    $type.GetField('canSaveSendList', $flags).SetValue($window, $true)
    [void]$type.GetMethod('LoadQuickSendList', $flags).Invoke($window, $null)

    function Invoke-Edit([string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        try { [void]$type.GetMethod($name, $flags).Invoke($window, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Assert-Edit([bool]$condition, [string]$message) {
        if (-not $condition) { throw "FAIL $message" }
        Write-Host "PASS $message"
    }
    function Set-OnlyRow([string]$text) {
        $type.GetField('canSaveSendList', $flags).SetValue($window, $false)
        try {
            $rows.Clear()
            $item = $type.GetMethod('CreateBlankQuickSendItem', $flags).Invoke($window, [object[]]@(1))
            $item.text = $text
            $rows.Add($item)
        }
        finally { $type.GetField('canSaveSendList', $flags).SetValue($window, $true) }
        Invoke-Edit 'ClearQuickSendUndo'
        $window.SaveSendList($null, [EventArgs]::Empty)
    }
    Set-OnlyRow '41 54'
    $original = $rows[0]
    $original.hex = $true
    $original.appendCrlf = $false
    $original.commit = 'Custom send'
    $original.recvScriptPath = 'isolated-test-converter'
    $original.recvScriptPara = 'custom parameters'
    $original.disableSuggestion = $true
    if ($null -ne $original.GetType().GetProperty('responseMode')) {
        $original.responseMode = 2
        $original.expectedResponse = 'OK'
        $original.responseTimeoutMs = 1234
        $original.responseRetries = 0
    }
    [void]$setEditorItem.Invoke($editor, [object[]]@($original))
    Invoke-Edit 'DuplicateQuickCommand' ([object[]]@($original))
    $copy = $rows[1]
    Assert-Edit ($rows.Count -eq 2 -and -not [object]::ReferenceEquals($copy, $original)) 'Duplicate creates a distinct row immediately after its source.'
    foreach ($property in @('text','hex','appendCrlf','commit','recvScriptPath','recvScriptPara','disableSuggestion','responseMode','expectedResponse','responseTimeoutMs','responseRetries')) {
        if ($null -ne $original.GetType().GetProperty($property)) {
            if ($original.$property -ne $copy.$property) { throw "FAIL Duplicate lost $property" }
        }
    }
    Write-Host 'PASS Duplicate retains all command and response settings.'

    $actions = $window.FindName('QuickCommandActionsMenu')
    Assert-Edit ($null -eq $editor.FindName('DuplicateCommandButton') -and $null -eq $editor.FindName('DeleteCommandButton') -and
        $null -ne $actions.FindName('CopyButton') -and $null -ne $actions.FindName('DeleteButton') -and
        $null -eq $editor.GetType().GetEvent('MoveUpRequested') -and $null -eq $editor.GetType().GetEvent('MoveDownRequested')) `
        'Copy/delete live in the handle radial menu, not in the settings panel.'
    $reorderHint = $editor.FindName('ReorderHint')
    Assert-Edit ($null -ne $reorderHint -and -not [string]::IsNullOrWhiteSpace($reorderHint.Text) -and
        $null -eq $type.GetMethod('TextBlock_MouseRightButtonDown', $flags) -and
        $null -ne $type.GetMethod('QuickSendDragHandle_MouseDown', $flags)) `
        'Command settings show a concise drag-handle hint instead of the old numbered reorder action.'

    function Set-RadialTarget($item) {
        $anchor = New-Object Windows.Controls.Button
        $anchor.Tag = $item
        $type.GetField('quickActionsAnchor', $flags).SetValue($window, $anchor)
        $type.GetField('quickActionsItem', $flags).SetValue($window, $item)
        $type.GetField('quickActionsPage', $flags).SetValue($window, $settings.quickSendSelect)
        $type.GetField('quickActionsGeneration', $flags).SetValue($window, $type.GetField('quickReorderGeneration', $flags).GetValue($window))
    }
    Set-RadialTarget $original
    [void]$setEditorItem.Invoke($editor, [object[]]@($null))
    $actions.FindName('CopyButton').RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent)))
    Assert-Edit ($rows.Count -eq 3 -and $rows[1].text -eq $original.text -and
        -not [object]::ReferenceEquals($rows[1],$original)) 'The actual radial Copy button duplicates its captured command, not the editor selection.'
    $rows.RemoveAt(1) # Remove this fixture copy before exercising deletion/undo.
    Invoke-Edit 'QuickCommandActions_Copy' ([object[]]@($null, [EventArgs]::Empty))
    Assert-Edit ($rows.Count -eq 2) 'A closed radial menu cannot replay its previous action.'
    Set-RadialTarget $original
    $type.GetField('quickActionsPage', $flags).SetValue($window, ($settings.quickSendSelect + 1))
    Invoke-Edit 'QuickCommandActions_Delete' ([object[]]@($null, [EventArgs]::Empty))
    Assert-Edit ($rows.Count -eq 2) 'A stale page cannot delete a command through the radial menu.'
    Set-RadialTarget $original
    Invoke-Edit 'InvalidateQuickReorder'
    Invoke-Edit 'QuickCommandActions_Delete' ([object[]]@($null, [EventArgs]::Empty))
    Assert-Edit ($rows.Count -eq 2) 'Unloading/reloading the list cancels a pending radial action.'
    Set-RadialTarget $original
    $type.GetField('quickActionsAnchor', $flags).GetValue($window).Tag = $copy
    Invoke-Edit 'QuickCommandActions_Delete' ([object[]]@($null, [EventArgs]::Empty))
    Assert-Edit ($rows.Count -eq 2) 'A recycled row container cannot apply its old radial command.'
    Set-RadialTarget $copy
    $actions.FindName('DeleteButton').RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent)))
    Assert-Edit ($rows.Count -eq 1 -and $window.FindName('UndoQuickCommandButton').IsEnabled) 'Deletion enables one-step undo without deleting another row.'
    Invoke-Edit 'UndoQuickCommand_Click' ([object[]]@($null, $null))
    Assert-Edit ($rows.Count -eq 2 -and $rows[1].text -eq '41 54' -and $rows[1].hex -and -not $window.FindName('UndoQuickCommandButton').IsEnabled) 'Undo restores the deleted command once with its settings.'

    Set-OnlyRow 'Last command'
    $placeholder = $rows[0]
    Invoke-Edit 'RemoveQuickSendItem' ([object[]]@($placeholder))
    Invoke-Edit 'UndoQuickCommand_Click' ([object[]]@($null, $null))
    Assert-Edit ($rows.Count -eq 1 -and $rows[0].text -eq 'Last command') 'Undo replaces an untouched last-row placeholder.'

    Set-OnlyRow 'Last command'
    $placeholder = $rows[0]
    Invoke-Edit 'RemoveQuickSendItem' ([object[]]@($placeholder))
    $placeholder.text = 'New command after deletion'
    Invoke-Edit 'UndoQuickCommand_Click' ([object[]]@($null, $null))
    Assert-Edit ($rows.Count -eq 2 -and $rows.Contains($placeholder) -and $placeholder.text -eq 'New command after deletion') 'Undo preserves newly entered placeholder text.'

    Set-OnlyRow 'Last command'
    $placeholder = $rows[0]
    Invoke-Edit 'RemoveQuickSendItem' ([object[]]@($placeholder))
    $placeholder.hex = $true
    $placeholder.appendCrlf = $false
    $placeholder.commit = 'Edited blank command'
    Invoke-Edit 'UndoQuickCommand_Click' ([object[]]@($null, $null))
    Assert-Edit ($rows.Count -eq 2 -and $rows.Contains($placeholder) -and $placeholder.hex -and -not $placeholder.appendCrlf) 'Undo preserves option-only edits to an otherwise blank placeholder.'

    Invoke-Edit 'RemoveQuickSendItem' ([object[]]@($rows[0]))
    Invoke-Edit 'LoadQuickSendList'
    Assert-Edit (-not $window.FindName('UndoQuickCommandButton').IsEnabled) 'Reloading another page or workspace clears stale undo state.'
    # Exercise the same begin/commit path used by native drag/drop, without
    # moving the user's cursor or entering a real desktop drag loop.
    Set-OnlyRow 'AT+FIRST'
    $first = $rows[0]
    $first.recvScriptPara = 'keep this parameter'
    $first.expectedResponse = 'KEEP_OK'
    for ($i = 2; $i -le 40; $i++) {
        $item = $type.GetMethod('CreateBlankQuickSendItem', $flags).Invoke($window, [object[]]@($i))
        $item.text = 'AT+COMMAND_' + $i
        [void]$rows.Add($item)
    }
    $window.SaveSendList($null, [EventArgs]::Empty)
    function Invoke-Reorder([string]$name, [object[]]$arguments = @()) {
        for ($i=0; $i -lt $arguments.Length; $i++) { if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject } }
        return $type.GetMethod($name, [Reflection.BindingFlags]'NonPublic,Instance,Static').Invoke($window, $arguments)
    }
    $session = Invoke-Reorder 'BeginQuickReorder' ([object[]]@($first))
    Assert-Edit ([bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, $rows.Count))) -and
        [object]::ReferenceEquals($rows[$rows.Count - 1], $first) -and $first.expectedResponse -eq 'KEEP_OK' -and
        $first.recvScriptPara -eq 'keep this parameter') 'Dropping at the end moves the same model and preserves response/script settings.'
    Assert-Edit ($settings.quickSend[$settings.quickSend.Count - 1].text -eq 'AT+FIRST') 'A completed drop saves the new order through the existing automatic-snapshot path.'
    Invoke-Edit 'ResetQuickReorder'
    $session = Invoke-Reorder 'BeginQuickReorder' ([object[]]@($first))
    Assert-Edit ([bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, 0))) -and
        [object]::ReferenceEquals($rows[0], $first)) 'Dropping before the first row moves upward correctly.'
    Invoke-Edit 'ResetQuickReorder'
    $session = Invoke-Reorder 'BeginQuickReorder' ([object[]]@($first))
    Assert-Edit (-not [bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, 1))) -and
        -not [bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, -1))) -and
        -not [bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, ($rows.Count + 1))))) 'Adjacent/no-op and out-of-list drops do not reorder anything.'
    Invoke-Edit 'ResetQuickReorder'
    Assert-Edit (-not [bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, 8))) -and
        [object]::ReferenceEquals($rows[0], $first)) 'Canceling invalidates the session without changing order.'
    $session = Invoke-Reorder 'BeginQuickReorder' ([object[]]@($first))
    Invoke-Edit 'InvalidateQuickReorder'
    Assert-Edit (-not [bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, 8)))) 'A page reload or unload invalidates the old drag session.'
    $foreignData = New-Object Windows.DataObject
    $foreignData.SetData('llcom-plus/private-quick-command-reorder', 'move')
    Assert-Edit ($null -eq (Invoke-Reorder 'GetQuickReorderSession' ([object[]]@($foreignData)))) 'External drag data cannot start a reorder operation.'
    $session = Invoke-Reorder 'BeginQuickReorder' ([object[]]@($first))
    $removed = $rows[$rows.Count - 1]
    $rows.RemoveAt($rows.Count - 1)
    Assert-Edit (-not [bool](Invoke-Reorder 'CommitQuickReorder' ([object[]]@($session, 8)))) 'A changed collection invalidates the captured destination ordering.'
    [void]$rows.Add($removed)
    Invoke-Edit 'ResetQuickReorder'

    $tab = $window.FindName('QuickSendTab')
    $content = $tab.Content
    $tab.Content = $null
    $content.Resources.MergedDictionaries.Add($window.Resources)
    $surface = New-Object Windows.Controls.Border
    $surface.Padding = New-Object Windows.Thickness(12)
    $surface.Child = $content
    $list = $window.FindName('toSendList')
    $previewDir = Join-Path $repoRoot 'artifacts\quick-drag-sort'
    [void](New-Item -ItemType Directory -Force -Path $previewDir)
    function Arrange-Reorder {
        $surface.Measure((New-Object Windows.Size(720,620)))
        $surface.Arrange((New-Object Windows.Rect(0,0,720,620)))
        $surface.UpdateLayout()
    }
    function Save-ReorderPreview([string]$name) {
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(720,620,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($surface)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $previewPath = Join-Path $previewDir ($name + '.png')
        $stream = [IO.File]::Open($previewPath,[IO.FileMode]::Create)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        Write-Host "RENDER $previewPath"
    }
    function Test-RadialMenu([string]$theme, [string]$language) {
        $popup = $window.FindName('QuickCommandActionsPopup')
        $menu = $popup.Child
        $popup.Child = $null
        $radialSurface = New-Object Windows.Controls.Border
        $radialSurface.Padding = New-Object Windows.Thickness(10)
        $radialSurface.Background = $app.TryFindResource('AppWindowBackgroundBrush')
        $radialSurface.Child = $menu
        $hoverKey = [Windows.UIElement].GetField('IsMouseOverPropertyKey', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
        try {
            $previewWidth = [int]$menu.Width + 20
            $previewHeight = [int]$menu.Height + 20
            Assert-Edit ($menu.Width -le 88 -and $menu.Height -le 152) 'Radial actions use the compact footprint without scaling down label text.'
            $radialSurface.Measure((New-Object Windows.Size($previewWidth,$previewHeight)))
            $radialSurface.Arrange((New-Object Windows.Rect(0,0,$previewWidth,$previewHeight)))
            $radialSurface.UpdateLayout()
            $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
            foreach ($probe in @(@(40,42,'CopyButton'), @(40,106,'DeleteButton'), @(20,76,''), @(60,76,''), @(86,2,''))) {
                # Offscreen visuals have IsVisible=false because no HWND is
                # created. Validate the actual template hit-test clips instead.
                $hitName = ''
                foreach ($buttonName in @('CopyButton','DeleteButton')) {
                    $button = $menu.FindName($buttonName)
                    [void]$button.ApplyTemplate()
                    $clip = [Windows.Media.VisualTreeHelper]::GetChild($button,0).Clip
                    Assert-Edit ($null -ne $clip) 'Radial button template clips its real interactive surface.'
                    $point = $menu.TranslatePoint((New-Object Windows.Point($probe[0],$probe[1])), $button)
                    if ($clip.FillContains($point)) { $hitName = $buttonName }
                }
                Assert-Edit ($hitName -eq $probe[2]) "Radial sector hit-testing respects the center hole and separator ($theme $($probe[0]),$($probe[1]))."
            }
            $deleteButton = $menu.FindName('DeleteButton')
            Assert-Edit ($deleteButton.Foreground.ToString() -eq $app.TryFindResource('AppDangerBrush').ToString()) "Radial delete keeps its danger color ($theme)."
            foreach ($buttonName in @('CopyButton','DeleteButton')) {
                $button = $menu.FindName($buttonName)
                $label = $button.Content.Children[1]
                $face = New-Object Windows.Media.Typeface($label.FontFamily,$label.FontStyle,$label.FontWeight,$label.FontStretch)
                $formatted = New-Object Windows.Media.FormattedText($label.Text,[Globalization.CultureInfo]::GetCultureInfo($language),$label.FlowDirection,$face,$label.FontSize,$label.Foreground)
                $position = $label.TranslatePoint((New-Object Windows.Point(0,0)),$button)
                $glyphs = $formatted.BuildGeometry($position)
                $clip = [Windows.Media.VisualTreeHelper]::GetChild($button,0).Clip
                $clippedGlyphs = [Windows.Media.Geometry]::Combine($glyphs,$clip,[Windows.Media.GeometryCombineMode]::Exclude,$null)
                Assert-Edit ($clippedGlyphs.GetArea() -lt 0.1) "The compact arc does not crop label glyphs ($theme $language $buttonName)."
            }
            foreach ($state in @('Normal','Copy','Delete')) {
                $menu.FindName('CopyButton').SetValue($hoverKey, ($state -eq 'Copy'))
                $deleteButton.SetValue($hoverKey, ($state -eq 'Delete'))
                $radialSurface.UpdateLayout()
                foreach ($buttonName in @('CopyButton','DeleteButton')) {
                    $button = $menu.FindName($buttonName)
                    $fill = $button.Template.FindName('SectorFill',$button)
                    $outline = $button.Template.FindName('Sector',$button)
                    $expectedOpacity = if (($buttonName -eq 'CopyButton' -and $state -eq 'Copy') -or ($buttonName -eq 'DeleteButton' -and $state -eq 'Delete')) { 0.92 } else { 0.78 }
                    Assert-Edit ([Math]::Abs($fill.Opacity - $expectedOpacity) -lt 0.001) "Only the sector background fades, with stronger hover contrast ($theme $state $buttonName)."
                    Assert-Edit ($button.Opacity -eq 1 -and $button.Content.Opacity -eq 1 -and $outline.Opacity -eq 1) 'Radial icons, labels and outline remain fully opaque.'
                }
                $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($previewWidth,$previewHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
                $bitmap.Render($radialSurface)
                if ($state -eq 'Normal') {
                    $transparentBitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$menu.Width,[int]$menu.Height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
                    $transparentBitmap.Render($menu)
                    $pixel = New-Object byte[] 4
                    $transparentBitmap.CopyPixels((New-Object Windows.Int32Rect(70,65,1,1)),$pixel,4,0)
                    Assert-Edit ($pixel[3] -ge 180 -and $pixel[3] -le 205) "Rendered sector pixels preserve real background transparency ($theme $language)."
                }
                $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                $previewPath = Join-Path $previewDir "radial-$theme-$state-$language.png"
                $stream = [IO.File]::Open($previewPath,[IO.FileMode]::Create)
                try { $encoder.Save($stream) } finally { $stream.Dispose() }
                Write-Host "RENDER $previewPath"
            }
        }
        finally {
            $menu.FindName('CopyButton').ClearValue($hoverKey)
            $menu.FindName('DeleteButton').ClearValue($hoverKey)
            $radialSurface.Child = $null
            $popup.Child = $menu
        }
    }
    function Test-DragHandleAlignment($handle, $container, [string]$theme) {
        # Exercise the real WPF template offscreen, without stealing focus or
        # moving the user's mouse. The icon must stay at the border's center.
        $hoverKey = [Windows.UIElement].GetField('IsMouseOverPropertyKey', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
        $focusKey = [Windows.UIElement].GetField('IsKeyboardFocusedPropertyKey', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
        $icon = $handle.Content
        $chrome = $handle.Template.FindName('Chrome',$handle)
        $textBox = $container.Template.FindName('QuickSendRowTextBox',$container)
        $normalSize = $handle.RenderSize
        try {
            foreach ($state in @('Normal','Hover','Focus')) {
                $handle.SetValue($hoverKey, ($state -eq 'Hover'))
                $handle.SetValue($focusKey, ($state -eq 'Focus'))
                Arrange-Reorder
                $iconCenter = $icon.TranslatePoint((New-Object Windows.Point(($icon.ActualWidth / 2), ($icon.ActualHeight / 2))), $handle)
                $borderCenter = $chrome.TranslatePoint((New-Object Windows.Point(($chrome.ActualWidth / 2), ($chrome.ActualHeight / 2))), $handle)
                $handleCenter = $handle.TranslatePoint((New-Object Windows.Point(($handle.ActualWidth / 2), ($handle.ActualHeight / 2))), $container)
                $textCenter = $textBox.TranslatePoint((New-Object Windows.Point(0, ($textBox.ActualHeight / 2))), $container)
                Assert-Edit ([Math]::Abs($iconCenter.X - $borderCenter.X) -le 0.5 -and
                    [Math]::Abs($iconCenter.Y - $borderCenter.Y) -le 0.5 -and
                    [Math]::Abs($handleCenter.Y - $textCenter.Y) -le 0.5 -and
                    $handle.RenderSize -eq $normalSize) "The six-dot icon, rounded outline, and command row remain centered ($theme/$state)."
                if ($state -eq 'Hover') {
                    Assert-Edit ($chrome.Background.ToString() -eq $app.TryFindResource('AppGlassControlHoverBackground').ToString() -and
                        $chrome.BorderBrush.ToString() -eq $app.TryFindResource('AppGlassBorderBrush').ToString()) "The hover outline is visible ($theme)."
                }
                elseif ($state -eq 'Focus') {
                    Assert-Edit ($chrome.BorderBrush.ToString() -eq $app.TryFindResource('AppGlassFocusBorderBrush').ToString()) "The focused outline is visible ($theme)."
                }
                Save-ReorderPreview "quick-handle-$theme-$state"
            }
        }
        finally {
            $handle.ClearValue($hoverKey)
            $handle.ClearValue($focusKey)
            Arrange-Reorder
        }
    }
    function Find-Scroll($element) {
        if ($element -is [Windows.Controls.ScrollViewer]) { return $element }
        for ($j=0; $j -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($element); $j++) {
            $found = Find-Scroll ([Windows.Media.VisualTreeHelper]::GetChild($element,$j))
            if ($null -ne $found) { return $found }
        }
        return $null
    }
    foreach ($theme in @('Light','Dark')) {
        $surface.Child = $null
        [void]$globalType.GetMethod('ApplyTheme').Invoke($null, [object[]]@(($theme -eq 'Dark')))
        $surface.Background = $app.TryFindResource('AppWindowBackgroundBrush')
        $surface.Child = $content
        Arrange-Reorder
        $scroll = Find-Scroll $list
        $scroll.ScrollToTop()
        Arrange-Reorder
        $container = $list.ItemContainerGenerator.ContainerFromItem($first)
        $handle = $container.Template.FindName('QuickSendRowDragHandle',$container)
        Assert-Edit ($null -ne $handle -and [Windows.Automation.AutomationProperties]::GetName($handle).Length -gt 0) "An accessible six-dot drag handle is rendered ($theme)."
        Assert-Edit ((Invoke-Reorder 'GetQuickSendNavigationColumnFromSource' ([object[]]@($handle))) -eq -1) 'The drag handle cannot be treated as a serial-send keyboard cell.'
        Test-DragHandleAlignment $handle $container $theme
        foreach ($language in @('zh-CN','en-US')) {
            [void]$globalType.GetMethod('LoadLanguageFile').Invoke($null, @($language))
            Test-RadialMenu $theme $language
        }
        [void]$globalType.GetMethod('LoadLanguageFile').Invoke($null, @('zh-CN'))
        [void](Invoke-Reorder 'BeginQuickReorder' ([object[]]@($first)))
        $third = $list.ItemContainerGenerator.ContainerFromItem($rows[2])
        $position = $third.TranslatePoint((New-Object Windows.Point(8, ($third.ActualHeight * 0.75))), $list)
        Invoke-Edit 'ShowQuickReorderInsertion' ([object[]]@($position))
        Arrange-Reorder
        Assert-Edit ($window.FindName('QuickSendInsertionLine').Visibility -eq [Windows.Visibility]::Visible -and
            -not $window.FindName('QuickSendDragOverlay').IsHitTestVisible) "The insertion line is visible and never blocks editing ($theme)."
        Save-ReorderPreview "quick-drag-$theme"
        $viewport = Invoke-Reorder 'QuickReorderViewport'
        $type.GetField('quickReorderInside',$flags).SetValue($window,$true)
        $type.GetField('quickReorderPointer',$flags).SetValue($window,(New-Object Windows.Point(10,($viewport.Bottom - 2))))
        $before = $scroll.VerticalOffset
        Invoke-Edit 'QuickReorderScrollTick' ([object[]]@($null,[EventArgs]::Empty))
        Arrange-Reorder
        Assert-Edit ($scroll.VerticalOffset -gt $before) 'Dragging at the bottom edge scrolls the virtualized list.'
        Invoke-Edit 'ResetQuickReorder'
        Assert-Edit ($window.FindName('QuickSendInsertionLine').Visibility -eq [Windows.Visibility]::Collapsed) 'Cancel removes the insertion indicator.'
    }
    $surface.Child = $null
    $tab.Content = $content
    Assert-Edit (-not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No command was sent or physical serial port opened.'
    Write-Host 'PASS All isolated quick-command editing and drag-sort checks.'
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $globalType) {
        $globalType.GetProperty('isMainWindowsClosed').SetValue($null, $true, $null)
        $backupType = $assembly.GetType('llcom_plus.Tools.QuickSendBackupService', $true)
        [void]$backupType.GetMethod('Shutdown', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, $null)
    }
    if ($null -ne $app) { $app.Shutdown() }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or
        $testName -notmatch '^llcom-quick-edit-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
exit $exitCode
