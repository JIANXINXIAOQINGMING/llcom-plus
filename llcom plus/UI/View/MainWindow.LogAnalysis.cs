using System;
using System.Windows;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private Pages.LogAnalysisPage mainTimelinePage;

        private void MainLogFind_Click(object sender, RoutedEventArgs e) { ShowMainLogSearch(); }

        private void ShowMainLogSearch()
        {
            if (dataShowFrame.Content is Pages.DataShowPage single) single.ShowLogSearch();
            else if (dataShowFrame.Content is Pages.MultiPortPage split) split.ShowLogSearch();
        }

        private bool IsMainLogSearchActive =>
            (dataShowFrame.Content as Pages.DataShowPage)?.IsSearchActive == true ||
            (dataShowFrame.Content as Pages.MultiPortPage)?.IsSearchActive == true;

        private void EnsureTimelineInitialized()
        {
            if (mainTimelinePage == null)
            {
                mainTimelinePage = new Pages.LogAnalysisPage();
            }
            MainTimelineFrame.Content = mainTimelinePage;
        }

        private void OpenLogAnalysis(string port = null, DateTime? timestamp = null)
        {
            SetRightToolsCollapsed(false);
            EnsureTimelineInitialized();
            TimelineTab.IsSelected = true;
            mainTimelinePage.FocusSearch(port, timestamp);
        }

        private void CloseMainTimeline()
        {
            if (TimelineTab.IsSelected) QuickSendTab.IsSelected = true;
            MainTimelineFrame.Content = null;
        }
    }
}
