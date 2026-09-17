using llcom_plus.Model;
using llcom_plus.Tools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace llcom_plus.Pages
{
    /// <summary>
    /// CircularSendPage.xaml 的交互逻辑
    /// </summary>
    public partial class CircularSendPage : Page
    {
        private const int DefaultRowCount = 10;
        private const int ZeroDelayYieldBatchSize = 64;
        private bool suppressSave = false;
        private bool suppressSelectionHeader = false;
        private CancellationTokenSource loopCts = null;
        internal bool IsWorkflowRunning => loopCts != null;
        private string lastStorageNotificationMessage = string.Empty;
        private ActiveSerialTarget runTarget;
        private const int MaximumReportRows = 2000;
        private readonly List<SerialTestReportRow> reportRows = new List<SerialTestReportRow>();
        private long reportTotal, reportPassed, reportFailed, reportSent, reportCancelled;

        public ObservableCollection<CircularSendItem> Items { get; } = new ObservableCollection<CircularSendItem>();
        public ObservableCollection<CircularTestResultItem> ReportItems { get; } = new ObservableCollection<CircularTestResultItem>();

        public CircularSendPage()
        {
            InitializeComponent();
            DataContext = this;
            LoadItems();
        }

        private string StoragePath => Path.Combine(Global.ProfilePath, "circular_send.json");

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            SetRunning(loopCts != null);
            UpdateSelectionHeader();
        }

        private void CommandDataGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (CommandDataGrid == null || CommandColumn == null || CommandDataGrid.ActualWidth <= 0)
                return;

            var fixedWidth = CommandDataGrid.Columns
                .Where(column => !ReferenceEquals(column, CommandColumn))
                .Sum(column => column.Width.IsAbsolute ? column.Width.Value : Math.Max(0, column.ActualWidth));
            var chromeAllowance = SystemParameters.VerticalScrollBarWidth + 4;
            var commandWidth = Math.Max(
                CommandColumn.MinWidth,
                CommandDataGrid.ActualWidth - fixedWidth - chromeAllowance);

            if (Math.Abs(CommandColumn.ActualWidth - commandWidth) > 0.5)
                CommandColumn.Width = new DataGridLength(commandWidth, DataGridLengthUnitType.Pixel);
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            StopLoop();
            SaveItems();
        }

        private void LoadItems()
        {
            suppressSave = true;
            try
            {
                Items.Clear();
                if (File.Exists(StoragePath))
                {
                    var loaded = JsonConvert.DeserializeObject<List<CircularSendItem>>(File.ReadAllText(StoragePath));
                    if (loaded != null)
                    {
                        foreach (var item in loaded.Where(item => item != null))
                            AddItem(item);
                    }
                }

                if (Items.Count == 0)
                {
                    for (var i = 0; i < DefaultRowCount; i++)
                        AddItem(new CircularSendItem());
                }

                RefreshIndexes();
            }
            catch (Exception ex)
            {
                Items.Clear();
                for (var i = 0; i < DefaultRowCount; i++)
                    AddItem(new CircularSendItem());
                RefreshIndexes();
                ShowStorageError(ex);
            }
            finally
            {
                suppressSave = false;
            }
        }

        private void AddItem(CircularSendItem item)
        {
            item.Changed += Item_Changed;
            Items.Add(item);
            UpdateSelectionHeader();
        }

        private void Item_Changed(object sender, EventArgs e)
        {
            UpdateSelectionHeader();
            SaveItems();
        }

        private void SaveItems()
        {
            if (suppressSave)
                return;

            var tempPath = StoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Global.ProfilePath);
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(Items, Formatting.Indented));
                File.Copy(tempPath, StoragePath, true);
                StorageErrorTextBlock.Text = string.Empty;
                StorageErrorTextBlock.Visibility = Visibility.Collapsed;
                lastStorageNotificationMessage = string.Empty;
            }
            catch (Exception ex)
            {
                ShowStorageError(ex);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private void ShowStorageError(Exception ex)
        {
            var format = TryFindResource("CircularSendSaveFailedFormat") as string ?? "循环发送配置保存/读取失败：{0}";
            StorageErrorTextBlock.Text = string.Format(format, ex.Message);
            StorageErrorTextBlock.Visibility = Visibility.Visible;
            if (!string.Equals(lastStorageNotificationMessage, ex.Message, StringComparison.Ordinal))
            {
                lastStorageNotificationMessage = ex.Message;
                var source = TryFindResource("NotificationCircularSendSource") as string ?? "循环发送配置";
                Global.PublishNotification(
                    string.Format(
                        TryFindResource("NotificationOperationFailedTitleFormat") as string ?? "{0} 失败",
                        source),
                    ex.Message,
                    AppNotificationLevel.Error,
                    category: AppNotificationCategory.Task);
            }
        }

        private void RefreshIndexes()
        {
            for (var i = 0; i < Items.Count; i++)
                Items[i].Index = i + 1;
        }

        private void AddRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (loopCts != null) return;
            AddItem(new CircularSendItem());
            RefreshIndexes();
            SaveItems();
        }

        private void RemoveRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (loopCts != null)
                return;

            if (!((sender as Button)?.Tag is CircularSendItem item))
                return;

            item.Changed -= Item_Changed;
            Items.Remove(item);
            if (Items.Count == 0)
                AddItem(new CircularSendItem());
            RefreshIndexes();
            UpdateSelectionHeader();
            SaveItems();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (loopCts != null)
                return;

            suppressSave = true;
            try
            {
                foreach (var item in Items)
                    item.Changed -= Item_Changed;
                Items.Clear();
                for (var i = 0; i < DefaultRowCount; i++)
                    AddItem(new CircularSendItem());
                RefreshIndexes();
            }
            finally
            {
                suppressSave = false;
            }
            SaveItems();
            StatusTextBlock.Text = TryFindResource("CircularSendCleared") as string ?? "已清空";
        }

        private void EnableAllCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ToggleSelectionFromHeader();
            e.Handled = true;
        }

        private void EnableAllCheckBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space && e.Key != Key.Enter)
                return;

            ToggleSelectionFromHeader();
            e.Handled = true;
        }

        private void ToggleSelectionFromHeader()
        {
            if (suppressSelectionHeader)
                return;

            SetSelection(EnableAllCheckBox.IsChecked != true);
        }

        private void SetSelection(bool selected)
        {
            suppressSave = true;
            try
            {
                foreach (var item in Items)
                    item.IsSelected = selected;
            }
            finally
            {
                suppressSave = false;
            }
            SaveItems();
            UpdateSelectionHeader();
        }

        private void UpdateSelectionHeader()
        {
            if (EnableAllCheckBox == null)
                return;

            var eligibleItems = Items.ToList();
            bool? state = false;
            if (eligibleItems.Count > 0)
            {
                var selectedCount = eligibleItems.Count(item => item.IsSelected);
                state = selectedCount == 0
                    ? false
                    : selectedCount == eligibleItems.Count
                        ? true
                        : (bool?)null;
            }

            suppressSelectionHeader = true;
            try
            {
                EnableAllCheckBox.IsChecked = state;
            }
            finally
            {
                suppressSelectionHeader = false;
            }
        }

        private void ImportQuickSendButton_Click(object sender, RoutedEventArgs e)
        {
            if (loopCts != null) return;
            var imported = 0;
            var seen = new HashSet<string>(
                Items.Where(item => !string.IsNullOrWhiteSpace(item.Command))
                    .Select(item => GetCommandKey(item.Command, item.Hex)),
                StringComparer.OrdinalIgnoreCase);

            suppressSave = true;
            try
            {
                foreach (var item in EnumerateQuickSendItems())
                {
                    var command = item.text?.Trim();
                    if (string.IsNullOrWhiteSpace(command))
                        continue;

                    var key = GetCommandKey(command, item.hex);
                    if (!seen.Add(key))
                        continue;

                    FillOrAddImportedItem(command, item.hex);
                    imported++;
                }
            }
            finally
            {
                suppressSave = false;
            }

            RefreshIndexes();
            SaveItems();
            StatusTextBlock.Text = string.Format(
                TryFindResource("CircularSendImported") as string ?? "已导入 {0} 条",
                imported);
        }

        private void FillOrAddImportedItem(string command, bool hex)
        {
            var blank = Items.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.Command));
            if (blank != null)
            {
                blank.IsSelected = true;
                blank.Command = command;
                blank.Hex = hex;
                blank.DelayMs = "";
                blank.Status = "";
                return;
            }

            AddItem(new CircularSendItem
            {
                IsSelected = true,
                Command = command,
                Hex = hex
            });
        }

        private IEnumerable<ToSendData> EnumerateQuickSendItems()
        {
            var allLists = Global.setting.GetAllQuickSendLists();
            if (allLists == null)
                yield break;

            foreach (var list in allLists)
            {
                if (list == null)
                    continue;

                foreach (var item in list.Where(item => item != null))
                    yield return item;
            }
        }

        private static string GetCommandKey(string command, bool hex)
        {
            return $"{hex}:{command.Trim()}";
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (loopCts != null)
                return;

            if (!TryReadRunTimes(out var runTimes) || !TryReadDefaultDelay(out var defaultDelay))
                return;

            var plan = BuildPlan(defaultDelay);
            if (plan == null || plan.Count == 0)
            {
                Tools.MessageBox.Show(TryFindResource("CircularSendNoCommands") as string ?? "请先勾选至少一条命令");
                return;
            }

            if (!CaptureRunTarget(plan.Any(step => step.Options.Mode != SerialTestMatchMode.None))) return;
            SaveItems();
            ResetReport();
            loopCts = new CancellationTokenSource();
            SetRunning(true);
            try
            {
                await RunLoopAsync(plan, runTimes, loopCts.Token);
                StatusTextBlock.Text = reportFailed > 0 ? "测试完成（有失败步骤）" :
                    TryFindResource("CircularSendDone") as string ?? "发送完成";
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = TryFindResource("CircularSendStopped") as string ?? "已停止";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = TryFindResource("CircularSendFailed") as string ?? "发送失败";
                Tools.MessageBox.Show(ex.Message);
            }
            finally
            {
                loopCts?.Dispose();
                loopCts = null;
                runTarget = null;
                SetRunning(false);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopLoop();
        }

        private void StopLoop()
        {
            loopCts?.Cancel();
        }

        private async Task RunLoopAsync(List<CircularSendStep> plan, int runTimes, CancellationToken token)
        {
            var yieldForZeroDelayLoop = ShouldYieldForZeroDelayLoop(
                runTimes,
                plan.All(step => step.DelayMs == 0));
            var zeroDelayBatchCount = 0;
            var round = 0;
            while (runTimes == 0 || round < runTimes)
            {
                round++;
                for (var i = 0; i < plan.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Global.IsActiveSerialTargetOpen())
                        throw new InvalidOperationException(TryFindResource("CircularSendPortNotOpen") as string ?? "请先打开串口");

                    var step = plan[i];
                    step.Source.Status = string.Format(
                        TryFindResource("CircularSendSendingStatus") as string ?? "第 {0} 轮发送中",
                        round);
                    SerialTestResult result;
                    try
                    {
                        if (step.Options.Mode == SerialTestMatchMode.None)
                        {
                            await SendStepAsync(step, token);
                            result = new SerialTestResult { Success = true, Attempts = 1, Outcome = "Sent",
                                Details = "发送完成（未验证响应）" };
                        }
                        else
                        {
                            if (runTarget == null) throw new InvalidOperationException("请先选择测试串口。");
                            var captured = runTarget;
                            result = await SerialTestRunner.ExecuteAsync(step.Options, captured.Identity,
                                sendToken => Dispatcher.Invoke(new Func<Task>(() => SendStepAsync(step, sendToken))),
                                () => captured.IsOpen, token);
                        }
                        token.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        step.Source.Status = TryFindResource("CircularSendStopped") as string ?? "已停止";
                        AddReport(step, round, new SerialTestResult { Outcome = "Cancelled", Details = "用户停止或页面已离开；未执行后续步骤。" });
                        throw;
                    }
                    catch (Exception ex)
                    {
                        step.Source.Status = TryFindResource("CircularSendFailed") as string ?? "发送失败";
                        AddReport(step, round, new SerialTestResult { Outcome = "SendFailed", Details = ex.Message });
                        throw;
                    }
                    AddReport(step, round, result);
                    step.Source.Status = result.Validated
                        ? (result.Success ? "验证通过" : "验证失败")
                        : string.Format(TryFindResource("CircularSendSentStatus") as string ?? "第 {0} 轮已发送", round);
                    if (!result.Success && !step.Options.ContinueOnFailure)
                        throw new InvalidOperationException($"第 {round} 轮，步骤 #{step.Source.Index}：{result.Details}");
                    StatusTextBlock.Text = string.Format(
                        TryFindResource("CircularSendRunning") as string ?? "第 {0} 轮，#{1}",
                        round,
                        step.Source.Index);

                    var isLast = runTimes > 0 && round == runTimes && i == plan.Count - 1;
                    if (!isLast && step.DelayMs > 0)
                    {
                        zeroDelayBatchCount = 0;
                        await Task.Delay(step.DelayMs, token);
                    }
                    else if (!isLast && yieldForZeroDelayLoop &&
                        ++zeroDelayBatchCount >= ZeroDelayYieldBatchSize)
                    {
                        zeroDelayBatchCount = 0;
                        await Task.Yield();
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
        }

        internal static bool ShouldYieldForZeroDelayLoop(int runTimes, bool allDelaysAreZero)
        {
            return runTimes >= 0 && allDelaysAreZero;
        }

        private async void SendOneButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (!(button?.Tag is CircularSendItem item) || loopCts != null)
                return;

            if (string.IsNullOrWhiteSpace(item.Command))
                return;

            try
            {
                var step = new CircularSendStep(item, item.Command.Trim(), item.Hex, 0);
                if (!CaptureRunTarget(step.Options.Mode != SerialTestMatchMode.None)) return;
                loopCts = new CancellationTokenSource();
                SetRunning(true);
                ResetReport();
                await RunLoopAsync(new List<CircularSendStep> { step }, 1, loopCts.Token);
            }
            catch (OperationCanceledException)
            {
                item.Status = TryFindResource("CircularSendStopped") as string ?? "已停止";
            }
            catch (Exception ex)
            {
                item.Status = TryFindResource("CircularSendFailed") as string ?? "发送失败";
                Tools.MessageBox.Show(ex.Message);
            }
            finally
            {
                loopCts?.Dispose(); loopCts = null; runTarget = null;
                SetRunning(false);
            }
        }

        private async Task SendStepAsync(CircularSendStep step, CancellationToken token)
        {
            var data = step.Hex
                ? Global.Hex2Byte(step.Command)
                : Global.GetEncoding().GetBytes(step.Command);

            var request = new UartSendRequest
            {
                Data = data,
                IsHex = step.Hex,
                ApplySendProcessing = true,
                SessionStringLogOverride = step.Hex ? step.Command : null,
                // Preserve the source so each captured COM profile supplies its own
                // text encoding before the ordinary send script and CRLF processing.
                SourceText = step.Command,
                ExpectedTargetIdentity = runTarget?.Identity
            };

            if (!await Global.RequestSendDataAsync(request, token))
                throw new InvalidOperationException(TryFindResource("CircularSendRequestFailed") as string ?? "发送请求失败");
        }

        private List<CircularSendStep> BuildPlan(int defaultDelay)
        {
            var plan = new List<CircularSendStep>();
            foreach (var item in Items.Where(item => item.IsSelected && !string.IsNullOrWhiteSpace(item.Command)))
            {
                if (!TryReadDelay(item, defaultDelay, out var delay))
                    return null;

                try { plan.Add(new CircularSendStep(item, item.Command.Trim(), item.Hex, delay)); }
                catch (ArgumentException ex)
                {
                    Tools.MessageBox.Show($"步骤 #{item.Index}：{ex.Message}");
                    return null;
                }
            }
            return plan;
        }

        private bool TryReadRunTimes(out int runTimes)
        {
            var text = RunTimesTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                text = "1";
            if (!int.TryParse(text, out runTimes) || runTimes < 0)
            {
                Tools.MessageBox.Show(TryFindResource("CircularSendInvalidTimes") as string ?? "循环次数请输入大于等于 0 的整数");
                return false;
            }
            return true;
        }

        private bool TryReadDefaultDelay(out int delay)
        {
            var text = DefaultDelayTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                text = "0";
            if (!int.TryParse(text, out delay) || delay < 0)
            {
                Tools.MessageBox.Show(TryFindResource("CircularSendInvalidDelay") as string ?? "延时请输入大于等于 0 的整数");
                return false;
            }
            return true;
        }

        private bool TryReadDelay(CircularSendItem item, int defaultDelay, out int delay)
        {
            delay = defaultDelay;
            var text = item.DelayMs?.Trim();
            if (string.IsNullOrEmpty(text))
                return true;

            if (!int.TryParse(text, out delay) || delay < 0)
            {
                Tools.MessageBox.Show($"{TryFindResource("CircularSendInvalidDelay") as string ?? "延时请输入大于等于 0 的整数"}\r\n#{item.Index}: {text}");
                return false;
            }
            return true;
        }

        private void SetRunning(bool running)
        {
            StartButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
            CommandDataGrid.IsReadOnly = running;
            EnableAllCheckBox.IsEnabled = !running;
            CircularToolbar.IsEnabled = !running;
            StepOptionsPanel.IsEnabled = !running;
            RunTimesTextBox.IsEnabled = !running;
            DefaultDelayTextBox.IsEnabled = !running;
        }

        private bool CaptureRunTarget(bool responseValidation)
        {
            var target = Global.CaptureActiveSerialTarget();
            if (target == null || !target.IsOpen)
            {
                Tools.MessageBox.Show("请先手动打开需要测试的串口。测试不会自动连接或唤醒其它 COM。");
                return false;
            }
            if (responseValidation && target.Identity.StartsWith("serial-all:", StringComparison.Ordinal))
            {
                Tools.MessageBox.Show("响应验证不支持“全部”广播目标，请选择一个具体的 COM。");
                return false;
            }
            runTarget = target;
            return true;
        }

        private void ResetReport()
        {
            reportRows.Clear(); ReportItems.Clear();
            reportTotal = reportPassed = reportFailed = reportSent = reportCancelled = 0;
            ReportSummaryTextBlock.Text = "测试运行中。响应耗时从发送请求到匹配数据到达，由软件观测（包含排队/唤醒等待）。";
        }

        private void AddReport(CircularSendStep step, int round, SerialTestResult result)
        {
            reportTotal++;
            if (result.Outcome == "Cancelled") reportCancelled++;
            else if (!result.Success) reportFailed++;
            else if (result.Validated) reportPassed++;
            else reportSent++;
            var row = new SerialTestReportRow { Timestamp = DateTime.Now, Round = round,
                Step = step.Source.Index, Port = runTarget?.DisplayName ?? "", Command = step.Command, Result = result };
            if (reportRows.Count >= MaximumReportRows) { reportRows.RemoveAt(0); ReportItems.RemoveAt(0); }
            reportRows.Add(row); ReportItems.Add(new CircularTestResultItem(row));
            long checkedCount = reportPassed + reportFailed;
            string successRate = checkedCount == 0 ? "—" : (100.0 * reportPassed / checkedCount).ToString("0.0") + "%";
            ReportSummaryTextBlock.Text = $"已执行 {reportTotal} 步 · 验证通过 {reportPassed} · 失败 {reportFailed} · 仅发送 {reportSent} · 停止 {reportCancelled} · 验证成功率 {successRate}" +
                $"\r\n报告保留最近 {MaximumReportRows} 步；当前保留 {reportRows.Count} 步。响应耗时为软件观测值，包含排队及唤醒等待。";
        }

        private void ExportReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (reportRows.Count == 0) { Tools.MessageBox.Show("暂无测试结果，请先运行测试。"); return; }
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv",
                FileName = "serial-test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
            if (dialog.ShowDialog() != true) return;
            try { File.WriteAllText(dialog.FileName, SerialTestReport.ToCsv(reportRows), new UTF8Encoding(true)); }
            catch (Exception ex) { Tools.MessageBox.Show("导出报告失败：" + ex.Message); }
        }
    }

    public class CircularSendItem : INotifyPropertyChanged
    {
        private int index;
        private bool isSelected;
        private string command = "";
        private bool hex;
        private string delayMs = "";
        private string status = "";
        private int expectationMode;
        private string expectedResponse = "", responseTimeoutMs = "1000", retryCount = "0";
        private bool continueOnFailure;

        public int ExpectationMode { get => expectationMode; set { expectationMode = value; OnPropertyChanged(nameof(ExpectationMode)); } }
        public string ExpectedResponse { get => expectedResponse; set { expectedResponse = value ?? ""; OnPropertyChanged(nameof(ExpectedResponse)); } }
        public string ResponseTimeoutMs { get => responseTimeoutMs; set { responseTimeoutMs = value ?? ""; OnPropertyChanged(nameof(ResponseTimeoutMs)); } }
        public string RetryCount { get => retryCount; set { retryCount = value ?? ""; OnPropertyChanged(nameof(RetryCount)); } }
        public bool ContinueOnFailure { get => continueOnFailure; set { continueOnFailure = value; OnPropertyChanged(nameof(ContinueOnFailure)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler Changed;

        [JsonIgnore]
        public int Index
        {
            get => index;
            set
            {
                index = value;
                OnPropertyChanged(nameof(Index), false);
            }
        }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public string Command
        {
            get => command;
            set
            {
                command = value ?? "";
                OnPropertyChanged(nameof(Command));
            }
        }

        public bool Hex
        {
            get => hex;
            set
            {
                hex = value;
                OnPropertyChanged(nameof(Hex));
            }
        }

        public string DelayMs
        {
            get => delayMs;
            set
            {
                delayMs = value ?? "";
                OnPropertyChanged(nameof(DelayMs));
            }
        }

        [JsonIgnore]
        public string Status
        {
            get => status;
            set
            {
                status = value ?? "";
                OnPropertyChanged(nameof(Status), false);
            }
        }

        private void OnPropertyChanged(string propertyName, bool save = true)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            if (save)
                Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal class CircularSendStep
    {
        public CircularSendStep(CircularSendItem source, string command, bool hex, int delayMs)
        {
            Source = source;
            Command = command;
            Hex = hex;
            DelayMs = delayMs;
            if (!int.TryParse(source.ResponseTimeoutMs, out var timeout)) throw new ArgumentException("响应超时请输入整数毫秒。");
            if (!int.TryParse(source.RetryCount, out var retries)) throw new ArgumentException("重试次数请输入整数，默认 0。");
            Options = new SerialTestOptions(source.ExpectationMode, source.ExpectedResponse, timeout, retries, source.ContinueOnFailure);
        }

        public CircularSendItem Source { get; }
        public string Command { get; }
        public bool Hex { get; }
        public int DelayMs { get; }
        internal SerialTestOptions Options { get; }
    }

    public sealed class CircularTestResultItem
    {
        internal CircularTestResultItem(SerialTestReportRow row)
        {
            StepLabel = row.Round + " / " + row.Step; Port = row.Port;
            Outcome = row.Result.Outcome == "Passed" ? "通过" : row.Result.Outcome == "Sent" ? "已发送" :
                row.Result.Outcome == "Cancelled" ? "已停止" : row.Result.Outcome == "Timeout" ? "超时" : "失败";
            ResponseMs = row.Result.ResponseMs?.ToString("0.0") ?? "—";
            Attempts = row.Result.Attempts; Details = row.Result.Details;
        }
        public string StepLabel { get; }
        public string Port { get; }
        public string Outcome { get; }
        public string ResponseMs { get; }
        public int Attempts { get; }
        public string Details { get; }
    }
}
