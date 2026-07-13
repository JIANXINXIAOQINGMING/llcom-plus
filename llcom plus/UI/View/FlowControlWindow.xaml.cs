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
            Closing += FlowControlWindow_Closing;
            Tools.Global.UartProfileChangedEvent += Global_UartProfileChangedEvent;
        }

        private void Global_UartProfileChangedEvent(object sender, System.EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                DataContext = null;
                DataContext = Tools.Global.setting;
            });
        }

        private void FlowControlWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (Tools.Global.isMainWindowsClosed)
            {
                Tools.Global.UartProfileChangedEvent -= Global_UartProfileChangedEvent;
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
