using llcom_plus.Tools;
using System.Windows;

namespace llcom_plus
{
    public partial class DataCalcWindow : Window
    {
        public DataCalcWindow()
            : this(new DataCalcResult())
        {
        }

        public DataCalcWindow(DataCalcResult result)
        {
            InitializeComponent();
            LoadResult(result ?? new DataCalcResult());
        }

        private void LoadResult(DataCalcResult result)
        {
            LengthResultTextBox.Text = result.Length ?? "";
            Md5ResultTextBox.Text = result.Md5 ?? "";
            Sha1ResultTextBox.Text = result.Sha1 ?? "";
            Sha256ResultTextBox.Text = result.Sha256 ?? "";
            Sha512ResultTextBox.Text = result.Sha512 ?? "";
            Crc16ResultTextBox.Text = result.Crc16Modbus ?? "";
            Crc32ResultTextBox.Text = result.Crc32 ?? "";
        }
    }
}
