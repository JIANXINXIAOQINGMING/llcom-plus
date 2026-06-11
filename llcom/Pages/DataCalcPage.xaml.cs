using llcom.Tools;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace llcom.Pages
{
    /// <summary>
    /// DataCalcPage.xaml 的交互逻辑
    /// </summary>
    public partial class DataCalcPage : Page
    {
        private byte[] selectedFileBytes = null;
        private string selectedFilePath = "";

        public DataCalcPage()
        {
            InitializeComponent();
            UpdateResult(null, TryFindResource("DataCalcNoData") as string ?? "No data");
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
                selectedFileBytes = File.ReadAllBytes(selectedFilePath);
                FilePathTextBox.Text = selectedFilePath;
                FileLengthTextBlock.Text = $"{selectedFileBytes.LongLength} bytes";
                SendFileDataButton.IsEnabled = selectedFileBytes.Length > 0;
                UpdateResult(selectedFileBytes, selectedFilePath);
            }
            catch (Exception ex)
            {
                selectedFilePath = "";
                selectedFileBytes = null;
                SendFileDataButton.IsEnabled = false;
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex);
            }
        }

        private void SendFileDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedFileBytes == null || selectedFileBytes.Length == 0)
            {
                Tools.MessageBox.Show(TryFindResource("DataCalcNoData") as string ?? "No data");
                return;
            }

            if (!Global.RequestSendRawData(selectedFileBytes))
                Tools.MessageBox.Show(TryFindResource("DataCalcSendUnavailable") as string ?? "Serial sender is unavailable.");
        }

        private void CalculateManualButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateManualResult();
        }

        private void ManualDataChanged(object sender, RoutedEventArgs e)
        {
            UpdateManualResult();
        }

        private void UpdateManualResult()
        {
            if (ManualDataTextBox == null || ResultTextBox == null)
                return;

            var data = HexInputCheckBox.IsChecked == true ?
                Global.Hex2Byte(ManualDataTextBox.Text ?? "") :
                Global.GetEncoding().GetBytes(ManualDataTextBox.Text ?? "");
            UpdateResult(data, TryFindResource("DataCalcManualInput") as string ?? "Manual input");
        }

        private void UpdateResult(byte[] data, string source)
        {
            ResultTextBox.Text = BuildResult(data, source);
        }

        private string BuildResult(byte[] data, string source)
        {
            if (data == null)
                return $"{source}\r\nLength: 0 bytes";

            var sb = new StringBuilder();
            sb.AppendLine(source);
            sb.AppendLine($"Length: {data.LongLength} bytes");
            sb.AppendLine($"MD5:    {ComputeHash(MD5.Create(), data)}");
            sb.AppendLine($"SHA1:   {ComputeHash(SHA1.Create(), data)}");
            sb.AppendLine($"SHA256: {ComputeHash(SHA256.Create(), data)}");
            sb.AppendLine($"SHA512: {ComputeHash(SHA512.Create(), data)}");
            sb.AppendLine($"CRC16(Modbus): 0x{ComputeCrc16Modbus(data):X4}");
            sb.AppendLine($"CRC32:         0x{ComputeCrc32(data):X8}");
            return sb.ToString();
        }

        private string ComputeHash(HashAlgorithm hashAlgorithm, byte[] data)
        {
            using (hashAlgorithm)
            {
                return BitConverter.ToString(hashAlgorithm.ComputeHash(data)).Replace("-", "");
            }
        }

        private ushort ComputeCrc16Modbus(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x0001) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    else
                        crc >>= 1;
                }
            }
            return crc;
        }

        private uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 1) == 1)
                        crc = (crc >> 1) ^ 0xEDB88320;
                    else
                        crc >>= 1;
                }
            }
            return ~crc;
        }
    }
}
