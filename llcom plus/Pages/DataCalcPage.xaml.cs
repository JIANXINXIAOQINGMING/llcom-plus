using llcom_plus.Tools;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
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
            UpdateSelectedSourceResult();
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
                SourceComboBox.SelectedIndex = 1;
                UpdateSelectedSourceResult();
            }
            catch (Exception ex)
            {
                selectedFilePath = "";
                selectedFileBytes = null;
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFileFail") as string ?? "?!"}\r\n" + ex);
            }
        }

        private void SendSelectedDataButton_Click(object sender, RoutedEventArgs e)
        {
            var data = GetSelectedSourceBytes();
            if (data == null || data.Length == 0)
            {
                Tools.MessageBox.Show(TryFindResource("DataCalcNoData") as string ?? "No data");
                return;
            }

            if (!Global.EnsureActiveSerialTargetOpen())
                return;

            if (!Global.RequestSendRawData(data))
                Tools.MessageBox.Show(TryFindResource("DataCalcSendUnavailable") as string ?? "Serial sender is unavailable.");
        }

        private void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedSourceResult();
        }

        private void InputChanged(object sender, RoutedEventArgs e)
        {
            UpdateSelectedSourceResult();
        }

        private void UpdateSelectedSourceResult()
        {
            if (SourceResultTextBox == null || SourceComboBox == null)
                return;

            UpdateResult(GetSelectedSourceBytes(), GetSelectedSourceName());
        }

        private byte[] GetSelectedSourceBytes()
        {
            if (SourceComboBox != null && SourceComboBox.SelectedIndex == 1)
                return selectedFileBytes;
            return GetManualDataBytes();
        }

        private string GetSelectedSourceName()
        {
            if (SourceComboBox != null && SourceComboBox.SelectedIndex == 1)
            {
                if (!string.IsNullOrWhiteSpace(selectedFilePath))
                    return selectedFilePath;
                return TryFindResource("DataCalcNoFileSelected") as string ?? "No file selected";
            }
            return TryFindResource("DataCalcManualInput") as string ?? "Manual input";
        }

        private byte[] GetManualDataBytes()
        {
            return HexInputCheckBox.IsChecked == true ?
                Global.Hex2Byte(ManualDataTextBox.Text ?? "") :
                Global.GetEncoding().GetBytes(ManualDataTextBox.Text ?? "");
        }

        private void UpdateResult(byte[] data, string source)
        {
            SourceResultTextBox.Text = source;
            if (data == null)
            {
                LengthResultTextBox.Text = "0 bytes";
                ClearResultHashes();
                return;
            }

            LengthResultTextBox.Text = $"{data.LongLength} bytes";
            Md5ResultTextBox.Text = ComputeHash(MD5.Create(), data);
            Sha1ResultTextBox.Text = ComputeHash(SHA1.Create(), data);
            Sha256ResultTextBox.Text = ComputeHash(SHA256.Create(), data);
            Sha512ResultTextBox.Text = ComputeHash(SHA512.Create(), data);
            Crc16ResultTextBox.Text = $"0x{ComputeCrc16Modbus(data):X4}";
            Crc32ResultTextBox.Text = $"0x{ComputeCrc32(data):X8}";
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
