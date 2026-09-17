using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class LogSearchHighlightProbe
{
    private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static RenderTargetBitmap Render(FrameworkElement surface)
    {
        surface.Measure(new Size(600, 230));
        surface.Arrange(new Rect(0, 0, 600, 230));
        surface.UpdateLayout();
        surface.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
        var result = new RenderTargetBitmap(600, 230, 96, 96, PixelFormats.Pbgra32);
        result.Render(surface);
        return result;
    }
    private static void Save(RenderTargetBitmap bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    private static int ChangedPixels(RenderTargetBitmap before, RenderTargetBitmap after)
    {
        var a = new byte[600 * 230 * 4];
        var b = new byte[a.Length];
        before.CopyPixels(a, 600 * 4, 0);
        after.CopyPixels(b, 600 * 4, 0);
        int count = 0;
        // The match sits below the query box. Ignore the query/caret area.
        for (int y = 50; y < 200; y++)
            for (int x = 0; x < 580; x++)
            {
                int i = (y * 600 + x) * 4;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 90) count++;
            }
        return count;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert(timer.ElapsedMilliseconds < 5000, "Search did not finish on its owning dispatcher.");
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            tick.Tick += delegate { tick.Stop(); frame.Continue = false; };
            tick.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static int AdornerCount(RichTextBox box)
    {
        var adorners = AdornerLayer.GetAdornerLayer(box).GetAdorners(box);
        return adorners == null ? 0 : adorners.Length;
    }

    public static string[] Run(Assembly assembly, string artifacts)
    {
        var messages = new List<string>();
        var global = assembly.GetType("llcom_plus.Tools.Global", true);
        var appearanceType = assembly.GetType("llcom_plus.Views.LogFindBar+SelectionAppearance", true);
        var theme = global.GetProperty("IsDarkTheme");
        foreach (bool dark in new[] { false, true })
        {
            theme.GetSetMethod(true).Invoke(null, new object[] { dark });
            var query = new TextBox { Text = "+VER: MODULE_TEST", Height = 36, Margin = new Thickness(8) };
            var foreground = dark ? Brushes.LightGreen : Brushes.DarkGreen;
            var first = new Run("+VER: MODULE_TEST") { Foreground = foreground };
            var second = new Run("+VER: MODULE_TEST") { Foreground = foreground };
            var document = new FlowDocument { FontSize = 18, FontFamily = new FontFamily("Consolas"), PagePadding = new Thickness(8) };
            document.Blocks.Add(new Paragraph(first));
            document.Blocks.Add(new Paragraph(new Run("AT_OK")));
            document.Blocks.Add(new Paragraph(second));
            var log = new RichTextBox { Document = document, IsReadOnly = true, BorderThickness = new Thickness(0),
                Background = dark ? new SolidColorBrush(Color.FromRgb(25, 37, 52)) : Brushes.White,
                Foreground = foreground, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(query);
            grid.Children.Add(log);
            Grid.SetRow(log, 1);
            var surface = new AdornerDecorator { Child = grid };
            // Hidden, never activated and never shown. A presentation source is required
            // for WPF's real inactive selection drawing, unlike a bare Measure/Arrange.
            var parameters = new HwndSourceParameters("Offline log highlight probe") { Width = 600, Height = 230,
                PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 };
            using (var source = new HwndSource(parameters))
            {
                source.RootVisual = surface;
                FocusManager.SetIsFocusScope(grid, true);
                FocusManager.SetFocusedElement(grid, query);
                var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
                var initial = Render(surface);
                using (var appearance = (IDisposable)Activator.CreateInstance(appearanceType, Instance, null, new object[] { log }, null))
                {
                    log.Selection.Select(first.ContentStart, first.ContentEnd);
                    var highlighted = Render(surface);
                    int changed = ChangedPixels(initial, highlighted);
                    Save(highlighted, Path.Combine(artifacts, dark ? "search-dark.png" : "search-light.png"));
                    Assert(!log.IsKeyboardFocusWithin && ReferenceEquals(FocusManager.GetFocusedElement(grid), query), "Find stole focus from the query box.");
                    Assert(changed > 500, "Inactive match has no visible background: changed only " + changed + " pixels.");
                    theme.GetSetMethod(true).Invoke(null, new object[] { !dark });
                    global.GetMethod("RaiseThemeChanged", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    var recolored = Render(surface);
                    Assert(ChangedPixels(highlighted, recolored) > 500, "Changing theme did not recolor the active match.");
                    theme.GetSetMethod(true).Invoke(null, new object[] { dark });
                    global.GetMethod("RaiseThemeChanged", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    log.Selection.Select(second.ContentStart, second.ContentEnd);
                    var next = Render(surface);
                    Assert(ChangedPixels(highlighted, next) > 500, "Next match did not move the highlighted background.");
                    log.Selection.Select(document.ContentStart, document.ContentStart);
                    var cleared = Render(surface);
                    Assert(ChangedPixels(initial, cleared) < 10, "Clearing selection left painted match artifacts.");
                }
                Assert(log.ReadLocalValue(TextBoxBase.SelectionBrushProperty) == DependencyProperty.UnsetValue &&
                    log.ReadLocalValue(TextBoxBase.SelectionOpacityProperty) == DependencyProperty.UnsetValue &&
                    !log.IsInactiveSelectionHighlightEnabled, "Closing search did not restore selection view properties.");
                Assert(text == new TextRange(document.ContentStart, document.ContentEnd).Text &&
                    ReferenceEquals(first.Foreground, foreground) && ReferenceEquals(second.Foreground, foreground), "Search mutated retained log text or colors.");
                Assert(AdornerCount(log) == 0, "Closing the appearance left an attached drawing layer.");
                source.RootVisual = null;
            }
            messages.Add((dark ? "Dark" : "Light") + " theme: inactive match has a visible background, next moves it, and clearing leaves no artifacts or log changes.");
        }
        var bound = new RichTextBox();
        var holder = new Border { Background = Brushes.Purple };
        BindingOperations.SetBinding(bound, TextBoxBase.SelectionBrushProperty, new Binding("Background") { Source = holder });
        using ((IDisposable)Activator.CreateInstance(appearanceType, Instance, null, new object[] { bound }, null)) { }
        Assert(BindingOperations.GetBindingBase(bound, TextBoxBase.SelectionBrushProperty) != null && ReferenceEquals(bound.SelectionBrush, Brushes.Purple),
            "Existing selection brush binding was lost.");
        messages.Add("Existing selection brush bindings survive a search session.");

        var dynamicBox = new RichTextBox();
        dynamicBox.Resources["probe-selection-brush"] = Brushes.Coral;
        dynamicBox.SetResourceReference(TextBoxBase.SelectionBrushProperty, "probe-selection-brush");
        using ((IDisposable)Activator.CreateInstance(appearanceType, Instance, null, new object[] { dynamicBox }, null)) { }
        dynamicBox.Resources["probe-selection-brush"] = Brushes.Gold;
        Assert(ReferenceEquals(dynamicBox.SelectionBrush, Brushes.Gold), "Closing search broke a dynamic selection brush resource.");
        messages.Add("Dynamic selection brush resources continue updating after search closes.");

        var previousContext = SynchronizationContext.Current;
        var app = Application.Current;
        if (app == null)
        {
            Application.ResourceAssembly = assembly;
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var barType = assembly.GetType("llcom_plus.Views.LogFindBar", true);
            var bar = (UserControl)Activator.CreateInstance(barType);
            var queryBox = (TextBox)bar.FindName("QueryBox");
            var logA = new RichTextBox { Document = new FlowDocument(new Paragraph(new Run("AT_OK then AT_OK"))) };
            var logB = new RichTextBox { Document = new FlowDocument(new Paragraph(new Run("SECOND AT_OK"))) };
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(75) });
            content.RowDefinitions.Add(new RowDefinition());
            content.RowDefinitions.Add(new RowDefinition());
            content.Children.Add(bar);
            content.Children.Add(logA);
            content.Children.Add(logB);
            Grid.SetRow(logA, 1);
            Grid.SetRow(logB, 2);
            var surface = new AdornerDecorator { Child = content };
            var parameters = new HwndSourceParameters("Offline find lifecycle probe") { Width = 600, Height = 230,
                PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080 };
            using (var source = new HwndSource(parameters))
            {
                source.RootVisual = surface;
                Render(surface);
                var open = barType.GetMethod("Open", Instance);
                var close = barType.GetMethod("Close", Instance);
                queryBox.Text = "AT_OK";
                open.Invoke(bar, new object[] { logA, "COM7" });
                WaitFor(() => logA.Selection.Text == "AT_OK");
                Render(surface);
                Assert(AdornerCount(logA) == 1, "Opening search attached no highlight layer.");
                open.Invoke(bar, new object[] { logB, "COM8" });
                WaitFor(() => logB.Selection.Text == "AT_OK");
                Render(surface);
                Assert(logA.Selection.IsEmpty && AdornerCount(logA) == 0 && AdornerCount(logB) == 1,
                    "Retargeting search did not release the previous log highlight.");
                queryBox.Text = "NO_MATCH";
                Assert(logB.Selection.IsEmpty, "Typing a new query left a stale highlighted match.");
                WaitFor(() => !(bool)barType.GetField("searching", Instance).GetValue(bar) &&
                    !((DispatcherTimer)barType.GetField("debounce", Instance).GetValue(bar)).IsEnabled);
                queryBox.Text = "AT_OK";
                WaitFor(() => logB.Selection.Text == "AT_OK");
                logB.Document = new FlowDocument(new Paragraph(new Run("Fresh RX AT_OK")));
                Render(surface);
                barType.GetMethod("Search", Instance).Invoke(bar, new object[] { 1 });
                WaitFor(() => logB.Selection.Text == "AT_OK");
                close.Invoke(bar, null);
                Assert(logB.Selection.IsEmpty && AdornerCount(logB) == 0 && bar.Visibility == Visibility.Collapsed,
                    "Closing find left a highlight or a live drawing layer.");
                Assert(new TextRange(logA.Document.ContentStart, logA.Document.ContentEnd).Text.Contains("AT_OK then AT_OK") &&
                    new TextRange(logB.Document.ContentStart, logB.Document.ContentEnd).Text.Contains("Fresh RX AT_OK"),
                    "Find lifecycle changed actual log contents.");
                source.RootVisual = null;
            }
            messages.Add("Full find lifecycle: retarget, changed query, replaced log, refresh and close leave no stale highlight or modified log.");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        return messages.ToArray();
    }
}
