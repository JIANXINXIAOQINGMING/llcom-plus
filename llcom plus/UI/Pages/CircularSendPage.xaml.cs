using llcom_plus.Model;
using llcom_plus.Tools;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    /// <summary>
    /// CircularSendPage.xaml 的交互逻辑
    /// </summary>
    public partial class CircularSendPage : Page
    {
        private const int DefaultRowCount = 10;
        private bool suppressSave = false;
        private CancellationTokenSource loopCts = null;
        private string lastStorageNotificationMessage = string.Empty;

        public ObservableCollection<CircularSendItem> Items { get; } = new ObservableCollection<CircularSendItem>();

        public CircularSendPage()
        {
            InitializeComponent();
            DataContext = this;
            LoadItems();
        }

        private string StoragePath => Path.Combine(Global.ProfilePath, "circular_send.json");

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            SetRunning(false);
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
        }

        private void Item_Changed(object sender, EventArgs e)
        {
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

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelection(true);
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelection(false);
        }

        private void SetSelection(bool selected)
        {
            suppressSave = true;
            try
            {
                foreach (var item in Items)
                    item.IsSelected = selected && !string.IsNullOrWhiteSpace(item.Command);
            }
            finally
            {
                suppressSave = false;
            }
            SaveItems();
        }

        private void ImportQuickSendButton_Click(object sender, RoutedEventArgs e)
        {
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

            if (!Global.EnsureActiveSerialTargetOpen())
                return;

            if (!TryReadRunTimes(out var runTimes) || !TryReadDefaultDelay(out var defaultDelay))
                return;

            var plan = BuildPlan(defaultDelay);
            if (plan == null || plan.Count == 0)
            {
                Tools.MessageBox.Show(TryFindResource("CircularSendNoCommands") as string ?? "请先勾选至少一条命令");
                return;
            }

            SaveItems();
            loopCts = new CancellationTokenSource();
            SetRunning(true);
            try
            {
                await RunLoopAsync(plan, runTimes, loopCts.Token);
                StatusTextBlock.Text = TryFindResource("CircularSendDone") as string ?? "发送完成";
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
                    SendStep(step);
                    step.Source.Status = string.Format(
                        TryFindResource("CircularSendSentStatus") as string ?? "第 {0} 轮已发送",
                        round);
                    StatusTextBlock.Text = string.Format(
                        TryFindResource("CircularSendRunning") as string ?? "第 {0} 轮，#{1}",
                        round,
                        step.Source.Index);

                    var isLast = runTimes > 0 && round == runTimes && i == plan.Count - 1;
                    if (!isLast && step.DelayMs > 0)
                        await Task.Delay(step.DelayMs, token);
                }
            }
        }

        private void SendOneButton_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.Tag is CircularSendItem item))
                return;

            if (string.IsNullOrWhiteSpace(item.Command))
                return;

            try
            {
                if (!Global.EnsureActiveSerialTargetOpen())
                    return;

                var step = new CircularSendStep(item, item.Command.Trim(), item.Hex, 0);
                SendStep(step);
                item.Status = TryFindResource("CircularSendSentOnce") as string ?? "已发送";
            }
            catch (Exception ex)
            {
                item.Status = TryFindResource("CircularSendFailed") as string ?? "发送失败";
                Tools.MessageBox.Show(ex.Message);
            }
        }

        private void SendStep(CircularSendStep step)
        {
            var data = step.Hex
                ? Global.Hex2Byte(step.Command)
                : Global.GetEncoding().GetBytes(step.Command);

            var request = new UartSendRequest
            {
                Data = data,
                IsHex = step.Hex,
                ApplySendProcessing = true,
                SessionStringLogOverride = step.Hex ? step.Command : null
            };

            if (!Global.RequestSendData(request))
                throw new InvalidOperationException(TryFindResource("CircularSendRequestFailed") as string ?? "发送请求失败");
        }

        private List<CircularSendStep> BuildPlan(int defaultDelay)
        {
            var plan = new List<CircularSendStep>();
            foreach (var item in Items.Where(item => item.IsSelected && !string.IsNullOrWhiteSpace(item.Command)))
            {
                if (!TryReadDelay(item, defaultDelay, out var delay))
                    return null;

                plan.Add(new CircularSendStep(item, item.Command.Trim(), item.Hex, delay));
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
        }

        public CircularSendItem Source { get; }
        public string Command { get; }
        public bool Hex { get; }
        public int DelayMs { get; }
    }
}
