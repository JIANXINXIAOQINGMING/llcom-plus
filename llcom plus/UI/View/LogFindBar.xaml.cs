using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace llcom_plus.Views
{
    /// <summary>Inline, display-only find. Selection highlighting never rewrites the colored log.</summary>
    public partial class LogFindBar : UserControl
    {
        private readonly DispatcherTimer debounce;
        private RichTextBox target;
        private DocumentSnapshot snapshot;
        private LogTextSearch.Result result;
        private CancellationTokenSource searchCancellation;
        private int selectedIndex = -1;
        private int documentRevision;
        private bool dirty;
        private bool searching;
        private string contextLabel;
        private SelectionAppearance selectionAppearance;

        public LogFindBar()
        {
            InitializeComponent();
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            debounce.Tick += (sender, args) => { debounce.Stop(); Search(); };
            Unloaded += (sender, args) => Close();
        }

        public bool IsSearchActive => Visibility == Visibility.Visible && target != null;
        public event EventHandler SearchStateChanged;

        public void Open(RichTextBox log, string label = null)
        {
            if (log == null)
                return;
            var changed = !ReferenceEquals(target, log);
            if (changed)
            {
                if (target != null)
                {
                    target.TextChanged -= LogChanged;
                    ClearSearchSelection();
                }
                selectionAppearance?.Dispose();
                target = log;
                target.TextChanged += LogChanged;
                selectionAppearance = new SelectionAppearance(target);
                documentRevision++;
            }
            contextLabel = label ?? string.Empty;
            Visibility = Visibility.Visible;
            SearchStateChanged?.Invoke(this, EventArgs.Empty);
            if (changed || result == null || dirty)
                Search();
            QueryBox.Focus();
            QueryBox.SelectAll();
        }

        public void Close()
        {
            var wasActive = IsSearchActive;
            debounce?.Stop();
            CancelSearch();
            if (target != null)
            {
                target.TextChanged -= LogChanged;
                ClearSearchSelection();
            }
            selectionAppearance?.Dispose();
            selectionAppearance = null;
            target = null;
            snapshot = null;
            result = null;
            selectedIndex = -1;
            searching = false;
            Visibility = Visibility.Collapsed;
            if (wasActive)
                SearchStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void LogChanged(object sender, TextChangedEventArgs e)
        {
            documentRevision++;
            dirty = true;
            UpdateStatus();
        }

        private void QueryChanged(object sender, TextChangedEventArgs e) => QueueSearch();
        private void OptionsChanged(object sender, RoutedEventArgs e) => QueueSearch();
        private void QueueSearch()
        {
            if (!IsSearchActive || debounce == null)
                return;
            // Invalidate old work immediately, not after the typing delay.
            CancelSearch();
            ClearSearchSelection();
            debounce.Stop();
            debounce.Start();
        }

        private void CancelSearch()
        {
            var previous = searchCancellation;
            searchCancellation = null;
            previous?.Cancel();
            previous?.Dispose();
        }

        private async void Search(int direction = 1)
        {
            if (!IsSearchActive)
                return;
            debounce.Stop();
            CancelSearch();
            ClearSearchSelection();
            var cancellation = new CancellationTokenSource();
            searchCancellation = cancellation;
            var token = cancellation.Token;
            var log = target;
            var revision = documentRevision;
            var query = QueryBox.Text ?? string.Empty;
            var regex = RegexBox.IsChecked == true;
            var matchCase = MatchCaseBox.IsChecked == true;
            result = null;
            selectedIndex = -1;
            dirty = false;
            searching = true;
            UpdateStatus();
            try
            {
                // WPF pointers are read only on their owning dispatcher. Regex runs on a worker.
                var captured = DocumentSnapshot.Capture(log.Document);
                var found = await Task.Run(() => LogTextSearch.Find(captured.Text, query, regex, matchCase, token), token);
                if (token.IsCancellationRequested || !IsSearchActive || !ReferenceEquals(target, log))
                    return;
                snapshot = captured;
                result = found;
                dirty = revision != documentRevision;
                searching = false;
                if (result.Hits.Count > 0)
                {
                    selectedIndex = direction < 0 ? result.Hits.Count - 1 : 0;
                    SelectCurrent();
                }
                UpdateStatus();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(target, log))
                    return;
                searching = false;
                StatusText.Text = ex is System.Text.RegularExpressions.RegexMatchTimeoutException
                    ? "表达式耗时过长，请简化后重试。"
                    : ex is ArgumentException ? "查找内容无效：" + ex.Message
                    : ex.Message;
                PreviousButton.IsEnabled = false;
                NextButton.IsEnabled = false;
            }
            finally
            {
                if (ReferenceEquals(searchCancellation, cancellation))
                {
                    searchCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        private void Move(int direction)
        {
            if (result == null || debounce.IsEnabled)
            {
                Search(direction);
                return;
            }
            if (result.Hits.Count == 0)
            {
                if (dirty)
                    Search(direction);
                return;
            }
            selectedIndex = (selectedIndex + direction + result.Hits.Count) % result.Hits.Count;
            if (!SelectCurrent())
            {
                Search(direction);
                return;
            }
            UpdateStatus();
        }

        private bool SelectCurrent()
        {
            if (snapshot == null || target == null || !ReferenceEquals(snapshot.Document, target.Document) ||
                selectedIndex < 0 || selectedIndex >= result.Hits.Count)
                return false;
            var hit = result.Hits[selectedIndex];
            var start = snapshot.PointerAt(hit.Index, false);
            var end = snapshot.PointerAt(hit.Index + hit.Length, true);
            if (start == null || end == null)
                return false;
            // Appends leave text pointers valid, but bounded log trimming may remove the selected record.
            var actual = new TextRange(start, end).Text.Replace("\r\n", "\n");
            if (!string.Equals(actual, snapshot.Text.Substring(hit.Index, hit.Length), StringComparison.Ordinal))
            {
                dirty = true;
                return false;
            }
            target.Selection.Select(start, end);
            // Keep keyboard focus in the find box while highlighting in the original RX/TX display.
            start.Paragraph?.BringIntoView();
            var rectangle = start.GetCharacterRect(LogicalDirection.Forward);
            if (!rectangle.IsEmpty)
                target.ScrollToVerticalOffset(Math.Max(0, target.VerticalOffset + rectangle.Top - target.ViewportHeight / 2));
            return true;
        }

        private void ClearSearchSelection()
        {
            if (target != null)
                target.Selection.Select(target.Document.ContentStart, target.Document.ContentStart);
        }

        /// <summary>Give find results a real background even while the query box owns focus.
        /// This changes only view properties, never Run formatting or saved log content.</summary>
        private sealed class SelectionAppearance : IDisposable
        {
            private readonly RichTextBox box;
            private readonly Dictionary<DependencyProperty, object> originalValues = new Dictionary<DependencyProperty, object>();
            private readonly Dictionary<DependencyProperty, BindingBase> originalBindings = new Dictionary<DependencyProperty, BindingBase>();
            private readonly MatchHighlightAdorner highlight;
            private AdornerLayer layer;
            private bool disposed;

            internal SelectionAppearance(RichTextBox box)
            {
                this.box = box;
                foreach (var property in new[] { TextBoxBase.SelectionBrushProperty, TextBoxBase.SelectionOpacityProperty,
                    TextBoxBase.IsInactiveSelectionHighlightEnabledProperty })
                {
                    originalValues[property] = box.ReadLocalValue(property);
                    originalBindings[property] = BindingOperations.GetBindingBase(box, property);
                }
                box.SetCurrentValue(TextBoxBase.IsInactiveSelectionHighlightEnabledProperty, true);
                highlight = new MatchHighlightAdorner(box);
                box.SelectionChanged += SelectionChanged;
                box.LayoutUpdated += LayoutUpdated;
                Global.ThemeChanged += ThemeChanged;
                Apply();
                Refresh();
            }

            private void ThemeChanged(object sender, EventArgs args) => Apply();
            private void SelectionChanged(object sender, RoutedEventArgs args) => Refresh();
            private void LayoutUpdated(object sender, EventArgs args) => Refresh();
            private void Refresh()
            {
                if (disposed)
                    return;
                var currentLayer = AdornerLayer.GetAdornerLayer(box);
                if (!ReferenceEquals(layer, currentLayer))
                {
                    layer?.Remove(highlight);
                    layer = currentLayer;
                    layer?.Add(highlight);
                    // The overlay remains visible even when Windows suppresses native inactive
                    // selection painting. Avoid painting the native selection a second time.
                    box.SetCurrentValue(TextBoxBase.SelectionOpacityProperty, layer == null ? 0.55 : 0.0);
                }
                highlight.Refresh();
            }

            private void Apply()
            {
                if (disposed)
                    return;
                if (!box.Dispatcher.CheckAccess())
                {
                    box.Dispatcher.BeginInvoke(new Action(Apply));
                    return;
                }
                // Use a saturated fill, not the translucent glass accent used by ordinary text boxes.
                var brush = new SolidColorBrush(Global.IsDarkTheme ? Color.FromRgb(60, 135, 255) : Color.FromRgb(255, 189, 0));
                brush.Freeze();
                box.SetCurrentValue(TextBoxBase.SelectionBrushProperty, brush);
                box.SetCurrentValue(TextBoxBase.SelectionOpacityProperty, layer == null ? 0.55 : 0.0);
                highlight.SetTheme(Global.IsDarkTheme);
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                Global.ThemeChanged -= ThemeChanged;
                box.SelectionChanged -= SelectionChanged;
                box.LayoutUpdated -= LayoutUpdated;
                layer?.Remove(highlight);
                layer = null;
                foreach (var value in originalValues)
                {
                    var binding = originalBindings[value.Key];
                    if (binding != null)
                        BindingOperations.SetBinding(box, value.Key, binding);
                    else if (value.Value == DependencyProperty.UnsetValue)
                        box.ClearValue(value.Key);
                    else
                        box.SetValue(value.Key, value.Value);
                }
            }
        }

        /// <summary>Paint only the visible lines of the current match. The log's Runs,
        /// document and bindings are untouched; this layer never intercepts mouse input.</summary>
        private sealed class MatchHighlightAdorner : Adorner
        {
            private readonly RichTextBox box;
            private List<Rect> rectangles = new List<Rect>();
            private Rect viewport;
            private Brush fill;
            private Pen outline;

            internal MatchHighlightAdorner(RichTextBox box) : base(box)
            {
                this.box = box;
                IsHitTestVisible = false;
                SetTheme(Global.IsDarkTheme);
            }

            internal void SetTheme(bool dark)
            {
                fill = new SolidColorBrush(dark ? Color.FromArgb(100, 65, 155, 255) : Color.FromArgb(105, 255, 184, 0));
                fill.Freeze();
                outline = new Pen(new SolidColorBrush(dark ? Color.FromRgb(105, 185, 255) : Color.FromRgb(195, 127, 0)), 1);
                outline.Freeze();
                InvalidateVisual();
            }

            internal void Refresh()
            {
                if (!box.IsArrangeValid || !box.IsMeasureValid)
                    return;
                var nextViewport = new Rect(box.RenderSize);
                var presenter = FindPresenter(box);
                if (presenter != null)
                    nextViewport = new Rect(presenter.TranslatePoint(new Point(), box), presenter.RenderSize);
                var next = new List<Rect>();
                if (!box.Selection.IsEmpty && nextViewport.Width > 0 && nextViewport.Height > 0)
                {
                    // A regex may match megabytes. Start at the viewport, and walk display
                    // lines rather than every character, keeping live RX rendering bounded.
                    var position = box.GetPositionFromPoint(new Point(nextViewport.Left + 1, nextViewport.Top + 1), true);
                    var line = position?.GetLineStartPosition(0);
                    for (var count = 0; line != null && count < 2048; count++)
                    {
                        var lineRect = line.GetCharacterRect(LogicalDirection.Forward);
                        if (lineRect.IsEmpty || lineRect.Top > nextViewport.Bottom)
                            break;
                        var nextLine = line.GetLineStartPosition(1);
                        var lineEnd = nextLine ?? box.Document.ContentEnd;
                        if (line.CompareTo(box.Selection.End) >= 0)
                            break;
                        if (lineEnd.CompareTo(box.Selection.Start) > 0)
                        {
                            var start = line.CompareTo(box.Selection.Start) < 0 ? box.Selection.Start : line;
                            var end = lineEnd.CompareTo(box.Selection.End) > 0 ? box.Selection.End : lineEnd;
                            var startRect = start.GetCharacterRect(LogicalDirection.Forward);
                            var endRect = end.GetCharacterRect(LogicalDirection.Backward);
                            if (!startRect.IsEmpty && !endRect.IsEmpty)
                            {
                                var right = Math.Abs(startRect.Top - endRect.Top) < 2 ? endRect.Right : nextViewport.Right;
                                var rectangle = new Rect(Math.Min(startRect.Left, right), startRect.Top,
                                    Math.Max(2, Math.Abs(right - startRect.Left)), Math.Max(1, startRect.Height));
                                rectangle.Intersect(nextViewport);
                                if (!rectangle.IsEmpty)
                                    next.Add(rectangle);
                            }
                        }
                        if (nextLine == null || nextLine.CompareTo(line) <= 0)
                            break;
                        line = nextLine;
                    }
                }
                if (viewport != nextViewport || !rectangles.SequenceEqual(next))
                {
                    viewport = nextViewport;
                    rectangles = next;
                    InvalidateVisual();
                }
            }

            private static ScrollContentPresenter FindPresenter(DependencyObject parent)
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                {
                    var child = VisualTreeHelper.GetChild(parent, index);
                    if (child is ScrollContentPresenter presenter)
                        return presenter;
                    var nested = FindPresenter(child);
                    if (nested != null)
                        return nested;
                }
                return null;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                if (viewport.IsEmpty)
                    return;
                drawingContext.PushClip(new RectangleGeometry(viewport));
                foreach (var rectangle in rectangles)
                    drawingContext.DrawRectangle(fill, outline, rectangle);
                drawingContext.Pop();
            }
        }

        private void UpdateStatus()
        {
            if (StatusText == null)
                return;
            var count = result?.Hits.Count ?? 0;
            PreviousButton.IsEnabled = !searching && (count > 0 || dirty);
            NextButton.IsEnabled = PreviousButton.IsEnabled;
            RefreshButton.IsEnabled = IsSearchActive && !searching;
            var prefix = string.IsNullOrWhiteSpace(contextLabel) ? string.Empty : contextLabel + " · ";
            var text = searching ? "查找中…" : string.IsNullOrEmpty(QueryBox.Text) ? "输入内容查找" :
                count == 0 ? "未找到" : (selectedIndex + 1) + " / " + count + (result.LimitReached ? "+" : string.Empty);
            if (!searching && snapshot?.Truncated == true)
                text += " · 最近部分日志";
            text += dirty ? " · 日志已更新，请刷新" : " · 仅暂停滚动";
            StatusText.Text = prefix + text;
            StatusText.ToolTip = "搜索当前界面保留的日志文字，不影响接收和保存。最多搜索最近 1,048,576 个字符、显示 2000 项；HEX 原始数据及跨串口筛选请切换到时间线页。";
        }

        private void QueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.F3)
            {
                Move((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
            }
        }

        private void PreviousClicked(object sender, RoutedEventArgs e) => Move(-1);
        private void NextClicked(object sender, RoutedEventArgs e) => Move(1);
        private void CloseClicked(object sender, RoutedEventArgs e) => Close();
        private void RefreshClicked(object sender, RoutedEventArgs e) => Search();

        internal sealed class DocumentSnapshot
        {
            private sealed class Segment
            {
                public int Index;
                public string Text;
                public TextPointer Start;
                public TextPointer End;
                public bool IsText;
            }

            private readonly List<Segment> segments = new List<Segment>();
            public string Text { get; private set; }
            public bool Truncated { get; private set; }
            public FlowDocument Document { get; private set; }

            internal static DocumentSnapshot Capture(FlowDocument document)
            {
                var result = new DocumentSnapshot { Document = document };
                var remaining = LogTextSearch.MaximumCharacters;
                var timer = Stopwatch.StartNew();
                var pointer = document.ContentEnd;
                // Walk backwards so bounded searches include the newest retained serial log.
                while (pointer != null && pointer.CompareTo(document.ContentStart) > 0)
                {
                    if (remaining <= 0 || result.segments.Count >= 50000 || timer.ElapsedMilliseconds > 100)
                    {
                        result.Truncated = true;
                        break;
                    }
                    var context = pointer.GetPointerContext(LogicalDirection.Backward);
                    if (context == TextPointerContext.Text)
                    {
                        var text = pointer.GetTextInRun(LogicalDirection.Backward);
                        var length = Math.Min(remaining, text.Length);
                        var start = pointer.GetPositionAtOffset(-length, LogicalDirection.Forward);
                        result.segments.Add(new Segment
                        {
                            Text = text.Substring(text.Length - length), Start = start, End = pointer, IsText = true
                        });
                        remaining -= length;
                        pointer = start;
                        continue;
                    }
                    var previous = pointer.GetNextContextPosition(LogicalDirection.Backward);
                    var element = pointer.GetAdjacentElement(LogicalDirection.Backward);
                    // A line break is one display character. Paragraph boundaries are also searchable newlines.
                    if (context == TextPointerContext.ElementEnd && (element is LineBreak || element is Paragraph))
                    {
                        result.segments.Add(new Segment { Text = "\n", Start = previous, End = pointer });
                        remaining--;
                    }
                    pointer = previous;
                }
                result.segments.Reverse();
                var textBuilder = new StringBuilder();
                foreach (var segment in result.segments)
                {
                    segment.Index = textBuilder.Length;
                    textBuilder.Append(segment.Text);
                }
                result.Text = textBuilder.ToString();
                return result;
            }

            internal TextPointer PointerAt(int index, bool endBias)
            {
                if (segments.Count == 0)
                    return null;
                foreach (var segment in segments)
                {
                    var end = segment.Index + segment.Text.Length;
                    if (index < end || (endBias && index == end))
                    {
                        var offset = Math.Max(0, index - segment.Index);
                        return segment.IsText ? segment.Start.GetPositionAtOffset(offset, LogicalDirection.Forward)
                            : offset == 0 ? segment.Start : segment.End;
                    }
                }
                return segments.Last().End;
            }
        }
    }
}
