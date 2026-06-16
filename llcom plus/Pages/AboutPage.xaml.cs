using System;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    /// <summary>
    /// AboutPage.xaml 的交互逻辑
    /// </summary>
    public partial class AboutPage : Page
    {
        private const string OriginalProjectUrl = "https://github.com/chenxuuu/llcom";
        private const string ProjectUrl = "https://github.com/JIANXINXIAOQINGMING/llcom-lawrence";
        private const string ReleasesUrl = ProjectUrl + "/releases";
        private bool checkingUpdate = false;

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

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (checkingUpdate)
                return;

            checkingUpdate = true;
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = ResourceText("AboutUpdateChecking", "Checking...");
            var shouldShutdown = false;
            try
            {
                if (Tools.Global.IsMSIX())
                {
                    Tools.MessageBox.Show(ResourceText("AboutUpdateMsix", "Please update the MSIX package from the store or release page."));
                    System.Diagnostics.Process.Start(ReleasesUrl);
                    return;
                }

                var release = await Tools.GitHubReleaseUpdater.CheckLatestAsync();
                Tools.Global.HasNewVersion = release.HasUpdate;
                if (!release.HasUpdate)
                {
                    Tools.MessageBox.Show(string.Format(
                        ResourceText("AboutUpdateNoNewVersion", "Already latest version: {0}"),
                        release.CurrentVersion));
                    return;
                }

                if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                {
                    Tools.MessageBox.Show(string.Format(
                        ResourceText("AboutUpdateNoAsset", "Found {0}, but no zip package is attached. Opening release page."),
                        release.Version));
                    System.Diagnostics.Process.Start(release.ReleaseUrl);
                    return;
                }

                CheckUpdateButton.Content = ResourceText("AboutUpdateDownloading", "Downloading...");
                await Tools.GitHubReleaseUpdater.DownloadAndPrepareInstallAsync(release);
                Tools.MessageBox.Show(string.Format(
                    ResourceText("AboutUpdateReady", "Version {0} has been downloaded. The app will close and install it now."),
                    release.Version));
                shouldShutdown = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Tools.MessageBox.Show($"{ResourceText("AboutUpdateFailed", "Update failed.")}\r\n{ex.Message}");
            }
            finally
            {
                if (!shouldShutdown)
                {
                    CheckUpdateButton.Content = ResourceText("AboutReleaseButton", "Check updates");
                    CheckUpdateButton.IsEnabled = true;
                    checkingUpdate = false;
                }
            }
        }

        private string ResourceText(string key, string fallback)
        {
            return TryFindResource(key) as string ?? fallback;
        }
    }
}
