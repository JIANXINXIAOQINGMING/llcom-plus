using llcom_plus.Tools;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace llcom_plus.Pages
{
    public partial class PlotPage : Page
    {
        private readonly DispatcherTimer renderTimer;
        private readonly ObservableCollection<SeriesRow> seriesRows = new ObservableCollection<SeriesRow>();
        private PlotConfiguration configuration = new PlotConfiguration();
        private PlotCaptureSession capture;
        private PlotWorkflow stoppedWorkflow;
        private bool initialized, disposed, paused, resettingUi;
        private long lastRevision = -1;
        private bool forceRender = true;
        private string transientMessage = "";
        private bool English => (Global.setting?.language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        private string L(string chinese, string english) => English ? english : chinese;

        public PlotPage()
        {
            InitializeComponent();
            renderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            renderTimer.Tick += RenderTimer_Tick;
            SeriesGrid.ItemsSource = seriesRows;
            ColorColumn.ItemsSource = new[] { "#007AFF", "#E88422", "#20A46A", "#D25091", "#8565D0", "#14A7AC", "#D55E50", "#9A9D26", "#657BAC", "#9F785A" };
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (disposed) return;
            if (!initialized)
            {
                initialized = true;
                configuration = PlotSettingsStore.Load(Global.ProfilePath, out string warning);
                stoppedWorkflow = new PlotWorkflow(configuration);
                SetLocalizedText(); LoadConfigurationUi();
                Global.ThemeChanged += Global_ThemeChanged;
                Global.ProgramClosedEvent += Global_ProgramClosed;
                ScriptEnv.ScriptApis.LinePlotAdd += Script_LinePlotAdd;
                ApplyApplicationTheme();
                if (!string.IsNullOrEmpty(warning))
                    transientMessage = L("曲线配置读取失败，原文件已保留：", "Could not load plot settings; original preserved: ") + warning;
            }
            renderTimer.Start(); RenderCurrent(true);
        }

        // Navigation pauses rendering, not an explicitly started capture session.
        private void Page_Unloaded(object sender, RoutedEventArgs e) => renderTimer.Stop();
        private void Global_ProgramClosed(object sender, EventArgs e)
        {
            disposed = true;
            capture?.Dispose(); capture = null;
            Global.ThemeChanged -= Global_ThemeChanged;
            Global.ProgramClosedEvent -= Global_ProgramClosed;
            ScriptEnv.ScriptApis.LinePlotAdd -= Script_LinePlotAdd;
            if (Dispatcher.CheckAccess()) renderTimer.Stop();
            else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => renderTimer.Stop()));
        }

        private void SetLocalizedText()
        {
            resettingUi = true;
            SourceLabel.Text = L("数据来源", "Source");
            FormatCombo.ItemsSource = new[] { L("单个数值", "Single value"), "CSV", "key=value", "JSON", L("正则表达式", "Regular expression"), L("脚本 apiAddPoint", "Script apiAddPoint") };
            SeparatorCombo.ItemsSource = new[] { L("逗号 ,", "Comma ,"), L("分号 ;", "Semicolon ;"), "Tab" };
            FitButton.Content = L("查看全局", "Fit all"); ClearButton.Content = L("清空曲线", "Clear chart");
            ExportButton.Content = L("导出 CSV", "Export CSV"); FollowCheck.Content = L("跟随最新数据", "Follow latest");
            ConfigurationExpander.Header = L("解析和曲线设置", "Parsing and series");
            SeparatorLabel.Text = L("分隔符", "Separator"); PatternLabel.Text = L("正则表达式", "Regex");
            ApplyButton.Content = L("应用配置", "Apply settings"); DiscoverButton.Content = L("重新识别字段", "Rediscover fields");
            ApplyHint.Text = L("更改解析配置会开始一段新曲线，不修改串口设置。", "Changing parsing starts a new chart; serial settings are unchanged.");
            VisibleColumn.Header = L("显示", "Show"); KeyColumn.Header = L("字段", "Field"); NameColumn.Header = L("名称", "Name");
            UnitColumn.Header = L("单位", "Unit"); ColorColumn.Header = L("颜色", "Color"); CurrentColumn.Header = L("当前", "Now");
            MinimumColumn.Header = L("最小", "Min"); MaximumColumn.Header = L("最大", "Max");
            EmptyTitle.Text = L("还没有曲线数据", "No chart data yet");
            resettingUi = false; UpdateButtons();
        }

        private void LoadConfigurationUi()
        {
            resettingUi = true;
            RefreshPorts(); SourceCombo.Text = configuration.SourcePort;
            FormatCombo.SelectedIndex = (int)configuration.Format;
            SeparatorCombo.SelectedIndex = configuration.Separator == ";" ? 1 : configuration.Separator == "\t" ? 2 : 0;
            PatternBox.Text = configuration.Pattern;
            seriesRows.Clear();
            foreach (var options in configuration.Series) seriesRows.Add(new SeriesRow(options.Copy()));
            resettingUi = false; UpdateFormatHint();
        }

        private void RefreshPorts()
        {
            var current = SourceCombo.Text;
            SourceCombo.ItemsSource = SerialTraceHub.GetKnownPorts();
            SourceCombo.Text = current;
        }
        private void SourceCombo_DropDownOpened(object sender, EventArgs e) => RefreshPorts();
        private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        { if (!resettingUi && initialized) UpdateFormatHint(); }

        private void UpdateFormatHint()
        {
            var format = (PlotInputFormat)Math.Max(0, FormatCombo.SelectedIndex);
            bool script = format == PlotInputFormat.Script;
            SourceCombo.IsEnabled = !script;
            SeparatorCombo.Visibility = SeparatorLabel.Visibility = format == PlotInputFormat.Csv ? Visibility.Visible : Visibility.Collapsed;
            PatternBox.Visibility = PatternLabel.Visibility = format == PlotInputFormat.Regex ? Visibility.Visible : Visibility.Collapsed;
            string[] hints = English
                ? new[] { "Example: 23.5 + newline. Decimal point is '.'; no units in the input.", "Example: 23.5,48,3.7 + newline. Columns are numbered from 1; numeric fields only.", "Example: temp=23.5,humidity=48 + newline. Field names are case-sensitive.", "Example: {\"temp\":23.5,\"humidity\":48} + newline. Top-level numeric fields or numeric arrays.", "Use numeric capture groups, e.g. temp=(?<temp>-?\\d+(?:\\.\\d+)?). Each record ends in a newline.", "Advanced mode: only apiAddPoint(value, line) adds points (line: 0–9). This does not run or change any script." }
                : new[] { "示例：23.5 加换行。小数点使用英文句点，输入不要带单位。", "示例：23.5,48,3.7 加换行。字段从第 1 列编号，只接受数值列。", "示例：temp=23.5,humidity=48 加换行。字段名区分大小写。", "示例：{\"temp\":23.5,\"humidity\":48} 加换行。读取最外层数值字段或数值数组。", "使用数值捕获组，例如 temp=(?<temp>-?\\d+(?:\\.\\d+)?)。每条记录以换行结束。", "高级模式：仅由 apiAddPoint(数值, 线号) 添加数据（线号 0～9）。本页不会运行或修改脚本。" };
            FormatHint.Text = hints[(int)format] + L(" 最多 10 条曲线，每条保留最近 5000 点。", " Up to 10 series; latest 5000 points per series.");
            EmptyHint.Text = script
                ? L("选择脚本模式并开始采集，再通过现有接收脚本调用 apiAddPoint。", "Start script capture, then call apiAddPoint from your existing receive script.")
                : L("选择串口和数据格式，开始采集。每条数据请以换行结束。", "Select a COM port and format, then start capture. End each record with a newline.");
        }

        private PlotConfiguration ReadConfiguration(bool rediscover)
        {
            SeriesGrid.CommitEdit(DataGridEditingUnit.Cell, true); SeriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var next = new PlotConfiguration
            {
                SourcePort = (SourceCombo.Text ?? "").Trim(), Format = (PlotInputFormat)Math.Max(0, FormatCombo.SelectedIndex),
                Separator = SeparatorCombo.SelectedIndex == 1 ? ";" : SeparatorCombo.SelectedIndex == 2 ? "\t" : ",",
                Pattern = PatternBox.Text ?? ""
            };
            if (!rediscover && SameParser(next, configuration)) next.Series = seriesRows.Select(row => row.ToOptions()).ToList();
            next.Validate();
            if (next.Format != PlotInputFormat.Script && string.IsNullOrWhiteSpace(next.SourcePort))
                throw new FormatException(L("请先选择或输入要采集的 COM 口。", "Select or enter the COM port to capture."));
            return next;
        }
        private static bool SameParser(PlotConfiguration first, PlotConfiguration second) =>
            first.Format == second.Format && first.SourcePort == second.SourcePort && first.Separator == second.Separator && first.Pattern == second.Pattern;

        private bool ApplyConfiguration(bool rediscover, bool start)
        {
            try
            {
                var next = ReadConfiguration(rediscover);
                PlotSettingsStore.Save(Global.ProfilePath, next);
                var currentWorkflow = capture?.Workflow ?? stoppedWorkflow;
                bool canRetain = !rediscover && SameParser(next, configuration) && currentWorkflow != null &&
                    currentWorkflow.Snapshot().Series.Select(item => item.Options.Key).SequenceEqual(next.Series.Select(item => item.Key)) &&
                    ((capture != null) == start);
                if (canRetain) currentWorkflow.UpdateSeriesOptions(next.Series);
                else
                {
                    capture?.Dispose(); capture = null;
                    stoppedWorkflow = new PlotWorkflow(next);
                    if (start) capture = new PlotCaptureSession(next);
                }
                configuration = next;
                lastRevision = -1; forceRender = true;
                LoadConfigurationUi(); UpdateButtons();
                transientMessage = L("配置已保存。", "Settings saved."); RenderCurrent(true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException)
            {
                transientMessage = L("配置未应用：", "Settings not applied: ") + ex.Message;
                ErrorText.Text = transientMessage; return false;
            }
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (capture == null) { paused = false; ApplyConfiguration(false, true); return; }
            stoppedWorkflow = capture.Workflow; capture.Dispose(); capture = null;
            UpdateButtons(); RenderCurrent(!paused);
        }
        private void ApplyButton_Click(object sender, RoutedEventArgs e) => ApplyConfiguration(false, capture != null);
        private void DiscoverButton_Click(object sender, RoutedEventArgs e) => ApplyConfiguration(true, capture != null);
        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            paused = !paused; UpdateButtons();
            if (!paused) RenderCurrent(true);
        }
        private void UpdateButtons()
        {
            CaptureButton.Content = capture == null ? L("开始采集", "Start capture") : L("停止采集", "Stop capture");
            PauseButton.Content = paused ? L("恢复显示", "Resume view") : L("暂停显示", "Pause view");
            PauseButton.ToolTip = L("只冻结画面，后台仍采集；恢复后显示最新数据。", "Freeze the view only. Capture continues; resume shows the latest data.");
            ClearButton.ToolTip = L("只清空曲线和解析缓冲，不清除串口日志。", "Clear chart and parsing buffer only; serial logs are unchanged.");
        }

        private void Script_LinePlotAdd(object sender, Model.LinePlotPoint point)
        {
            var current = capture;
            if (current != null && configuration.Format == PlotInputFormat.Script && !disposed)
                current.Workflow.AddScriptPoint(point.N, point.Line, DateTime.Now);
        }
        private void RenderTimer_Tick(object sender, EventArgs e) => RenderCurrent(false);
        private void RenderCurrent(bool force)
        {
            if (!initialized || disposed) return;
            var snapshot = (capture?.Workflow ?? stoppedWorkflow)?.Snapshot();
            if (snapshot == null) return;
            long dropped = capture?.DroppedCount ?? 0;
            StatusText.Text = (capture == null ? L("已停止", "Stopped") : paused ? L("显示已暂停 · 仍在采集", "View paused · capturing") : L("采集中", "Capturing")) +
                "  |  " + L("有效记录 ", "Accepted ") + snapshot.AcceptedLines + "  ·  " + L("跳过 ", "Skipped ") + snapshot.RejectedLines +
                (snapshot.PendingCharacters > 0 ? "  ·  " + L("等待换行 ", "Waiting for newline ") + snapshot.PendingCharacters : "") +
                (dropped > 0 ? "  ·  " + L("过载丢弃 ", "Overflow drops ") + dropped : "") +
                "  |  " + L("主机接收时间（非硬件采样时间）", "Host receive time (not hardware sample time)");
            if (paused && !force) return;
            if (!force && !forceRender && snapshot.Revision == lastRevision) return;
            lastRevision = snapshot.Revision; forceRender = false;
            PreviewText.Text = snapshot.Preview.Length > 0 ? L("解析预览：", "Preview: ") + snapshot.Preview : "";
            ErrorText.Text = snapshot.Error.Length > 0 ? L("未绘制：", "Not plotted: ") + snapshot.Error : transientMessage;
            foreach (var item in snapshot.Series)
            {
                var row = seriesRows.FirstOrDefault(candidate => candidate.Key == item.Options.Key);
                if (row == null) { row = new SeriesRow(item.Options); seriesRows.Add(row); }
                row.UpdateStats(item.Samples);
            }

            var limits = Plot.Plot.GetAxisLimits(); Plot.Plot.Clear();
            int plotted = 0;
            foreach (var item in snapshot.Series)
            {
                if (!item.Options.Visible || item.Samples.Length == 0) continue;
                var xs = item.Samples.Select(sample => sample.Timestamp.ToOADate()).ToArray();
                var ys = item.Samples.Select(sample => sample.Value).ToArray();
                string label = string.IsNullOrWhiteSpace(item.Options.Name) ? item.Options.Key : item.Options.Name;
                if (!string.IsNullOrWhiteSpace(item.Options.Unit)) label += " (" + item.Options.Unit + ")";
                Plot.Plot.AddScatter(xs, ys, color: System.Drawing.ColorTranslator.FromHtml(item.Options.Color),
                    lineWidth: 1.5f, markerSize: xs.Length == 1 ? 5 : 0, label: label);
                plotted++;
            }
            Plot.Plot.XAxis.DateTimeFormat(true); Plot.Plot.XLabel(L("主机接收时间", "Host receive time"));
            Plot.Plot.Legend(plotted > 0);
            EmptyOverlay.Visibility = plotted == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = snapshot.Series.Any(item => item.Samples.Length > 0)
                ? L("所有曲线已隐藏", "All series are hidden") : L("还没有曲线数据", "No chart data yet");
            if (plotted > 0)
            {
                if (force || FollowCheck.IsChecked == true) Plot.Plot.AxisAuto();
                else Plot.Plot.SetAxisLimits(limits);
            }
            else Plot.Plot.SetAxisLimits(DateTime.Now.AddSeconds(-10).ToOADate(), DateTime.Now.ToOADate(), -1, 1);
            Plot.Render();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (capture != null) capture.Clear(); else stoppedWorkflow?.Clear();
            transientMessage = L("已清空曲线，串口日志不受影响。", "Chart cleared; serial logs are unchanged.");
            forceRender = true;
            Dispatcher.BeginInvoke(new Action(() => RenderCurrent(true)), DispatcherPriority.Background);
        }
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (paused) { Plot.Plot.AxisAuto(); Plot.Render(); }
            else RenderCurrent(true);
        }
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = (capture?.Workflow ?? stoppedWorkflow)?.Snapshot();
            if (snapshot == null || !snapshot.Series.Any(item => item.Samples.Length > 0))
            {
                transientMessage = L("没有可以导出的数据。", "No data to export."); ErrorText.Text = transientMessage; return;
            }
            var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "plot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", AddExtension = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                File.WriteAllText(dialog.FileName, PlotWorkflow.ToCsv(snapshot), new UTF8Encoding(true));
                transientMessage = L("已导出当前保留的数据（含隐藏曲线）。", "Exported retained data, including hidden series.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { transientMessage = L("导出失败：", "Export failed: ") + ex.Message; }
            ErrorText.Text = transientMessage;
        }

        private void Global_ThemeChanged(object sender, EventArgs e)
        {
            if (disposed || Dispatcher.HasShutdownStarted) return;
            if (Dispatcher.CheckAccess()) ApplyApplicationTheme(); else Dispatcher.BeginInvoke(new Action(ApplyApplicationTheme));
        }
        private void ApplyApplicationTheme()
        {
            if (disposed) return;
            Plot.Plot.Style(Global.IsDarkTheme ? ScottPlot.Style.Black : ScottPlot.Style.Light1);
            if (Global.IsDarkTheme)
            {
                // Keep ticks and labels legible against the application's dark
                // surface instead of inheriting the preset's dim grey axes.
                Plot.Plot.Style(
                    figureBackground: System.Drawing.Color.FromArgb(19, 29, 41),
                    dataBackground: System.Drawing.Color.FromArgb(19, 29, 41),
                    grid: System.Drawing.Color.FromArgb(52, 69, 89),
                    tick: System.Drawing.Color.FromArgb(208, 220, 232),
                    axisLabel: System.Drawing.Color.FromArgb(208, 220, 232),
                    titleLabel: System.Drawing.Color.FromArgb(208, 220, 232));
            }
            forceRender = true;
            if (IsLoaded && !paused) RenderCurrent(true); else Plot.Render();
        }

        private sealed class SeriesRow : INotifyPropertyChanged
        {
            private readonly PlotSeriesOptions options;
            public SeriesRow(PlotSeriesOptions value) { options = value.Copy(); }
            public string Key => options.Key;
            public string Name { get => options.Name; set => options.Name = value; }
            public string Unit { get => options.Unit; set => options.Unit = value; }
            public string Color { get => options.Color; set => options.Color = value; }
            public bool Visible { get => options.Visible; set => options.Visible = value; }
            public string Current { get; private set; } = "—";
            public string Minimum { get; private set; } = "—";
            public string Maximum { get; private set; } = "—";
            public event PropertyChangedEventHandler PropertyChanged;
            internal PlotSeriesOptions ToOptions() => options.Copy();
            internal void UpdateStats(PlotSample[] values)
            {
                Current = values.Length == 0 ? "—" : values[values.Length - 1].Value.ToString("G6", CultureInfo.InvariantCulture);
                Minimum = values.Length == 0 ? "—" : values.Min(item => item.Value).ToString("G6", CultureInfo.InvariantCulture);
                Maximum = values.Length == 0 ? "—" : values.Max(item => item.Value).ToString("G6", CultureInfo.InvariantCulture);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Minimum)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Maximum)));
            }
        }
    }
}
