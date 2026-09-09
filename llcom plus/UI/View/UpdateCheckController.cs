using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FontAwesomeControl = FontAwesome.WPF.FontAwesome;

namespace llcom_plus
{
    internal sealed class UpdateCheckController
    {
        private const string ProjectUrl = "https://github.com/JIANXINXIAOQINGMING/llcom-plus";
        private const string ReleasesUrl = ProjectUrl + "/releases";
        private static readonly TimeSpan OnlineCheckCacheDuration = TimeSpan.FromMinutes(5);

        private readonly Window owner;
        private readonly Button updateButton;
        private readonly FontAwesomeControl updateIcon;
        private readonly FrameworkElement updateBadge;
        private readonly object onlineCheckSync = new object();
        private Task<Tools.GitHubReleaseInfo> activeOnlineCheckTask;
        private Tools.GitHubReleaseInfo cachedOnlineRelease;
        private DateTime cachedOnlineReleaseUtc = DateTime.MinValue;
        private bool checkingUpdate;
        private bool startupCheckRequested;
        private string lastNotifiedVersion = string.Empty;
        private readonly CancellationTokenSource lifetimeCts = new CancellationTokenSource();
        private readonly CancellationToken lifetimeToken;
        private bool ownerClosed;

        public UpdateCheckController(
            Window owner,
            Button updateButton,
            FontAwesomeControl updateIcon,
            FrameworkElement updateBadge)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.updateButton = updateButton ?? throw new ArgumentNullException(nameof(updateButton));
            this.updateIcon = updateIcon ?? throw new ArgumentNullException(nameof(updateIcon));
            this.updateBadge = updateBadge ?? throw new ArgumentNullException(nameof(updateBadge));
            lifetimeToken = lifetimeCts.Token;
            owner.Closed += Owner_Closed;
        }

        private void Owner_Closed(object sender, EventArgs e)
        {
            if (ownerClosed)
                return;
            ownerClosed = true;
            owner.Closed -= Owner_Closed;
            lifetimeCts.Cancel();
            lifetimeCts.Dispose();
        }

        /// <summary>
        /// Checks GitHub once after the main window becomes interactive. This path is
        /// deliberately silent unless an update exists: it never opens a dialog,
        /// starts a download, launches a browser, or installs a local package.
        /// </summary>
        public async Task CheckOnStartupAsync()
        {
            if (startupCheckRequested || ownerClosed)
                return;

            startupCheckRequested = true;
            try
            {
                var release = await GetLatestReleaseAsync(forceRefresh: false);
                if (ownerClosed)
                    return;
                ApplyUpdateAvailability(release, publishNotification: true);
            }
            catch (Exception ex)
            {
                if (ownerClosed)
                    return;
                ApplyCachedUpdateAvailabilityAfterFailure();
                Tools.Logger.AddUartLogDebug(
                    $"[Update] startup check failed: {ex.GetBaseException().Message}");
            }
        }

