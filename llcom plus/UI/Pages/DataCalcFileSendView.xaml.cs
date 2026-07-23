using llcom_plus.Tools;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
            Completed
        }

        private const int DefaultSendChunkSize = 4096;
        private const int FileReadBufferSize = 81920;
        private string selectedFilePath = "";
        private long selectedFileLength = 0;
        private CancellationTokenSource sendCts = null;
        private SendRunState sendState = SendRunState.Ready;
        private string resumeSourceKey = "";
        private long resumeSentBytes = 0;
        private long resumeTotalBytes = 0;
        private long activeSentBytes = 0;

        public DataCalcFileSendView()
        {
            InitializeComponent();
            ResetSendProgress();
            Unloaded += DataCalcFileSendView_Unloaded;
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = TryFindResource("SendFileFilter") as string ?? "All files|*.*"
            };
            if (openFileDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            try
            {
                selectedFilePath = openFileDialog.FileName;
                selectedFileLength = new FileInfo(selectedFilePath).Length;
                FilePathTextBox.Text = selectedFilePath;
                SourceComboBox.SelectedIndex = 1;
                ClearSendSessionState();
                UpdateSendProgress(0, selectedFileLength);
            }
            catch (Exception ex)
            {
                selectedFilePath = "";
                selectedFileLength = 0;
                FilePathTextBox.Text = TryFindResource("DataCalcNoFileSelected") as string ?? "No file selected";
                ResetSendProgress();
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex);
            }
        }

        private void DataCalcFileSendView_Unloaded(object sender, RoutedEventArgs e)
        {
            sendCts?.Cancel();
        }

        private async void SendSelectedDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (sendCts != null)
            {
                sendCts.Cancel();
                return;
            }

            var isFileSource = IsFileSourceSelected();
            var manualData = isFileSource ? null : GetManualDataBytes();
            var totalLength = isFileSource ? GetSelectedFileLength() : (manualData?.Length ?? 0);
            var sourceKey = BuildSourceKey(isFileSource, manualData);
            var canResume = CanResume(sourceKey, totalLength);
            if (sendState == SendRunState.Paused && !canResume)
                ClearSendSessionState();
            var startOffset = canResume ? resumeSentBytes : 0;
            if (totalLength <= 0)
            {
                Tools.MessageBox.Show(TryFindResource("DataCalcNoData") as string ?? "No data");
                return;
            }

            if (!Global.EnsureActiveSerialTargetOpen())
                return;

            var cts = new CancellationTokenSource();
            sendCts = cts;
            sendState = SendRunState.Sending;
            resumeSourceKey = sourceKey;
            resumeTotalBytes = totalLength;
            Interlocked.Exchange(ref activeSentBytes, startOffset);
            SetSendingState(true);
            UpdateSendProgress(startOffset, totalLength);
            try
            {
                if (isFileSource)
                    await SendSelectedFileAsync(selectedFilePath, totalLength, startOffset, cts.Token);
                else
                    await SendSelectedDataAsync(manualData, startOffset, cts.Token);
                sendState = SendRunState.Completed;
                resumeSentBytes = totalLength;
                UpdateSendProgress(totalLength, totalLength);
                var source = TryFindResource("NotificationFileSendSource") as string ?? "文件发送";
                Global.PublishNotification(
                    string.Format(
                        TryFindResource("NotificationOperationCompletedTitleFormat") as string ?? "{0} 已完成",
                        source),
                    isFileSource
                        ? $"{Path.GetFileName(selectedFilePath)} · {totalLength:N0} bytes"
                        : $"{totalLength:N0} bytes",
                    AppNotificationLevel.Success,
                    category: AppNotificationCategory.Task);
            }
            catch (OperationCanceledException)
            {
                var sentBytes = Math.Min(Interlocked.Read(ref activeSentBytes), totalLength);
                if (sentBytes > 0 && sentBytes < totalLength)
                {
                    sendState = SendRunState.Paused;
                    resumeSentBytes = sentBytes;
                    resumeTotalBytes = totalLength;
                    resumeSourceKey = sourceKey;
                    UpdateSendProgress(sentBytes, totalLength);
                }
                else
                {
                    sendState = SendRunState.Ready;
                    resumeSentBytes = 0;
                    resumeTotalBytes = 0;
                }
                SendProgressTextBlock.Text = TryFindResource("DataCalcSendCancelled") as string ?? "Stopped";
            }
            catch (TimeoutException ex)
            {
                sendState = SendRunState.Ready;
                PublishSendFailure(ex.Message);
                Tools.MessageBox.Show($"{TryFindResource("DataCalcSendTimeout") as string ?? "Serial write timed out. Send stopped."}\r\n" + ex.Message);
            }
            catch (Exception ex)
            {
                sendState = SendRunState.Ready;
                PublishSendFailure(ex.Message);
                Tools.MessageBox.Show(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(sendCts, cts))
                    sendCts = null;
                cts.Dispose();
                SetSendingState(false);
            }
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

        private void CalculateDataButton_Click(object sender, RoutedEventArgs e)
        {
            var data = GetSelectedSourceBytes();
            if (data == null || data.Length == 0)
            {
                Tools.MessageBox.Show(TryFindResource("DataCalcNoData") as string ?? "No data");
                return;
            }

            var window = new DataCalcWindow(DataCalcCalculator.Calculate(data))
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private byte[] GetSelectedSourceBytes()
        {
            if (IsFileSourceSelected())
                return File.Exists(selectedFilePath) ? File.ReadAllBytes(selectedFilePath) : null;
            return GetManualDataBytes();
        }

        private byte[] GetManualDataBytes()
        {
            return HexInputCheckBox.IsChecked == true ?
                Global.Hex2Byte(ManualDataTextBox.Text ?? "") :
                Global.GetEncoding().GetBytes(ManualDataTextBox.Text ?? "");
        }

        private bool IsFileSourceSelected()
        {
            return SourceComboBox != null && SourceComboBox.SelectedIndex == 1;
        }

        private long GetSelectedFileLength()
        {
            if (string.IsNullOrWhiteSpace(selectedFilePath) || !File.Exists(selectedFilePath))
                return 0;

            try
            {
                selectedFileLength = new FileInfo(selectedFilePath).Length;
                return selectedFileLength;
            }
            catch
            {
                return 0;
            }
        }

        private Task SendSelectedDataAsync(byte[] data, long startOffset, CancellationToken token)
        {
            var portNotOpenMessage = TryFindResource("DataCalcPortNotOpen") as string ?? "Please open the serial port first.";
            return Task.Run(() => SendSelectedDataOnWorker(data, startOffset, token, portNotOpenMessage), token);
        }

        private Task SendSelectedFileAsync(string filePath, long totalLength, long startOffset, CancellationToken token)
        {
            var portNotOpenMessage = TryFindResource("DataCalcPortNotOpen") as string ?? "Please open the serial port first.";
            return Task.Run(() => SendSelectedFileOnWorker(filePath, totalLength, startOffset, token, portNotOpenMessage), token);
        }

        private void SendSelectedDataOnWorker(byte[] data, long startOffset, CancellationToken token, string portNotOpenMessage)
        {
            token.ThrowIfCancellationRequested();
            if (!Global.IsActiveSerialTargetOpen())
                throw new InvalidOperationException(portNotOpenMessage);

            var chunkSize = GetSendChunkSize();
            var delayMs = Math.Max(0, Global.setting?.sendThrottleDelayMs ?? 0);
            var progressWatch = Stopwatch.StartNew();
            var startIndex = (int)Math.Max(0, Math.Min(startOffset, data.Length));
            for (var offset = startIndex; offset < data.Length; offset += chunkSize)
            {
                token.ThrowIfCancellationRequested();
                if (!Global.IsActiveSerialTargetOpen())
                    throw new InvalidOperationException(portNotOpenMessage);

                var count = Math.Min(chunkSize, data.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(data, offset, chunk, 0, count);
                if (!Global.SendRawDataToActiveTarget(chunk, token))
                    throw new InvalidOperationException(portNotOpenMessage);

                var sentBytes = offset + count;
                if (sentBytes >= data.Length || progressWatch.ElapsedMilliseconds >= 80)
                {
                    PostSendProgress(sentBytes, data.Length);
                    progressWatch.Restart();
                }

                if (sentBytes >= data.Length)
                    continue;

                if (delayMs > 0)
                    WaitSendDelay(delayMs, token);
            }
        }

        private void SendSelectedFileOnWorker(string filePath, long totalLength, long startOffset, CancellationToken token, string portNotOpenMessage)
        {
            token.ThrowIfCancellationRequested();
            if (!Global.IsActiveSerialTargetOpen())
                throw new InvalidOperationException(portNotOpenMessage);

            var chunkSize = Math.Max(1, Math.Min(GetSendChunkSize(), FileReadBufferSize));
            var delayMs = Math.Max(0, Global.setting?.sendThrottleDelayMs ?? 0);
            var progressWatch = Stopwatch.StartNew();
            long sentBytes = Math.Max(0, Math.Min(startOffset, totalLength));

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileReadBufferSize))
            {
                if (sentBytes > 0)
                    stream.Seek(sentBytes, SeekOrigin.Begin);

                var buffer = new byte[chunkSize];
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Global.IsActiveSerialTargetOpen())
                        throw new InvalidOperationException(portNotOpenMessage);

                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    if (!Global.SendRawDataToActiveTarget(chunk, token))
                        throw new InvalidOperationException(portNotOpenMessage);

                    sentBytes += read;
                    if (sentBytes >= totalLength || progressWatch.ElapsedMilliseconds >= 80)
                    {
                        PostSendProgress(sentBytes, totalLength);
                        progressWatch.Restart();
                    }

                    if (delayMs > 0 && sentBytes < totalLength)
                        WaitSendDelay(delayMs, token);
                }
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

        private void ResetSendProgress()
        {
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
            Interlocked.Exchange(ref activeSentBytes, sentBytes);
            var percent = totalBytes == 0 ? 0 : sentBytes * 100.0 / totalBytes;

            SendProgressBar.Value = percent;
            SendProgressTextBlock.Text = string.Format(
                CultureInfo.CurrentCulture,
                TryFindResource("DataCalcSendProgressFormat") as string ?? "{0}/{1} bytes ({2:0}%)",
                sentBytes,
                totalBytes,
                percent);
        }

        private void PostSendProgress(long sentBytes, long totalBytes)
        {
            Interlocked.Exchange(ref activeSentBytes, sentBytes);
            if (Dispatcher.CheckAccess())
            {
                UpdateSendProgress(sentBytes, totalBytes);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => UpdateSendProgress(sentBytes, totalBytes)));
        }

        private string BuildSourceKey(bool isFileSource, byte[] manualData)
        {
            if (isFileSource)
            {
                try
                {
                    var fileInfo = new FileInfo(selectedFilePath);
                    return $"file|{fileInfo.FullName}|{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
                }
                catch
                {
                    return $"file|{selectedFilePath}";
                }
            }

            return $"manual|{HexInputCheckBox.IsChecked == true}|{ManualDataTextBox.Text ?? ""}|{manualData?.Length ?? 0}";
        }

        private bool CanResume(string sourceKey, long totalLength)
        {
            return sendState == SendRunState.Paused &&
                   string.Equals(sourceKey, resumeSourceKey, StringComparison.Ordinal) &&
                   totalLength == resumeTotalBytes &&
                   resumeSentBytes > 0 &&
                   resumeSentBytes < totalLength;
        }

        private void ClearSendSessionState()
        {
            if (sendState == SendRunState.Sending)
                return;

            sendState = SendRunState.Ready;
            resumeSourceKey = "";
            resumeSentBytes = 0;
            resumeTotalBytes = 0;
            UpdateSendButtonContent(false);
        }

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ClearSendSessionState();
        }

        private void ManualDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearSendSessionState();
        }

        private void InputSourceChanged(object sender, RoutedEventArgs e)
        {
            ClearSendSessionState();
        }

        private void SetSendingState(bool sending)
        {
            UpdateSendButtonContent(sending);
            SelectFileButton.IsEnabled = !sending;
            SourceComboBox.IsEnabled = !sending;
            HexInputCheckBox.IsEnabled = !sending;
            ManualDataTextBox.IsReadOnly = sending;
            CalculateDataButton.IsEnabled = !sending;
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
            else if (sendState == SendRunState.Completed)
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
