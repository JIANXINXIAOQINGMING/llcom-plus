using llcom_plus.Tools;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    public partial class DataCalcFileSendView : UserControl
    {
        private byte[] selectedFileBytes = null;
        private string selectedFilePath = "";

        public DataCalcFileSendView()
        {
            InitializeComponent();
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
            }
            catch (Exception ex)
            {
                selectedFilePath = "";
                selectedFileBytes = null;
                FilePathTextBox.Text = TryFindResource("DataCalcNoFileSelected") as string ?? "No file selected";
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

            if (!Global.RequestSendRawData(data))
                Tools.MessageBox.Show(TryFindResource("DataCalcSendUnavailable") as string ?? "Serial sender is unavailable.");
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
            if (SourceComboBox != null && SourceComboBox.SelectedIndex == 1)
                return selectedFileBytes;
            return HexInputCheckBox.IsChecked == true ?
                Global.Hex2Byte(ManualDataTextBox.Text ?? "") :
                Global.GetEncoding().GetBytes(ManualDataTextBox.Text ?? "");
        }
    }
}