        public async Task CheckAsync()
        {
            if (checkingUpdate || ownerClosed)
                return;

            checkingUpdate = true;
            updateButton.IsEnabled = false;
            updateIcon.Spin = true;
            SetStatus("AboutUpdateChecking", "Checking...");
            var shouldShutdown = false;

            try
            {
                if (Tools.Global.IsMSIX())
                {
                    Tools.MessageBox.Show(ResourceText(
                        "AboutUpdateMsix",
                        "Please update the MSIX package from the store or release page."));
                    System.Diagnostics.Process.Start(ReleasesUrl);
                    return;
                }

                Tools.GitHubReleaseInfo release = null;
                Exception onlineError = null;
                try
                {
                    // A deliberate button click must re-query GitHub even when the
                    // startup result is still fresh. If that startup request is still
                    // in flight, both paths continue to share the same task.
                    release = await GetLatestReleaseAsync(forceRefresh: true);
                    if (ownerClosed)
                        return;
                    ApplyUpdateAvailability(release, publishNotification: true);
                }
                catch (Exception ex)
                {
                    if (ownerClosed)
                        return;
                    onlineError = ex;
                    ApplyCachedUpdateAvailabilityAfterFailure();
                }

                if (release?.HasUpdate == true)
                {
                    if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                    {
                        var localFallback = Tools.GitHubReleaseUpdater.FindLatestLocalUpdatePackage();
                        if (localFallback != null)
                        {
                            SetStatus(
                                "AboutUpdateInstallingLocal",
                                "Found local update {0}; validating and preparing installation...",
                                localFallback.DisplayVersion);
                            StartLocalUpdateAndShutdown(localFallback.Path);
                            shouldShutdown = true;
                            return;
                        }

                        Tools.MessageBox.Show(string.Format(
                            ResourceText(
                                "AboutUpdateNoAsset",
                                "Found {0}, but no zip package is attached. Opening release page."),
                            release.Version));
                        System.Diagnostics.Process.Start(release.ReleaseUrl);
                        return;
                    }

                    if (!release.CanAutoInstall)
                    {
                        Tools.MessageBox.Show(
                            release.AutomaticUpdateTrustError + "\r\n\r\n" +
                            ResourceText("AboutUpdateManualOnly", "The release page will be opened for manual download."));
                        System.Diagnostics.Process.Start(release.ReleaseUrl);
                        return;
                    }

                    var assetName = string.IsNullOrWhiteSpace(release.AssetName)
                        ? ResourceText("AboutUpdateAssetUnknown", "Unknown package")
                        : release.AssetName;
                    var sizeText = FormatByteSize(release.AssetSizeBytes);
                    var hasCachedPackage = Tools.GitHubReleaseUpdater.TryGetCachedUpdatePackage(release, out var zipPath);
                    if (!hasCachedPackage)
                    {
                        var updateConfirm = Tools.InputDialog.OpenDialog(
                            string.Format(
                                ResourceText(
                                    "AboutUpdateFoundConfirm",
                                    "Found a new version.\r\nCurrent version: {0}\r\nLatest version: {1}\r\nPackage: {2}\r\nSize: {3}\r\n\r\nDownload and update now?"),
                                Tools.AppInfo.DisplayVersion,
                                release.Version,
                                assetName,
                                sizeText),
                            null,
                            ResourceText("AboutUpdateFoundTitle", "New version available")).Item1;
                        if (!updateConfirm)
                            return;

                        SetStatus("AboutUpdateDownloading", "Downloading...");
                        zipPath = await DownloadUpdateWithProgressAsync(release);
                        if (ownerClosed)
                            return;
                    }

                    Tools.Global.PublishNotification(
                        ResourceText("NotificationUpdateDownloadedTitle", "Update package downloaded"),
                        $"{release.Version} · {System.IO.Path.GetFileName(zipPath)}",
                        Tools.AppNotificationLevel.Success,
                        category: Tools.AppNotificationCategory.Update);

                    var installConfirm = Tools.InputDialog.OpenDialog(
                        string.Format(
                            ResourceText(
                                "AboutUpdateInstallConfirm",
                                "Version {0} has been downloaded.\r\nPackage: {1}\r\n\r\nRestart and install now? The app will reopen automatically after installation.\r\nIf you choose No, the package will be kept for next time; closing the app will install it without reopening."),
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

                SetStatus(
                    "AboutUpdateCheckingLocal",
                    "Checking the installation directory for local updates...");
                var localPackage = Tools.GitHubReleaseUpdater.FindLatestLocalUpdatePackage();
                if (localPackage != null)
                {
                    SetStatus(
                        "AboutUpdateInstallingLocal",
                        "Found local update {0}; validating and preparing installation...",
                        localPackage.DisplayVersion);
                    StartLocalUpdateAndShutdown(localPackage.Path);
                    shouldShutdown = true;
                    return;
                }

                if (onlineError != null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            ResourceText(
                                "AboutUpdateOnlineFailedLocalNone",
                                "Online update check failed, and no usable local package was found.\r\n{0}"),
                            onlineError.GetBaseException().Message),
                        onlineError);
                }

                Tools.MessageBox.Show(string.Format(
                    ResourceText("AboutUpdateNoNewVersion", "Already latest version: {0}"),
                    Tools.AppInfo.DisplayVersion));
            }
            catch (OperationCanceledException)
            {
                if (!ownerClosed)
                    SetStatus("AboutUpdateCancelled", "Update download cancelled.");
            }
            catch (Exception ex)
            {
                if (ownerClosed)
                    return;
                Tools.Global.PublishNotification(
                    string.Format(
                        ResourceText("NotificationOperationFailedTitleFormat", "{0} failed"),
                        ResourceText("AboutReleaseButton", "Check updates")),
                    ex.GetBaseException().Message,
                    Tools.AppNotificationLevel.Error,
                    category: Tools.AppNotificationCategory.Update);
                Tools.MessageBox.Show(
                    $"{ResourceText("AboutUpdateFailed", "Update failed.")}\r\n{ex.Message}");
            }
            finally
            {
                if (!shouldShutdown && !ownerClosed)
                {
                    updateIcon.Spin = false;
                    ApplyUpdateIndicator(cachedOnlineRelease);
                    updateButton.IsEnabled = true;
                    checkingUpdate = false;
                }
            }
        }

        public void RefreshIndicatorText()
        {
            if (!ownerClosed)
                ApplyUpdateIndicator(cachedOnlineRelease);
        }

        private Task<Tools.GitHubReleaseInfo> GetLatestReleaseAsync(bool forceRefresh)
        {
            Task<Tools.GitHubReleaseInfo> checkTask;
            lock (onlineCheckSync)
            {
                checkTask = activeOnlineCheckTask;
                if (checkTask == null &&
                    !forceRefresh &&
                    cachedOnlineRelease != null &&
                    DateTime.UtcNow - cachedOnlineReleaseUtc < OnlineCheckCacheDuration)
                {
                    return Task.FromResult(cachedOnlineRelease);
                }

                if (checkTask == null)
                {
                    checkTask = Tools.GitHubReleaseUpdater.CheckLatestAsync(lifetimeToken);
                    activeOnlineCheckTask = checkTask;
                }
            }

            return AwaitAndCacheLatestReleaseAsync(checkTask);
        }

        private async Task<Tools.GitHubReleaseInfo> AwaitAndCacheLatestReleaseAsync(
            Task<Tools.GitHubReleaseInfo> checkTask)
        {
            try
            {
                var release = await checkTask;
                lock (onlineCheckSync)
                {
                    cachedOnlineRelease = release;
                    cachedOnlineReleaseUtc = DateTime.UtcNow;
                }
                return release;
            }
            finally
            {
                lock (onlineCheckSync)
                {
                    if (ReferenceEquals(activeOnlineCheckTask, checkTask))
                        activeOnlineCheckTask = null;
                }
            }
        }

        private void ApplyCachedUpdateAvailabilityAfterFailure()
        {
            Tools.GitHubReleaseInfo cachedRelease;
            lock (onlineCheckSync)
                cachedRelease = cachedOnlineRelease;

            ApplyUpdateAvailability(cachedRelease, publishNotification: false);
        }

        private void ApplyUpdateAvailability(
            Tools.GitHubReleaseInfo release,
            bool publishNotification)
        {
            var hasUpdate = release?.HasUpdate == true;
            Tools.Global.HasNewVersion = hasUpdate;
            ApplyUpdateIndicator(release);

            if (!hasUpdate || !publishNotification)
                return;

            if (!TryMarkUpdateNotification(release.Version))
                return;

            Tools.Global.PublishNotification(
                string.Format(
                    ResourceText("NotificationUpdateAvailableTitleFormat", "Version {0} is available"),
                    release.Version),
                string.Format(
                    ResourceText("NotificationUpdateAvailableMessageFormat", "Current version: {0}"),
                    Tools.AppInfo.DisplayVersion),
                Tools.AppNotificationLevel.Info,
                category: Tools.AppNotificationCategory.Update);
        }

        private bool TryMarkUpdateNotification(Version version)
        {
            if (version == null)
                return false;

            var versionText = version.ToString();
            if (string.Equals(lastNotifiedVersion, versionText, StringComparison.OrdinalIgnoreCase))
                return false;

            lastNotifiedVersion = versionText;
            return true;
        }

        private void ApplyUpdateIndicator(Tools.GitHubReleaseInfo release)
        {
            if (release?.HasUpdate == true)
            {
                updateBadge.Visibility = Visibility.Visible;
                updateButton.ToolTip = string.Format(
                    ResourceText(
                        "AboutUpdateAvailableTooltipFormat",
                        "Version {0} is available. Click to update."),
                    release.Version);
                return;
            }

            updateBadge.Visibility = Visibility.Collapsed;
            updateButton.SetResourceReference(FrameworkElement.ToolTipProperty, "AboutReleaseButton");
        }

        private void SetStatus(string resourceKey, string fallback, params object[] arguments)
        {
            var status = ResourceText(resourceKey, fallback);
            updateButton.ToolTip = arguments == null || arguments.Length == 0
                ? status
                : string.Format(status, arguments);
        }

        private static void StartLocalUpdateAndShutdown(string packagePath)
        {
            Tools.GitHubReleaseUpdater.StartInstallAfterExit(packagePath);
            // Closing saves scripts/settings and flushes pending snapshots. Never race
            // that work with a forced process exit; the installer waits for normal exit.
            Application.Current.Shutdown();
        }

        private string ResourceText(string key, string fallback)
        {
            return owner.TryFindResource(key) as string ?? fallback;
        }

        private async Task<string> DownloadUpdateWithProgressAsync(Tools.GitHubReleaseInfo release)
        {
            UpdateProgressWindow progressWindow = null;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken))
            {
                try
                {
                    progressWindow = new UpdateProgressWindow(
                        ResourceText("AboutUpdateProgressTitle", "Download update"),
                        ResourceText("AboutUpdateDownloading", "Downloading..."),
                        () => cts.Cancel())
                    {
                        Owner = owner
                    };
                    progressWindow.Show();

                    var progress = new Progress<Tools.GitHubDownloadProgress>(value =>
                    {
                        if (ownerClosed)
                            return;
                        progressWindow.Report(
                            value,
                            ResourceText("AboutUpdateDownloading", "Downloading..."),
                            ResourceText("AboutUpdateSizeUnknown", "Unknown"));
                    });

                    return await Tools.GitHubReleaseUpdater.DownloadUpdateAsync(release, progress, cts.Token);
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
        }

        private string FormatByteSize(long bytes)
        {
            return bytes > 0
                ? FormatByteSizeValue(bytes)
                : ResourceText("AboutUpdateSizeUnknown", "Unknown");
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

            return unitIndex == 0
                ? $"{bytes} {units[unitIndex]}"
                : $"{value:0.##} {units[unitIndex]}";
        }

        private sealed class UpdateProgressWindow : Window
        {
            private readonly ProgressBar progressBar;
            private readonly TextBlock statusTextBlock;
            private readonly Button cancelButton;
            private readonly Action cancel;
            private bool canClose;
            private bool cancelRequested;

            public UpdateProgressWindow(string title, string initialStatus, Action cancel)
            {
                this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
                Title = title;
                Width = 420;
                MinHeight = 120;
                SizeToContent = SizeToContent.Height;
                ResizeMode = ResizeMode.NoResize;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ShowInTaskbar = false;
                Topmost = true;

                // This window is created in code, so it does not automatically pick up
                // the application window style. In dark mode the implicit TextBlock
                // style was therefore drawing light text over the stock white client area.
                SetResourceReference(StyleProperty, "AppGlassWindowStyle");
                SetResourceReference(BackgroundProperty, "AppWindowBackgroundBrush");
                SetResourceReference(ForegroundProperty, "AppGlassTextBrush");

                var panel = new StackPanel { Margin = new Thickness(16) };
                statusTextBlock = new TextBlock
                {
                    Text = initialStatus,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppGlassTextBrush");
                progressBar = new ProgressBar
                {
                    Height = 18,
                    Minimum = 0,
                    Maximum = 100,
                    IsIndeterminate = true
                };
                progressBar.SetResourceReference(ProgressBar.ForegroundProperty, "AppAccentBrush");
                progressBar.SetResourceReference(ProgressBar.BackgroundProperty, "AppGlassControlBackground");
                cancelButton = new Button
                {
                    Width = 100,
                    Margin = new Thickness(0, 12, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Content = "Cancel"
                };
                cancelButton.Click += (_, __) => RequestCancel();
                panel.Children.Add(statusTextBlock);
                panel.Children.Add(progressBar);
                panel.Children.Add(cancelButton);
                Content = panel;

                Closing += (_, e) =>
                {
                    if (!canClose)
                    {
                        RequestCancel();
                        e.Cancel = true;
                    }
                };
            }

            private void RequestCancel()
            {
                if (cancelRequested || canClose)
                    return;
                cancelRequested = true;
                cancelButton.IsEnabled = false;
                statusTextBlock.Text = "Cancelling update download...";
                cancel();
            }

            public void AllowClose()
            {
                canClose = true;
            }

            public void Report(
                Tools.GitHubDownloadProgress progress,
                string downloadingText,
                string unknownSizeText)
            {
                if (progress == null || cancelRequested)
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
