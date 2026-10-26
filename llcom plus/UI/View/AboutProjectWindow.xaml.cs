using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;

namespace llcom_plus
{
    public partial class AboutProjectWindow : Window
    {
        internal const string ProjectUrl = "https://github.com/JIANXINXIAOQINGMING/llcom-plus";
        internal const string UpstreamUrl = "https://github.com/chenxuuu/llcom";

        public AboutProjectWindow()
        {
            InitializeComponent();
            // An ICO ImageSource otherwise picks the first (16px) frame. Use
            // the bundled high-resolution frame for a sharp logo at high DPI.
            var iconResource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Assets/AppIcon.ico"));
            if (iconResource != null)
            {
                using (var stream = iconResource.Stream)
                {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapFrame largest = null;
                    foreach (var frame in decoder.Frames)
                        if (largest == null || frame.PixelWidth > largest.PixelWidth) largest = frame;
                    if (largest != null) ProjectLogo.Source = largest;
                }
            }
            VersionText.Text = Tools.AppInfo.DisplayVersion + (Environment.Is64BitProcess ? " · x64" : " · x86");
            // Leave room for the title bar even on small screens / high DPI.
            MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 48);
            Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 48));
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            Tools.Win32.ApplyWindowTheme(this, Tools.Global.setting?.darkMode ?? Tools.Global.IsDarkTheme, true);
        }

        private void ProjectHomeButton_Click(object sender, RoutedEventArgs e) => OpenProjectLink(ProjectUrl);
        private void UpstreamButton_Click(object sender, RoutedEventArgs e) => OpenProjectLink(UpstreamUrl);

        private void OpenProjectLink(string url)
        {
            // Only the two fixed, verified project URLs are reachable from here.
            // Merely opening the About window performs no network request.
            if (url != ProjectUrl && url != UpstreamUrl) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error)
            {
                var message = TryFindResource("AboutProjectOpenLinkFailed") as string ?? "Unable to open the browser.";
                Tools.MessageBox.Show(message + Environment.NewLine + error.Message);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
