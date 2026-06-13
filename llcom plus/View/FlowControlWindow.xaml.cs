using System.Windows;

namespace llcom_plus
{
    /// <summary>
    /// FlowControlWindow.xaml 的交互逻辑
    /// </summary>
    public partial class FlowControlWindow : Window
    {
        private bool loaded = false;

        public FlowControlWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (loaded)
                return;
            loaded = true;
            DataContext = Tools.Global.setting;
            Topmost = true;
            Closing += FlowControlWindow_Closing;
        }

        private void FlowControlWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (Tools.Global.isMainWindowsClosed)
            {
                e.Cancel = false;
                return;
            }

            e.Cancel = true;
            Hide();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }
    }
}
