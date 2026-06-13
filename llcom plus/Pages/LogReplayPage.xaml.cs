using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    /// <summary>
    /// LogReplayPage.xaml 的交互逻辑
    /// </summary>
    public partial class LogReplayPage : Page
    {
        private readonly object receiveLock = new object();
        private readonly List<byte> receiveBuffer = new List<byte>();
        private CancellationTokenSource replayCts = null;

        public ObservableCollection<ReplayStep> Steps { get; } = new ObservableCollection<ReplayStep>();

        public LogReplayPage()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Global.uart.UartDataRecived += Uart_UartDataRecived;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            Global.uart.UartDataRecived -= Uart_UartDataRecived;
            StopReplay();
        }

        private void Uart_UartDataRecived(object sender, EventArgs e)
        {
            if (sender is byte[] data && data.Length > 0)
            {
                lock (receiveLock)
                    receiveBuffer.AddRange(data);
            }
        }

        private void SelectLogButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = TryFindResource("LogReplayLogFilter") as string ?? "Log files|*.log|All files|*.*"
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            try
            {
                LoadReplayLog(dialog.FileName);
                LogPathTextBox.Text = dialog.FileName;
                StartReplayButton.IsEnabled = Steps.Count > 0;
                ReplayStatusTextBlock.Text = string.Format(
                    TryFindResource("LogReplayLoaded") as string ?? "Loaded {0} steps",
                    Steps.Count);
            }
            catch (Exception ex)
            {
                Steps.Clear();
                StartReplayButton.IsEnabled = false;
                Tools.MessageBox.Show($"{TryFindResource("LogReplayLoadFail") as string ?? "Log load failed"}\r\n{ex}");
            }
        }

        private async void StartReplayButton_Click(object sender, RoutedEventArgs e)
        {
            if (Steps.Count == 0)
                return;

            if (!Global.uart.IsOpen())
            {
                ReplayStatusTextBlock.Text = TryFindResource("LogReplayPortNotOpen") as string ?? "Please open the serial port first";
                return;
            }

            if (!int.TryParse(TimeoutTextBox.Text, out var timeoutMs) || timeoutMs <= 0)
                timeoutMs = 5000;

            replayCts = new CancellationTokenSource();
            StartReplayButton.IsEnabled = false;
            StopReplayButton.IsEnabled = true;
            foreach (var step in Steps)
                step.Status = "";

            var ok = true;
            try
            {
                for (int i = 0; i < Steps.Count; i++)
                {
                    var step = Steps[i];
                    if (replayCts.IsCancellationRequested)
                        break;

                    ReplayStepsGrid.ScrollIntoView(step);
                    if (step.Direction == ReplayDirection.Send)
                    {
                        ClearReceiveBuffer();
                        step.Status = TryFindResource("LogReplaySending") as string ?? "Sending";
                        if (!Global.RequestSendRawData(step.Data))
                        {
                            step.Status = TryFindResource("LogReplaySendFail") as string ?? "Send failed";
                            ok = false;
                            break;
                        }
                        step.Status = TryFindResource("LogReplaySent") as string ?? "Sent";
                        await Task.Delay(50, replayCts.Token);
                    }
                    else
                    {
                        step.Status = TryFindResource("LogReplayWaiting") as string ?? "Waiting";
                        var matched = await WaitForReceiveAsync(step.Data, timeoutMs, replayCts.Token);
                        if (!matched)
                        {
                            step.Status = TryFindResource("LogReplayTimeoutStatus") as string ?? "Timeout";
                            ok = false;
                            break;
                        }
                        step.Status = TryFindResource("LogReplayMatched") as string ?? "Matched";
                    }
                }
            }
            catch (TaskCanceledException)
            {
                ok = false;
            }
            finally
            {
                StopReplayButton.IsEnabled = false;
                StartReplayButton.IsEnabled = Steps.Count > 0;
                ReplayStatusTextBlock.Text = replayCts.IsCancellationRequested ?
                    (TryFindResource("LogReplayStopped") as string ?? "Stopped") :
                    (ok ? (TryFindResource("LogReplayDone") as string ?? "Done") : (TryFindResource("LogReplayFailed") as string ?? "Failed"));
                replayCts.Dispose();
                replayCts = null;
            }
        }

        private void StopReplayButton_Click(object sender, RoutedEventArgs e)
        {
            StopReplay();
        }

        private void StopReplay()
        {
            replayCts?.Cancel();
        }

        private void LoadReplayLog(string path)
        {
            var primaryIsHex = IsHexLogPath(path);
            var primary = ParseLogFile(path, primaryIsHex);
            var pairedPath = GetPairedLogPath(path, primaryIsHex);
            if (!string.IsNullOrEmpty(pairedPath) && File.Exists(pairedPath))
            {
                var paired = ParseLogFile(pairedPath, !primaryIsHex);
                MergePairedLog(primary, paired, primaryIsHex);
            }

            Steps.Clear();
            for (int i = 0; i < primary.Count; i++)
            {
                primary[i].Index = i + 1;
                primary[i].Status = "";
                Steps.Add(primary[i]);
            }
        }

        private bool IsHexLogPath(string path)
        {
            var parent = Directory.GetParent(path);
            return parent != null && parent.Name.Equals("HEX", StringComparison.OrdinalIgnoreCase);
        }

        private string GetPairedLogPath(string path, bool primaryIsHex)
        {
            var parent = Directory.GetParent(path);
            var portFolder = parent?.Parent;
            if (parent == null || portFolder == null)
                return null;

            var pairedFolder = Path.Combine(portFolder.FullName, primaryIsHex ? "STRING" : "HEX");
            return Path.Combine(pairedFolder, Path.GetFileName(path));
        }

        private List<ReplayStep> ParseLogFile(string path, bool isHex)
        {
            var steps = new List<ReplayStep>();
            var regex = new Regex(@"^\[(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\] \[(?<dir>[^\]]+)\]\s?(?<data>.*)$");
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var match = regex.Match(line);
                if (!match.Success)
                    continue;

                var direction = match.Groups["dir"].Value;
                if (!direction.Equals("send", StringComparison.OrdinalIgnoreCase) &&
                    !direction.Equals("recv", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dataText = match.Groups["data"].Value;
                var data = isHex ? Global.Hex2Byte(dataText) : ParseEscapedStringBytes(dataText);
                if (data.Length == 0)
                    continue;

                steps.Add(new ReplayStep
                {
                    Direction = direction.Equals("send", StringComparison.OrdinalIgnoreCase) ? ReplayDirection.Send : ReplayDirection.Receive,
                    Text = dataText,
                    Data = data
                });
            }
            return steps;
        }

        private void MergePairedLog(List<ReplayStep> primary, List<ReplayStep> paired, bool primaryIsHex)
        {
            var count = Math.Min(primary.Count, paired.Count);
            for (int i = 0; i < count; i++)
            {
                if (primary[i].Direction != paired[i].Direction)
                    continue;

                if (primaryIsHex)
                    primary[i].Text = paired[i].Text;
                else
                    primary[i].Data = paired[i].Data;
            }
        }

        private byte[] ParseEscapedStringBytes(string value)
        {
            var bytes = new List<byte>();
            var plain = new StringBuilder();

            void FlushPlain()
            {
                if (plain.Length == 0)
                    return;
                bytes.AddRange(Global.GetEncoding().GetBytes(plain.ToString()));
                plain.Clear();
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i == value.Length - 1)
                {
                    plain.Append(value[i]);
                    continue;
                }

                var next = value[++i];
                switch (next)
                {
                    case '\\':
                        plain.Append('\\');
                        break;
                    case '0':
                        FlushPlain();
                        bytes.Add(0x00);
                        break;
                    case 'a':
                        FlushPlain();
                        bytes.Add(0x07);
                        break;
                    case 'b':
                        FlushPlain();
                        bytes.Add(0x08);
                        break;
                    case 't':
                        FlushPlain();
                        bytes.Add(0x09);
                        break;
                    case 'n':
                        FlushPlain();
                        bytes.Add(0x0a);
                        break;
                    case 'v':
                        FlushPlain();
                        bytes.Add(0x0b);
                        break;
                    case 'f':
                        FlushPlain();
                        bytes.Add(0x0c);
                        break;
                    case 'r':
                        FlushPlain();
                        bytes.Add(0x0d);
                        break;
                    case 'e':
                        FlushPlain();
                        bytes.Add(0x1b);
                        break;
                    case 'x':
                        if (i + 2 < value.Length &&
                            byte.TryParse(value.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                        {
                            FlushPlain();
                            bytes.Add(b);
                            i += 2;
                        }
                        else
                        {
                            plain.Append("\\x");
                        }
                        break;
                    default:
                        plain.Append('\\');
                        plain.Append(next);
                        break;
                }
            }

            FlushPlain();
            return bytes.ToArray();
        }

        private void ClearReceiveBuffer()
        {
            lock (receiveLock)
                receiveBuffer.Clear();
        }

        private async Task<bool> WaitForReceiveAsync(byte[] expected, int timeoutMs, CancellationToken token)
        {
            var start = DateTime.Now;
            while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
            {
                token.ThrowIfCancellationRequested();
                lock (receiveLock)
                {
                    if (IsMatch(receiveBuffer, expected))
                        return true;
                }
                await Task.Delay(50, token);
            }
            return false;
        }

        private bool IsMatch(List<byte> buffer, byte[] expected)
        {
            if (buffer.Count == 0 || expected.Length == 0)
                return false;

            var mode = (MatchModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (mode == "exact")
                return ByteListEquals(buffer, expected);

            return IndexOf(buffer, expected) >= 0;
        }

        private bool ByteListEquals(List<byte> buffer, byte[] expected)
        {
            if (buffer.Count != expected.Length)
                return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (buffer[i] != expected[i])
                    return false;
            }
            return true;
        }

        private int IndexOf(List<byte> buffer, byte[] expected)
        {
            if (expected.Length > buffer.Count)
                return -1;
            for (int i = 0; i <= buffer.Count - expected.Length; i++)
            {
                var matched = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (buffer[i + j] != expected[j])
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched)
                    return i;
            }
            return -1;
        }
    }

    public enum ReplayDirection
    {
        Send,
        Receive
    }

    public class ReplayStep : INotifyPropertyChanged
    {
        private string status = "";
        private string text = "";

        public int Index { get; set; }
        public ReplayDirection Direction { get; set; }
        public string DirectionText => Direction == ReplayDirection.Send ? "send" : "recv";
        public byte[] Data { get; set; } = new byte[0];

        public string Text
        {
            get => text;
            set
            {
                text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public string Status
        {
            get => status;
            set
            {
                status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
