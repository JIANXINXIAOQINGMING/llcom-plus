using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
        internal const int MaxReceiveBufferBytes = 1024 * 1024;
        internal const long MaxReplayLogFileBytes = 32L * 1024 * 1024;
        internal const int MaxReplayLogLines = 250000;
        internal const int MaxReplayLineCharacters = 256 * 1024;
        internal const int MaxReplayRecords = 100000;
        internal const int MaxReplayRecordCharacters = 1024 * 1024;
        internal const int MaxReplayFieldCharacters = 256 * 1024;
        internal const int MaxReplaySteps = 100000;
        internal const int MaxReplayStepBytes = 1024 * 1024;

        private readonly object receiveLock = new object();
        private readonly List<byte> receiveBuffer = new List<byte>();
        private CancellationTokenSource replayCts = null;
        private CancellationTokenSource loadCts = null;
        private long nextLoadRunId = 0;
        private long activeLoadRunId = 0;
        private int receiveCaptureEnabled = 0;
        private bool receiveSubscribed = false;
        private bool isUnloaded = false;

        private sealed class ReplayStepCollection : ObservableCollection<ReplayStep>
        {
            internal void ReplaceAll(IList<ReplayStep> values)
            {
                CheckReentrancy();
                Items.Clear();
                if (values != null)
                {
                    foreach (var value in values)
                        Items.Add(value);
                }
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        public ObservableCollection<ReplayStep> Steps { get; } = new ReplayStepCollection();

        public LogReplayPage()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void ReplayStepsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ReplayStepsGrid == null || ReplayDataColumn == null || ReplayStepsGrid.ActualWidth <= 0)
                return;

            var fixedWidth = ReplayStepsGrid.Columns
                .Where(column => !ReferenceEquals(column, ReplayDataColumn))
                .Sum(column => column.Width.IsAbsolute ? column.Width.Value : Math.Max(0, column.ActualWidth));
            var chromeAllowance = SystemParameters.VerticalScrollBarWidth + 4;
            var dataWidth = Math.Max(
                ReplayDataColumn.MinWidth,
                ReplayStepsGrid.ActualWidth - fixedWidth - chromeAllowance);

            if (Math.Abs(ReplayDataColumn.ActualWidth - dataWidth) > 0.5)
                ReplayDataColumn.Width = new DataGridLength(dataWidth, DataGridLengthUnitType.Pixel);
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            isUnloaded = false;
            if (!receiveSubscribed)
            {
                Global.ActiveSerialTargetReceivedEvent += ActiveSerialTargetReceived;
                receiveSubscribed = true;
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            isUnloaded = true;
            if (receiveSubscribed)
            {
                Global.ActiveSerialTargetReceivedEvent -= ActiveSerialTargetReceived;
                receiveSubscribed = false;
            }
            CancelLogLoad();
            StopReplay();
            ClearReceiveBuffer();
        }

        private void ActiveSerialTargetReceived(object sender, byte[] data)
        {
            if (data == null || data.Length == 0 || Volatile.Read(ref receiveCaptureEnabled) == 0)
                return;

            lock (receiveLock)
            {
                if (Volatile.Read(ref receiveCaptureEnabled) == 0)
                    return;
                AppendReceiveDataBounded(receiveBuffer, data);
            }
        }

        internal static void AppendReceiveDataBounded(List<byte> buffer, byte[] data)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (data == null || data.Length == 0)
                return;

            if (data.Length >= MaxReceiveBufferBytes)
            {
                buffer.Clear();
                var start = data.Length - MaxReceiveBufferBytes;
                for (var index = start; index < data.Length; index++)
                    buffer.Add(data[index]);
                return;
            }

            var overflow = buffer.Count + data.Length - MaxReceiveBufferBytes;
            if (overflow > 0)
                buffer.RemoveRange(0, Math.Min(overflow, buffer.Count));
            buffer.AddRange(data);
        }

        private async void SelectLogButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = TryFindResource("LogReplayLogFilter") as string ?? "Log files|*.log|All files|*.*"
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            StopReplay();
            CancelLogLoad();
            var selectedPath = dialog.FileName;
            var cts = new CancellationTokenSource();
            loadCts = cts;
            var runId = Interlocked.Increment(ref nextLoadRunId);
            Interlocked.Exchange(ref activeLoadRunId, runId);
            var selectButton = sender as Button;
            if (selectButton != null)
                selectButton.IsEnabled = false;
            StartReplayButton.IsEnabled = false;

            try
            {
                CheckReplayLogFileSize(selectedPath);
                var parsed = await Task.Run(
                    () => ParseReplayLog(selectedPath, cts.Token),
                    cts.Token);
                if (!IsCurrentLoad(runId, cts))
                    return;

                ReplaceSteps(parsed);
                LogPathTextBox.Text = selectedPath;
                StartReplayButton.IsEnabled = Steps.Count > 0;
                ReplayStatusTextBlock.Text = string.Format(
                    TryFindResource("LogReplayLoaded") as string ?? "Loaded {0} steps",
                    Steps.Count);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentLoad(runId, cts))
                {
                    ReplaceSteps(new List<ReplayStep>());
                    StartReplayButton.IsEnabled = false;
                    Tools.MessageBox.Show($"{TryFindResource("LogReplayLoadFail") as string ?? "Log load failed"}\r\n{ex.GetBaseException().Message}");
                }
            }
            finally
            {
                var detached = ReferenceEquals(
                    Interlocked.CompareExchange(ref loadCts, null, cts),
                    cts);
                Interlocked.CompareExchange(ref activeLoadRunId, 0, runId);
                cts.Dispose();
                if (detached && !isUnloaded && selectButton != null)
                    selectButton.IsEnabled = true;
            }
        }

        private bool IsCurrentLoad(long runId, CancellationTokenSource cts)
        {
            return !isUnloaded &&
                   runId != 0 &&
                   runId == Interlocked.Read(ref activeLoadRunId) &&
                   ReferenceEquals(Interlocked.CompareExchange(ref loadCts, null, null), cts);
        }

        private void CancelLogLoad()
        {
            Interlocked.Exchange(ref activeLoadRunId, 0);
            var cts = Interlocked.Exchange(ref loadCts, null);
            if (cts == null)
                return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        private async void StartReplayButton_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref replayCts, null, null) != null)
                return;

            ReplayStepsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ReplayStepsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            if (Steps.Count == 0)
                return;

            if (!Global.EnsureActiveSerialTargetOpen())
            {
                ReplayStatusTextBlock.Text = TryFindResource("LogReplayPortNotOpen") as string ?? "Please open the serial port first";
                return;
            }

            if (!int.TryParse(TimeoutTextBox.Text, out var timeoutMs) || timeoutMs <= 0)
                timeoutMs = 5000;

            var loopReplay = LoopReplayCheckBox.IsChecked == true;
            var loopRound = 0;
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref replayCts, cts, null) != null)
            {
                cts.Dispose();
                return;
            }

            ClearReceiveBuffer();
            Volatile.Write(ref receiveCaptureEnabled, 1);
            StartReplayButton.IsEnabled = false;
            StopReplayButton.IsEnabled = true;
            LoopReplayCheckBox.IsEnabled = false;

            var ok = true;
            try
            {
                do
                {
                    cts.Token.ThrowIfCancellationRequested();
                    loopRound++;
                    foreach (var step in Steps)
                        step.Status = "";

                    if (loopReplay)
                    {
                        ReplayStatusTextBlock.Text = string.Format(
                            TryFindResource("LogReplayLoopRound") as string ?? "Loop {0}",
                            loopRound);
                    }

                    ok = await ReplayOnceAsync(timeoutMs, cts.Token);
                    if (ok && loopReplay && !cts.IsCancellationRequested)
                        await Task.Delay(200, cts.Token);
                }
                while (ok && loopReplay && !cts.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                ok = false;
            }
            finally
            {
                Volatile.Write(ref receiveCaptureEnabled, 0);
                ClearReceiveBuffer();
                var wasCancelled = cts.IsCancellationRequested;
                var detached = ReferenceEquals(
                    Interlocked.CompareExchange(ref replayCts, null, cts),
                    cts);
                cts.Dispose();
                if (detached && !isUnloaded)
                {
                    StopReplayButton.IsEnabled = false;
                    StartReplayButton.IsEnabled = Steps.Count > 0;
                    LoopReplayCheckBox.IsEnabled = true;
                    ReplayStatusTextBlock.Text = wasCancelled
                        ? (TryFindResource("LogReplayStopped") as string ?? "Stopped")
                        : (ok
                            ? (TryFindResource("LogReplayDone") as string ?? "Done")
                            : (TryFindResource("LogReplayFailed") as string ?? "Failed"));
                }
            }
        }

        private void StopReplayButton_Click(object sender, RoutedEventArgs e)
        {
            StopReplay();
        }

        private void IgnoreResponseCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is ReplayStep step)
                step.IgnoreResponseValue = checkBox.IsChecked == true;
        }

        private async Task<bool> ReplayOnceAsync(int timeoutMs, CancellationToken token)
        {
            for (int i = 0; i < Steps.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var step = Steps[i];

                ReplayStepsGrid.ScrollIntoView(step);
                if (step.Direction == ReplayDirection.Send)
                {
                    ClearReceiveBuffer();
                    step.Status = TryFindResource("LogReplaySending") as string ?? "Sending";
                    if (!Global.RequestSendRawData(step.Data))
                    {
                        step.Status = TryFindResource("LogReplaySendFail") as string ?? "Send failed";
                        return false;
                    }
                    step.Status = step.IgnoreResponseValue
                        ? (TryFindResource("LogReplaySentCountOnly") as string ?? "Sent (count only)")
                        : (TryFindResource("LogReplaySent") as string ?? "Sent");

                    if (step.IgnoreResponseValue)
                    {
                        var lastReceiveIndex = await ContinueAfterReceiveCountAsync(i, timeoutMs, token);
                        if (lastReceiveIndex < 0)
                            return false;

                        if (lastReceiveIndex > i)
                            i = lastReceiveIndex;
                    }

                    await Task.Delay(50, token);
                }
                else
                {
                    step.Status = TryFindResource("LogReplayWaiting") as string ?? "Waiting";
                    var matched = await WaitForReceiveAsync(step.Data, timeoutMs, false, token);
                    if (!matched)
                    {
                        step.Status = TryFindResource("LogReplayTimeoutStatus") as string ?? "Timeout";
                        return false;
                    }
                    step.Status = TryFindResource("LogReplayMatched") as string ?? "Matched";
                }
            }

            return true;
        }

        private async Task<int> ContinueAfterReceiveCountAsync(int sendIndex, int timeoutMs, CancellationToken token)
        {
            var lastReceiveIndex = sendIndex;
            for (int i = sendIndex + 1; i < Steps.Count && Steps[i].Direction == ReplayDirection.Receive; i++)
            {
                token.ThrowIfCancellationRequested();
                var receiveStep = Steps[i];
                ReplayStepsGrid.ScrollIntoView(receiveStep);
                receiveStep.Status = TryFindResource("LogReplayWaiting") as string ?? "Waiting";
                var received = await WaitForReceiveAsync(receiveStep.Data, timeoutMs, true, token);
                if (!received)
                {
                    receiveStep.Status = TryFindResource("LogReplayTimeoutStatus") as string ?? "Timeout";
                    return -1;
                }

                receiveStep.Status = TryFindResource("LogReplayReceived") as string ?? "Received";
                lastReceiveIndex = i;
            }

            return lastReceiveIndex;
        }

        private void StopReplay()
        {
            Volatile.Write(ref receiveCaptureEnabled, 0);
            ClearReceiveBuffer();
            var cts = Interlocked.CompareExchange(ref replayCts, null, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        private enum ReplayLogFormat
        {
            Canonical,
            Legacy
        }

        private sealed class ParsedReplayLogRecord
        {
            internal ReplayLogFormat Format { get; set; }
            internal ReplayDirection Direction { get; set; }
            internal StringBuilder DataText { get; set; }
        }

        private static readonly Regex CanonicalLogLineRegex = new Regex(
            @"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})  (?<dir>TX|RX) \u2502(?: (?<data>.*))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LegacyLogLineRegex = new Regex(
            @"^\[(?<time>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\] \[(?<dir>send|recv)\]\s?(?<data>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex CanonicalContinuationRegex = new Regex(
            @"^\s*\u2502(?: (?<data>.*))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LegacyLogHeaderRegex = new Regex(
            @"^\[\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]\s+\[[^\]]+\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CanonicalLogHeaderRegex = new Regex(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\s{2}\S+\s+\u2502",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private void LoadReplayLog(string path)
        {
            ReplaceSteps(ParseReplayLog(path));
        }

        private void ReplaceSteps(IList<ReplayStep> values)
        {
            values = values ?? new List<ReplayStep>();
            for (var index = 0; index < values.Count; index++)
            {
                values[index].Index = index + 1;
                values[index].Status = "";
            }
            ((ReplayStepCollection)Steps).ReplaceAll(values);
        }

        internal static List<ReplayStep> ParseReplayLog(string path)
        {
            return ParseReplayLog(path, CancellationToken.None);
        }

        internal static List<ReplayStep> ParseReplayLog(
            string path,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A replay log path is required.", nameof(path));

            cancellationToken.ThrowIfCancellationRequested();
            var primaryIsHex = IsHexLogPath(path);
            var primary = ParseLogFile(path, primaryIsHex, cancellationToken);
            var pairedPath = GetPairedLogPath(path, primaryIsHex);
            if (!string.IsNullOrEmpty(pairedPath) && File.Exists(pairedPath))
            {
                var paired = ParseLogFile(pairedPath, !primaryIsHex, cancellationToken);
                MergePairedLog(primary, paired, primaryIsHex, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return primary;
        }

        private static bool IsHexLogPath(string path)
        {
            var parent = Directory.GetParent(path);
            return parent != null && parent.Name.Equals("HEX", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPairedLogPath(string path, bool primaryIsHex)
        {
            var parent = Directory.GetParent(path);
            var portFolder = parent?.Parent;
            if (parent == null || portFolder == null)
                return null;

            var pairedFolder = Path.Combine(portFolder.FullName, primaryIsHex ? "STRING" : "HEX");
            return Path.Combine(pairedFolder, Path.GetFileName(path));
        }

        internal static List<ReplayStep> ParseLogFile(string path, bool isHex)
        {
            return ParseLogFile(path, isHex, CancellationToken.None);
        }

        internal static List<ReplayStep> ParseLogFile(
            string path,
            bool isHex,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A replay log path is required.", nameof(path));

            CheckReplayLogFileSize(path);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseLogLines(File.ReadLines(path, Encoding.UTF8), isHex, cancellationToken);
        }

        private static void CheckReplayLogFileSize(string path)
        {
            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
                throw new FileNotFoundException("The replay log does not exist.", fileInfo.FullName);
            if (fileInfo.Length > MaxReplayLogFileBytes)
            {
                throw new InvalidDataException(
                    $"Replay log exceeds the {MaxReplayLogFileBytes / (1024 * 1024)} MiB limit.");
            }
        }

        internal static List<ReplayStep> ParseLogLines(IEnumerable<string> lines, bool isHex)
        {
            return ParseLogLines(lines, isHex, CancellationToken.None);
        }

        internal static List<ReplayStep> ParseLogLines(
            IEnumerable<string> lines,
            bool isHex,
            CancellationToken cancellationToken)
        {
            if (lines == null)
                throw new ArgumentNullException(nameof(lines));

            var steps = new List<ReplayStep>();
            ParsedReplayLogRecord pending = null;
            var lineCount = 0;
            var recordCount = 0;
            foreach (var sourceLine in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineCount++;
                if (lineCount > MaxReplayLogLines)
                    throw new InvalidDataException($"Replay log exceeds the {MaxReplayLogLines:N0}-line limit.");

                var line = sourceLine ?? string.Empty;
                if (line.Length > MaxReplayLineCharacters)
                {
                    throw new InvalidDataException(
                        $"Replay log line {lineCount:N0} exceeds the {MaxReplayLineCharacters:N0}-character limit.");
                }

                if (TryParseReplayLogRecord(line, out var parsed))
                {
                    AddParsedReplayLogRecord(steps, pending, isHex, cancellationToken);
                    recordCount++;
                    if (recordCount > MaxReplayRecords)
                        throw new InvalidDataException($"Replay log exceeds the {MaxReplayRecords:N0}-record limit.");
                    pending = parsed;
                    continue;
                }

                if (!isHex && pending != null)
                {
                    string continuation;
                    if (pending.Format == ReplayLogFormat.Canonical &&
                        TryParseCanonicalContinuation(line, out continuation))
                    {
                        AppendContinuation(pending, continuation);
                        continue;
                    }

                    if (pending.Format == ReplayLogFormat.Legacy &&
                        TryParseLegacyContinuation(pending.DataText, line, out continuation))
                    {
                        AppendContinuation(pending, continuation);
                        continue;
                    }
                }

                AddParsedReplayLogRecord(steps, pending, isHex, cancellationToken);
                pending = null;
            }

            AddParsedReplayLogRecord(steps, pending, isHex, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return steps;
        }

        private static bool TryParseReplayLogRecord(string line, out ParsedReplayLogRecord record)
        {
            var match = CanonicalLogLineRegex.Match(line);
            if (match.Success)
            {
                var value = match.Groups["data"].Success ? match.Groups["data"].Value : string.Empty;
                ValidateReplayField(value);
                record = new ParsedReplayLogRecord
                {
                    Format = ReplayLogFormat.Canonical,
                    Direction = match.Groups["dir"].Value.Equals("TX", StringComparison.OrdinalIgnoreCase)
                        ? ReplayDirection.Send
                        : ReplayDirection.Receive,
                    DataText = new StringBuilder(value)
                };
                return true;
            }

            match = LegacyLogLineRegex.Match(line);
            if (match.Success)
            {
                var value = match.Groups["data"].Value;
                ValidateReplayField(value);
                record = new ParsedReplayLogRecord
                {
                    Format = ReplayLogFormat.Legacy,
                    Direction = match.Groups["dir"].Value.Equals("send", StringComparison.OrdinalIgnoreCase)
                        ? ReplayDirection.Send
                        : ReplayDirection.Receive,
                    DataText = new StringBuilder(value)
                };
                return true;
            }

            record = null;
            return false;
        }

        private static void ValidateReplayField(string value)
        {
            if ((value?.Length ?? 0) > MaxReplayFieldCharacters)
            {
                throw new InvalidDataException(
                    $"Replay field exceeds the {MaxReplayFieldCharacters:N0}-character limit.");
            }
        }

        private static void AppendContinuation(ParsedReplayLogRecord record, string continuation)
        {
            continuation = continuation ?? string.Empty;
            ValidateReplayField(continuation);
            if (record.DataText.Length > MaxReplayRecordCharacters - continuation.Length)
            {
                throw new InvalidDataException(
                    $"Replay record exceeds the {MaxReplayRecordCharacters:N0}-character limit.");
            }
            record.DataText.Append(continuation);
        }

        private static bool TryParseCanonicalContinuation(string line, out string continuation)
        {
            var match = CanonicalContinuationRegex.Match(line);
            if (!match.Success)
            {
                continuation = null;
                return false;
            }

            continuation = match.Groups["data"].Success ? match.Groups["data"].Value : string.Empty;
            return true;
        }

        private static bool TryParseLegacyContinuation(
            StringBuilder currentData,
            string line,
            out string continuation)
        {
            continuation = null;
            if (!EndsWithUnescapedLineFeed(currentData) || IsReplayLogBoundary(line))
                return false;

            if (!TryParseCanonicalContinuation(line, out continuation))
                continuation = line;
            return true;
        }

        private static bool EndsWithUnescapedLineFeed(StringBuilder value)
        {
            if (value == null || value.Length == 0 || value[value.Length - 1] != 'n')
                return false;

            var slashCount = 0;
            for (var index = value.Length - 2; index >= 0 && value[index] == '\\'; index--)
                slashCount++;
            return slashCount % 2 == 1;
        }

        private static bool IsReplayLogBoundary(string line)
        {
            return line.StartsWith("[START]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("[END]", StringComparison.OrdinalIgnoreCase) ||
                LegacyLogHeaderRegex.IsMatch(line) ||
                CanonicalLogHeaderRegex.IsMatch(line);
        }

        private static void AddParsedReplayLogRecord(
            List<ReplayStep> steps,
            ParsedReplayLogRecord record,
            bool isHex,
            CancellationToken cancellationToken)
        {
            if (record == null)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            var dataText = record.DataText?.ToString() ?? string.Empty;
            var data = isHex
                ? Global.Hex2Byte(dataText)
                : ParseEscapedStringBytes(dataText, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Length == 0)
                return;

            if (record.Direction == ReplayDirection.Receive)
            {
                foreach (var frame in SplitReceiveFrames(data, cancellationToken))
                {
                    if (frame.Length == 0)
                        continue;
                    EnsureReplayStepFits(frame.Length);
                    AddReplayStep(steps, new ReplayStep
                    {
                        Direction = ReplayDirection.Receive,
                        Text = FormatReplayText(frame, isHex),
                        Data = frame
                    });
                }
                return;
            }

            EnsureReplayStepFits(data.Length);
            AddReplayStep(steps, new ReplayStep
            {
                Direction = ReplayDirection.Send,
                Text = dataText,
                Data = data
            });
        }

        private static void AddReplayStep(List<ReplayStep> steps, ReplayStep step)
        {
            if (steps.Count >= MaxReplaySteps)
                throw new InvalidDataException($"Replay log exceeds the {MaxReplaySteps:N0}-step limit.");
            steps.Add(step);
        }

        private static void EnsureReplayStepFits(int byteCount)
        {
            if (byteCount > MaxReplayStepBytes)
            {
                throw new InvalidDataException(
                    $"Replay step exceeds the {MaxReplayStepBytes:N0}-byte limit.");
            }
        }

        private static IEnumerable<byte[]> SplitReceiveFrames(
            byte[] data,
            CancellationToken cancellationToken)
        {
            var start = 0;
            for (int index = 0; index < data.Length; index++)
            {
                if ((index & 0x0fff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (data[index] != 0x0a)
                    continue;

                var length = index - start + 1;
                EnsureReplayStepFits(length);
                var frame = new byte[length];
                Array.Copy(data, start, frame, 0, length);
                yield return frame;
                start = index + 1;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (start < data.Length)
            {
                var length = data.Length - start;
                EnsureReplayStepFits(length);
                var frame = new byte[length];
                Array.Copy(data, start, frame, 0, length);
                yield return frame;
            }
        }

        private static string FormatReplayText(byte[] data, bool isHex)
        {
            if (isHex)
                return Global.Byte2Hex(data, " ", data.Length);

            return BytesToEscapedString(data);
        }

        private static string BytesToEscapedString(byte[] data)
        {
            var text = new StringBuilder();
            var plainBytes = new List<byte>();

            void FlushPlain()
            {
                if (plainBytes.Count == 0)
                    return;

                text.Append(Global.GetEncoding().GetString(plainBytes.ToArray()).Replace("\\", "\\\\"));
                plainBytes.Clear();
            }

            foreach (var value in data)
            {
                if (value == 0x5c)
                {
                    FlushPlain();
                    text.Append("\\\\");
                    continue;
                }

                if (value <= 0x1f || value == 0x7f)
                {
                    FlushPlain();
                    text.Append(ByteToEscapedSymbol(value));
                    continue;
                }

                plainBytes.Add(value);
            }

            FlushPlain();
            return text.ToString();
        }

        private static string ByteToEscapedSymbol(byte data)
        {
            switch (data)
            {
                case 0x00:
                    return "\\0";
                case 0x07:
                    return "\\a";
                case 0x08:
                    return "\\b";
                case 0x09:
                    return "\\t";
                case 0x0a:
                    return "\\n";
                case 0x0b:
                    return "\\v";
                case 0x0c:
                    return "\\f";
                case 0x0d:
                    return "\\r";
                case 0x1b:
                    return "\\e";
                default:
                    return $"\\x{data:X2}";
            }
        }

        internal static void MergePairedLog(
            List<ReplayStep> primary,
            List<ReplayStep> paired,
            bool primaryIsHex)
        {
            MergePairedLog(primary, paired, primaryIsHex, CancellationToken.None);
        }

        private static void MergePairedLog(
            List<ReplayStep> primary,
            List<ReplayStep> paired,
            bool primaryIsHex,
            CancellationToken cancellationToken)
        {
            var count = Math.Min(primary.Count, paired.Count);
            for (int index = 0; index < count; index++)
            {
                if ((index & 0x0fff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (primary[index].Direction != paired[index].Direction)
                    continue;

                if (primaryIsHex)
                    primary[index].Text = paired[index].Text;
                else
                    primary[index].Data = paired[index].Data;
            }
        }

        private static byte[] ParseEscapedStringBytes(
            string value,
            CancellationToken cancellationToken)
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

            for (int index = 0; index < value.Length; index++)
            {
                if ((index & 0x0fff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (value[index] != '\\' || index == value.Length - 1)
                {
                    plain.Append(value[index]);
                    continue;
                }

                var next = value[++index];
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
                        if (index + 2 < value.Length &&
                            byte.TryParse(
                                value.Substring(index + 1, 2),
                                System.Globalization.NumberStyles.HexNumber,
                                null,
                                out var parsedByte))
                        {
                            FlushPlain();
                            bytes.Add(parsedByte);
                            index += 2;
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

            cancellationToken.ThrowIfCancellationRequested();
            FlushPlain();
            return bytes.ToArray();
        }

        private void ClearReceiveBuffer()
        {
            lock (receiveLock)
                receiveBuffer.Clear();
        }

        private async Task<bool> WaitForReceiveAsync(
            byte[] expected,
            int timeoutMs,
            bool anyResponse,
            CancellationToken token)
        {
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
            {
                token.ThrowIfCancellationRequested();
                lock (receiveLock)
                {
                    if (anyResponse ? TryConsumeReceiveFrame(expected) : TryConsumeExpected(expected))
                        return true;
                }
                await Task.Delay(50, token);
            }
            return false;
        }

        private bool TryConsumeReceiveFrame(byte[] expected)
        {
            if (receiveBuffer.Count == 0)
                return false;

            if (Array.IndexOf(expected, (byte)0x0a) >= 0)
            {
                var lineEnd = receiveBuffer.IndexOf(0x0a);
                if (lineEnd < 0)
                    return false;

                receiveBuffer.RemoveRange(0, lineEnd + 1);
                return true;
            }

            var consumeCount = Math.Min(receiveBuffer.Count, Math.Max(1, expected.Length));
            receiveBuffer.RemoveRange(0, consumeCount);
            return true;
        }

        private bool TryConsumeExpected(byte[] expected)
        {
            if (receiveBuffer.Count == 0 || expected.Length == 0)
                return false;

            var mode = (MatchModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (mode == "exact")
            {
                if (!StartsWith(receiveBuffer, expected))
                    return false;

                receiveBuffer.RemoveRange(0, expected.Length);
                return true;
            }

            var index = IndexOf(receiveBuffer, expected);
            if (index < 0)
                return false;

            receiveBuffer.RemoveRange(0, index + expected.Length);
            return true;
        }

        private static bool StartsWith(List<byte> buffer, byte[] expected)
        {
            if (buffer.Count < expected.Length)
                return false;
            for (int index = 0; index < expected.Length; index++)
            {
                if (buffer[index] != expected[index])
                    return false;
            }
            return true;
        }

        private static int IndexOf(List<byte> buffer, byte[] expected)
        {
            if (expected.Length > buffer.Count)
                return -1;
            for (int index = 0; index <= buffer.Count - expected.Length; index++)
            {
                var matched = true;
                for (int expectedIndex = 0; expectedIndex < expected.Length; expectedIndex++)
                {
                    if (buffer[index + expectedIndex] != expected[expectedIndex])
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched)
                    return index;
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
        private bool ignoreResponseValue = false;
        private string status = "";
        private string text = "";

        public int Index { get; set; }
        public ReplayDirection Direction { get; set; }
        public string DirectionText => Direction == ReplayDirection.Send ? "send" : "recv";
        public bool IsSendStep => Direction == ReplayDirection.Send;
        public byte[] Data { get; set; } = new byte[0];

        public bool IgnoreResponseValue
        {
            get => ignoreResponseValue;
            set
            {
                ignoreResponseValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IgnoreResponseValue)));
            }
        }

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
