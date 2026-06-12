using System.ComponentModel;
using System.Windows;

namespace llcom
{
    /// <summary>
    /// DataCalcWindow.xaml 的交互逻辑
    /// </summary>
    public partial class DataCalcWindow : Window
    {
        public DataCalcWindow()
        {
            InitializeComponent();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (Tools.Global.isMainWindowsClosed)
                return;

            e.Cancel = true;
            Hide();
        }
    }
}
