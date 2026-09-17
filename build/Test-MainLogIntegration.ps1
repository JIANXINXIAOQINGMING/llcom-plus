[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64',
    [switch]$RenderPreviews
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw 'Use matching-bitness Windows PowerShell 5.1 with -STA.'
}
$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root "llcom plus\bin\$Platform\$Configuration"
$previewDir = Join-Path $root 'artifacts\main-log-ui'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-main-log-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$app = $null
$globalType = $null
$single = $null
$split = $null
$window = $null
$exitCode = 0
$previousContext = [Threading.SynchronizationContext]::Current
try {
    Add-Type -AssemblyName PresentationFramework
    Add-Type -ReferencedAssemblies @('WindowsBase','PresentationCore','PresentationFramework','System.Xaml') -TypeDefinition @'
using System;
using System.Windows;
public static class OfflineUiExceptionObserver
{
    public static int FailureCount;
    private static void Report(Exception ex)
    {
        FailureCount++;
        Console.Error.WriteLine("OFFLINE UI EXCEPTION: " + ex.GetType().FullName);
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine(ex.StackTrace);
        if (ex.InnerException != null) Report(ex.InnerException);
    }
    public static void Install(Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => Report((Exception)e.ExceptionObject);
        app.DispatcherUnhandledException += (sender, e) => { Report(e.Exception); e.Handled = true; };
    }
    public static System.Collections.IList CreateTracePreviewRows(System.Reflection.Assembly assembly)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var entryType = assembly.GetType("llcom_plus.Tools.SerialTraceEntry", true);
        var rowType = assembly.GetType("llcom_plus.Tools.SerialTraceRow", true);
        var kindType = assembly.GetType("llcom_plus.Tools.SerialTraceKind", true);
        var result = new System.Collections.ArrayList();
        var text = new[] { "AT+VER?\r\n", "+VER:MODULE_TEST\r\nAT_OK\r\n", "RI: Low -> High" };
        var kinds = new[] { "Tx", "Rx", "Pin" };
        for (int i = 0; i < text.Length; i++)
        {
            var data = i == 2 ? new byte[0] : System.Text.Encoding.UTF8.GetBytes(text[i]);
            var entry = Activator.CreateInstance(entryType, flags, null,
                new object[] { (long)i + 1, DateTime.Now, i == 2 ? "COM8" : "COM7", "offline-preview", Enum.Parse(kindType, kinds[i]), data, text[i], 65001 }, null);
            result.Add(Activator.CreateInstance(rowType, flags, null, new object[] { entry, false }, null));
        }
        return result;
    }
}
'@
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
    $flags = [Reflection.BindingFlags]'NonPublic,Instance'
    $allInstance = [Reflection.BindingFlags]'Public,NonPublic,Instance'
    $globalType = $assembly.GetType('llcom_plus.Tools.Global', $true)
    $globalType.GetField('ProfilePath').SetValue($null, $testRoot + '\')
    $settingsType = $assembly.GetType('llcom_plus.Model.Settings', $true)
    $settings = [Activator]::CreateInstance($settingsType, $true)
    $globalType.GetField('setting').SetValue($null, $settings)
    [Windows.Application]::ResourceAssembly = $assembly
    $app = [Activator]::CreateInstance($assembly.GetType('llcom_plus.App', $true))
    $app.InitializeComponent()
    [OfflineUiExceptionObserver]::Install($app)
    [Windows.Application].GetField('_startupUri', $flags).SetValue($app, $null)
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    # Normal WPF input dispatch installs this context automatically. Reflection
    # calls from a PowerShell host need it explicitly so awaited UI handlers
    # resume on their owning dispatcher rather than on the thread pool.
    [Threading.SynchronizationContext]::SetSynchronizationContext((New-Object Windows.Threading.DispatcherSynchronizationContext($app.Dispatcher)))
    # Construct and measure offscreen only. No Show, normal Window_Loaded,
    # update checks, send handlers, or SerialPort.Open are called.
    $window = [Activator]::CreateInstance($assembly.GetType('llcom_plus.MainWindow', $true))
    $windowType = $window.GetType()
    $frame = $window.FindName('dataShowFrame')
    $timelineFrame = $window.FindName('MainTimelineFrame')
    $tabs = $window.FindName('QuickSendTab').Parent
    $tabs.SelectedItem = $window.FindName('QuickSendTab')
    $selectedTab = $tabs.SelectedItem
    $single = [Activator]::CreateInstance($assembly.GetType('llcom_plus.Pages.DataShowPage', $true))
    $sample = "[2026/09/11 11:01:48.876] " + [char]0x2190 + " AT+VER?`r`n[2026/09/11 11:01:49.006] " + [char]0x2192 + " +VER:MODULE_TEST`r`nAT_OK`r`n"
    $single.SetLogTextSnapshot($sample)
    $frame.Content = $single
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)

    function Invoke-Main([string]$name, [object[]]$arguments = @()) {
        try { [void]$windowType.GetMethod($name, $flags).Invoke($window, $arguments) }
        catch { throw $_.Exception.GetBaseException() }
    }
    function Assert-Main([bool]$condition, [string]$message) {
        if (-not $condition) { throw "FAIL $message" }
        Write-Host "PASS $message"
    }
    function Pump-Ui {
        $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    }
    function Wait-InlineSearch($bar) {
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            Pump-Ui
            $busy = $bar.GetType().GetField('searching', $flags).GetValue($bar)
            $debounce = $bar.GetType().GetField('debounce', $flags).GetValue($bar)
            if (-not $busy -and -not $debounce.IsEnabled) { return }
            [Threading.Thread]::Sleep(10)
        }
        throw 'Inline search did not settle within the bounded offscreen render wait.'
    }
    function Render-Main($element, [int]$width, [int]$height, [string]$name) {
        $surface = New-Object Windows.Controls.Border
        $surface.Padding = New-Object Windows.Thickness(8)
        $surface.Background = $app.TryFindResource('AppWindowBackgroundBrush')
        # Match the normal Window's adorner surface while remaining offscreen.
        $adornerHost = New-Object Windows.Documents.AdornerDecorator
        $adornerHost.Child = $element
        $surface.Child = $adornerHost
        $surface.Measure((New-Object Windows.Size($width, $height)))
        $surface.Arrange((New-Object Windows.Rect(0, 0, $width, $height)))
        $surface.UpdateLayout()
        Pump-Ui
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($surface)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $path = Join-Path $previewDir ($name + '.png')
        $stream = [IO.File]::Open($path, [IO.FileMode]::Create)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        $adornerHost.Child = $null
        $surface.Child = $null
        Write-Host "RENDER $path"
    }

    Invoke-Main 'MainLogFind_Click' ([object[]]@($null, $null))
    Assert-Main ($single.IsSearchActive -and [object]::ReferenceEquals($tabs.SelectedItem, $selectedTab)) 'Main Find opens inline search without switching the right-hand tool tab.'
    Assert-Main ($single.GetLogTextSnapshot().Contains('AT_OK')) 'Opening inline search preserves the original log.'
    $single.CloseLogSearch()
    Assert-Main (-not $single.IsSearchActive) 'Closing inline search restores the ordinary log view.'
    # The shortcut reads the real Keyboard.Modifiers static property. Inspect its
    # forwarding guard instead of injecting input into the user's actual desktop.
    $source = [IO.File]::ReadAllText((Join-Path $root 'llcom plus\UI\View\MainWindow.xaml.cs'))
    Assert-Main ($source -match '(?s)private void MainWindow_AnalysisKeyDown\(.*?Keyboard\.Modifiers != ModifierKeys\.Control.*?ScriptTab\.IsSelected.*?ShowMainLogSearch\(\);.*?e\.Handled = true;') 'Ctrl+F forwards to main-log search and leaves the script editor shortcut alone.'

    $timelineTab = $window.FindName('TimelineTab')
    $timelineTab.IsSelected = $true
    Pump-Ui
    Assert-Main ($null -ne $timelineFrame.Content -and [object]::ReferenceEquals($tabs.SelectedItem, $timelineTab) -and $tabs.Items.IndexOf($timelineTab) -eq $tabs.Items.IndexOf($selectedTab) + 1) 'Timeline is a lazily initialized peer tab directly beside Quick Send.'
    $timelinePage = $windowType.GetField('mainTimelinePage', $flags).GetValue($window)
    Assert-Main ($timelinePage.FindName('PageHeading').Visibility -eq [Windows.Visibility]::Visible -and $null -eq $window.FindName('TimelineDrawerRow') -and $null -eq $window.FindName('MainTimelineButton') -and [object]::ReferenceEquals($frame.Content, $single)) 'Timeline has its full page and does not split or replace the original serial log.'
    $timelinePage.FindName('SearchBox').Text = 'preserved filter'
    $selectedTab.IsSelected = $true
    $timelineTab.IsSelected = $true
    Pump-Ui
    Assert-Main ([object]::ReferenceEquals($timelinePage, $timelineFrame.Content) -and $timelinePage.FindName('SearchBox').Text -eq 'preserved filter') 'Switching tabs retains timeline filters and its existing page.'
    Invoke-Main 'OpenLogAnalysis' ([object[]]@('COM7', [DateTime]::Now))
    Pump-Ui
    Assert-Main ($timelineTab.IsSelected -and $timelinePage.FindName('PortBox').Text -eq 'COM7') 'Notification navigation selects the timeline tab and the requested COM.'
    $timelinePage.FindName('SearchBox').Clear()
    $selectedTab.IsSelected = $true

    $splitType = $assembly.GetType('llcom_plus.Pages.MultiPortPage', $true)
    $split = [Activator]::CreateInstance($splitType, [object[]]@(2, $false, ''))
    [void]$splitType.GetMethod('EnsureSlotsCreated', $flags).Invoke($split, $null)
    $frame.Content = $split
    Pump-Ui
    Invoke-Main 'MainLogFind_Click' ([object[]]@($null, $null))
    Assert-Main ($split.IsSearchActive) 'Main Find also opens search for the active split pane.'
    $firstTarget = $split.FindName('SplitLogFindBar').GetType().GetField('target', $flags).GetValue($split.FindName('SplitLogFindBar'))
    $split.SetActiveSlot(2)
    $secondTarget = $split.FindName('SplitLogFindBar').GetType().GetField('target', $flags).GetValue($split.FindName('SplitLogFindBar'))
    Assert-Main ($split.IsSearchActive -and $null -ne $firstTarget -and $null -ne $secondTarget -and -not [object]::ReferenceEquals($firstTarget, $secondTarget)) 'Changing the active split pane retargets inline search instead of searching the old COM.'
    $split.CloseLogSearch()
    Assert-Main (-not $split.IsSearchActive -and -not $split.IsSlotOpen(1) -and -not $split.IsSlotOpen(2)) 'Split search closes without opening either serial connection.'
    $slotOne = $splitType.GetMethod('GetSlot', $flags).Invoke($split, [object[]]@(1))
    [void]$slotOne.GetType().GetMethod('SetPortName', $allInstance).Invoke($slotOne, [object[]]@('COM98', $true))
    $split.RefreshSlotPorts([string[]]@('COM97'))
    Assert-Main ($split.GetSlotPortName(1) -eq 'COM98' -and -not $split.IsSlotOpen(1)) 'A workspace-assigned missing COM survives device-list refresh without redirecting to another port.'
    $split.SetSlotPortName(1, 'COM96')
    $split.RefreshSlotPorts([string[]]@('COM97'))
    Assert-Main ($split.GetSlotPortName(1) -eq 'COM97' -and -not $split.IsSlotOpen(1)) 'An explicit manual port selection releases the workspace assignment lock.'
    $frame.Content = $single
    Pump-Ui

    if ($RenderPreviews) {
        [void](New-Item -ItemType Directory -Force -Path $previewDir)
        $mainGrid = $window.Content
        $window.Content = $null
        $mainGrid.IsEnabled = $true
        $mainGrid.Resources.MergedDictionaries.Add($window.Resources)
        Render-Main $mainGrid 1320 840 'main-tabs-layout'
        $actions = $window.FindName('RightTopActions')
        foreach ($tab in $tabs.Items) {
            $chrome = $tab.Template.FindName('Chrome', $tab)
            $tabRight = $chrome.TranslatePoint((New-Object Windows.Point($chrome.ActualWidth, 0)), $mainGrid).X
            $actionsLeft = $actions.TranslatePoint((New-Object Windows.Point(0, 0)), $mainGrid).X
            Assert-Main ($tabRight -le $actionsLeft) 'Main tab headers do not overlap notification/theme/update actions.'
        }
        $window.Content = $mainGrid
        $logGrid = $frame.Parent
        $logBorder = $logGrid.Parent
        $logBorder.Child = $null
        $logGrid.Resources.MergedDictionaries.Add($window.Resources)
        $logGrid.DataContext = $settings
        $editor = $window.FindName('QuickSendItemSettingsEditor')
        $editorParent = $editor.Parent
        if ($editorParent -is [Windows.Controls.Border]) { $editorParent.Child = $null }
        elseif ($editorParent -is [Windows.Controls.Primitives.Popup]) { $editorParent.Child = $null }
        else { throw 'Unexpected quick settings parent; refusing to reparent the preview.' }
        $editor.Resources.MergedDictionaries.Add($window.Resources)
        $row = $windowType.GetMethod('CreateBlankQuickSendItem', $flags).Invoke($window, [object[]]@(1))
        $row.text = 'AT'
        $row.responseMode = 2
        $row.expectedResponse = 'OK'
        $row.responseTimeoutMs = 5000
        [void]$editor.GetType().GetMethod('SetItem', $flags).Invoke($editor, [object[]]@($row))
        $expander = $editor.FindName('ResponseModeComboBox').Parent
        while ($null -ne $expander -and $expander -isnot [Windows.Controls.Expander]) { $expander = $expander.Parent }
        if ($null -eq $expander) { throw 'Missing response workflow expander.' }
        $expander.IsExpanded = $true
        # Seed only the offscreen timeline grid with real immutable row models.
        # No serial data is injected and no application receive/send events fire.
        $timelineRows = [OfflineUiExceptionObserver]::CreateTracePreviewRows($assembly)
        foreach ($theme in @('Light','Dark')) {
            $settingsType.GetField('_darkMode', $flags).SetValue($settings, ($theme -eq 'Dark'))
            [void]$globalType.GetMethod('ApplyTheme').Invoke($null, [object[]]@(($theme -eq 'Dark')))
            # DataShowPage.Loaded is intentionally not raised; recreate this
            # fixture text so its brushes use the current theme just as a real
            # subscribed page would after receiving the theme-changed event.
            $single.SetLogTextSnapshot($sample)
            foreach ($width in @(470,800)) {
                Invoke-Main 'MainLogFind_Click' ([object[]]@($null, $null))
                $single.FindName('LogSearchBar').FindName('QueryBox').Text = 'AT'
                Wait-InlineSearch $single.FindName('LogSearchBar')
                Render-Main $logGrid $width 720 "main-find-$theme-$width"
                Invoke-Main 'OpenLogAnalysis' ([object[]]@($null, $null))
                Pump-Ui
                $timelinePage.FindName('Records').ItemsSource = $timelineRows
                Render-Main $logGrid $width 720 "main-log-with-timeline-tab-$theme-$width"
                $timelineTab.Content = $null
                try { Render-Main $timelineFrame ([Math]::Max(650, $width)) 720 "timeline-tab-$theme-$width" }
                finally { $timelineTab.Content = $timelineFrame }
                $selectedTab.IsSelected = $true
            }
            Render-Main $editor 370 620 "quick-response-top-$theme"
            $scroll = $expander.Parent
            while ($null -ne $scroll -and $scroll -isnot [Windows.Controls.ScrollViewer]) { $scroll = $scroll.Parent }
            if ($null -ne $scroll) { $scroll.ScrollToEnd() }
            Render-Main $editor 370 620 "quick-response-options-$theme"
            if ($null -ne $scroll) { $scroll.ScrollToTop() }
        }
    }
    Assert-Main (-not $globalType.GetField('uart').GetValue($null).IsOpen()) 'No physical serial port was opened by the integration test.'
    Assert-Main ([OfflineUiExceptionObserver]::FailureCount -eq 0) 'No dispatcher or background exception occurred during the offline lifecycle test.'
    Write-Host 'PASS All main-log and timeline-tab integration checks.'
}
catch { $exitCode = 1; Write-Host $_.Exception.ToString() }
finally {
    if ($null -ne $single) { $single.CloseLogSearch() }
    if ($null -ne $split) { $split.CloseLogSearch() }
    if ($null -ne $window) {
        $method = $window.GetType().GetMethod('CloseMainTimeline', [Reflection.BindingFlags]'NonPublic,Instance')
        if ($null -ne $method) { [void]$method.Invoke($window, $null) }
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
        $testName -notmatch '^llcom-main-log-[0-9a-f]{32}$') { throw 'Refusing cleanup outside the isolated test directory.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
exit $exitCode
