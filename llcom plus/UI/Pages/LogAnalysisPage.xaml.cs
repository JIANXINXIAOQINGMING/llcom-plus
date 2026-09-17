using llcom_plus.Tools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace llcom_plus.Pages
{
    public partial class LogAnalysisPage : Page
    {
        private readonly HashSet<long> bookmarks = new HashSet<long>();
        private readonly DispatcherTimer timer;
        private IReadOnlyList<SerialTraceRow> result = Array.Empty<SerialTraceRow>();
        private CancellationTokenSource searchCancellation;
        private long renderedId = -1;
        private bool dirty = true, paused, searching;
        private DateTime? jumpTime;
        public LogAnalysisPage()
        {
            InitializeComponent();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (s, e) => Refresh();
            PortBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(FilterChanged));
        }
        private void Page_Loaded(object sender, RoutedEventArgs e) { dirty = true; timer.Start(); Refresh(); }
        private void Page_Unloaded(object sender, RoutedEventArgs e) { timer.Stop(); searchCancellation?.Cancel(); }
        internal void FocusQuery()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        internal void FocusSearch(string port = null, DateTime? timestamp = null, bool power = false)
        {
            if (port != null) { PortBox.Text = string.IsNullOrWhiteSpace(port) ? "全部串口" : port; SearchBox.Clear(); BookmarksOnly.IsChecked = false; }
            KindBox.SelectedIndex = power ? 6 : 0;
            jumpTime = timestamp;
            paused = timestamp.HasValue;
            PauseButton.Content = paused ? "继续视图" : "暂停视图";
            dirty = true;
            Dispatcher.BeginInvoke(new Action(() => { SearchBox.Focus(); SearchBox.SelectAll(); Refresh(); }));
        }
        private void FilterChanged(object sender, RoutedEventArgs e) { dirty = true; }
        private void Search_Click(object sender, RoutedEventArgs e) { dirty = true; Refresh(); }
        private void PortsOpened(object sender, EventArgs e)
        {
            var text = PortBox.Text;
            PortBox.ItemsSource = new[] { "全部串口" }.Concat(SerialTraceHub.GetKnownPorts());
            PortBox.Text = text;
        }
        private async void Refresh()
        {
            if (!IsLoaded || searching || (!dirty && (paused || renderedId == SerialTraceHub.LastId))) return;
            dirty = false;
            searching = true;
            searchCancellation?.Dispose();
            var cancellation = searchCancellation = new CancellationTokenSource();
            var snapshot = SerialTraceHub.Snapshot();
            var lastId = snapshot.LastOrDefault()?.Id ?? 0;
            var query = SearchBox.Text;
            var mode = (TraceSearchMode)Math.Max(0, ModeBox.SelectedIndex);
            var port = PortBox.Text.Trim();
            if (port == "全部串口") port = "";
            var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var marks = new HashSet<long>(bookmarks);
            var only = BookmarksOnly.IsChecked == true;
            try
            {
                var found = await Task.Run(() => SerialTraceQuery.Find(snapshot, query, mode, port, kind, marks, only, cancellation.Token));
                if (cancellation.IsCancellationRequested || !IsLoaded) return;
                result = found;
                renderedId = lastId;
                var selected = new HashSet<long>(Records.SelectedItems.Cast<SerialTraceRow>().Select(row => row.Id));
                var visible = found.Skip(Math.Max(0, found.Count - 2000)).ToArray();
                SerialTraceRow nearest = null;
                if (jumpTime.HasValue)
                {
                    nearest = found.OrderBy(row => Math.Abs((row.Entry.Timestamp - jumpTime.Value).TotalMilliseconds)).FirstOrDefault();
                    if (nearest != null && !visible.Contains(nearest))
                    {
                        var index = found.ToList().IndexOf(nearest);
                        visible = found.Skip(Math.Max(0, index - 1000)).Take(2000).ToArray();
                    }
                    jumpTime = null;
                }
                Records.ItemsSource = visible;
                foreach (var row in visible.Where(row => selected.Contains(row.Id))) Records.SelectedItems.Add(row);
                if (nearest != null) { Records.SelectedItem = nearest; Records.ScrollIntoView(nearest); }
                bookmarks.RemoveWhere(id => snapshot.Count > 0 && id < snapshot[0].Id);
                Status.Text = $"匹配 {found.Count} 条，显示 {visible.Length} 条；已滚动淘汰 {SerialTraceHub.DroppedCount} 条。" +
                    (paused ? " 视图已暂停，后台仍在记录。" : " 实时更新。");
                if (snapshot.Count == 0) Status.Text = "暂无事件。连接串口后，收发、引脚和通知事件会出现在这里。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status.Text = ex.Message; renderedId = lastId; }
            finally { searching = false; }
        }
        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            paused = !paused;
            PauseButton.Content = paused ? "继续视图" : "暂停视图";
            dirty = true; Refresh();
        }
        private void Bookmark_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in Records.SelectedItems.Cast<SerialTraceRow>())
                if (!bookmarks.Add(row.Id)) bookmarks.Remove(row.Id);
            dirty = true; Refresh();
        }
        private void Records_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = Records.SelectedItems.Cast<SerialTraceRow>().OrderBy(row => row.Entry.Timestamp).ToArray();
            if (selected.Length == 0) { SelectionText.Text = "选择记录查看详情 / Select an event"; Details.Clear(); return; }
            var first = selected[0];
            SelectionText.Text = selected.Length == 2
                ? $"Δt = {(selected[1].Entry.Timestamp - first.Entry.Timestamp).TotalMilliseconds:F3} ms（主机时间）"
                : $"已选 {selected.Length} 条 · {first.Entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}";
            Details.Text = first.Entry.ConnectionId + Environment.NewLine + first.Text + Environment.NewLine + "HEX: " + first.Hex;
            if (first.Entry.Kind == SerialTraceKind.Tx)
            {
                var rx = SerialTraceHub.Snapshot().FirstOrDefault(entry => entry.Id > first.Id &&
                    entry.Kind == SerialTraceKind.Rx && entry.ConnectionId == first.Entry.ConnectionId);
                SelectionText.Text += rx == null ? " · 此后尚无 RX" :
                    $" · 后续首条 RX 间隔 {(rx.Timestamp - first.Entry.Timestamp).TotalMilliseconds:F3} ms（不代表匹配回复）";
            }
        }
        private void Records_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataColumn != null) DataColumn.Width = Math.Max(150, e.NewSize.Width - 280);
        }
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var selected = Records.SelectedItems.Cast<SerialTraceRow>().ToArray();
            // Two selections explicitly define a fault section of the current filter.
            var rows = selected.Length == 2 ? result.Where(row => row.Id >= selected.Min(x => x.Id) &&
                row.Id <= selected.Max(x => x.Id)).ToArray() : result.ToArray();
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "serial-timeline.csv" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true))) SerialTraceQuery.Export(writer, rows);
                Status.Text = $"已导出 {rows.Length} 条（选择两条时，仅导出其间的筛选结果）。";
            }
            catch (Exception ex) { Status.Text = "导出失败 / Export failed: " + ex.Message; }
        }
    }
}
