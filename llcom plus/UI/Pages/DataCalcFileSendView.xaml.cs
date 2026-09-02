using llcom_plus.Tools;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    public partial class DataCalcFileSendView : UserControl
    {
        private enum SendRunState
        {
            Ready,
            Sending,
            Paused,
            Completed,
            RestartRequired
        }

        private const int DefaultSendChunkSize = 4096;
        private const int FileReadBufferSize = 81920;
        private string selectedFilePath = "";
        private long selectedFileLength = 0;
        private CancellationTokenSource sendCts = null;
        private CancellationTokenSource calculationCts = null;
        private SendRunState sendState = SendRunState.Ready;
        private string resumeSourceKey = "";
        private long resumeSentBytes = 0;
        private long resumeTotalBytes = 0;
        private ActiveSerialTarget resumeTargetLease = null;
        private string resumeTargetIdentity = "";
        private DataCalcFileFingerprint resumeFileFingerprint = null;
        private long activeSentBytes = 0;
        private long nextSendRunId = 0;
        private long activeProgressRunId = 0;
        private long nextCalculationRunId = 0;
        private long activeCalculationRunId = 0;
        private bool isUnloaded = false;

        private sealed class TargetUnavailableException : IOException
        {
            internal TargetUnavailableException(string message)
                : base(message)
            {
            }
        }

        public DataCalcFileSendView()
        {
            InitializeComponent();
            ResetSendProgress();
            Loaded += DataCalcFileSendView_Loaded;
            Unloaded += DataCalcFileSendView_Unloaded;
        }

        private void DataCalcFileSendView_Loaded(object sender, RoutedEventArgs e)
        {
            isUnloaded = false;
            SetSendingState(Interlocked.CompareExchange(ref sendCts, null, null) != null);
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = TryFindResource("SendFileFilter") as string ?? "All files|*.*"
            };
            if (openFileDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            CancelCalculation();
            try
            {
                var fingerprint = DataCalcFileFingerprint.Capture(openFileDialog.FileName);
                selectedFilePath = fingerprint.FullPath;
                selectedFileLength = fingerprint.Length;
                FilePathTextBox.Text = selectedFilePath;
                SourceComboBox.SelectedIndex = 1;
                ClearSendSessionState();
                Interlocked.Exchange(ref activeSentBytes, 0);
                UpdateSendProgress(0, selectedFileLength);
            }
            catch (Exception ex)
            {
                selectedFilePath = "";
                selectedFileLength = 0;
                FilePathTextBox.Text = TryFindResource("DataCalcNoFileSelected") as string ?? "No file selected";
                ResetSendProgress();
                if (!isUnloaded)
                    Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex.Message);
            }
        }

        private void DataCalcFileSendView_Unloaded(object sender, RoutedEventArgs e)
        {
            isUnloaded = true;
            Interlocked.Exchange(ref activeProgressRunId, 0);
            var activeSend = Interlocked.CompareExchange(ref sendCts, null, null);
            if (activeSend != null)
            {
                try { activeSend.Cancel(); } catch (ObjectDisposedException) { }
            }
            CancelCalculation();
        }

        private async void SendSelectedDataButton_Click(object sender, RoutedEventArgs e)
        {
            var activeSend = Interlocked.CompareExchange(ref sendCts, null, null);
            if (activeSend != null)
            {
                activeSend.Cancel();
                return;
            }

            CancelCalculation();
            var previousState = sendState;
            var isFileSource = IsFileSourceSelected();
            var filePath = selectedFilePath;
            var manualText = ManualDataTextBox.Text ?? string.Empty;
            var manualIsHex = HexInputCheckBox.IsChecked == true;
            var encoding = (Encoding)Global.GetEncoding().Clone();
            var portNotOpenMessage = TryFindResource("DataCalcPortNotOpen") as string ?? "Please open the serial port first.";
            var noDataMessage = TryFindResource("DataCalcNoData") as string ?? "No data";

            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref sendCts, cts, null) != null)
            {
                cts.Dispose();
                return;
            }

            var runId = Interlocked.Increment(ref nextSendRunId);
            Interlocked.Exchange(ref activeProgressRunId, runId);
            sendState = SendRunState.Sending;
            SetSendingState(true);

            DataCalcFileFingerprint fileFingerprint = null;
            byte[] manualData = null;
            ActiveSerialTarget targetLease = null;
            string targetIdentity = null;
            string sourceKey = null;
            long totalLength = 0;
            long startOffset = 0;
            var sessionPrepared = false;
            var canResume = false;
            try
            {
                if (isFileSource)
                {
                    fileFingerprint = await Task.Run(
                        () => DataCalcFileFingerprint.CaptureWithContentIdentity(filePath, cts.Token),
                        cts.Token);
                    selectedFileLength = fileFingerprint.Length;
                }
                else
                {
                    manualData = await Task.Run(
                        () => ParseManualData(manualText, manualIsHex, encoding, cts.Token),
                        cts.Token);
                }

                cts.Token.ThrowIfCancellationRequested();
                totalLength = isFileSource ? fileFingerprint.Length : manualData?.LongLength ?? 0;
                if (totalLength <= 0)
                    throw new InvalidDataException(noDataMessage);

                sourceKey = BuildSourceKey(isFileSource, manualData, fileFingerprint);
                canResume = CanResume(previousState, sourceKey, totalLength, fileFingerprint);
                if (previousState == SendRunState.Paused && !canResume)
                {
                    RequireResend();
                    throw new IOException("The selected source changed after the send was paused. Start a new send.");
                }

                startOffset = canResume ? resumeSentBytes : 0;
                if (canResume)
                {
                    targetLease = resumeTargetLease;
                }
                else
                {
                    if (!Global.EnsureActiveSerialTargetOpen())
                    {
                        sendState = previousState;
                        return;
                    }
                    targetLease = Global.CaptureActiveSerialTarget();
                }

                targetIdentity = targetLease?.Identity;
                EnsureSameLeaseOpen(targetLease, targetIdentity, cts, portNotOpenMessage);

                sendState = SendRunState.Sending;
                resumeSourceKey = sourceKey;
                resumeTotalBytes = totalLength;
                resumeTargetLease = targetLease;
                resumeTargetIdentity = targetIdentity;
                resumeFileFingerprint = fileFingerprint;
                Interlocked.Exchange(ref activeSentBytes, startOffset);
                UpdateSendProgress(startOffset, totalLength);
                sessionPrepared = true;

                if (isFileSource)
                {
                    await SendSelectedFileAsync(
                        fileFingerprint,
                        totalLength,
                        startOffset,
                        targetLease,
                        targetIdentity,
                        runId,
                        cts,
                        portNotOpenMessage);
                }
                else
                {
                    await SendSelectedDataAsync(
                        manualData,
                        startOffset,
                        targetLease,
                        targetIdentity,
                        runId,
                        cts,
                        portNotOpenMessage);
                }

                var committedBytes = Math.Min(Interlocked.Read(ref activeSentBytes), totalLength);
                if (committedBytes != totalLength)
                    throw new IOException("The serial target did not commit all selected data.");

                sendState = SendRunState.Completed;
                resumeSentBytes = totalLength;
                if (IsCurrentSendRun(runId, cts))
                {
                    UpdateSendProgress(totalLength, totalLength);
                    var source = TryFindResource("NotificationFileSendSource") as string ?? "文件发送";
                    Global.PublishNotification(
                        string.Format(
                            TryFindResource("NotificationOperationCompletedTitleFormat") as string ?? "{0} 已完成",
                            source),
                        isFileSource
                            ? $"{Path.GetFileName(fileFingerprint.FullPath)} · {totalLength:N0} bytes"
                            : $"{totalLength:N0} bytes",
                        AppNotificationLevel.Success,
                        category: AppNotificationCategory.Task);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (!sessionPrepared)
                {
                    sendState = previousState;
                }
                else
                {
                    var committedBytes = Math.Min(Interlocked.Read(ref activeSentBytes), totalLength);
                    if (committedBytes >= totalLength)
                    {
                        sendState = SendRunState.Completed;
                        resumeSentBytes = totalLength;
                        if (IsCurrentSendRun(runId, cts))
                            UpdateSendProgress(totalLength, totalLength);
                    }
                    else if (committedBytes > 0 &&
                        targetLease?.SupportsResumableCommits == true &&
                        IsSameLeaseOpen(targetLease, targetIdentity))
                    {
                        sendState = SendRunState.Paused;
                        resumeSentBytes = committedBytes;
                        resumeTotalBytes = totalLength;
                        resumeSourceKey = sourceKey;
                        resumeFileFingerprint = fileFingerprint;
                        if (IsCurrentSendRun(runId, cts))
                            UpdateSendProgress(committedBytes, totalLength);
                    }
                    else if (startOffset > 0 &&
                        previousState == SendRunState.Paused &&
                        IsSameLeaseOpen(resumeTargetLease, resumeTargetIdentity))
                    {
                        sendState = SendRunState.Paused;
                    }
                    else
                    {
                        RequireResend();
                    }
                }

                if (IsCurrentSendRun(runId, cts))
                    SendProgressTextBlock.Text = TryFindResource("DataCalcSendCancelled") as string ?? "Stopped";
            }
            catch (TimeoutException ex)
            {
                RequireResend();
                ShowSendFailureIfCurrent(runId, cts, ex,
                    TryFindResource("DataCalcSendTimeout") as string ?? "Serial write timed out. Send stopped.");
            }
            catch (TargetUnavailableException ex)
            {
                RequireResend();
                ShowSendFailureIfCurrent(runId, cts, ex, null);
            }
            catch (IOException ex)
            {
                RequireResend();
                ShowSendFailureIfCurrent(runId, cts, ex, null);
            }
            catch (Exception ex)
            {
                RequireResend();
                ShowSendFailureIfCurrent(runId, cts, ex, null);
            }
            finally
            {
                Interlocked.CompareExchange(ref activeProgressRunId, 0, runId);
                var detached = ReferenceEquals(
                    Interlocked.CompareExchange(ref sendCts, null, cts),
                    cts);
                cts.Dispose();
                if (detached && !isUnloaded)
                    SetSendingState(false);
            }
        }

        private void ShowSendFailureIfCurrent(
            long runId,
            CancellationTokenSource cts,
            Exception exception,
            string heading)
        {
            if (!IsCurrentSendRun(runId, cts))
                return;

            PublishSendFailure(exception.Message);
            Tools.MessageBox.Show(string.IsNullOrWhiteSpace(heading)
                ? exception.Message
                : heading + "\r\n" + exception.Message);
        }

        private bool IsCurrentSendRun(long runId, CancellationTokenSource cts)
        {
            return !isUnloaded &&
                   Interlocked.Read(ref activeProgressRunId) == runId &&
                   ReferenceEquals(Interlocked.CompareExchange(ref sendCts, null, null), cts);
        }

        private void PublishSendFailure(string detail)
        {
            var source = TryFindResource("NotificationFileSendSource") as string ?? "文件发送";
            Global.PublishNotification(
                string.Format(
                    TryFindResource("NotificationOperationFailedTitleFormat") as string ?? "{0} 失败",
                    source),
                detail ?? string.Empty,
                AppNotificationLevel.Error,
                category: AppNotificationCategory.Task);
        }

        private async void CalculateDataButton_Click(object sender, RoutedEventArgs e)
        {
            CancelCalculation();
            var isFileSource = IsFileSourceSelected();
            var filePath = selectedFilePath;
            var manualText = ManualDataTextBox.Text ?? string.Empty;
            var manualIsHex = HexInputCheckBox.IsChecked == true;
            var encoding = (Encoding)Global.GetEncoding().Clone();
            var noDataMessage = TryFindResource("DataCalcNoData") as string ?? "No data";

            var cts = new CancellationTokenSource();
            var runId = Interlocked.Increment(ref nextCalculationRunId);
            calculationCts = cts;
            Interlocked.Exchange(ref activeCalculationRunId, runId);
            CalculateDataButton.IsEnabled = false;
            try
            {
                DataCalcResult result;
                if (isFileSource)
                {
                    result = await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var fingerprint = DataCalcFileFingerprint.Capture(filePath);
                        if (fingerprint.Length <= 0)
                            throw new InvalidDataException(noDataMessage);
                        return CalculateFile(fingerprint, cts.Token);
                    }, cts.Token);
                }
                else
                {
                    result = await Task.Run(() =>
                    {
                        var data = ParseManualData(manualText, manualIsHex, encoding, cts.Token);
                        if (data.LongLength <= 0)
                            throw new InvalidDataException(noDataMessage);
                        using (var stream = new MemoryStream(data, false))
                            return DataCalcCalculator.Calculate(stream, cts.Token);
                    }, cts.Token);
                }

                if (!IsCurrentCalculation(runId, cts))
                    return;

                var window = new DataCalcWindow(result)
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentCalculation(runId, cts))
                    Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex.Message);
            }
            finally
            {
                var detached = ReferenceEquals(
                    Interlocked.CompareExchange(ref calculationCts, null, cts),
                    cts);
                Interlocked.CompareExchange(ref activeCalculationRunId, 0, runId);
                cts.Dispose();
                if (detached && !isUnloaded && CalculateDataButton != null)
                    CalculateDataButton.IsEnabled = sendCts == null;
            }
        }

        private static DataCalcResult CalculateFile(
            DataCalcFileFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            DataCalcResult result;
            using (var stream = fingerprint.OpenRead(FileReadBufferSize, cancellationToken))
                result = DataCalcCalculator.Calculate(stream, cancellationToken);
            fingerprint.VerifyUnchanged(cancellationToken);
            return result;
        }

        private bool IsCurrentCalculation(long runId, CancellationTokenSource cts)
        {
            return IsCalculationResultCurrent(
                       runId,
                       Interlocked.Read(ref activeCalculationRunId),
                       isUnloaded) &&
                   ReferenceEquals(Interlocked.CompareExchange(ref calculationCts, null, null), cts);
        }

        internal static bool IsCalculationResultCurrent(
            long resultRunId,
            long activeRunId,
            bool unloaded)
        {
            return !unloaded && resultRunId != 0 && resultRunId == activeRunId;
        }

        private void CancelCalculation()
        {
            Interlocked.Exchange(ref activeCalculationRunId, 0);
            var cts = Interlocked.Exchange(ref calculationCts, null);
            if (cts == null)
                return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        private static byte[] ParseManualData(
            string text,
            bool isHex,
            Encoding encoding,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = isHex
                ? Global.Hex2Byte(text ?? string.Empty)
                : (encoding ?? Encoding.UTF8).GetBytes(text ?? string.Empty);
            cancellationToken.ThrowIfCancellationRequested();
            return data;
        }

        private bool IsFileSourceSelected()
        {
            return SourceComboBox != null && SourceComboBox.SelectedIndex == 1;
        }

        private Task SendSelectedDataAsync(
            byte[] data,
            long startOffset,
            ActiveSerialTarget targetLease,
            string targetIdentity,
            long runId,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            return Task.Run(
                () => SendSelectedDataOnWorker(
                    data,
                    startOffset,
                    targetLease,
                    targetIdentity,
                    runId,
                    runCts,
                    portNotOpenMessage),
                runCts.Token);
        }

        private Task SendSelectedFileAsync(
            DataCalcFileFingerprint fingerprint,
            long totalLength,
            long startOffset,
            ActiveSerialTarget targetLease,
            string targetIdentity,
            long runId,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            return Task.Run(
                () => SendSelectedFileOnWorker(
                    fingerprint,
                    totalLength,
                    startOffset,
                    targetLease,
                    targetIdentity,
                    runId,
                    runCts,
                    portNotOpenMessage),
                runCts.Token);
        }

        private void SendSelectedDataOnWorker(
            byte[] data,
            long startOffset,
            ActiveSerialTarget targetLease,
            string targetIdentity,
            long runId,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            var token = runCts.Token;
            token.ThrowIfCancellationRequested();
            EnsureSameLeaseOpen(targetLease, targetIdentity, runCts, portNotOpenMessage);

            var chunkSize = GetSendChunkSize();
            var delayMs = Math.Max(0, Global.setting?.sendThrottleDelayMs ?? 0);
            var progressWatch = Stopwatch.StartNew();
            var startIndex = (int)Math.Max(0, Math.Min(startOffset, data.Length));
            for (var offset = startIndex; offset < data.Length; offset += chunkSize)
            {
                token.ThrowIfCancellationRequested();
                EnsureSameLeaseOpen(targetLease, targetIdentity, runCts, portNotOpenMessage);

                var count = Math.Min(chunkSize, data.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(data, offset, chunk, 0, count);
                SendChunk(
                    targetLease,
                    targetIdentity,
                    chunk,
                    data.LongLength,
                    runId,
                    progressWatch,
                    runCts,
                    portNotOpenMessage);

                if (offset + count < data.Length && delayMs > 0)
                    WaitSendDelay(delayMs, token);
            }
        }

        private void SendSelectedFileOnWorker(
            DataCalcFileFingerprint fingerprint,
            long totalLength,
            long startOffset,
            ActiveSerialTarget targetLease,
            string targetIdentity,
            long runId,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            var token = runCts.Token;
            token.ThrowIfCancellationRequested();
            EnsureSameLeaseOpen(targetLease, targetIdentity, runCts, portNotOpenMessage);
            if (totalLength != fingerprint.Length || !fingerprint.HasContentIdentity)
                throw new IOException("The selected file identity is unavailable or changed before sending.");

            var chunkSize = Math.Max(1, Math.Min(GetSendChunkSize(), FileReadBufferSize));
            var delayMs = Math.Max(0, Global.setting?.sendThrottleDelayMs ?? 0);
            var progressWatch = Stopwatch.StartNew();
            long sourceOffset = Math.Max(0, Math.Min(startOffset, totalLength));

            using (var stream = fingerprint.OpenVerifiedRead(FileReadBufferSize, token))
            {
                if (sourceOffset > 0)
                    stream.Seek(sourceOffset, SeekOrigin.Begin);

                var buffer = new byte[chunkSize];
                while (sourceOffset < totalLength)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureSameLeaseOpen(targetLease, targetIdentity, runCts, portNotOpenMessage);

                    var requested = (int)Math.Min(buffer.Length, totalLength - sourceOffset);
                    var read = stream.ReadAsync(buffer, 0, requested, token)
                        .GetAwaiter()
                        .GetResult();
                    if (read <= 0)
                        throw new EndOfStreamException("The selected file ended before its recorded length.");

                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    SendChunk(
                        targetLease,
                        targetIdentity,
                        chunk,
                        totalLength,
                        runId,
                        progressWatch,
                        runCts,
                        portNotOpenMessage);
                    sourceOffset += read;

                    if (sourceOffset < totalLength && delayMs > 0)
                        WaitSendDelay(delayMs, token);
                }
            }
            fingerprint.VerifyUnchanged(token);
        }

        private void SendChunk(
            ActiveSerialTarget targetLease,
            string targetIdentity,
            byte[] chunk,
            long totalLength,
            long runId,
            Stopwatch progressWatch,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            var token = runCts.Token;
            long committedForChunk = 0;
            var sent = targetLease.Send(
                chunk,
                token,
                count =>
                {
                    if (count <= 0)
                        return;

                    Interlocked.Add(ref committedForChunk, count);
                    var committedTotal = AdvanceActiveSentBytes(count, totalLength);
                    if (committedTotal >= totalLength || progressWatch.ElapsedMilliseconds >= 80)
                    {
                        PostSendProgress(committedTotal, totalLength, runId);
                        progressWatch.Restart();
                    }
                });

            if (!sent || !IsSameLeaseOpen(targetLease, targetIdentity))
                CancelForUnavailable(runCts, portNotOpenMessage);
            if (Interlocked.Read(ref committedForChunk) != chunk.LongLength)
                throw new IOException("The serial target reported an incomplete committed byte count.");
        }

        private long AdvanceActiveSentBytes(int count, long totalLength)
        {
            while (true)
            {
                var current = Interlocked.Read(ref activeSentBytes);
                if (current >= totalLength)
                    return current;

                var increment = Math.Min((long)count, totalLength - current);
                var next = current + increment;
                if (Interlocked.CompareExchange(ref activeSentBytes, next, current) == current)
                    return next;
            }
        }

        private static void WaitSendDelay(int delayMs, CancellationToken token)
        {
            if (token.WaitHandle.WaitOne(delayMs))
                token.ThrowIfCancellationRequested();
        }

        private static int GetSendChunkSize()
        {
            var packetSize = Math.Max(0, Global.setting?.sendThrottlePacketSize ?? 0);
            return packetSize > 0 ? packetSize : DefaultSendChunkSize;
        }

        private static void EnsureSameLeaseOpen(
            ActiveSerialTarget targetLease,
            string targetIdentity,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            if (IsSameLeaseOpen(targetLease, targetIdentity))
                return;
            CancelForUnavailable(runCts, portNotOpenMessage);
        }

        private static void CancelForUnavailable(
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            try { runCts.Cancel(); } catch (ObjectDisposedException) { }
            throw new TargetUnavailableException(portNotOpenMessage);
        }

        private static bool IsSameLeaseOpen(ActiveSerialTarget targetLease, string targetIdentity)
        {
            return targetLease != null &&
                   string.Equals(targetLease.Identity, targetIdentity, StringComparison.Ordinal) &&
                   targetLease.IsOpen;
        }

        private void ResetSendProgress()
        {
            Interlocked.Exchange(ref activeSentBytes, 0);
            if (SendProgressBar == null || SendProgressTextBlock == null)
                return;

            SendProgressBar.Value = 0;
            SendProgressTextBlock.Text = TryFindResource("DataCalcSendProgressIdle") as string ?? "0/0 bytes (0%)";
        }

        private void UpdateSendProgress(long sentBytes, long totalBytes)
        {
            if (SendProgressBar == null || SendProgressTextBlock == null)
                return;

            totalBytes = Math.Max(0, totalBytes);
            sentBytes = Math.Max(0, Math.Min(sentBytes, totalBytes));
            var percent = totalBytes == 0 ? 0 : sentBytes * 100.0 / totalBytes;

            SendProgressBar.Value = percent;
            SendProgressTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                TryFindResource("DataCalcSendProgressFormat") as string ?? "{0}/{1} bytes ({2:0}%)",
                sentBytes,
                totalBytes,
                percent);
        }

        private void PostSendProgress(long sentBytes, long totalBytes, long runId)
        {
            Action update = () =>
            {
                if (isUnloaded || Interlocked.Read(ref activeProgressRunId) != runId)
                    return;
                var latestCommitted = Math.Min(Interlocked.Read(ref activeSentBytes), totalBytes);
                UpdateSendProgress(Math.Max(sentBytes, latestCommitted), totalBytes);
            };

            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;
                if (Dispatcher.CheckAccess())
                    update();
                else
                    Dispatcher.BeginInvoke(update);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string BuildSourceKey(
            bool isFileSource,
            byte[] manualData,
            DataCalcFileFingerprint fileFingerprint)
        {
            if (isFileSource)
            {
                return fileFingerprint == null || !fileFingerprint.HasContentIdentity
                    ? "file|missing"
                    : $"file|{fileFingerprint.FullPath}|{fileFingerprint.Length}|{fileFingerprint.ContentSha256}";
            }

            return $"manual|{manualData?.LongLength ?? 0}|{DataCalcFileFingerprint.ComputeSha256(manualData)}";
        }

        private bool CanResume(
            SendRunState previousState,
            string sourceKey,
            long totalLength,
            DataCalcFileFingerprint fileFingerprint)
        {
            var fingerprintMatches = fileFingerprint == null
                ? resumeFileFingerprint == null
                : fileFingerprint.MatchesContentIdentity(resumeFileFingerprint);
            return previousState == SendRunState.Paused &&
                   string.Equals(sourceKey, resumeSourceKey, StringComparison.Ordinal) &&
                   totalLength == resumeTotalBytes &&
                   resumeSentBytes > 0 &&
                   resumeSentBytes < totalLength &&
                   fingerprintMatches &&
                   IsSameLeaseOpen(resumeTargetLease, resumeTargetIdentity);
        }

        private void ClearSendSessionState()
        {
            if (sendState == SendRunState.Sending)
                return;
            ResetSendSession(SendRunState.Ready);
        }

        private void RequireResend()
        {
            ResetSendSession(SendRunState.RestartRequired);
        }

        private void ResetSendSession(SendRunState nextState)
        {
            sendState = nextState;
            resumeSourceKey = "";
            resumeSentBytes = 0;
            resumeTotalBytes = 0;
            resumeTargetLease = null;
            resumeTargetIdentity = "";
            resumeFileFingerprint = null;
            UpdateSendButtonContent(false);
        }

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CancelCalculation();
            ClearSendSessionState();
        }

        private void ManualDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            CancelCalculation();
            ClearSendSessionState();
        }

        private void InputSourceChanged(object sender, RoutedEventArgs e)
        {
            CancelCalculation();
            ClearSendSessionState();
        }

        private void SetSendingState(bool sending)
        {
            UpdateSendButtonContent(sending);
            if (SelectFileButton == null)
                return;
            SelectFileButton.IsEnabled = !sending;
            SourceComboBox.IsEnabled = !sending;
            HexInputCheckBox.IsEnabled = !sending;
            ManualDataTextBox.IsReadOnly = sending;
            CalculateDataButton.IsEnabled = !sending && calculationCts == null;
        }

        private void UpdateSendButtonContent(bool sending)
        {
            if (SendSelectedDataButton == null)
                return;

            string resourceKey;
            string fallback;
            if (sending)
            {
                resourceKey = "DataCalcStopSend";
                fallback = "Stop";
            }
            else if (sendState == SendRunState.Paused)
            {
                resourceKey = "DataCalcResumeSend";
                fallback = "Resume";
            }
            else if (sendState == SendRunState.Completed || sendState == SendRunState.RestartRequired)
            {
                resourceKey = "DataCalcResend";
                fallback = "Resend";
            }
            else
            {
                resourceKey = "DataCalcSendSelected";
                fallback = "Send Selected";
            }

            SendSelectedDataButton.Content = TryFindResource(resourceKey) as string ?? fallback;
        }
    }
}
