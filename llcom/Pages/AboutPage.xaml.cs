using System;
using System.Windows;
using System.Windows.Controls;

namespace llcom.Pages
{
    /// <summary>
    /// AboutPage.xaml 的交互逻辑
    /// </summary>
    public partial class AboutPage : Page
    {
        private const string OriginalProjectUrl = "https://github.com/chenxuuu/llcom";
        private const string ProjectUrl = "https://github.com/JIANXINXIAOQINGMING/llcom-lawrence";
        private const string ReleasesUrl = ProjectUrl + "/releases";

        public AboutPage()
        {
            InitializeComponent();
        }

        private static bool loaded = false;
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (loaded)
                return;
            loaded = true;
            this.DataContext = Tools.Global.setting;
            aboutScrollViewer.ScrollToTop();
            versionTextBlock.Text = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
        }

        private void OpenSourceButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(ProjectUrl);
        }

        private void OpenOriginalSourceButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(OriginalProjectUrl);
        }

        private void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(ReleasesUrl);
        }
    }
}
