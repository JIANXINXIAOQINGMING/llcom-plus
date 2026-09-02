using llcom_plus.Tools;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    /// <summary>
    /// DataCalcPage.xaml 的交互逻辑
    /// </summary>
    public partial class DataCalcPage : Page
    {
        private const int FileReadBufferSize = 81920;
        private const int ManualCalculationDebounceMilliseconds = 250;
        private string selectedFilePath = "";
        private CancellationTokenSource calculationCts = null;
        private CancellationTokenSource sendCts = null;
        private int calculationVersion = 0;
        private long sendRunVersion = 0;
        private bool isUnloaded = false;
        private Button activeSendButton = null;

        private sealed class TargetUnavailableException : IOException
        {
            internal TargetUnavailableException(string message)
                : base(message)
            {
            }
        }

        public DataCalcPage()
        {
            InitializeComponent();
            Loaded += DataCalcPage_Loaded;
            Unloaded += DataCalcPage_Unloaded;
            _ = UpdateSelectedSourceResultAsync();
        }

        private void DataCalcPage_Loaded(object sender, RoutedEventArgs e)
        {
            var wasUnloaded = isUnloaded;
            isUnloaded = false;
            if (wasUnloaded)
                _ = UpdateSelectedSourceResultAsync();
        }

        private void DataCalcPage_Unloaded(object sender, RoutedEventArgs e)
        {
            isUnloaded = true;
            Interlocked.Increment(ref calculationVersion);
            Interlocked.Increment(ref sendRunVersion);
            CancelAndDetach(ref calculationCts);
            CancelAndDetach(ref sendCts);
        }

        private static void CancelAndDetach(ref CancellationTokenSource source)
        {
            var cts = Interlocked.Exchange(ref source, null);
            if (cts == null)
                return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
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
                var fingerprint = DataCalcFileFingerprint.Capture(openFileDialog.FileName);
                selectedFilePath = fingerprint.FullPath;
                FilePathTextBox.Text = selectedFilePath;
                var sourceChanged = SourceComboBox.SelectedIndex != 1;
                SourceComboBox.SelectedIndex = 1;
                if (!sourceChanged)
                    _ = UpdateSelectedSourceResultAsync();
            }
            catch (Exception ex)
            {
                selectedFilePath = "";
                if (!isUnloaded)
                    Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex.Message);
            }
        }

        private async void SendSelectedDataButton_Click(object sender, RoutedEventArgs e)
        {
            var runningCts = Interlocked.CompareExchange(ref sendCts, null, null);
            if (runningCts != null)
            {
                runningCts.Cancel();
                return;
            }

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

            var runId = Interlocked.Increment(ref sendRunVersion);
            activeSendButton = sender as Button;
            SetSendingState(true);
            try
            {
                DataCalcFileFingerprint fingerprint = null;
                byte[] manualData = null;
                if (isFileSource)
                {
                    fingerprint = await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var captured = DataCalcFileFingerprint.Capture(filePath);
                        cts.Token.ThrowIfCancellationRequested();
                        return captured;
                    }, cts.Token);
                }
                else
                {
                    manualData = await Task.Run(
                        () => ParseManualData(manualText, manualIsHex, encoding, cts.Token),
                        cts.Token);
                }

                cts.Token.ThrowIfCancellationRequested();
                var totalLength = isFileSource ? fingerprint.Length : manualData?.LongLength ?? 0;
                if (totalLength <= 0)
                    throw new InvalidDataException(noDataMessage);

                if (!IsCurrentSend(runId, cts))
                    return;
                if (!Global.EnsureActiveSerialTargetOpen())
                    return;

                var targetLease = Global.CaptureActiveSerialTarget();
                EnsureTargetAvailable(targetLease, targetLease?.Identity, cts, portNotOpenMessage);
                var targetIdentity = targetLease.Identity;

                if (isFileSource)
                {
                    await Task.Run(
                        () => SendFileOnWorker(
                            fingerprint,
                            targetLease,
                            targetIdentity,
                            cts,
                            portNotOpenMessage),
                        cts.Token);
                }
                else
                {
                    await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        EnsureTargetAvailable(targetLease, targetIdentity, cts, portNotOpenMessage);
                        if (!targetLease.Send(manualData, cts.Token))
                            CancelForUnavailable(cts, portNotOpenMessage);
                        EnsureTargetAvailable(targetLease, targetIdentity, cts, portNotOpenMessage);
                    }, cts.Token);
                }
            }
            catch (TargetUnavailableException ex)
            {
                if (IsCurrentSend(runId, cts))
                    Tools.MessageBox.Show(ex.Message);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentSend(runId, cts))
                    Tools.MessageBox.Show(ex.Message);
            }
            finally
            {
                var detached = ReferenceEquals(
                    Interlocked.CompareExchange(ref sendCts, null, cts),
                    cts);
                cts.Dispose();
                if (detached && !isUnloaded && runId == Interlocked.Read(ref sendRunVersion))
                    SetSendingState(false);
            }
        }

        private static void SendFileOnWorker(
            DataCalcFileFingerprint fingerprint,
            ActiveSerialTarget targetLease,
            string targetIdentity,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            var token = runCts.Token;
            using (var stream = fingerprint.OpenRead(FileReadBufferSize, token))
            {
                var buffer = new byte[FileReadBufferSize];
                long sentBytes = 0;
                while (sentBytes < fingerprint.Length)
                {
                    token.ThrowIfCancellationRequested();
                    EnsureTargetAvailable(targetLease, targetIdentity, runCts, portNotOpenMessage);

                    var requested = (int)Math.Min(buffer.Length, fingerprint.Length - sentBytes);
                    var read = stream.ReadAsync(buffer, 0, requested, token)
                        .GetAwaiter()
                        .GetResult();
                    if (read <= 0)
                        throw new EndOfStreamException("The selected file ended before its recorded length.");

                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    if (!targetLease.Send(chunk, token))
                        CancelForUnavailable(runCts, portNotOpenMessage);
                    EnsureTargetAvailable(targetLease, targetIdentity, runCts, portNotOpenMessage);
                    sentBytes += read;
                }
            }
            fingerprint.VerifyUnchanged(token);
        }

        private static void EnsureTargetAvailable(
            ActiveSerialTarget targetLease,
            string targetIdentity,
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            if (targetLease != null &&
                string.Equals(targetLease.Identity, targetIdentity, StringComparison.Ordinal) &&
                targetLease.IsOpen)
            {
                return;
            }

            CancelForUnavailable(runCts, portNotOpenMessage);
        }

        private static void CancelForUnavailable(
            CancellationTokenSource runCts,
            string portNotOpenMessage)
        {
            try { runCts.Cancel(); } catch (ObjectDisposedException) { }
            throw new TargetUnavailableException(portNotOpenMessage);
        }

        private bool IsCurrentSend(long runId, CancellationTokenSource cts)
        {
            return !isUnloaded &&
                   runId == Interlocked.Read(ref sendRunVersion) &&
                   ReferenceEquals(Interlocked.CompareExchange(ref sendCts, null, null), cts);
        }

        private void SetSendingState(bool sending)
        {
            SelectFileButton.IsEnabled = !sending;
            SourceComboBox.IsEnabled = !sending;
            HexInputCheckBox.IsEnabled = !sending;
            ManualDataTextBox.IsReadOnly = sending;
            if (activeSendButton != null)
            {
                activeSendButton.IsEnabled = true;
                activeSendButton.SetResourceReference(
                    ContentControl.ContentProperty,
                    sending ? "DataCalcStopSend" : "DataCalcSendSelected");
            }
        }

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _ = UpdateSelectedSourceResultAsync();
        }

        private void InputChanged(object sender, RoutedEventArgs e)
        {
            _ = UpdateSelectedSourceResultAsync();
        }

        private async Task UpdateSelectedSourceResultAsync()
        {
            if (SourceResultTextBox == null || SourceComboBox == null || isUnloaded)
                return;

            var isFileSource = IsFileSourceSelected();
            var source = GetSelectedSourceName();
            var filePath = selectedFilePath;
            var manualText = ManualDataTextBox.Text ?? string.Empty;
            var manualIsHex = HexInputCheckBox.IsChecked == true;
            var encoding = (Encoding)Global.GetEncoding().Clone();

            var version = Interlocked.Increment(ref calculationVersion);
            var cts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref calculationCts, cts);
            if (previousCts != null)
            {
                try { previousCts.Cancel(); } catch (ObjectDisposedException) { }
            }

            var token = cts.Token;
            try
            {
                if (!isFileSource)
                    await Task.Delay(ManualCalculationDebounceMilliseconds, token);

                DataCalcFileFingerprint fingerprint = null;
                DataCalcResult result;
                if (isFileSource)
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        if (IsCurrentCalculation(version, cts))
                            UpdateResult(null, source);
                        return;
                    }

                    fingerprint = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var captured = DataCalcFileFingerprint.Capture(filePath);
                        token.ThrowIfCancellationRequested();
                        return captured;
                    }, token);

                    if (IsCurrentCalculation(version, cts))
                    {
                        SourceResultTextBox.Text = source;
                        LengthResultTextBox.Text = $"{fingerprint.Length} bytes";
                        ClearResultHashes();
                    }

                    result = await Task.Run(
                        () => CalculateFile(fingerprint, token),
                        token);
                }
                else
                {
                    result = await Task.Run(() =>
                    {
                        var data = ParseManualData(manualText, manualIsHex, encoding, token);
                        token.ThrowIfCancellationRequested();
                        return DataCalcCalculator.Calculate(
                            new MemoryStream(data, false),
                            token);
                    }, token);
                }

                if (IsCurrentCalculation(version, cts))
                    UpdateResult(result, source);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrentCalculation(version, cts))
                {
                    UpdateResult(null, source);
                    Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex.Message);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref calculationCts, null, cts);
                cts.Dispose();
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

        private bool IsCurrentCalculation(int version, CancellationTokenSource cts)
        {
            return IsCalculationResultCurrent(
                       version,
                       Interlocked.CompareExchange(ref calculationVersion, 0, 0),
                       isUnloaded) &&
                   ReferenceEquals(Interlocked.CompareExchange(ref calculationCts, null, null), cts);
        }

        internal static bool IsCalculationResultCurrent(
            int resultVersion,
            int activeVersion,
            bool unloaded)
        {
            return !unloaded && resultVersion == activeVersion;
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

        private string GetSelectedSourceName()
        {
            if (IsFileSourceSelected())
            {
                if (!string.IsNullOrWhiteSpace(selectedFilePath))
                    return selectedFilePath;
                return TryFindResource("DataCalcNoFileSelected") as string ?? "No file selected";
            }
            return TryFindResource("DataCalcManualInput") as string ?? "Manual input";
        }

        private void UpdateResult(DataCalcResult result, string source)
        {
            SourceResultTextBox.Text = source;
            if (result == null)
            {
                LengthResultTextBox.Text = "0 bytes";
                ClearResultHashes();
                return;
            }

            LengthResultTextBox.Text = result.Length ?? "0 bytes";
            Md5ResultTextBox.Text = result.Md5 ?? "";
            Sha1ResultTextBox.Text = result.Sha1 ?? "";
            Sha256ResultTextBox.Text = result.Sha256 ?? "";
            Sha512ResultTextBox.Text = result.Sha512 ?? "";
            Crc16ResultTextBox.Text = result.Crc16Modbus ?? "";
            Crc32ResultTextBox.Text = result.Crc32 ?? "";
        }

        private void ClearResultHashes()
        {
            Md5ResultTextBox.Text = "";
            Sha1ResultTextBox.Text = "";
            Sha256ResultTextBox.Text = "";
            Sha512ResultTextBox.Text = "";
            Crc16ResultTextBox.Text = "";
            Crc32ResultTextBox.Text = "";
        }
    }
}
