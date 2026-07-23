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
        private const string ProjectUrl = "https://github.com/JIANXINXIAOQINGMING/llcom-plus";
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
            versionTextBlock.Text = Tools.AppInfo.DisplayVersion;
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

                Tools.GitHubReleaseInfo release = null;
                Exception onlineError = null;
                try
                {
                    release = await Tools.GitHubReleaseUpdater.CheckLatestAsync();
                    Tools.Global.HasNewVersion = release.HasUpdate;
                }
                catch (Exception ex)
                {
                    onlineError = ex;
                    Tools.Global.HasNewVersion = false;
                }

                if (release?.HasUpdate == true)
                {
                    Tools.Global.PublishNotification(
                        string.Format(
                            ResourceText("NotificationUpdateAvailableTitleFormat", "Version {0} is available"),
                            release.Version),
                        string.Format(
                            ResourceText("NotificationUpdateAvailableMessageFormat", "Current version: {0}"),
                            Tools.AppInfo.DisplayVersion),
                        Tools.AppNotificationLevel.Info,
                        category: Tools.AppNotificationCategory.Update);

                    if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                    {
                        var localFallback = Tools.GitHubReleaseUpdater.FindLatestLocalUpdatePackage();
                        if (localFallback != null)
                        {
                            CheckUpdateButton.Content = string.Format(
                                ResourceText("AboutUpdateInstallingLocal", "Found local update {0}; validating and preparing installation..."),
                                localFallback.DisplayVersion);
                            StartLocalUpdateAndShutdown(localFallback.Path);
                            shouldShutdown = true;
                            return;
                        }

                        Tools.MessageBox.Show(string.Format(
                            ResourceText("AboutUpdateNoAsset", "Found {0}, but no zip package is attached. Opening release page."),
                            release.Version));
                        System.Diagnostics.Process.Start(release.ReleaseUrl);
                        return;
                    }

                    var assetName = string.IsNullOrWhiteSpace(release.AssetName) ? ResourceText("AboutUpdateAssetUnknown", "Unknown package") : release.AssetName;
                    var sizeText = FormatByteSize(release.AssetSizeBytes);
                    var hasCachedPackage = Tools.GitHubReleaseUpdater.TryGetCachedUpdatePackage(release, out var zipPath);
                    if (!hasCachedPackage)
                    {
                        var updateConfirm = Tools.InputDialog.OpenDialog(
                            string.Format(
                                ResourceText("AboutUpdateFoundConfirm", "Found a new version.\r\nCurrent version: {0}\r\nLatest version: {1}\r\nPackage: {2}\r\nSize: {3}\r\n\r\nDownload and update now?"),
                                Tools.AppInfo.DisplayVersion,
                                release.Version,
                                assetName,
                                sizeText),
                            null,
                            ResourceText("AboutUpdateFoundTitle", "New version available")).Item1;
                        if (!updateConfirm)
                            return;

                        CheckUpdateButton.Content = ResourceText("AboutUpdateDownloading", "Downloading...");
                        zipPath = await DownloadUpdateWithProgressAsync(release);
                    }

                    Tools.Global.PublishNotification(
                        ResourceText("NotificationUpdateDownloadedTitle", "Update package downloaded"),
                        $"{release.Version} · {System.IO.Path.GetFileName(zipPath)}",
                        Tools.AppNotificationLevel.Success,
                        category: Tools.AppNotificationCategory.Update);

                    var installConfirm = Tools.InputDialog.OpenDialog(
                        string.Format(
                            ResourceText("AboutUpdateInstallConfirm", "Version {0} has been downloaded.\r\nPackage: {1}\r\n\r\nRestart and install now? The app will reopen automatically after installation.\r\nIf you choose No, the package will be kept for next time; closing the app will install it without reopening."),
                            release.Version,
                            zipPath),
                        null,
                        ResourceText("AboutUpdateInstallTitle", "Install update")).Item1;
                    if (!installConfirm)
                        return;

                    StartLocalUpdateAndShutdown(zipPath);
                    shouldShutdown = true;
                    return;
                }

                CheckUpdateButton.Content = ResourceText("AboutUpdateCheckingLocal", "Checking the installation directory for local updates...");
                var localPackage = Tools.GitHubReleaseUpdater.FindLatestLocalUpdatePackage();
                if (localPackage != null)
                {
                    CheckUpdateButton.Content = string.Format(
                        ResourceText("AboutUpdateInstallingLocal", "Found local update {0}; validating and preparing installation..."),
                        localPackage.DisplayVersion);
                    StartLocalUpdateAndShutdown(localPackage.Path);
                    shouldShutdown = true;
                    return;
                }

                if (onlineError != null)
                    throw new InvalidOperationException(string.Format(
                        ResourceText("AboutUpdateOnlineFailedLocalNone", "Online update check failed, and no usable local package was found.\r\n{0}"),
                        onlineError.GetBaseException().Message), onlineError);

                Tools.MessageBox.Show(string.Format(
                    ResourceText("AboutUpdateNoNewVersion", "Already latest version: {0}"),
                    Tools.AppInfo.DisplayVersion));
            }
            catch (Exception ex)
            {
                Tools.Global.PublishNotification(
                    string.Format(
                        ResourceText("NotificationOperationFailedTitleFormat", "{0} failed"),
                        ResourceText("AboutReleaseButton", "Check updates")),
                    ex.GetBaseException().Message,
                    Tools.AppNotificationLevel.Error,
                    category: Tools.AppNotificationCategory.Update);
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

        private static void StartLocalUpdateAndShutdown(string packagePath)
        {
            Tools.GitHubReleaseUpdater.StartInstallAfterExit(packagePath);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1500);
                Environment.Exit(0);
            });
            Application.Current.Shutdown();
        }

        private string ResourceText(string key, string fallback)
        {
            return TryFindResource(key) as string ?? fallback;
        }

        private async System.Threading.Tasks.Task<string> DownloadUpdateWithProgressAsync(Tools.GitHubReleaseInfo release)
        {
            UpdateProgressWindow progressWindow = null;
            try
            {
                progressWindow = new UpdateProgressWindow(
                    ResourceText("AboutUpdateProgressTitle", "Download update"),
                    ResourceText("AboutUpdateDownloading", "Downloading..."));
                progressWindow.Owner = Window.GetWindow(this);
                progressWindow.Show();

                var progress = new Progress<Tools.GitHubDownloadProgress>(value =>
                {
                    progressWindow.Report(
                        value,
                        ResourceText("AboutUpdateDownloading", "Downloading..."),
                        ResourceText("AboutUpdateSizeUnknown", "Unknown"));
                });

                return await Tools.GitHubReleaseUpdater.DownloadUpdateAsync(release, progress);
            }
            finally
            {
                if (progressWindow != null)
                {
                    progressWindow.AllowClose();
                    progressWindow.Close();
                }
            }
        }

        private string FormatByteSize(long bytes)
        {
            return bytes > 0 ? FormatByteSizeValue(bytes) : ResourceText("AboutUpdateSizeUnknown", "Unknown");
        }

        private static string FormatByteSizeValue(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            var unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{value:0.##} {units[unitIndex]}";
        }

        private sealed class UpdateProgressWindow : Window
        {
            private readonly ProgressBar progressBar;
            private readonly TextBlock statusTextBlock;
            private bool canClose;

            public UpdateProgressWindow(string title, string initialStatus)
            {
                Title = title;
                Width = 420;
                MinHeight = 120;
                SizeToContent = System.Windows.SizeToContent.Height;
                ResizeMode = System.Windows.ResizeMode.NoResize;
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
                ShowInTaskbar = false;
                Topmost = true;

                var panel = new StackPanel { Margin = new Thickness(16) };
                statusTextBlock = new TextBlock
                {
                    Text = initialStatus,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                progressBar = new ProgressBar
                {
                    Height = 18,
                    Minimum = 0,
                    Maximum = 100,
                    IsIndeterminate = true
                };
                panel.Children.Add(statusTextBlock);
                panel.Children.Add(progressBar);
                Content = panel;

                Closing += (_, e) =>
                {
                    if (!canClose)
                        e.Cancel = true;
                };
            }

            public void AllowClose()
            {
                canClose = true;
            }

            public void Report(Tools.GitHubDownloadProgress progress, string downloadingText, string unknownSizeText)
            {
                if (progress == null)
                    return;

                if (progress.TotalBytes > 0)
                {
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = progress.Percent;
                    statusTextBlock.Text = string.Format(
                        "{0}\r\n{1} / {2} ({3:0.0}%)",
                        downloadingText,
                        FormatByteSizeValue(progress.BytesReceived),
                        FormatByteSizeValue(progress.TotalBytes),
                        progress.Percent);
                }
                else
                {
                    progressBar.IsIndeterminate = true;
                    statusTextBlock.Text = string.Format(
                        "{0}\r\n{1}: {2}",
                        downloadingText,
                        unknownSizeText,
                        FormatByteSizeValue(progress.BytesReceived));
                }
            }
        }
    }
}
