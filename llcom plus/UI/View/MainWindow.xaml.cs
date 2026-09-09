using FontAwesome.WPF;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml;
using llcom_plus.Model;
using System.Text.RegularExpressions;
using llcom_plus.Tools;
using ICSharpCode.AvalonEdit.Folding;
using System.Threading;
using System.Windows.Interop;
using System.Drawing;
using ICSharpCode.AvalonEdit;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using llcom_plus.ScriptEnv;
using System.Web.UI.WebControls.WebParts;
using Color = System.Windows.Media.Color;

namespace llcom_plus
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private const double PreferredWindowWidth = 1440;
        private const double PreferredWindowHeight = 820;
        private const double ExpandedRightToolsMinWidth = 650;
        private const double ExpandedRightToolsDefaultWidth = 760;
        private const double ExpandedRightToolsFloorWidth = 580;
        private const double MainPaneBaseWidth = 430;
        private const double SinglePaneDesiredMinimumWidth = 560;
        private const double SplitPaneDesiredMinimumWidth = 700;
        private const double MainPaneLayoutReserve = 0;
        internal const long MaxQuickSendImportFileBytes = 8L * 1024 * 1024;
        internal const int MaxQuickSendImportJsonDepth = 32;
        internal const int MaxQuickSendImportPages = 64;
        internal const int MaxQuickSendItemsPerPage = 2000;
        internal const int MaxQuickSendFieldCharacters = 256 * 1024;
        internal const long MaxQuickSendTotalCharacters = 4L * 1024 * 1024;

        public MainWindow()
        {
            StartupProfiler.Mark("MainWindow ctor enter");
            StartupProfiler.Measure("MainWindow.InitializeComponent", InitializeComponent);
            updateCheckController = new UpdateCheckController(
                this,
                CheckUpdateButton,
                CheckUpdateIcon,
                UpdateAvailableBadge);
            notificationView = CollectionViewSource.GetDefaultView(notificationItems);
            notificationView.Filter = FilterNotification;
            NotificationListBox.ItemsSource = notificationView;
            RefreshNotificationFilterOptions();
            UpdateNotificationUi();
            StartupProfiler.Measure("MainWindow restore placement", () =>
            {
                var availableWidth = Math.Max(this.MinWidth, SystemParameters.WorkArea.Width - 32);
                var availableHeight = Math.Max(this.MinHeight, SystemParameters.WorkArea.Height - 32);
                if (Tools.Global.setting.windowHeight != 0 &&
                    Tools.Global.setting.windowLeft > 0 &&
                    Tools.Global.setting.windowTop > 0 &&
                    Tools.Global.setting.windowTop < SystemParameters.FullPrimaryScreenHeight &&
                    Tools.Global.setting.windowLeft < SystemParameters.FullPrimaryScreenWidth)
                {
                    this.Left = Tools.Global.setting.windowLeft;
                    this.Top = Tools.Global.setting.windowTop;
                    this.Width = Math.Min(Math.Max(Tools.Global.setting.windowWidth, this.MinWidth), availableWidth);
                    this.Height = Math.Min(Math.Max(Tools.Global.setting.windowHeight, this.MinHeight), availableHeight);
                }
                else
                {
                    this.Width = Math.Min(Math.Max(PreferredWindowWidth, this.MinWidth), availableWidth);
                    this.Height = Math.Min(Math.Max(PreferredWindowHeight, this.MinHeight), availableHeight);
                }
            });
            LocationChanged += MainWindow_PlacementChanged;
            SizeChanged += MainWindow_PlacementChanged;
            StartupProfiler.Mark("MainWindow ctor exit");
        }
        ObservableCollection<ToSendData> toSendListItems = new ObservableCollection<ToSendData>();
        private const int QuickSendNavigationFirstColumn = 0;
        private const int QuickSendNavigationLastColumn = 2;
        private FrameworkElement quickSendSettingsAnchor;
        private bool quickSendKeyboardNavigationMode = false;
        private bool quickSendExplicitEditMode = false;
        private int quickSendNavigationRowIndex = -1;
        private int quickSendNavigationColumn = QuickSendNavigationFirstColumn;
        private TextBox quickSendNavigationTextBox = null;
        private bool forcusClosePort = true;
        private bool canSaveSendList = true;
        private bool isOpeningPort = false;
        private bool applyingSendSuggestion = false;
        private bool lazyLoadReady = false;
        private bool scriptEditorInitialized = false;
        private Task runtimeFilesTask = null;
        private CancellationTokenSource quickSendImportCts = null;
        private long nextQuickSendImportRunId = 0;
        private long activeQuickSendImportRunId = 0;
        private readonly object receiveScriptContextLock = new object();
        private ReceiveScriptContext currentReceiveScriptContext = new ReceiveScriptContext();
        private readonly List<ToolModule> toolModules = new List<ToolModule>();
        private bool toolsInitialized = false;
        private bool rightToolsCollapsed = false;
        private double expandedRightToolsWidth = ExpandedRightToolsDefaultWidth;
        private double mainGridLayoutWidth;
        private Pages.MultiPortPage mainSplitPortPage = null;
        private Pages.DataShowPage pendingSingleSerialLogPage = null;
        private int appliedSerialSplitScreenCount = -1;
        private bool syncingSerialSplitControls = false;
        private bool refreshingSendTargetSelector = false;
        private int lastSerialSendTargetSlot = 1;
        private const int MaxNotificationItems = 200;
        private readonly ObservableCollection<AppNotificationItem> notificationItems =
            new ObservableCollection<AppNotificationItem>();
        private ICollectionView notificationView;
        private NotificationFilter selectedNotificationFilter = NotificationFilter.All;
        private bool refreshingNotificationFilters;
        private int unreadNotificationCount;
        private Window notificationDetachedWindow;
        private string lastMainSendTargetDisplayName = string.Empty;
        private bool windowIsClosing;
        private readonly UpdateCheckController updateCheckController;
        public static string recvScriptBackup = "";

        private sealed class SendSuggestionItem
        {
            public string SendText { get; set; }
            public string ButtonText { get; set; }

            public override string ToString()
            {
                return SendText ?? string.Empty;
            }
        }

        private sealed class QuickSendImportItem
        {
            public int id { get; set; }
            public string text { get; set; }
            public bool hex { get; set; }
            public string commit { get; set; }
            public string recvScriptPath { get; set; }
            public string recvScriptPara { get; set; }
            public bool appendCrlf { get; set; }
            public bool disableSuggestion { get; set; }
        }

        private sealed class QuickSendImportResult
        {
            public bool ImportsAllPages { get; set; }
            public List<List<QuickSendImportItem>> Pages { get; set; }
            public List<string> PageNames { get; set; }
        }

        private sealed class AppNotificationItem
        {
            public DateTime Timestamp { get; set; }
            public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            public string Title { get; set; }
            public string Message { get; set; }
            public AppNotificationLevel Level { get; set; }
            public AppNotificationCategory Category { get; set; }
            public string PortName { get; set; }
            public System.Windows.Media.Brush IndicatorBrush { get; set; }
        }

        private enum NotificationFilter
        {
            All,
            Info,
            Success,
            Warning,
            Error
        }

        private sealed class NotificationFilterOption
        {
            public NotificationFilter Filter { get; set; }
            public string Text { get; set; }
            public System.Windows.Media.Brush IndicatorBrush { get; set; }
        }

        private sealed class SendTargetItem
        {
            public string Text { get; set; }
            public int SerialSlot { get; set; }
            public bool IsNetwork { get; set; }
            public bool IsAllSerial { get; set; }

            public override string ToString()
            {
                return Text ?? string.Empty;
            }
        }

        private sealed class ToolModule
        {
            private readonly Func<FrameworkElement> contentFactory;
            private FrameworkElement content;

            public ToolModule(string key, string title, Func<FrameworkElement> contentFactory)
            {
                Key = key;
                Title = title;
                this.contentFactory = contentFactory;
            }

            public string Key { get; }
            public string Title { get; }

            public FrameworkElement GetContent()
            {
                if (content == null)
                    content = contentFactory();
                return content;
            }
        }
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartupProfiler.Mark("MainWindow.Loaded enter");
            //延迟启动，加快软件第一屏出现速度
            Task.Run(() =>
            {
                StartupProfiler.Mark("MainWindow.Loaded dispatcher task queued");
                this.Dispatcher.Invoke(new Action(delegate {
                    StartupProfiler.Mark("MainWindow.Loaded dispatcher block enter");
                    StartupProfiler.Measure("Loaded register events", () =>
                    {
                        //接收到、发送数据成功回调
                        Tools.Global.uart.UartDataRecived += Uart_UartDataRecived;
                        Tools.Global.uart.UartDataSent += Uart_UartDataSent;
                        Tools.Global.SendRawDataRequest += Global_SendRawDataRequest;
                        Tools.Global.SendDataRequest += Global_SendDataRequest;
                        Tools.Global.SendDataAsyncRequest = Global_SendDataRequestAsync;
                        Tools.Global.MainSendTargetChangedEvent += Global_MainSendTargetChangedEvent;
                        Tools.Global.ThemeChanged += Global_ThemeChanged;
                        Tools.Global.UartPortClosedEvent += Global_UartPortClosedEvent;
                        Tools.Global.SerialSplitScreenChangedEvent += Global_SerialSplitScreenChangedEvent;
                        Tools.Global.SerialPinStatusChangedEvent += Global_SerialPinStatusChangedEvent;
                        Tools.Global.AppNotificationEvent += Global_AppNotificationEvent;
                        Tools.Global.IsActiveSerialTargetOpenRequest = IsActiveSerialTargetOpenForTools;
                        Tools.Global.EnsureActiveSerialTargetOpenRequest = EnsureActiveSerialTargetOpenForTools;
                        Tools.Global.SendRawDataToActiveTargetRequest = SendRawDataToActiveTargetForTools;
                        Tools.Global.CaptureActiveSerialTargetRequest = CaptureActiveSerialTargetForTools;
                    });

                    //初始化所有数据
                    StartupProfiler.Measure("Global.Initial", Tools.Global.Initial);
                    StartupProfiler.Mark("Global.PrepareRuntimeFiles task schedule");
                    runtimeFilesTask = Task.Run(() =>
                        StartupProfiler.Measure("Global.PrepareRuntimeFiles background", Tools.Global.PrepareRuntimeFiles));

                    StartupProfiler.Measure("Loaded window events and topmost", () =>
                    {
                        //重写关闭窗口代码
                        this.Closing += MainWindow_Closing;

                        //窗口置顶事件
                        Tools.Global.setting.MainWindowTop += new EventHandler(topEvent);
                        if (Tools.Global.setting.topmost)//设置窗口置顶
                            this.Topmost = true;
                    });

                    //收发数据显示页面
                    StartupProfiler.Measure("Apply serial split layout", ApplySerialSplitLayout);

                    StartupProfiler.Measure("Loaded baud rate init", () =>
                    {
                        //加载初始波特率
                        SetBaudRateComboBoxFromSetting();
                    });

                    StartupProfiler.Measure("Loaded device hook", () =>
                    {
                        // 绑定事件监听,用于监听HID设备插拔
                        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
                    });

                    //刷新设备列表
                    StartupProfiler.Measure("refreshPortList schedule", () => refreshPortList());

                    StartupProfiler.Measure("Loaded bind data contexts", () =>
                    {
                        //绑定数据
                        this.toSendDataTextBox.DataContext = Tools.Global.setting;
                        toSendList.ItemsSource = toSendListItems;
                        this.sentCountTextBlock.DataContext = Tools.Global.setting;
                        this.receivedCountTextBlock.DataContext = Tools.Global.setting;
                        UpdateMainSendTargetUi();
                    });

                    StartupProfiler.Measure("LoadQuickSendList", () =>
                    {
                        //初始化快捷发送栏的数据
                        canSaveSendList = false;
                        if (Global.setting.quickSendSelect == -1)
                            Global.setting.quickSendSelect = 0;
                        LoadQuickSendList();
                        // Loading normalizes legacy/default labels and raises DataChanged.
                        // Subscribe only after the in-memory view is complete so startup
                        // can never overwrite a valid settings.json with partial UI state.
                        ToSendData.DataChanged += SaveSendList;
                        canSaveSendList = true;
                        if (Tools.Global.TryConsumeQuickSendLegacyRecoveryCandidate(out var candidate))
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                                OfferLegacyQuickSendRecovery(candidate)));
                        }
                        if (Tools.Global.ConsumeQuickSendLegacyRecoveryNotice())
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                                Tools.MessageBox.Show(
                                    TryFindResource("QuickSendBackupRecoveredLegacy") as string ??
                                    "Quick send data was restored from a legacy backup.")));
                        }
                    });

                    StartupProfiler.Measure("Loaded title and events", () =>
                    {
                        this.Title += $" - {Tools.AppInfo.DisplayVersion}";
                        UpdateThemeToggleMenu();

                        //更换标题栏
                        var title = "";
                        title = this.Title;
                        Tools.Global.ChangeTitleEvent += (n, s) =>
                        {
                            this.Dispatcher.Invoke(() => this.Title = title + s);
                        };

                        Tools.Global.RefreshScriptListEvent += (n, s) =>
                        {
                            this.Dispatcher.Invoke(() =>
                            {
                                if (scriptEditorInitialized)
                                    RefreshScriptList();
                            });
                        };
                    });

                    //加载完了，可以允许点击
                    lazyLoadReady = true;
                    MainGrid.IsEnabled = true;
                    StartupProfiler.Mark("MainWindow interactive");
                    Dispatcher.BeginInvoke(
                        new Action(() => _ = updateCheckController.CheckOnStartupAsync()),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    StartupProfiler.Mark("MainWindow.Loaded dispatcher block exit");
                }));
                StartupProfiler.Mark("MainWindow.Loaded dispatcher task finished");
            });
            StartupProfiler.Measure("Loaded recv script backup", () =>
            {
                recvScriptBackup = Tools.Global.setting.recvScript;
                if (string.IsNullOrEmpty(recvScriptBackup)) recvScriptBackup = "default";
            });
            StartupProfiler.Mark("MainWindow.Loaded exit");
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!lazyLoadReady)
                return;
            if (!ReferenceEquals(e.OriginalSource, MainTabControl))
                return;

            if (MainTabControl.SelectedItem != QuickSendTab)
            {
                CloseQuickSendItemSettings();
                ExitQuickSendKeyboardNavigation();
            }

            if (MainTabControl.SelectedItem == ScriptTab)
                EnsureScriptEditorInitialized();
            else if (MainTabControl.SelectedItem == ToolsTab)
                EnsureToolModulesInitialized();
        }

        private void EnsureScriptEditorInitialized()
        {
            if (scriptEditorInitialized)
                return;
            scriptEditorInitialized = true;
            WaitRuntimeFilesReady();

            SearchPanel.Install(textEditor.TextArea);

            Tools.EditorTheme.Apply(textEditor);

            loadScriptFile(Tools.Global.setting.runScript);

            ScriptEnv.ScriptApis.PrintScriptLog += ScriptApis_PrintScriptLog;
            ScriptEnv.JavaScriptRunEnv.ScriptRunError += JavaScriptRunEnv_ScriptRunError;
            new Thread(ScriptLogPrintTask).Start();
        }

        private void EnsureToolModulesInitialized()
        {
            if (!toolsInitialized)
            {
                toolsInitialized = true;
                RegisterToolModules();
                ToolListBox.ItemsSource = toolModules;
            }

            if (ToolListBox.SelectedIndex < 0 && toolModules.Count > 0)
                ToolListBox.SelectedIndex = 0;

            ShowSelectedToolModule();
        }

        private void RegisterToolModules()
        {
            toolModules.Clear();
            AddFrameTool("EncodingTools", GetResourceText("EncodingToolsTab", "编码转换工具"), "UI/Pages/ConvertPage.xaml");
            AddFrameTool("Mqtt", "MQTT", "UI/Pages/MqttTestPage.xaml");
            AddFrameTool("SerialMonitor", GetResourceText("SerialMonitorHeader", "串口监听"), "UI/Pages/SerialMonitorPage.xaml");
            AddFrameTool("LogReplay", GetResourceText("LogReplayToolTab", "日志回放"), "UI/Pages/LogReplayPage.xaml");
            AddFrameTool("CircularSend", GetResourceText("CircularSendToolTab", "循环发送"), "UI/Pages/CircularSendPage.xaml");
            AddFrameTool("EncodingFix", GetResourceText("EncodingFixHeader", "乱码修复"), "UI/Pages/EncodingFixPage.xaml");
            AddFrameTool("Plot", GetResourceText("PlotHeader", "曲线"), "UI/Pages/PlotPage.xaml");
            AddFrameTool("WinUsb", "WinUSB", "UI/Pages/WinUSBPage.xaml");
            AddContentTool("HttpTool", GetResourceText("HttpToolTab", "HTTP工具"), () => new HttpToolWindow());
            AddContentTool("DataCalc", GetResourceText("DataCalcToolTab", "数据计算/文件发送"), () => new Pages.DataCalcFileSendView());
            AddFrameTool("TcpClient", GetResourceText("TcpClientTitle", "socket客户端"), "UI/Pages/SocketClientPage.xaml");
        }

        private void RefreshToolModulesLocalization()
        {
            if (!toolsInitialized || ToolListBox == null)
                return;

            var selectedKey = (ToolListBox.SelectedItem as ToolModule)?.Key;
            RegisterToolModules();
            ToolListBox.ItemsSource = null;
            ToolListBox.ItemsSource = toolModules;

            var selectedModule = toolModules.FirstOrDefault(module => module.Key == selectedKey);
            ToolListBox.SelectedItem = selectedModule ?? toolModules.FirstOrDefault();
            ShowSelectedToolModule();
        }

        private void AddFrameTool(string key, string title, string pagePath)
        {
            toolModules.Add(new ToolModule(key, title, () =>
            {
                var frame = new Frame { NavigationUIVisibility = NavigationUIVisibility.Hidden };
                StartupProfiler.Measure($"CreateToolFrame {pagePath}", () =>
                    frame.Navigate(new Uri(pagePath, UriKind.Relative)));
                return frame;
            }));
        }

        private void Global_SerialSplitScreenChangedEvent(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplySerialSplitLayout));
        }

        private void AddSerialSplitPaneButton_Click(object sender, RoutedEventArgs e)
        {
            var currentCount = mainSplitPortPage?.SlotCount ?? GetSerialSplitScreenCount();
            if (currentCount >= 4)
            {
                UpdateAddSerialSplitPaneButton();
                return;
            }

            if (mainSplitPortPage?.AddSlot() == true)
                return;

            Tools.Global.setting.serialSplitScreenCount = Math.Max(2, currentCount + 1);
            ApplySerialSplitLayout();
        }

        private void MainSplitPortPage_SlotCountChanged(int count)
        {
            var normalizedCount = Math.Max(1, Math.Min(4, count));
            appliedSerialSplitScreenCount = normalizedCount;
            lastSerialSendTargetSlot = Math.Max(
                1,
                Math.Min(lastSerialSendTargetSlot, normalizedCount));

            if (Tools.Global.setting.serialSplitScreenCount != normalizedCount)
                Tools.Global.setting.serialSplitScreenCount = normalizedCount;

            RefreshSerialSplitTargetSelector(normalizedCount);
            UpdateAddSerialSplitPaneButton();
            UpdateMainPaneMinimum();
            UpdateSelectedSplitSlotControls();

            // 删除到只剩一个窗口时，立刻恢复普通单串口日志页。仅把分屏
            // 数量缩到 1 会让 PortSlot 的标题继续留在界面上。
            if (normalizedCount == 1)
                ApplySerialSplitLayout();
        }

        private void UpdateAddSerialSplitPaneButton()
        {
            if (AddSerialSplitPaneButton == null)
                return;

            var count = mainSplitPortPage?.SlotCount ?? GetSerialSplitScreenCount();
            var canAdd = count < 4;
            AddSerialSplitPaneButton.IsEnabled = canAdd;
            AddSerialSplitPaneButton.ToolTip = TryFindResource(
                canAdd ? "AddSerialSplitPaneTip" : "AddSerialSplitPaneMaximumTip") as string ??
                (canAdd ? "新增一个串口分屏，最多支持 4 个。" : "已达到最多 4 个分屏。");
        }

        private int GetSerialSplitScreenCount()
        {
            return Math.Max(1, Math.Min(4, Tools.Global.setting?.serialSplitScreenCount ?? 1));
        }

        private bool IsSerialSplitModeRequested()
        {
            return mainSplitPortPage != null || GetSerialSplitScreenCount() > 1;
        }

        private bool IsSerialSplitModeActive()
        {
            return mainSplitPortPage != null && appliedSerialSplitScreenCount >= 1;
        }

        private static bool ShouldUseSerialSplitPage(int count)
        {
            return count > 1;
        }

        private static Pages.DataShowPage CreateSingleSerialLogPage(Pages.MultiPortPage splitPage)
        {
            var page = new Pages.DataShowPage();
            page.SetLogSnapshot(splitPage?.GetSlotLogSnapshot(1));
            return page;
        }

        private void ShowSerialLogPage(Page page)
        {
            if (dataShowFrame == null || page == null || ReferenceEquals(dataShowFrame.Content, page))
                return;

            dataShowFrame.Navigate(page);
        }

        private void DataShowFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (pendingSingleSerialLogPage != null &&
                ReferenceEquals(e.Content, pendingSingleSerialLogPage))
            {
                pendingSingleSerialLogPage = null;
            }
        }

        private void ApplySerialSplitLayout()
        {
            if (dataShowFrame == null || serialSplitSendTargetPanel == null)
                return;

            var count = GetSerialSplitScreenCount();
            UpdateMainPaneMinimum();

            // 已进入分屏页面后直接原地调整 PortSlot，不能重建页面，否则其它
            // 分屏的串口连接、日志和滚动位置都会丢失。
            if (ShouldUseSerialSplitPage(count) &&
                mainSplitPortPage != null &&
                ReferenceEquals(dataShowFrame.Content, mainSplitPortPage))
            {
                mainSplitPortPage.ResizeSlotCount(count);
                appliedSerialSplitScreenCount = mainSplitPortPage.SlotCount;
                serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                RefreshSerialSplitTargetSelector(appliedSerialSplitScreenCount);
                serialSplitSendTargetComboBox.IsEnabled =
                    ShouldEnableSendTargetSelector(appliedSerialSplitScreenCount);
                UpdateAddSerialSplitPaneButton();
                if (!IsMainSendTargetSelected())
                    mainSplitPortPage.SetActiveSlot(GetSelectedSerialSplitSlot());
                UpdateSelectedSplitSlotControls();
                return;
            }

            RefreshSerialSplitTargetSelector(count);

            if (!ShouldUseSerialSplitPage(count))
            {
                // serialSplitScreenCount 会再异步通知一次布局更新。第一次更新已
                // 保存日志并开始导航时，第二次必须等待同一个页面完成，不能再
                // 从已置空的 mainSplitPortPage 创建一个空白日志页覆盖它。
                if (pendingSingleSerialLogPage != null)
                {
                    appliedSerialSplitScreenCount = 1;
                    serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                    serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
                    SetMainSerialControlsEnabled(true);
                    UpdateAddSerialSplitPaneButton();
                    return;
                }

                if (appliedSerialSplitScreenCount == 1 && dataShowFrame.Content is Pages.DataShowPage)
                {
                    serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                    serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
                    SetMainSerialControlsEnabled(true);
                    UpdateAddSerialSplitPaneButton();
                    return;
                }

                serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
                // 在释放分屏串口和页面之前先保存最后一个窗口的日志。否则切回
                // 普通日志页时只能创建一个空页面，窗口 1 的历史记录会全部丢失。
                var singleLogPage = dataShowFrame.Content as Pages.DataShowPage ??
                    CreateSingleSerialLogPage(mainSplitPortPage);
                if (mainSplitPortPage != null)
                {
                    mainSplitPortPage.ActiveSlotChanged -= MainSplitPortPage_ActiveSlotChanged;
                    mainSplitPortPage.SlotCountChanged -= MainSplitPortPage_SlotCountChanged;
                    // 必须在切换回单屏页面之前同步关闭并 Dispose 所有分屏串口，
                    // 不能再依赖旧页面稍后触发的 Unloaded。
                    mainSplitPortPage.ReleaseAllPortsForLayoutChange();
                }
                mainSplitPortPage = null;
                appliedSerialSplitScreenCount = 1;
                if (!ReferenceEquals(dataShowFrame.Content, singleLogPage))
                {
                    pendingSingleSerialLogPage = singleLogPage;
                    ShowSerialLogPage(singleLogPage);
                }
                SetMainSerialControlsEnabled(true);
                UpdateMainSerialConnectionStatus();
                UpdateAddSerialSplitPaneButton();
                return;
            }

            serialSplitSendTargetPanel.Visibility = Visibility.Visible;
            serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
            var initialFirstPortName = mainSplitPortPage?.GetSlotPortName(1);
            if (string.IsNullOrWhiteSpace(initialFirstPortName))
                initialFirstPortName = GetSelectedPortName();
            var initialFirstLogSnapshot =
                (dataShowFrame.Content as Pages.DataShowPage)?.GetLogSnapshot();

            if (mainSplitPortPage == null)
            {
                pendingSingleSerialLogPage = null;
                mainSplitPortPage = new Pages.MultiPortPage(
                    count,
                    false,
                    initialFirstPortName,
                    initialFirstLogSnapshot);
                mainSplitPortPage.ActiveSlotChanged += MainSplitPortPage_ActiveSlotChanged;
                mainSplitPortPage.SlotCountChanged += MainSplitPortPage_SlotCountChanged;
                ShowSerialLogPage(mainSplitPortPage);
                appliedSerialSplitScreenCount = count;
            }

            UpdateAddSerialSplitPaneButton();
            if (!IsMainSendTargetSelected())
                mainSplitPortPage.SetActiveSlot(GetSelectedSerialSplitSlot());
            UpdateSelectedSplitSlotControls();
        }

        private void MainSplitPortPage_ActiveSlotChanged(int slotNumber)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => MainSplitPortPage_ActiveSlotChanged(slotNumber)));
                return;
            }

            if (serialSplitSendTargetComboBox == null)
                return;

            lastSerialSendTargetSlot = Math.Max(1, slotNumber);
            var item = serialSplitSendTargetComboBox.Items
                .OfType<SendTargetItem>()
                .FirstOrDefault(target =>
                    !target.IsNetwork &&
                    !target.IsAllSerial &&
                    target.SerialSlot == lastSerialSendTargetSlot);
            if (item != null && !ReferenceEquals(serialSplitSendTargetComboBox.SelectedItem, item))
                serialSplitSendTargetComboBox.SelectedItem = item;
            else
                UpdateSelectedSplitSlotControls();
        }

        private void SetMainSerialControlsEnabled(bool enabled)
        {
            var pendingSwitch = IsMainSerialPortSwitchPending();
            if (serialPortsListComboBox != null)
                // 端口下拉框始终保持可选。选中其他端口只进入待切换状态，
                // 只有状态按钮会真正关闭旧端口并打开新端口。
                serialPortsListComboBox.IsEnabled = true;
            if (connectionStatusButton != null)
                connectionStatusButton.IsEnabled = enabled &&
                    (serialPortsListComboBox.Items.Count > 0 || Tools.Global.uart.IsOpen());
            if (baudRateComboBox != null)
                baudRateComboBox.IsEnabled = enabled && !pendingSwitch;
            if (FlowControlButton != null)
                FlowControlButton.IsEnabled = enabled && !pendingSwitch;
        }

        private bool ShouldEnableSendTargetSelector(int serialTargetCount)
        {
            return serialTargetCount > 1 || Tools.Global.HasMainSendTarget;
        }

        private void RefreshSerialSplitTargetSelector(int count, bool preferNetworkTarget = false)
        {
            if (serialSplitSendTargetComboBox == null)
                return;

            var oldTarget = serialSplitSendTargetComboBox.SelectedItem as SendTargetItem;
            if (oldTarget != null && !oldTarget.IsNetwork && !oldTarget.IsAllSerial)
                lastSerialSendTargetSlot = Math.Max(1, oldTarget.SerialSlot);

            refreshingSendTargetSelector = true;
            try
            {
                serialSplitSendTargetComboBox.Items.Clear();
                var safeCount = Math.Max(1, count);
                for (int i = 1; i <= safeCount; i++)
                {
                    serialSplitSendTargetComboBox.Items.Add(new SendTargetItem
                    {
                        Text = safeCount == 1
                            ? (TryFindResource("SerialSendTarget") as string ?? "串口")
                            : string.Format(TryFindResource("SplitSendTargetItem") as string ?? "窗口 {0}", i),
                        SerialSlot = i,
                        IsNetwork = false
                    });
                }

                SendTargetItem allSerialItem = null;
                if (safeCount > 1)
                {
                    allSerialItem = new SendTargetItem
                    {
                        Text = TryFindResource("SplitSendTargetAll") as string ?? "全部",
                        SerialSlot = 0,
                        IsNetwork = false,
                        IsAllSerial = true
                    };
                    serialSplitSendTargetComboBox.Items.Add(allSerialItem);
                }

                SendTargetItem networkItem = null;
                if (Tools.Global.HasMainSendTarget)
                {
                    networkItem = new SendTargetItem
                    {
                        Text = Tools.Global.MainSendTargetDisplayName,
                        SerialSlot = lastSerialSendTargetSlot,
                        IsNetwork = true
                    };
                    serialSplitSendTargetComboBox.Items.Add(networkItem);
                }

                SendTargetItem selected = null;
                if (networkItem != null && (preferNetworkTarget || oldTarget?.IsNetwork == true))
                    selected = networkItem;
                else if (allSerialItem != null && oldTarget?.IsAllSerial == true)
                    selected = allSerialItem;

                if (selected == null)
                {
                    var serialSlot = Math.Max(1, Math.Min(lastSerialSendTargetSlot, safeCount));
                    selected = serialSplitSendTargetComboBox.Items
                        .OfType<SendTargetItem>()
                        .FirstOrDefault(target =>
                            !target.IsNetwork &&
                            !target.IsAllSerial &&
                            target.SerialSlot == serialSlot);
                }

                serialSplitSendTargetComboBox.SelectedItem = selected ?? serialSplitSendTargetComboBox.Items.OfType<SendTargetItem>().FirstOrDefault();
                serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
                serialSplitSendTargetComboBox.ToolTip = Tools.Global.HasMainSendTarget
                    ? string.Format(
                        TryFindResource("SendDataToTargetTip") as string ?? "当前主发送框会发送到 {0}。",
                        Tools.Global.MainSendTargetDisplayName)
                    : null;
            }
            finally
            {
                refreshingSendTargetSelector = false;
            }
        }

        private bool IsMainSendTargetSelected()
        {
            return Tools.Global.HasMainSendTarget &&
                   (serialSplitSendTargetComboBox?.SelectedItem as SendTargetItem)?.IsNetwork == true;
        }

        private bool IsAllSerialTargetsSelected()
        {
            return IsSerialSplitModeActive() &&
                   (serialSplitSendTargetComboBox?.SelectedItem as SendTargetItem)?.IsAllSerial == true;
        }


        private int GetSelectedSerialSplitSlot()
        {
            var item = serialSplitSendTargetComboBox?.SelectedItem as SendTargetItem;
            if (item != null && !item.IsNetwork && !item.IsAllSerial)
            {
                lastSerialSendTargetSlot = Math.Max(1, item.SerialSlot);
                return lastSerialSendTargetSlot;
            }

            return Math.Max(1, Math.Min(lastSerialSendTargetSlot, GetSerialSplitScreenCount()));
        }

        private void SerialSplitSendTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (refreshingSendTargetSelector)
                return;

            var item = serialSplitSendTargetComboBox?.SelectedItem as SendTargetItem;
            if (item != null && !item.IsNetwork && !item.IsAllSerial)
                lastSerialSendTargetSlot = Math.Max(1, item.SerialSlot);

            if (item?.IsNetwork == true || mainSplitPortPage == null)
                return;

            if (item?.IsAllSerial == true)
            {
                UpdateSelectedSplitSlotControls();
                return;
            }

            mainSplitPortPage.SetActiveSlot(GetSelectedSerialSplitSlot());
            UpdateSelectedSplitSlotControls();
        }

        private void UpdateSelectedSplitSlotControls()
        {
            if (!IsSerialSplitModeActive() || mainSplitPortPage == null)
                return;

            syncingSerialSplitControls = true;
            try
            {
                if (IsAllSerialTargetsSelected())
                {
                    serialPortsListComboBox.IsEnabled = false;
                    connectionStatusButton.IsEnabled = false;
                    baudRateComboBox.IsEnabled = false;
                    FlowControlButton.IsEnabled = false;
                    statusTextBlock.Text = TryFindResource("SplitSendTargetAll") as string ?? "全部";
                    return;
                }

                var slot = GetSelectedSerialSplitSlot();
                var isOpen = mainSplitPortPage.IsSlotSelectedPortOpen(slot);
                var slotPort = mainSplitPortPage.GetSlotPortName(slot);
                SelectSerialPortComboBoxItem(slotPort);
                SetBaudRateComboBoxValue(mainSplitPortPage.GetSlotBaudRate(slot));

                serialPortsListComboBox.IsEnabled = true;
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0 || isOpen;
                baudRateComboBox.IsEnabled = true;
                FlowControlButton.IsEnabled = false;
                statusTextBlock.Text = string.Format(
                    TryFindResource("SplitModeConnectionStatus") as string ?? "窗口 {0} · {1}",
                    slot,
                    TryFindResource(isOpen ? "OpenPort_open" : "OpenPort_close") as string ?? "?!");
            }
            finally
            {
                syncingSerialSplitControls = false;
            }
        }

        private void SelectSerialPortComboBoxItem(string portName)
        {
            if (serialPortsListComboBox == null)
                return;

            if (string.IsNullOrWhiteSpace(portName))
            {
                if (serialPortsListComboBox.Items.Count > 0)
                    serialPortsListComboBox.SelectedIndex = Math.Min(GetSelectedSerialSplitSlot() - 1, serialPortsListComboBox.Items.Count - 1);
                return;
            }

            foreach (var item in serialPortsListComboBox.Items)
            {
                var text = item as string;
                if (!string.IsNullOrWhiteSpace(text) && text.Contains($"({portName})"))
                {
                    serialPortsListComboBox.SelectedItem = item;
                    return;
                }
            }
        }

        private void SetBaudRateComboBoxValue(int baudRate)
        {
            var text = baudRate.ToString();
            for (int i = 0; i < baudRateComboBox.Items.Count - 1; i++)
            {
                if ((baudRateComboBox.Items[i] as ComboBoxItem)?.Content?.ToString() == text)
                {
                    lastBaudRateSelectedIndex = i;
                    baudRateComboBox.SelectedIndex = i;
                    return;
                }
            }

            lastBaudRateSelectedIndex = baudRateComboBox.Items.Count - 1;
            baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = text;
            baudRateComboBox.Text = text;
        }

        private bool IsActiveSerialTargetOpenForTools()
        {
            return CaptureActiveSerialTargetForTools()?.IsOpen == true;
        }

        private ActiveSerialTarget CaptureActiveSerialTargetForTools()
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(new Func<ActiveSerialTarget>(CaptureActiveSerialTargetForTools));

            if (IsSerialSplitModeActive())
            {
                var page = mainSplitPortPage;
                if (page == null)
                    return null;

                if (IsAllSerialTargetsSelected())
                {
                    var targets = Enumerable.Range(1, page.SlotCount)
                        .Select(page.CaptureSerialTarget)
                        .Where(target => target != null)
                        .ToList();
                    if (targets.Count == 0)
                        return null;

                    var displayName = TryFindResource("SplitSendTargetAll") as string ?? "全部";
                    return new ActiveSerialTarget(
                        "serial-all:" + string.Join("|", targets.Select(target => target.Identity)),
                        displayName,
                        () => targets.All(target => target.IsOpen),
                        (data, token, committedBytes) =>
                            SendToCapturedSerialTargets(targets, data, token, committedBytes),
                        supportsResumableCommits: false);
                }

                return page.CaptureSerialTarget(GetSelectedSerialSplitSlot());
            }

            var connection = Tools.Global.uart.CaptureConnectionLease();
            return new ActiveSerialTarget(
                connection.Identity,
                connection.DisplayName,
                () => connection.IsOpen,
                (data, token, committedBytes) =>
                    connection.Send(data, token, committedBytes, raiseEvents: false));
        }

        private static bool SendToCapturedSerialTargets(
            IReadOnlyList<ActiveSerialTarget> targets,
            byte[] data,
            CancellationToken token,
            Action<int> committedBytes)
        {
            if (targets == null || targets.Count == 0)
                return false;

            var committedByTarget = new int[targets.Count];
            var reportedForAll = 0;
            var commitLock = new object();
            var allSent = true;
            for (var index = 0; index < targets.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var targetIndex = index;
                var target = targets[targetIndex];
                var sent = target.IsOpen && target.Send(
                    data,
                    token,
                    count =>
                    {
                        var report = 0;
                        lock (commitLock)
                        {
                            committedByTarget[targetIndex] = Math.Min(
                                data.Length,
                                committedByTarget[targetIndex] + Math.Max(0, count));
                            var committedForAll = committedByTarget.Min();
                            report = committedForAll - reportedForAll;
                            if (report > 0)
                                reportedForAll = committedForAll;
                        }
                        if (report > 0)
                            committedBytes?.Invoke(report);
                    });
                allSent = sent && allSent;
            }
            return allSent;
        }

        private bool EnsureActiveSerialTargetOpenForTools()
        {
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(new Func<bool>(EnsureActiveSerialTargetOpenForTools));

            if (IsSerialSplitModeActive())
            {
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();
                if (mainSplitPortPage == null)
                    return false;
                if (IsAllSerialTargetsSelected())
                {
                    var allOpened = true;
                    for (var slot = 1; slot <= mainSplitPortPage.SlotCount; slot++)
                        allOpened = mainSplitPortPage.EnsureSlotOpen(slot) && allOpened;
                    return allOpened;
                }
                return mainSplitPortPage.EnsureSlotOpen(GetSelectedSerialSplitSlot());
            }

            if (IsSelectedMainSerialPortOpen())
                return true;

            return OpenSelectedPortBlocking();
        }

        private bool OpenSelectedPortBlocking()
        {
            if (isOpeningPort)
                return IsSelectedMainSerialPortOpen();

            if (IsMainSerialPortSwitchPending())
            {
                ShowSerialPortSwitchRequiredBeforeSend();
                return false;
            }

            ApplySelectedUartProfile();
            var port = GetSelectedPortName();
            if (string.IsNullOrWhiteSpace(port))
            {
                ShowOpenPortFailed("未选择串口。");
                return false;
            }

            isOpeningPort = true;
            try
            {
                forcusClosePort = false;
                Tools.Global.uart.SetName(port);
                Tools.Global.uart.Open();
                Tools.Logger.StartSessionLog(port);
                serialPortsListComboBox.IsEnabled = true;
                connectionStatusButton.IsEnabled = true;
                UpdateMainSerialConnectionStatus();
                AddSerialConnectionNotification(port, reconnected: false);
                return true;
            }
            catch (Exception ex)
            {
                Tools.Logger.AddUartLogDebug($"[OpenSelectedPortBlocking]open error:{ex}");
                // 打开失败后停止把该端口当作“刚刚意外断开”的端口自动重连，
                // 否则每次刷新设备列表都会再次弹出同一个错误。
                forcusClosePort = true;
                ShowOpenPortFailed(ex.Message);
                return false;
            }
            finally
            {
                isOpeningPort = false;
            }
        }

        private bool SendRawDataToActiveTargetForTools(byte[] data, CancellationToken token)
        {
            if (data == null || data.Length == 0)
                return false;

            var target = CaptureActiveSerialTargetForTools();
            return target?.IsOpen == true && target.Send(data, token, null);
        }

        private void AddLaunchTool(string key, string title, string buttonText, RoutedEventHandler clickHandler)
        {
            toolModules.Add(new ToolModule(key, title, () => CreateLaunchToolPanel(buttonText, clickHandler)));
        }

        private void AddContentTool(string key, string title, Func<FrameworkElement> contentFactory)
        {
            toolModules.Add(new ToolModule(key, title, contentFactory));
        }

        private FrameworkElement CreateLaunchToolPanel(string buttonText, RoutedEventHandler clickHandler)
        {
            var grid = new Grid();
            var button = new Button
            {
                MinWidth = 180,
                MinHeight = 36,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = buttonText
            };
            button.Click += clickHandler;
            grid.Children.Add(button);
            return grid;
        }

        private string GetResourceText(string key, string fallback)
        {
            return TryFindResource(key) as string ?? fallback;
        }

        private void ToolListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!lazyLoadReady)
                return;
            if (!ReferenceEquals(e.OriginalSource, ToolListBox))
                return;

            ShowSelectedToolModule();
        }

        private void ShowSelectedToolModule()
        {
            var module = ToolListBox.SelectedItem as ToolModule;
            if (module == null)
                return;

            ToolContentHost.Content = module.GetContent();
        }

        private void RightToolsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            SetRightToolsCollapsed(!rightToolsCollapsed);
        }

        private void SetRightToolsCollapsed(bool collapsed)
        {
            if (collapsed)
            {
                if (RightToolsColumn.ActualWidth > 1)
                    expandedRightToolsWidth = RightToolsColumn.ActualWidth;

                RightToolsPanel.Visibility = Visibility.Collapsed;
                RightTopActions.Visibility = Visibility.Collapsed;
                RightToolsGridSplitter.Visibility = Visibility.Collapsed;
                RightToolsColumn.MinWidth = 0;
                RightToolsColumn.Width = new GridLength(0);
                RightToolsToggleIcon.Icon = FontAwesomeIcon.AngleDoubleLeft;
                RightToolsToggleButton.ToolTip = GetResourceText("ExpandRightTools", "展开右侧工具");
            }
            else
            {
                RightToolsColumn.MaxWidth = double.PositiveInfinity;
                RightToolsColumn.MinWidth = ExpandedRightToolsMinWidth;
                RightToolsColumn.Width = new GridLength(Math.Max(ExpandedRightToolsMinWidth, expandedRightToolsWidth));
                RightToolsPanel.Visibility = Visibility.Visible;
                RightTopActions.Visibility = Visibility.Visible;
                RightToolsGridSplitter.Visibility = Visibility.Visible;
                RightToolsToggleIcon.Icon = FontAwesomeIcon.AngleDoubleRight;
                RightToolsToggleButton.ToolTip = GetResourceText("CollapseRightTools", "收起右侧工具");
            }

            rightToolsCollapsed = collapsed;
            UpdateMainPaneMinimum();
        }

        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            mainGridLayoutWidth = e.NewSize.Width;
            UpdateMainPaneMinimum();
            if (NotificationPopup?.IsOpen == true)
                PositionNotificationPopup();
        }

        private void RightToolsGridSplitter_DragDelta(object sender, DragDeltaEventArgs e)
        {
            ClampRightToolsColumnWidth();
        }

        private void RightToolsGridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            ClampRightToolsColumnWidth();
            if (!rightToolsCollapsed && RightToolsColumn.ActualWidth > 0)
                expandedRightToolsWidth = RightToolsColumn.ActualWidth;
        }

        private void UpdateMainPaneMinimum()
        {
            if (MainGrid == null || MainLogFlexibleColumn == null)
                return;

            var desiredMinimum = IsSerialSplitModeRequested()
                ? SplitPaneDesiredMinimumWidth
                : SinglePaneDesiredMinimumWidth;
            var availableWidth = mainGridLayoutWidth > 0
                ? mainGridLayoutWidth
                : MainGrid.ActualWidth;
            if (ActualWidth > 0)
                availableWidth = Math.Min(availableWidth, ActualWidth);
            if (availableWidth <= 0)
            {
                MainLogFlexibleColumn.MinWidth = 0;
                return;
            }

            var toggleReserve = RightToolsToggleColumn?.ActualWidth ?? 0;
            var rightReserve = rightToolsCollapsed
                ? 0
                : Math.Min(
                    ExpandedRightToolsMinWidth,
                    Math.Max(
                        ExpandedRightToolsFloorWidth,
                        availableWidth - MainPaneBaseWidth - toggleReserve));
            if (!rightToolsCollapsed)
                RightToolsColumn.MinWidth = rightReserve;
            var maximumFeasibleMinimum = Math.Max(
                MainPaneBaseWidth,
                availableWidth - rightReserve - toggleReserve - MainPaneLayoutReserve);
            var effectiveMinimum = Math.Min(desiredMinimum, maximumFeasibleMinimum);
            MainLogFlexibleColumn.MinWidth = Math.Max(0, effectiveMinimum - MainPaneBaseWidth);

            if (rightToolsCollapsed)
            {
                RightToolsColumn.MaxWidth = 0;
                return;
            }

            RightToolsColumn.MaxWidth = Math.Max(
                rightReserve,
                availableWidth - effectiveMinimum - toggleReserve - MainPaneLayoutReserve);
            ClampRightToolsColumnWidth();
        }

        private void ClampRightToolsColumnWidth()
        {
            if (rightToolsCollapsed || RightToolsColumn == null)
                return;

            var maximumWidth = RightToolsColumn.MaxWidth;
            if (double.IsInfinity(maximumWidth) || double.IsNaN(maximumWidth))
                return;

            var requestedWidth = RightToolsColumn.Width.IsAbsolute
                ? RightToolsColumn.Width.Value
                : RightToolsColumn.ActualWidth;
            if (requestedWidth > maximumWidth)
                RightToolsColumn.Width = new GridLength(maximumWidth);
        }

        private void NavigateFrameOnce(Frame frame, string path)
        {
            if (frame.Content == null)
                StartupProfiler.Measure($"NavigateFrameOnce {path}", () =>
                    frame.Navigate(new Uri(path, UriKind.Relative)));
        }

        private void WaitRuntimeFilesReady()
        {
            try
            {
                if (runtimeFilesTask == null)
                    return;
                if (runtimeFilesTask.IsCompleted)
                {
                    runtimeFilesTask.Wait();
                    return;
                }
                StartupProfiler.Measure("WaitRuntimeFilesReady", () => runtimeFilesTask.Wait());
            }
            catch (AggregateException ex)
            {
                Tools.MessageBox.Show(ex.Flatten().InnerException?.Message ?? ex.Message);
            }
        }

        private bool DoInvoke(Action action)
        {
            if (Tools.Global.isMainWindowsClosed)
                return false;
            Dispatcher.Invoke(action);
            return true;
        }

        /// <summary>
        /// 加载快捷发送区数据
        /// </summary>
        private bool quickListSelectorRefreshing = false;

        private void LoadQuickSendList()
        {
            CloseQuickSendItemSettings();
            ExitQuickSendKeyboardNavigation();
            NormalizeQuickSendRows();
            toSendListItems.Clear();
            foreach (var i in Tools.Global.setting.quickSend)
            {
                if (string.IsNullOrWhiteSpace(i.commit))
                    i.commit = TryFindResource("QuickSendButton") as string ?? "?!";
                toSendListItems.Add(i);
            }
            CheckToSendListId();
            RefreshQuickSendPageSelector();
        }

        private void NormalizeQuickSendRows()
        {
            var defaultButtonText = TryFindResource("QuickSendButton") as string ?? "?!";
            var sampleTexts = new HashSet<string>
            {
                "example string",
                "JavaScript可通过接口获取此处数据",
                "aa 01 02 0d 0a",
                "此处数据会被JavaScript处理",
                "右击序号可以更改这一行的位置"
            };
            var sampleButtons = new HashSet<string>
            {
                "右击更改此处文字",
                "Hex数据也能发"
            };

            foreach (var list in Tools.Global.setting.GetAllQuickSendLists())
            {
                if (list == null)
                    continue;

                foreach (var item in list.Where(item => item != null))
                {
                    var hasSampleButton = sampleButtons.Contains(item.commit ?? "");
                    if (sampleTexts.Contains(item.text ?? "") || hasSampleButton)
                    {
                        item.text = "";
                        item.hex = false;
                        item.appendCrlf = true;
                        item.disableSuggestion = false;
                    }

                    if (string.IsNullOrWhiteSpace(item.commit) || hasSampleButton)
                        item.commit = defaultButtonText;
                }
            }
        }

        private void RefreshQuickSendPageSelector()
        {
            if (QuickListSelectComboBox == null)
                return;

            quickListSelectorRefreshing = true;
            try
            {
                QuickListSelectComboBox.ItemsSource = null;
                QuickListSelectComboBox.ItemsSource = GetQuickSendPageSelectorItems();
                QuickListSelectComboBox.SelectedIndex = Global.setting.quickSendSelect;
                if (QuickListNameTextBox != null)
                    QuickListNameTextBox.Text = GetLocalizedQuickSendPageName(
                        Global.setting.GetQuickListNameNow(),
                        Global.setting.quickSendSelect);
                // The dropdown footer is not part of ItemsSource or the page indices.
                QuickListSelectComboBox.Tag = Global.setting.GetQuickSendListCount() > 1;
            }
            finally
            {
                quickListSelectorRefreshing = false;
            }
        }

        private List<string> GetQuickSendPageSelectorItems()
        {
            var names = Global.setting.GetAllQuickListNames();
            var count = Global.setting.GetQuickSendListCount();
            var items = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var name = i < names.Count ? names[i] : "";
                name = GetLocalizedQuickSendPageName(name, i);
                items.Add($"{i + 1}. {name}");
            }
            return items;
        }

        private string GetLocalizedQuickSendPageName(string name, int index)
        {
            var value = name?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value) &&
                !value.Equals($"未命名{index}", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals($"Untitled {index}", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals($"Untitled{index}", StringComparison.OrdinalIgnoreCase))
                return name;

            var format = TryFindResource("QuickSendDefaultPageName") as string ?? "未命名{0}";
            return string.Format(format, index);
        }

        private void RefreshQuickSendDefaultButtonLabels()
        {
            var localizedButtonText = TryFindResource("QuickSendButton") as string ?? "发送";
            var allLists = Tools.Global.setting.GetAllQuickSendLists();
            var changed = false;
            var previousCanSave = canSaveSendList;
            canSaveSendList = false;
            try
            {
                foreach (var list in allLists.Where(list => list != null))
                {
                    foreach (var item in list.Where(item => item != null))
                    {
                        var buttonText = item.commit?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(buttonText) &&
                            !buttonText.Equals("发送", StringComparison.OrdinalIgnoreCase) &&
                            !buttonText.Equals("Send", StringComparison.OrdinalIgnoreCase) &&
                            !buttonText.Equals("?!", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (string.Equals(item.commit, localizedButtonText, StringComparison.Ordinal))
                            continue;

                        item.commit = localizedButtonText;
                        changed = true;
                    }
                }
            }
            finally
            {
                canSaveSendList = previousCanSave;
            }

            if (changed)
                Tools.Global.setting.SetAllQuickSendLists(allLists);
        }

        private void RefreshQuickSendPageSelectorItemsOnly()
        {
            if (QuickListSelectComboBox == null)
                return;

            var selectedIndex = Global.setting.quickSendSelect;
            QuickListSelectComboBox.ItemsSource = null;
            QuickListSelectComboBox.ItemsSource = GetQuickSendPageSelectorItems();
            QuickListSelectComboBox.SelectedIndex = selectedIndex;
        }

        private void Uart_UartDataSent(object sender, EventArgs e)
        {
            Tools.Logger.ShowData(sender as byte[], true, (e as UartSendEventArgs)?.SessionStringLogOverride);
        }

        private void Uart_UartDataRecived(object sender, EventArgs e)
        {
            if (e is UartReceiveEventArgs received && !received.IsCurrent)
                return;
            var data = sender as byte[];
            Tools.Logger.ShowData(data, false, null, GetReceiveScriptContext());
            if (!IsSerialSplitModeActive() ||
                Volatile.Read(ref lastSerialSendTargetSlot) == 1)
                Tools.Global.NotifyActiveSerialTargetReceived(data);
        }

        private void Global_SendRawDataRequest(byte[] data)
        {
            var pendingSend = Dispatcher.Invoke(new Func<Task>(() =>
            {
                SetReceiveScriptContext(recvScriptBackup, "", data);
                return sendUartData(data, true, false);
            }));
            // Background callers retain their synchronous request contract. The UI
            // dispatcher remains available during wake delays and driver writes.
            if (!Dispatcher.CheckAccess())
                pendingSend.GetAwaiter().GetResult();
        }

        private void Global_SendDataRequest(Tools.UartSendRequest request)
        {
            if (request?.Data == null)
                return;

            var pendingSend = Dispatcher.Invoke(new Func<Task>(() =>
            {
                SetReceiveScriptContext(recvScriptBackup, "", request.Data);
                return sendUartData(
                    request.Data,
                    request.IsHex,
                    request.ApplySendProcessing,
                    request.SessionStringLogOverride,
                    sourceText: request.SourceText);
            }));
            if (!Dispatcher.CheckAccess())
                pendingSend.GetAwaiter().GetResult();
        }

        private async Task<bool> Global_SendDataRequestAsync(
            Tools.UartSendRequest request,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request?.Data == null || windowIsClosing || Tools.Global.isMainWindowsClosed)
                return false;

            var pendingSend = Dispatcher.Invoke(new Func<Task>(() =>
            {
                token.ThrowIfCancellationRequested();
                if (windowIsClosing || Tools.Global.isMainWindowsClosed)
                    throw new OperationCanceledException("The serial window is closing.", token);

                SetReceiveScriptContext(recvScriptBackup, "", request.Data);
                // Resolve the target and profile on the UI thread before enqueueing;
                // later selection changes must not redirect this request.
                return sendUartData(
                    request.Data,
                    request.IsHex,
                    request.ApplySendProcessing,
                    request.SessionStringLogOverride,
                    sourceText: request.SourceText,
                    cancellationToken: token,
                    propagateErrors: true,
                    autoOpen: false);
            }));
            await pendingSend.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return true;
        }

        private void Global_MainSendTargetChangedEvent(object sender, EventArgs e)
        {
            var hasMainSendTarget = Tools.Global.HasMainSendTarget;
            var currentDisplayName = Tools.Global.MainSendTargetDisplayName;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (windowIsClosing || Tools.Global.isMainWindowsClosed)
                    return;

                if (!string.IsNullOrWhiteSpace(currentDisplayName) &&
                    !string.Equals(currentDisplayName, lastMainSendTargetDisplayName, StringComparison.Ordinal))
                {
                    AddNotification(
                        DateTime.Now,
                        string.Format(
                            TryFindResource("NotificationConnectedTitleFormat") as string ?? "{0} 已连接",
                            currentDisplayName),
                        TryFindResource("NotificationReadyForSend") as string ?? "已可作为主发送目标。",
                        AppNotificationLevel.Success,
                        AppNotificationCategory.Connection);
                }
                else if (string.IsNullOrWhiteSpace(currentDisplayName) &&
                         !string.IsNullOrWhiteSpace(lastMainSendTargetDisplayName))
                {
                    AddNotification(
                        DateTime.Now,
                        string.Format(
                            TryFindResource("NotificationDisconnectedTitleFormat") as string ?? "{0} 已断开",
                            lastMainSendTargetDisplayName),
                        string.Empty,
                        AppNotificationLevel.Info,
                        AppNotificationCategory.Connection);
                }

                lastMainSendTargetDisplayName = currentDisplayName ?? string.Empty;
                UpdateMainSendTargetUi(hasMainSendTarget);
            }));
        }

        private void UpdateMainSendTargetUi(bool preferNetworkTarget = false)
        {
            RefreshSerialSplitTargetSelector(GetSerialSplitScreenCount(), preferNetworkTarget);

            if (sendDataButton == null)
                return;

            sendDataButton.SetResourceReference(ContentControl.ContentProperty, "SendDataButton");
            sendDataButton.SetResourceReference(FrameworkElement.ToolTipProperty, "SendDataButtonTip");
        }

        private void Global_UartPortClosedEvent(object sender, string portName)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (Tools.Global.isMainWindowsClosed)
                    return;
                if (!forcusClosePort)
                {
                    var displayName = string.IsNullOrWhiteSpace(portName)
                        ? (TryFindResource("SerialPinUnknownPort") as string ?? "串口")
                        : portName;
                    AddNotification(
                        DateTime.Now,
                        string.Format(
                            TryFindResource("NotificationConnectionLostTitleFormat") as string ?? "{0} 连接中断",
                            displayName),
                        string.Empty,
                        AppNotificationLevel.Warning,
                        AppNotificationCategory.Connection);
                }
                if (IsSerialSplitModeRequested())
                {
                    ApplySerialSplitLayout();
                    return;
                }

                Tools.Logger.StopSessionLog();
                serialPortsListComboBox.IsEnabled = true;
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                UpdateMainSerialConnectionStatus();
                refreshPortList(string.IsNullOrWhiteSpace(portName) ? null : portName);
            }));
        }

        private void SetReceiveScriptContext(string scriptName, object parameter, byte[] sendRaw)
        {
            lock (receiveScriptContextLock)
            {
                currentReceiveScriptContext = new ReceiveScriptContext
                {
                    ScriptName = string.IsNullOrWhiteSpace(scriptName) ? recvScriptBackup : scriptName,
                    Parameter = parameter ?? "",
                    SendRaw = sendRaw == null ? new byte[0] : (byte[])sendRaw.Clone()
                };
            }
        }

        private ReceiveScriptContext GetReceiveScriptContext()
        {
            lock (receiveScriptContextLock)
            {
                return new ReceiveScriptContext
                {
                    ScriptName = currentReceiveScriptContext.ScriptName,
                    Parameter = currentReceiveScriptContext.Parameter,
                    SendRaw = currentReceiveScriptContext.SendRaw == null ? new byte[0] : (byte[])currentReceiveScriptContext.SendRaw.Clone()
                };
            }
        }

        private bool applyingUartProfile = false;

        private void SerialPortsListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (syncingSerialSplitControls || refreshLock)
                return;

            if (IsSerialSplitModeActive())
            {
                if (IsAllSerialTargetsSelected())
                {
                    UpdateSelectedSplitSlotControls();
                    return;
                }
                mainSplitPortPage?.SetSlotPortName(GetSelectedSerialSplitSlot(), GetSelectedPortName());
                UpdateSelectedSplitSlotControls();
                return;
            }

            // 已有串口连接时只记录待切换端口，不提前把新端口配置应用到旧连接。
            if (!Tools.Global.uart.IsOpen())
                ApplySelectedUartProfile();
            UpdateMainSerialConnectionStatus();
        }

        private void SerialPortsListComboBox_DropDownOpened(object sender, EventArgs e)
        {
            refreshPortList();
        }

        private bool IsSelectedMainSerialPortOpen()
        {
            var selectedPort = GetSelectedPortName();
            return Tools.Global.uart.IsOpen() &&
                !string.IsNullOrWhiteSpace(selectedPort) &&
                string.Equals(Tools.Global.uart.GetName(), selectedPort, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsMainSerialPortSwitchPending()
        {
            if (!Tools.Global.uart.IsOpen())
                return false;

            var selectedPort = GetSelectedPortName();
            var openPort = Tools.Global.uart.GetName();
            return !string.IsNullOrWhiteSpace(selectedPort) &&
                !string.IsNullOrWhiteSpace(openPort) &&
                !string.Equals(openPort, selectedPort, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateMainSerialConnectionStatus()
        {
            if (statusTextBlock == null || IsSerialSplitModeActive())
                return;

            var selectedPort = GetSelectedPortName();
            var openPort = Tools.Global.uart.IsOpen() ? Tools.Global.uart.GetName() : "";
            var pendingSwitch = IsMainSerialPortSwitchPending();

            if (pendingSwitch)
            {
                statusTextBlock.Text = string.Format(
                    TryFindResource("MainSerialPendingSwitchStatus") as string ?? "{0} 已打开 → 切换到 {1}",
                    openPort,
                    selectedPort);
                connectionStatusButton.ToolTip = string.Format(
                    TryFindResource("ConnectionStatusSwitchTip") as string ?? "{0} 仍在连接；点击后关闭 {0} 并打开 {1}",
                    openPort,
                    selectedPort);
                statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppAccentBrush");
            }
            else if (Tools.Global.uart.IsOpen())
            {
                statusTextBlock.Text = string.Format(
                    TryFindResource("MainSerialOpenStatus") as string ?? "{0} · 已打开",
                    openPort);
                connectionStatusButton.ToolTip = string.Format(
                    TryFindResource("ConnectionStatusCloseTip") as string ?? "点击关闭 {0}",
                    openPort);
                statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppGlassTextBrush");
            }
            else if (!string.IsNullOrWhiteSpace(selectedPort))
            {
                statusTextBlock.Text = string.Format(
                    TryFindResource("MainSerialClosedStatus") as string ?? "{0} · 关闭",
                    selectedPort);
                connectionStatusButton.ToolTip = string.Format(
                    TryFindResource("ConnectionStatusOpenTip") as string ?? "点击打开 {0}",
                    selectedPort);
                statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppGlassTextBrush");
            }
            else
            {
                statusTextBlock.Text = TryFindResource("MainSerialNoPortStatus") as string ?? "未选择串口";
                connectionStatusButton.ToolTip = TryFindResource("ConnectionStatusToggleTip") as string;
                statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "AppGlassTextBrush");
            }

            if (connectionStatusButton != null)
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0 || Tools.Global.uart.IsOpen();
            if (baudRateComboBox != null)
                baudRateComboBox.IsEnabled = !pendingSwitch;
            if (FlowControlButton != null)
                FlowControlButton.IsEnabled = !pendingSwitch;
        }

        private void ShowSerialPortSwitchRequiredBeforeSend()
        {
            var openPort = Tools.Global.uart.GetName();
            var selectedPort = GetSelectedPortName();
            var format = TryFindResource("SerialPortSwitchRequiredBeforeSend") as string
                ?? "{0} 仍在连接，{1} 尚未打开。请先点击“切换到 {1}”，再发送数据。";
            Tools.MessageBox.Show(string.Format(format, openPort, selectedPort));
        }

        private void CloseMainSerialPortForSwitch()
        {
            if (!Tools.Global.uart.IsOpen())
                return;

            var portName = Tools.Global.uart.GetName();
            try
            {
                forcusClosePort = true;
                Tools.Global.uart.Close(waitForDispose: true);
                Tools.Logger.StopSessionLog();
                UpdateMainSerialConnectionStatus();
                AddSerialDisconnectedNotification(portName);
            }
            catch (Exception ex)
            {
                Tools.Logger.AddUartLogDebug($"[CloseMainSerialPortForSwitch]close error:{ex}");
                throw;
            }
            finally
            {
                forcusClosePort = false;
            }
        }

        private void ApplySelectedUartProfile()
        {
            ApplyUartProfileForPort(GetSelectedPortName());
        }

        private void ApplyUartProfileForPort(string portName)
        {
            if (applyingUartProfile || Tools.Global.setting == null || string.IsNullOrWhiteSpace(portName))
                return;

            applyingUartProfile = true;
            try
            {
                Tools.Global.setting.SetActiveUartProfile(portName);
                if (!Tools.Global.uart.IsOpen())
                    Tools.Global.uart.SetName(portName);
                SetBaudRateComboBoxFromSetting();
            }
            finally
            {
                applyingUartProfile = false;
            }
        }

        private string GetSelectedPortName()
        {
            return ExtractPortName(serialPortsListComboBox?.SelectedItem as string ?? serialPortsListComboBox?.Text);
        }

        private static string ExtractPortName(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var match = Regex.Match(text, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.ToUpperInvariant();

            match = Regex.Match(text, @"\bCOM\d+\b", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.ToUpperInvariant() : "";
        }

        private void SetBaudRateComboBoxFromSetting()
        {
            if (baudRateComboBox == null || Tools.Global.setting == null)
                return;

            var text = Tools.Global.setting.baudRate.ToString();
            for (int i = 0; i < baudRateComboBox.Items.Count - 1; i++)
            {
                if ((baudRateComboBox.Items[i] as ComboBoxItem)?.Content?.ToString() == text)
                {
                    lastBaudRateSelectedIndex = i;
                    baudRateComboBox.SelectedIndex = i;
                    return;
                }
            }

            lastBaudRateSelectedIndex = baudRateComboBox.Items.Count - 1;
            baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = text;
            baudRateComboBox.Text = text;
        }

        private bool refreshLock = false;
        private bool skipSearch = false;
        private int searchCount = 0;
        /// <summary>
        /// 刷新设备列表
        /// </summary>
        private void refreshPortList(string lastPort = null)
        {
            StartupProfiler.Mark("refreshPortList enter");
            if (refreshLock)
            {
                StartupProfiler.Mark("refreshPortList skipped by lock");
                return;
            }
            refreshLock = true;
            // 刷新设备列表时保留用户刚选中的“待使用端口”，不能被当前仍打开的旧端口覆盖。
            var pendingSelectedPort = GetSelectedPortName();
            if (IsSerialSplitModeRequested() && mainSplitPortPage != null)
                pendingSelectedPort = mainSplitPortPage.GetSlotPortName(GetSelectedSerialSplitSlot());
            serialPortsListComboBox.Items.Clear();
            List<string> strs = new List<string>();
            searchCount = 0;
            Task.Run(() =>
            {
                StartupProfiler.Mark("refreshPortList worker enter");
                var actualPortNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var serialPortScanSucceeded = false;
                StartupProfiler.Measure("refreshPortList WMI query", () =>
                {
                    while (!skipSearch)
                    //while (true)
                    {
                        try
                        {
                            ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PnPEntity");
                            Regex regExp = new Regex("\\(COM\\d+\\)");
                            foreach (ManagementObject queryObj in searcher.Get())
                            {
                                if ((queryObj["Caption"] != null) && regExp.IsMatch(queryObj["Caption"].ToString()))
                                {
                                    strs.Add(queryObj["Caption"].ToString());
                                }
                            }
                            break;
                        }
                        catch(Exception ex)
                        {
                            if (++searchCount >= 3) {
                                skipSearch = true;
                                Tools.MessageBox.Show(ex.Message);
                            }
                            else Task.Delay(500).Wait();
                        }
                        //MessageBox.Show("fail了");
                    }
                });

                StartupProfiler.Measure("refreshPortList SerialPort.GetPortNames", () =>
                {
                    try
                    {
                        var systemPortNames = SerialPort.GetPortNames();
                        serialPortScanSucceeded = true;
                        foreach (string p in systemPortNames)//加上缺少的com口
                        {
                            //有些人遇到了微软库的bug，所以需要手动从0x00截断
                            var pp = p;
                            if (p.IndexOf("\0") > 0)
                                pp = p.Substring(0, p.IndexOf("\0"));
                            pp = pp.ToUpperInvariant();
                            actualPortNames.Add(pp);
                            bool notMatch = true;
                            foreach (string n in strs)
                            {
                                if (string.Equals(
                                    ExtractPortName(n),
                                    pp,
                                    StringComparison.OrdinalIgnoreCase))//如果和选中项目匹配
                                {
                                    notMatch = false;
                                    break;
                                }
                            }
                            if (notMatch)
                                strs.Add($"Serial Port {pp} ({pp})");//如果列表中没有，就自己加上
                        }
                    }
                    catch{ }
                    finally { /*Tools.MessageBox.Show(String.Join("\n",SerialPort.GetPortNames()));*/ }
                });

                // PnP 列表可能保留已经不存在的虚拟/历史 COM 设备（常见为 COM1）。
                // 只有 SerialPort.GetPortNames 当前也确认存在的端口才能进入下拉框。
                if (serialPortScanSucceeded)
                {
                    strs.RemoveAll(item =>
                    {
                        var portName = ExtractPortName(item);
                        return string.IsNullOrWhiteSpace(portName) || !actualPortNames.Contains(portName);
                    });
                }


                StartupProfiler.Measure("refreshPortList UI update", () => this.Dispatcher.Invoke(new Action(delegate {
                    var preferredPort = !string.IsNullOrWhiteSpace(pendingSelectedPort)
                        ? pendingSelectedPort
                        : (!string.IsNullOrWhiteSpace(lastPort) ? lastPort : Tools.Global.uart.GetName());
                    string selectedItem = null;
                    foreach (string i in strs)
                    {
                        serialPortsListComboBox.Items.Add(i);
                        if (!string.IsNullOrWhiteSpace(preferredPort) && i.Contains($"({preferredPort})"))
                            selectedItem = i;
                    }

                    if (selectedItem == null && strs.Count > 0)
                        selectedItem = strs[0];

                    if (IsSerialSplitModeRequested())
                    {
                        mainSplitPortPage?.RefreshSlotPorts(strs.Select(ExtractPortName).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray());
                        if (selectedItem != null)
                            serialPortsListComboBox.SelectedItem = selectedItem;
                        refreshLock = false;
                        ApplySerialSplitLayout();
                        return;
                    }

                    if (strs.Count >= 1)
                    {
                        serialPortsListComboBox.IsEnabled = true;
                        connectionStatusButton.IsEnabled = true;
                        serialPortsListComboBox.SelectedItem = selectedItem;
                        if (!Tools.Global.uart.IsOpen())
                            ApplyUartProfileForPort(ExtractPortName(selectedItem));
                    }
                    else
                    {
                        // 即使当前没有串口，也允许再次展开下拉框触发扫描。
                        serialPortsListComboBox.IsEnabled = true;
                        connectionStatusButton.IsEnabled = Tools.Global.uart.IsOpen();
                    }
                    refreshLock = false;
                    UpdateMainSerialConnectionStatus();

                    if (ShouldAutoReconnectAfterPortRefresh(
                        preferredPort,
                        ExtractPortName(selectedItem),
                        Tools.Global.uart.IsOpen(),
                        forcusClosePort,
                        Tools.Global.setting.autoReconnect,
                        isOpeningPort))
                    {
                        isOpeningPort = true;
                        Task.Run(() =>
                        {
                            try
                            {
                                Tools.Global.uart.Open();
                                Tools.Logger.StartSessionLog(Tools.Global.uart.GetName());
                                Dispatcher.Invoke(new Action(delegate
                                {
                                    serialPortsListComboBox.IsEnabled = true;
                                    connectionStatusButton.IsEnabled = true;
                                    UpdateMainSerialConnectionStatus();
                                    AddSerialConnectionNotification(Tools.Global.uart.GetName(), reconnected: true);
                                }));
                            }
                            catch (Exception ex)
                            {
                                Tools.Logger.AddUartLogDebug($"[autoReconnect]open error:{ex}");
                                forcusClosePort = true;
                                ShowOpenPortFailed(ex.Message);
                            }
                            finally
                            {
                                isOpeningPort = false;
                            }
                        });
                    }
                })));
                StartupProfiler.Mark($"refreshPortList worker exit, ports={strs.Count}");
            });
        }

        private static bool ShouldAutoReconnectAfterPortRefresh(
            string preferredPort,
            string selectedPort,
            bool serialPortIsOpen,
            bool forceClosePort,
            bool autoReconnect,
            bool openingPort)
        {
            return !serialPortIsOpen &&
                !forceClosePort &&
                autoReconnect &&
                !openingPort &&
                !string.IsNullOrWhiteSpace(preferredPort) &&
                string.Equals(preferredPort, selectedPort, StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshScriptList()
        {
            //刷新文件列表
            DirectoryInfo scriptFileDir = new DirectoryInfo(Tools.Global.ProfilePath + "user_script_run/");
            FileSystemInfo[] scriptFiles = scriptFileDir.GetFileSystemInfos();
            fileLoading = true;
            scriptFileList.Items.Clear();
            for (int i = 0; i < scriptFiles.Length; i++)
            {
                FileInfo file = scriptFiles[i] as FileInfo;
                //是文件
                if (file != null && file.Name.ToLower().EndsWith(".js"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    scriptFileList.Items.Add(name);
                    if (name== Tools.Global.setting.runScript)
                    {
                        scriptFileList.SelectedIndex = scriptFileList.Items.Count - 1;
                    }
                }
            }
            lastScriptFile = Tools.Global.setting.runScript;
            fileLoading = false;
        }

        private static int UsbPluginDeley = 0;
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcLButtonDown = 0x00A1;
            if (msg == WmNcLButtonDown)
                CloseNotificationPopup();

            if (msg == 0x219 && !Tools.Global.uart.IsOpen())// 监听USB设备插拔消息
            {
                if (UsbPluginDeley == 0)
                {
                    ++UsbPluginDeley;   // Task启动需要准备时间,这里提前对公共变量加一
                    Task.Run(() =>
                    {
                        do Task.Delay(100).Wait();
                        while (++UsbPluginDeley < 10);
                        UsbPluginDeley = 0;
                        Dispatcher.Invoke(() =>
                        {
                            UsbDeviceNotifier_OnDeviceNotify();
                        });
                        Logger.AddUartLogInfo($"[USB拔插事件] {DateTime.Now:HH:mm:ss.fff}");
                    });
                }
                else UsbPluginDeley = 1;
                handled = true;
            }
            return IntPtr.Zero;
        }
        private void UsbDeviceNotifier_OnDeviceNotify()
        {
            if (Tools.Global.uart.IsOpen())
            {
                refreshPortList();
                foreach (string c in serialPortsListComboBox.Items)
                {
                    if (c.Contains($"({Tools.Global.uart.GetName()})"))
                    {
                        serialPortsListComboBox.Text = c;
                        break;
                    }
                }
            }
            else
            {
                serialPortsListComboBox.IsEnabled = true;
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                UpdateMainSerialConnectionStatus();
                refreshPortList();
            }
        }

        /// <summary>
        /// 响应其他代码传来的窗口置顶事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void topEvent(object sender, EventArgs e)
        {
            this.Topmost = (bool)sender;
        }

        /// <summary>
        /// 窗口关闭事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            windowIsClosing = true;
            CloseQuickSendItemSettings();
            CancelQuickSendImport();
            Tools.Global.setting.windowLeft = this.Left;
            Tools.Global.setting.windowTop = this.Top;
            Tools.Global.setting.windowWidth = this.Width;
            Tools.Global.setting.windowHeight = this.Height;
            //自动保存脚本
            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);
            Tools.Global.ClearMainSendTarget();
            Tools.Global.ThemeChanged -= Global_ThemeChanged;
            Tools.Global.SerialPinStatusChangedEvent -= Global_SerialPinStatusChangedEvent;
            Tools.Global.AppNotificationEvent -= Global_AppNotificationEvent;
            Tools.QuickSendBackupService.FlushPending();
            Tools.QuickSendBackupService.Shutdown();
            Tools.Global.isMainWindowsClosed = true;
            Tools.GitHubReleaseUpdater.TryStartPendingInstallOnExit();
            foreach (Window win in App.Current.Windows.Cast<Window>().Where(win => win != this).ToList())
            {
                try
                {
                    win.Close();
                }
                catch (Exception ex)
                {
                    Tools.Logger.AddUartLogDebug($"[MainWindowClosing]window close error:{ex.Message}");
                }
            }
            e.Cancel = false;//正常关闭
        }



        private SettingWindow settingPage;
        private void MoreSettingButton_Click(object sender, RoutedEventArgs e)
        {
            if (settingPage == null)
                settingPage = new SettingWindow { Owner = this };

            PositionOwnedWindow(settingPage);
            settingPage.Show();
            settingPage.Activate();
        }

        private FlowControlWindow flowControlPage;
        private void FlowControlButton_Click(object sender, RoutedEventArgs e)
        {
            if (flowControlPage == null)
                flowControlPage = new FlowControlWindow { Owner = this };

            PositionOwnedWindow(flowControlPage);
            flowControlPage.Show();
            flowControlPage.Activate();
        }

        private void MainWindow_PlacementChanged(object sender, EventArgs e)
        {
            CloseQuickSendItemSettings();
            if (WindowState == WindowState.Minimized)
                return;
            CloseNotificationPopup();
            if (settingPage?.IsVisible == true)
                PositionOwnedWindow(settingPage);
            if (flowControlPage?.IsVisible == true)
                PositionOwnedWindow(flowControlPage);
        }

        private void PositionOwnedWindow(Window window)
        {
            if (window == null || WindowState == WindowState.Minimized)
                return;

            window.Owner = this;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            var workArea = SystemParameters.WorkArea;
            var left = Left + Math.Max(0, (ActualWidth - width) / 2d);
            var top = Top + Math.Max(0, (ActualHeight - height) / 2d);
            window.Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - width));
            window.Top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - height));
        }

        private void ApiDocumentButton_Click(object sender, RoutedEventArgs e)
        {
            var localDoc = System.IO.Path.Combine(Tools.Global.AppPath, Tools.Global.apiDocumentUrl);
            System.Diagnostics.Process.Start(File.Exists(localDoc) ? localDoc : Tools.Global.apiDocumentUrl);
        }

        private void OpenScriptFolderButton_Click(object sender, RoutedEventArgs e)
        {
            WaitRuntimeFilesReady();
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", Tools.Global.GetTrueProfilePath() + "user_script_run");
            }
            catch
            {
                Tools.MessageBox.Show($"尝试打开文件夹失败，请自行打开该路径：{Tools.Global.GetTrueProfilePath()}user_script_run");
            }
        }

        private void RefreshScriptListButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshScriptList();
        }

        private byte[] toSendData = null;//待发送的数据
        private bool? toSendDataIsHex = null;
        private bool toSendDataApplySendProcessing = true;
        private string toSendDataSessionStringLogOverride = null;
        private bool? toSendDataExtraEnterOverride = null;
        private string toSendDataSourceText = null;

        private void ShowOpenPortFailed(string detail = null)
        {
            if (Tools.Global.isMainWindowsClosed)
                return;

            var message = TryFindResource("ErrorOpenPort") as string ?? "串口打开失败！";
            if (!string.IsNullOrWhiteSpace(detail))
                message += "\r\n" + detail;

            Action show = () =>
            {
                serialPortsListComboBox.IsEnabled = true;
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                UpdateMainSerialConnectionStatus();
                Tools.MessageBox.Show(message);
            };

            if (Dispatcher.CheckAccess())
                show();
            else
                Dispatcher.BeginInvoke(show);
        }

        private void ClearPendingSendAfterOpenFailure()
        {
            toSendData = null;
            toSendDataIsHex = null;
            toSendDataApplySendProcessing = true;
            toSendDataSessionStringLogOverride = null;
            toSendDataExtraEnterOverride = null;
            toSendDataSourceText = null;
        }

        private void SendPendingDataAfterOpen()
        {
            if (toSendData == null)
                return;

            var data = toSendData;
            var isHex = toSendDataIsHex;
            var applySendProcessing = toSendDataApplySendProcessing;
            var sessionStringLogOverride = toSendDataSessionStringLogOverride;
            var extraEnterOverride = toSendDataExtraEnterOverride;
            var sourceText = toSendDataSourceText;
            ClearPendingSendAfterOpenFailure();
            sendUartData(
                data,
                isHex,
                applySendProcessing,
                sessionStringLogOverride,
                extraEnterOverride,
                sourceText);
        }

        private void openPort()
        {
            Tools.Logger.AddUartLogDebug($"[openPort]{isOpeningPort},{serialPortsListComboBox.SelectedItem}");
            if (IsSerialSplitModeRequested())
            {
                Tools.Logger.AddUartLogDebug("[openPort]skip split mode");
                return;
            }
            if (isOpeningPort)
            {
                Tools.Logger.AddUartLogDebug("[openPort]skip opening");
                return;
            }
            var selectedPort = GetSelectedPortName();
            if (string.IsNullOrWhiteSpace(selectedPort))
            {
                Tools.Logger.AddUartLogDebug("[openPort]no selected port");
                ShowOpenPortFailed("未选择串口。");
                return;
            }

            string[] ports;//获取所有串口列表
            try
            {
                Tools.Logger.AddUartLogDebug($"[openPort]GetPortNames");
                ports = SerialPort.GetPortNames();
                Tools.Logger.AddUartLogDebug($"[openPort]GetPortNames{ports.Length}");
            }
            catch(Exception e)
            {
                isOpeningPort = false;
                Tools.Logger.AddUartLogDebug($"[openPort]GetPortNames Exception:{e}");
                ShowOpenPortFailed(e.Message);
                return;
            }

            string port = "";//最终串口名
            foreach (string p in ports)//循环查找符合名称串口
            {
                //有些人遇到了微软库的bug，所以需要手动从0x00截断
                var pp = p;
                if (p.IndexOf("\0") > 0)
                    pp = p.Substring(0, p.IndexOf("\0"));
                if (string.Equals(pp, selectedPort, StringComparison.OrdinalIgnoreCase))
                {
                    port = pp;
                    break;
                }
            }
            Tools.Logger.AddUartLogDebug($"[openPort]PortName:{port},isOpeningPort:{isOpeningPort}");
            if (port == "")
            {
                ShowOpenPortFailed("当前选择的串口不在系统串口列表中，请刷新串口后重试。");
                return;
            }

            if (IsSelectedMainSerialPortOpen())
            {
                UpdateMainSerialConnectionStatus();
                return;
            }

            isOpeningPort = true;
            if (Tools.Global.uart.IsOpen())
            {
                try
                {
                    CloseMainSerialPortForSwitch();
                }
                catch (Exception ex)
                {
                    isOpeningPort = false;
                    ShowOpenPortFailed(ex.Message);
                    return;
                }
            }

            // 目标端口已经确认存在，旧端口也已关闭，现在才加载目标端口配置。
            ApplySelectedUartProfile();

            Task.Run(() =>
            {
                var portOpened = false;
                try
                {
                    forcusClosePort = false;//不再强制关闭串口
                    Tools.Logger.AddUartLogDebug($"[openPort]SetName");
                    Tools.Global.uart.SetName(port);
                    Tools.Logger.AddUartLogDebug($"[openPort]open");
                    Tools.Global.uart.Open();
                    portOpened = true;
                    Tools.Logger.StartSessionLog(port);
                    Tools.Logger.AddUartLogDebug($"[openPort]change show and send pending data");
                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        serialPortsListComboBox.IsEnabled = true;
                        connectionStatusButton.IsEnabled = true;
                        UpdateMainSerialConnectionStatus();
                        AddSerialConnectionNotification(port, reconnected: false);
                        // sendUartData 会读取当前端口下拉框和其它 WPF 状态，必须
                        // 在 UI 线程执行，不能从串口打开的后台线程直接调用。
                        SendPendingDataAfterOpen();
                    }));
                    Tools.Logger.AddUartLogDebug($"[openPort]done");
                }
                catch(Exception e)
                {
                    if (!portOpened)
                    {
                        Tools.Logger.AddUartLogDebug($"[openPort]open error:{e}");
                        forcusClosePort = true;
                        ClearPendingSendAfterOpenFailure();
                        ShowOpenPortFailed(e.Message);
                    }
                    else
                    {
                        Tools.Logger.AddUartLogDebug($"[openPort]post-open error:{e}");
                        Dispatcher.BeginInvoke(new Action(() =>
                            Tools.MessageBox.Show(
                                $"{TryFindResource("ErrorSendFail") as string ?? "发送失败"}\r\n{e.Message}")));
                    }
                }
                finally
                {
                    isOpeningPort = false;
                    Tools.Logger.AddUartLogDebug($"[openPort]all done");
                }
            });
        }
        private void ConnectionStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsSerialSplitModeRequested())
            {
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();
                if (IsAllSerialTargetsSelected())
                {
                    UpdateSelectedSplitSlotControls();
                    return;
                }
                var slot = GetSelectedSerialSplitSlot();
                var wasOpen = mainSplitPortPage?.IsSlotSelectedPortOpen(slot) == true;
                var isOpen = mainSplitPortPage?.ToggleSlotOpen(slot) == true;
                UpdateSelectedSplitSlotControls();
                if (!wasOpen && !isOpen)
                {
                    var detail = mainSplitPortPage?.GetSlotLastError(slot);
                    var message = TryFindResource("ErrorOpenPort") as string ?? "串口打开失败！";
                    if (!string.IsNullOrWhiteSpace(detail))
                        message += "\r\n" + detail;
                    Tools.MessageBox.Show(message);
                }
                return;
            }
            Tools.Logger.AddUartLogDebug($"[ConnectionStatusButton]now:{Tools.Global.uart.IsOpen()}");
            if (!IsSelectedMainSerialPortOpen())//打开当前选择的串口逻辑
            {
                openPort();
            }
            else//关闭串口逻辑
            {
                string lastPort = null;//记录一下上次的串口号
                var closed = false;
                try
                {
                    Tools.Logger.AddUartLogDebug($"[ConnectionStatusButton]close");
                    forcusClosePort = true;//不再重新开启串口
                    lastPort = Tools.Global.uart.GetName();//串口号
                    Tools.Global.uart.Close(waitForDispose: true);
                    Tools.Logger.StopSessionLog();
                    Tools.Logger.AddUartLogDebug($"[ConnectionStatusButton]close done");
                    closed = true;
                }
                catch (Exception ex)
                {
                    //串口关闭失败！
                    Tools.Logger.AddUartLogDebug($"[ConnectionStatusButton]close error:{ex}");
                    Tools.MessageBox.Show($"{TryFindResource("ErrorClosePort") as string ?? "?!"}\r\n{ex.Message}");
                }
                Tools.Logger.AddUartLogDebug($"[ConnectionStatusButton]change show");
                serialPortsListComboBox.IsEnabled = true;
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                if (closed)
                {
                    UpdateMainSerialConnectionStatus();
                    AddSerialDisconnectedNotification(lastPort);
                }
                else
                    UpdateMainSerialConnectionStatus();
                Tools.Logger.AddUartLogDebug($"[ConnectionStatusButton]change show done");
                if (closed)
                    refreshPortList(lastPort);
            }

        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsSerialSplitModeActive())
            {
                if (IsAllSerialTargetsSelected())
                    mainSplitPortPage?.ClearAllLogs();
                else
                    mainSplitPortPage?.ClearSlotLog(GetSelectedSerialSplitSlot());
            }
            else
                Tools.Logger.ClearData();
        }

        private void SendAndLogOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsSerialSplitModeActive())
            {
                mainSplitPortPage?.ToggleExternalOptions(SendAndLogOptionsButton);
                return;
            }

            if (dataShowFrame?.Content is Pages.DataShowPage dataShowPage)
                dataShowPage.ToggleOptions(SendAndLogOptionsButton);
        }

        private int lastBaudRateSelectedIndex = -1;
        private void BaudRateComboBox_Changed(object sender, EventArgs e)
        {
            if (syncingSerialSplitControls)
                return;
            if (IsAllSerialTargetsSelected())
                return;

            //选的没变
            if(lastBaudRateSelectedIndex == baudRateComboBox.SelectedIndex)
                return;

            if (baudRateComboBox.SelectedItem != null)
            {
                var splitMode = IsSerialSplitModeActive();
                var selectedSlot = splitMode ? GetSelectedSerialSplitSlot() : 0;
                var previousBaudRate = splitMode
                    ? mainSplitPortPage?.GetSlotBaudRate(selectedSlot) ?? 0
                    : Tools.Global.setting?.baudRate ?? 0;
                var selectedPortWasOpen = splitMode
                    ? mainSplitPortPage?.IsSlotSelectedPortOpen(selectedSlot) == true
                    : IsSelectedMainSerialPortOpen();
                var selectedPortName = splitMode
                    ? mainSplitPortPage?.GetSlotPortName(selectedSlot)
                    : Tools.Global.uart.GetName();

                lastBaudRateSelectedIndex = baudRateComboBox.SelectedIndex;
                if (baudRateComboBox.SelectedIndex == baudRateComboBox.Items.Count - 1)
                {
                    int br = 0;
                    Tuple<bool, string> ret = Tools.InputDialog.OpenDialog(TryFindResource("ShowBaudRate") as string ?? "?!",
                        "115200", TryFindResource("OtherRate") as string ?? "?!");
                    if (!ret.Item1 || !int.TryParse(ret.Item2,out br) || br <= 0)//啥都没选
                    {
                        Tools.MessageBox.Show(TryFindResource("OtherRateFail") as string ?? "?!");
                        return;
                    }
                    if (IsSerialSplitModeActive())
                    {
                        mainSplitPortPage?.SetSlotBaudRate(GetSelectedSerialSplitSlot(), br);
                        baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = br.ToString();
                        baudRateComboBox.Text = br.ToString();
                        NotifyBaudRateChangedIfNeeded(
                            selectedPortName,
                            selectedPortWasOpen,
                            previousBaudRate,
                            mainSplitPortPage?.GetSlotBaudRate(selectedSlot) ?? br);
                        return;
                    }
                    Tools.Global.setting.baudRate = br;
                    NotifyBaudRateChangedIfNeeded(
                        selectedPortName,
                        selectedPortWasOpen,
                        previousBaudRate,
                        Tools.Global.setting.baudRate);
                    Task.Run(() =>
                    {
                        this.Dispatcher.Invoke(new Action(delegate {
                            var text = Tools.Global.setting.baudRate.ToString();
                            baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = text;
                            baudRateComboBox.Text = text;
                        }));
                    });
                }
                else
                {
                    if (IsSerialSplitModeActive())
                    {
                        mainSplitPortPage?.SetSlotBaudRate(
                            GetSelectedSerialSplitSlot(),
                            int.Parse((baudRateComboBox.SelectedItem as ComboBoxItem).Content.ToString()));
                        baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = TryFindResource("OtherRate") as string ?? "?!";
                        NotifyBaudRateChangedIfNeeded(
                            selectedPortName,
                            selectedPortWasOpen,
                            previousBaudRate,
                            mainSplitPortPage?.GetSlotBaudRate(selectedSlot) ?? previousBaudRate);
                        return;
                    }
                    Tools.Global.setting.baudRate =
                        int.Parse((baudRateComboBox.SelectedItem as ComboBoxItem).Content.ToString());
                    baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = TryFindResource("OtherRate") as string ?? "?!";
                    NotifyBaudRateChangedIfNeeded(
                        selectedPortName,
                        selectedPortWasOpen,
                        previousBaudRate,
                        Tools.Global.setting.baudRate);
                }
            }
        }

        private void NotifyBaudRateChangedIfNeeded(
            string portName,
            bool portWasOpen,
            int previousBaudRate,
            int currentBaudRate)
        {
            if (!ShouldNotifyBaudRateChange(portWasOpen, previousBaudRate, currentBaudRate))
                return;

            var displayName = string.IsNullOrWhiteSpace(portName)
                ? (TryFindResource("SerialPinUnknownPort") as string ?? "串口")
                : portName;
            var title = string.Format(
                TryFindResource("NotificationBaudChangedTitleFormat") as string ??
                    "{0} 波特率已切换",
                displayName);
            var message = string.Format(
                TryFindResource("NotificationBaudChangedMessageFormat") as string ??
                    "{0} → {1} baud，后续发送使用新波特率。",
                previousBaudRate,
                currentBaudRate);
            Tools.Global.PublishNotification(
                title,
                message,
                AppNotificationLevel.Info,
                category: AppNotificationCategory.Connection,
                portName: portName);
            Tools.Logger.AddUartLogDebug(
                $"[BaudRateChanged]{displayName} {previousBaudRate}->{currentBaudRate}");
        }

        private static bool ShouldNotifyBaudRateChange(
            bool portWasOpen,
            int previousBaudRate,
            int currentBaudRate)
        {
            return portWasOpen &&
                previousBaudRate > 0 &&
                currentBaudRate > 0 &&
                previousBaudRate != currentBaudRate;
        }

        /// <summary>
        /// 发串口数据
        /// </summary>
        /// <param name="data"></param>
        private Task sendUartData(
            byte[] data,
            bool? is_hex = null,
            bool applySendProcessing = true,
            string sessionStringLogOverride = null,
            bool? extraEnterOverride = null,
            string sourceText = null,
            CancellationToken cancellationToken = default(CancellationToken),
            bool propagateErrors = false,
            bool autoOpen = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data == null)
                return Task.CompletedTask;

            if (IsSerialSplitModeActive())
            {
                if (IsAllSerialTargetsSelected())
                {
                    return SendToAllSplitSlotsAsync(
                        data,
                        is_hex,
                        applySendProcessing,
                        extraEnterOverride,
                        sourceText,
                        cancellationToken,
                        propagateErrors,
                        autoOpen);
                }

                var targetSlot = GetSelectedSerialSplitSlot();
                var targetProfile = mainSplitPortPage?.GetSlotProfileSnapshot(targetSlot);
                var targetHexMode = targetProfile?.hexSend ?? Tools.Global.setting.hexSend;
                var splitData = PrepareUartSendData(
                    data,
                    is_hex,
                    applySendProcessing,
                    targetHexMode,
                    extraEnterOverride,
                    targetProfile,
                    sourceText,
                    propagateErrors);
                if (splitData == null || splitData.Length == 0)
                    return Task.CompletedTask;

                return SendToSelectedSplitSlotAsync(
                    splitData, autoOpen, cancellationToken, propagateErrors, targetSlot);
            }

            if (IsMainSerialPortSwitchPending())
            {
                if (propagateErrors)
                    throw new InvalidOperationException("The selected serial port has changed; reopen it before sending.");
                ShowSerialPortSwitchRequiredBeforeSend();
                return Task.CompletedTask;
            }

            if (!IsSelectedMainSerialPortOpen())
            {
                if (!autoOpen)
                    throw new InvalidOperationException(TryFindResource("CircularSendPortNotOpen") as string ?? "请先打开串口");
                toSendData = (byte[])data.Clone();//带发送数据缓存起来，连上串口后发出去
                toSendDataIsHex = is_hex;
                toSendDataApplySendProcessing = applySendProcessing;
                toSendDataSessionStringLogOverride = sessionStringLogOverride;
                toSendDataExtraEnterOverride = extraEnterOverride;
                toSendDataSourceText = sourceText;
                openPort();
                return Task.CompletedTask;
            }

            if (Tools.Global.uart.IsOpen())
            {
                var connection = Tools.Global.uart.CaptureConnectionLease();
                byte[] dataConvert = PrepareUartSendData(
                    data,
                    is_hex,
                    applySendProcessing,
                    null,
                    extraEnterOverride,
                    connection.Profile,
                    sourceText,
                    propagateErrors);
                if (dataConvert == null)
                    return Task.CompletedTask;

                if (dataConvert.Length == 0)
                    return Task.CompletedTask;

                return SendMainSerialAsync(
                    connection, dataConvert, applySendProcessing ? data : null, sessionStringLogOverride,
                    cancellationToken: cancellationToken, propagateErrors: propagateErrors);
            }
            if (propagateErrors)
                throw new InvalidOperationException(TryFindResource("CircularSendPortNotOpen") as string ?? "请先打开串口");
            return Task.CompletedTask;
        }

        private async Task SendMainSerialAsync(
            Uart.ConnectionLease connection,
            byte[] data,
            byte[] rawData = null,
            string sessionStringLogOverride = null,
            bool showErrors = true,
            CancellationToken cancellationToken = default(CancellationToken),
            bool propagateErrors = false)
        {
            try
            {
                if (!await connection.SendAsync(
                    data, cancellationToken, null, true, rawData, sessionStringLogOverride))
                {
                    throw new IOException("The selected serial connection closed before the queued send.");
                }
            }
            catch (Exception ex)
            {
                if (propagateErrors || cancellationToken.IsCancellationRequested)
                    throw;
                if (showErrors && !windowIsClosing && !Tools.Global.isMainWindowsClosed)
                {
                    Tools.MessageBox.Show(
                        $"{TryFindResource("ErrorSendFail") as string ?? "发送失败"}\r\n{connection.DisplayName}: {ex.Message}");
                }
            }
        }

        private byte[] PrepareUartSendData(
            byte[] data,
            bool? isHex,
            bool applySendProcessing,
            bool? defaultHexSend = null,
            bool? extraEnterOverride = null,
            UartPortProfile profile = null,
            string sourceText = null,
            bool propagateErrors = false)
        {
            byte[] dataConvert = data;
            if (!applySendProcessing)
                return dataConvert;

            try
            {
                WaitRuntimeFilesReady();
                var targetHexMode = defaultHexSend ?? profile?.hexSend ?? Tools.Global.setting.hexSend;
                if (sourceText != null)
                {
                    dataConvert = (isHex ?? targetHexMode)
                        ? Tools.Global.Hex2Byte(sourceText)
                        : Tools.Global.GetEncoding(profile?.encoding ?? Tools.Global.setting.encoding)
                            .GetBytes(sourceText);
                }
                else if (isHex == null && targetHexMode)
                {
                    dataConvert = Tools.Global.Hex2Byte(Tools.Global.Byte2String(data));
                }

                var requestedScript = profile?.sendScript ?? Tools.Global.setting.sendScript;
                if (!Tools.Global.TryGetProfileScriptPath(
                        "user_script_send_convert",
                        requestedScript,
                        out var scriptName,
                        out var scriptPath) ||
                    !File.Exists(scriptPath))
                {
                    scriptName = "default";
                }
                dataConvert = ScriptEnv.JavaScriptLoader.Run(
                    $"{scriptName}.js",
                    new System.Collections.ArrayList
                    {
                        "uartData",
                        dataConvert
                    });
            }
            catch (Exception ex)
            {
                if (propagateErrors)
                    throw;
                Tools.MessageBox.Show($"{TryFindResource("ErrorScript") as string ?? "?!"}\r\n" + ex.ToString());
                return null;
            }

            if (dataConvert == null)
                return null;

            return AppendCrlf(
                dataConvert,
                extraEnterOverride ?? profile?.extraEnter ?? Tools.Global.setting.extraEnter);
        }

        private async Task SendToAllSplitSlotsAsync(
            byte[] sourceData,
            bool? isHex,
            bool applySendProcessing,
            bool? extraEnterOverride,
            string sourceText,
            CancellationToken cancellationToken = default(CancellationToken),
            bool propagateErrors = false,
            bool autoOpen = true)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();
                var page = mainSplitPortPage;
                if (page == null)
                {
                    if (propagateErrors)
                        throw new InvalidOperationException("The serial split view is unavailable.");
                    return;
                }

                var preparedTargets = new List<Tuple<int, byte[]>>();
                var failures = new List<string>();
                for (var slot = 1; slot <= page.SlotCount; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var targetProfile = page.GetSlotProfileSnapshot(slot);
                    var targetHexMode = targetProfile?.hexSend ?? page.IsSlotHexMode(slot);
                    var data = PrepareUartSendData(
                        sourceData,
                        isHex,
                        applySendProcessing,
                        targetHexMode,
                        extraEnterOverride,
                        targetProfile,
                        sourceText,
                        propagateErrors);
                    if (data == null || data.Length == 0)
                        continue;

                    if (!(autoOpen ? page.EnsureSlotOpen(slot) : page.IsSlotSelectedPortOpen(slot)))
                    {
                        failures.Add(FormatSplitTargetFailure(slot, page.GetSlotLastError(slot)));
                        continue;
                    }

                    preparedTargets.Add(Tuple.Create(slot, data));
                }

                var pendingSends = preparedTargets
                    .Select(target => Tuple.Create(
                        target.Item1,
                        page.SendBytesAsync(target.Item1, target.Item2, cancellationToken)))
                    .ToList();
                if (pendingSends.Count > 0)
                {
                    var results = await Task.WhenAll(pendingSends.Select(item => item.Item2));
                    for (var i = 0; i < results.Length; i++)
                    {
                        if (!results[i])
                            failures.Add(FormatSplitTargetFailure(
                                pendingSends[i].Item1,
                                TryFindResource("ErrorSendFail") as string ?? "发送失败"));
                    }
                }

                UpdateSelectedSplitSlotControls();
                if (propagateErrors && failures.Count > 0)
                    throw new IOException(string.Join("\r\n", failures));
                ShowSplitBroadcastFailures(failures);
            }
            catch (Exception ex)
            {
                if (propagateErrors || cancellationToken.IsCancellationRequested)
                    throw;
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFail") as string ?? "?!"}\r\n" + ex);
            }
        }

        private string FormatSplitTargetFailure(int slot, string detail)
        {
            var target = string.Format(
                TryFindResource("SplitSendTargetItem") as string ?? "窗口 {0}",
                slot);
            return string.IsNullOrWhiteSpace(detail) ? target : target + "：" + detail;
        }

        private void ShowSplitBroadcastFailures(IReadOnlyCollection<string> failures)
        {
            if (failures == null || failures.Count == 0)
                return;

            var title = TryFindResource("SplitSendAllOpenFailed") as string
                ?? "以下窗口打开失败，其他窗口已继续发送：";
            Tools.MessageBox.Show(title + "\r\n" + string.Join("\r\n", failures));
        }

        private async Task SendToSelectedSplitSlotAsync(
            byte[] data,
            bool autoOpen = true,
            CancellationToken cancellationToken = default(CancellationToken),
            bool propagateErrors = false,
            int? capturedSlot = null)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();

                var page = mainSplitPortPage;
                if (page == null)
                {
                    if (propagateErrors)
                        throw new InvalidOperationException("The serial split view is unavailable.");
                    return;
                }

                var slot = capturedSlot ?? GetSelectedSerialSplitSlot();
                if (autoOpen)
                {
                    if (!page.EnsureSlotOpen(slot))
                    {
                        UpdateSelectedSplitSlotControls();
                        var detail = page.GetSlotLastError(slot);
                        var message = TryFindResource("ErrorOpenPort") as string ?? "串口打开失败！";
                        if (!string.IsNullOrWhiteSpace(detail))
                            message += "\r\n" + detail;
                        if (propagateErrors)
                            throw new IOException(message);
                        Tools.MessageBox.Show(message);
                        return;
                    }
                    UpdateSelectedSplitSlotControls();
                }
                else if (!page.IsSlotSelectedPortOpen(slot))
                {
                    UpdateSelectedSplitSlotControls();
                    if (propagateErrors)
                        throw new InvalidOperationException(TryFindResource("CircularSendPortNotOpen") as string ?? "请先打开串口");
                    return;
                }

                if (!await page.SendBytesAsync(slot, data, cancellationToken) && propagateErrors)
                    throw new IOException(TryFindResource("ErrorSendFail") as string ?? "发送失败");
            }
            catch (Exception ex)
            {
                if (propagateErrors || cancellationToken.IsCancellationRequested)
                    throw;
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFail") as string ?? "?!"}\r\n" + ex.ToString());
            }
        }

        private async Task SendPreparedDataToSplitTargetsAsync(
            byte[] data,
            bool autoOpen)
        {
            if (!IsAllSerialTargetsSelected())
            {
                await SendToSelectedSplitSlotAsync(data, autoOpen: autoOpen);
                return;
            }

            if (mainSplitPortPage == null)
                ApplySerialSplitLayout();
            var page = mainSplitPortPage;
            if (page == null)
                return;

            var readySlots = new List<int>();
            for (var slot = 1; slot <= page.SlotCount; slot++)
            {
                if (autoOpen)
                {
                    if (!page.EnsureSlotOpen(slot))
                        continue;
                }
                else if (!page.IsSlotSelectedPortOpen(slot))
                {
                    continue;
                }

                readySlots.Add(slot);
            }

            var sends = readySlots
                .Select(slot => page.SendBytesAsync(slot, data))
                .ToList();
            if (sends.Count > 0)
                await Task.WhenAll(sends);
        }

        private void SendUartData_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            SendCurrentTextBoxData();
        }

        private void ToSendDataTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsCtrlKeyDown() && e.Key == Key.A)
            {
                toSendDataTextBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (sendSuggestPopup.IsOpen && HandleSendSuggestionKey(e))
                return;

            if ((e.Key != Key.Return && e.Key != Key.Enter) || !Tools.Global.setting.enterSend)
                return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                return;

            e.Handled = true;
            SendCurrentTextBoxData();
        }

        private void SendCurrentTextBoxData()
        {
            if (IsMainSendTargetSelected())
            {
                var targetData = PrepareMainSendTargetData(toSendDataTextBox.Text, Tools.Global.setting.hexSend);
                if (targetData.Length > 0)
                    Tools.Global.SendToMainSendTarget(targetData);
                return;
            }

            var sourceText = toSendDataTextBox.Text ?? string.Empty;
            var data = Global.GetEncoding().GetBytes(sourceText);
            var receiveScript = recvScriptBackup;
            if (IsSerialSplitModeActive() && mainSplitPortPage != null && !IsAllSerialTargetsSelected())
            {
                receiveScript = mainSplitPortPage
                    .GetSlotProfileSnapshot(GetSelectedSerialSplitSlot())
                    ?.recvScript ?? receiveScript;
            }
            SetReceiveScriptContext(receiveScript, "", data);
            sendUartData(
                data,
                null,
                true,
                Tools.Global.setting.hexSend ? sourceText : null,
                null,
                sourceText);
        }

        private byte[] PrepareMainSendTargetData(string text, bool isHex)
        {
            var data = isHex
                ? Global.Hex2Byte(text ?? string.Empty)
                : Global.GetEncoding().GetBytes(text ?? string.Empty);

            return AppendCrlf(data, Tools.Global.setting.extraEnter);
        }

        private static byte[] AppendCrlf(byte[] data, bool append)
        {
            data = data ?? new byte[0];
            if (!append)
                return data;

            var temp = data.ToList();
            temp.Add(0x0d);
            temp.Add(0x0a);
            return temp.ToArray();
        }

        private bool HandleSendSuggestionKey(KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                SelectSendSuggestion(sendSuggestListBox.SelectedIndex + 1);
                e.Handled = true;
                return true;
            }
            if (e.Key == Key.Up)
            {
                SelectSendSuggestion(sendSuggestListBox.SelectedIndex - 1);
                e.Handled = true;
                return true;
            }
            if (e.Key == Key.Return || e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = ApplySelectedSendSuggestion();
                return e.Handled;
            }
            if (e.Key == Key.Escape)
            {
                sendSuggestPopup.IsOpen = false;
                e.Handled = true;
                return true;
            }
            return false;
        }

        private void ToSendDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!applyingSendSuggestion)
                UpdateSendSuggestions();
        }

        private void ToSendDataTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (!sendSuggestListBox.IsKeyboardFocusWithin)
                    sendSuggestPopup.IsOpen = false;
            }));
        }

        private void SendSuggestListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ApplySelectedSendSuggestion())
                e.Handled = true;
        }

        private void UpdateSendSuggestions()
        {
            if (!toSendDataTextBox.IsKeyboardFocusWithin)
            {
                sendSuggestPopup.IsOpen = false;
                return;
            }

            int lineStart;
            int prefixLength;
            var prefix = GetCurrentSendLinePrefix(out lineStart, out prefixLength);
            var matchPrefix = prefix.TrimStart();
            if (matchPrefix.Length == 0)
            {
                sendSuggestPopup.IsOpen = false;
                return;
            }

            var items = GetQuickSendSuggestions()
                .Select(i => new
                {
                    Item = i,
                    Rank = GetSendSuggestionRank(i.SendText, matchPrefix)
                })
                .Where(i => i.Rank >= 0)
                .OrderBy(i => i.Rank)
                .ThenBy(i => i.Item.SendText.Length)
                .Select(i => i.Item)
                .Take(12)
                .ToList();

            if (items.Count == 0)
            {
                sendSuggestPopup.IsOpen = false;
                return;
            }

            sendSuggestListBox.ItemsSource = items;
            sendSuggestListBox.Width = toSendDataTextBox.ActualWidth;
            sendSuggestPopup.IsOpen = true;
            SelectSendSuggestion(0);
        }

        private void SelectSendSuggestion(int index)
        {
            if (sendSuggestListBox.Items.Count == 0)
                return;

            sendSuggestListBox.SelectedIndex = Math.Max(0, Math.Min(index, sendSuggestListBox.Items.Count - 1));
            ScrollSelectedSendSuggestionIntoView();
        }

        private void ScrollSelectedSendSuggestionIntoView()
        {
            var selected = sendSuggestListBox.SelectedItem;
            if (selected == null)
                return;

            sendSuggestListBox.ScrollIntoView(selected);
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (sendSuggestListBox.SelectedItem != null)
                    sendSuggestListBox.ScrollIntoView(sendSuggestListBox.SelectedItem);
            }));
        }

        private IEnumerable<SendSuggestionItem> GetQuickSendSuggestions()
        {
            var allItems = new List<ToSendData>();
            allItems.AddRange(toSendListItems);
            if (Tools.Global.setting.quickSendList != null)
            {
                foreach (var list in Tools.Global.setting.quickSendList)
                {
                    if (list != null)
                        allItems.AddRange(list);
                }
            }

            var defaultButtonText = TryFindResource("QuickSendButton") as string ?? "发送";
            return allItems
                .Where(i => i != null && !i.hex && !i.disableSuggestion && !string.IsNullOrWhiteSpace(i.text))
                .Select(i => new
                {
                    SendText = i.text.Trim(),
                    ButtonText = GetQuickSendSuggestionButtonText(i, defaultButtonText)
                })
                .GroupBy(i => i.SendText, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var buttonText = g.Select(i => i.ButtonText)
                        .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
                    return new SendSuggestionItem
                    {
                        SendText = g.Key,
                        ButtonText = buttonText ?? string.Empty
                    };
                });
        }

        private static int GetSendSuggestionRank(string suggestion, string prefix)
        {
            if (string.IsNullOrWhiteSpace(suggestion) || string.IsNullOrWhiteSpace(prefix))
                return -1;
            if (suggestion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (prefix.Length < 2)
                return -1;
            return suggestion.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : -1;
        }

        private static string GetQuickSendSuggestionButtonText(ToSendData item, string defaultButtonText)
        {
            var buttonText = item.commit?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(buttonText))
                return string.Empty;
            if (buttonText.Equals(defaultButtonText, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (buttonText.Equals("发送", StringComparison.OrdinalIgnoreCase) ||
                buttonText.Equals("Send", StringComparison.OrdinalIgnoreCase) ||
                buttonText.Equals("?!", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (buttonText.Equals(item.text?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return buttonText;
        }

        private string GetCurrentSendLinePrefix(out int lineStart, out int prefixLength)
        {
            var text = toSendDataTextBox.Text ?? "";
            var caret = Math.Min(toSendDataTextBox.CaretIndex, text.Length);
            if (caret <= 0)
            {
                lineStart = 0;
                prefixLength = 0;
                return "";
            }

            var searchStart = caret - 1;
            lineStart = Math.Max(text.LastIndexOf('\n', searchStart), text.LastIndexOf('\r', searchStart)) + 1;
            prefixLength = caret - lineStart;
            return prefixLength <= 0 ? "" : text.Substring(lineStart, prefixLength);
        }

        private bool ApplySelectedSendSuggestion()
        {
            var selected = sendSuggestListBox.SelectedItem as SendSuggestionItem;
            var selectedText = selected?.SendText;
            if (string.IsNullOrEmpty(selectedText))
                return false;

            int lineStart;
            int prefixLength;
            var prefix = GetCurrentSendLinePrefix(out lineStart, out prefixLength);
            var leadingSpaces = prefix.Length - prefix.TrimStart().Length;
            var replaceStart = lineStart + leadingSpaces;
            var replaceLength = Math.Max(prefixLength - leadingSpaces, 0);
            var text = toSendDataTextBox.Text ?? "";

            applyingSendSuggestion = true;
            try
            {
                toSendDataTextBox.Text = text.Remove(replaceStart, replaceLength).Insert(replaceStart, selectedText);
                toSendDataTextBox.CaretIndex = replaceStart + selectedText.Length;
            }
            finally
            {
                applyingSendSuggestion = false;
                sendSuggestPopup.IsOpen = false;
                toSendDataTextBox.Focus();
            }
            return true;
        }

        private void AddSendListButton_Click(object sender, RoutedEventArgs e)
        {
            ExitQuickSendKeyboardNavigation();
            var item = CreateBlankQuickSendItem(toSendListItems.Count + 1);
            toSendListItems.Add(item);
            SaveSendList(null, EventArgs.Empty);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var index = toSendListItems.IndexOf(item);
                if (index < 0)
                    return;
                var textBox = GetQuickSendNavigationElement(index, 0) as TextBox;
                if (textBox == null)
                    return;
                quickSendExplicitEditMode = true;
                textBox.IsReadOnly = false;
                textBox.Focus();
                Keyboard.Focus(textBox);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void RemoveQuickSendItem(ToSendData item)
        {
            if (item == null || !toSendListItems.Contains(item))
                return;

            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "pre-delete-item");
            ExitQuickSendKeyboardNavigation();
            if (toSendListItems.Count <= 1)
            {
                ClearQuickSendItem(item, 1);
                SaveSendList(null, EventArgs.Empty);
                return;
            }

            toSendListItems.Remove(item);
            CheckToSendListId();
            SaveSendList(null, EventArgs.Empty);
        }

        private ToSendData CreateBlankQuickSendItem(int id)
        {
            var oldCanSaveSendList = canSaveSendList;
            canSaveSendList = false;
            try
            {
                return new ToSendData
                {
                    id = id,
                    text = "",
                    hex = false,
                    commit = TryFindResource("QuickSendButton") as string ?? "?!",
                    recvScriptPath = "",
                    recvScriptPara = "",
                    appendCrlf = true,
                    disableSuggestion = false
                };
            }
            finally
            {
                canSaveSendList = oldCanSaveSendList;
            }
        }

        private void ClearQuickSendItem(ToSendData item, int id)
        {
            if (item == null)
                return;

            var oldCanSaveSendList = canSaveSendList;
            canSaveSendList = false;
            try
            {
                item.id = id;
                item.text = "";
                item.hex = false;
                item.commit = TryFindResource("QuickSendButton") as string ?? "?!";
                item.recvScriptPath = "";
                item.recvScriptPara = "";
                item.appendCrlf = true;
                item.disableSuggestion = false;
            }
            finally
            {
                canSaveSendList = oldCanSaveSendList;
            }
        }

        private bool HasQuickSendContent(ToSendData item)
        {
            return item != null &&
                   (!string.IsNullOrWhiteSpace(item.text) ||
                    !string.IsNullOrWhiteSpace(item.recvScriptPath) ||
                    !string.IsNullOrWhiteSpace(item.recvScriptPara));
        }

        private void knowSendDataButton_click(object sender, RoutedEventArgs e)
        {
            SendQuickSendItem(((Button)sender).Tag as ToSendData);
        }

        private void QuickSendList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var data = GetQuickSendDataFromSource(e.OriginalSource as DependencyObject);
            var sourceColumn = GetQuickSendNavigationColumnFromSource(e.OriginalSource as DependencyObject);
            // Focus can leave the row's three browse cells. Never interpret
            // Enter/Space on a secondary action as a serial send.
            if (sourceColumn < QuickSendNavigationFirstColumn)
            {
                ExitQuickSendKeyboardNavigation();
                return;
            }

            if (!quickSendKeyboardNavigationMode)
            {
                if (quickSendExplicitEditMode)
                {
                    if (key == Key.Escape && data != null)
                    {
                        BeginQuickSendKeyboardNavigation(data, QuickSendNavigationFirstColumn);
                        e.Handled = true;
                    }
                    return;
                }

                if (!IsQuickSendNavigationKey(key) || data == null)
                    return;

                var column = GetQuickSendNavigationColumnFromSource(e.OriginalSource as DependencyObject);
                if (column < QuickSendNavigationFirstColumn)
                    return;

                // The first arrow key only changes edit mode into browse mode.
                BeginQuickSendKeyboardNavigation(data, column);
                e.Handled = true;
                return;
            }

            // Tab/mouse focus may have moved independently of arrow browsing.
            // The focused cell, never a stale browse index, owns this key press.
            quickSendNavigationRowIndex = data == null ? -1 : toSendListItems.IndexOf(data);
            quickSendNavigationColumn = sourceColumn;
            if (quickSendNavigationRowIndex < 0 ||
                quickSendNavigationRowIndex >= toSendListItems.Count)
            {
                ExitQuickSendKeyboardNavigation();
                return;
            }

            data = toSendListItems[quickSendNavigationRowIndex];
            switch (key)
            {
                case Key.Up:
                    MoveQuickSendNavigationRow(-1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveQuickSendNavigationRow(1);
                    e.Handled = true;
                    break;
                case Key.Left:
                    MoveQuickSendNavigationColumn(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                    MoveQuickSendNavigationColumn(1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (quickSendNavigationColumn == 2)
                        ActivateQuickSendNavigationCell(data);
                    else
                        SendQuickSendItem(data);
                    e.Handled = true;
                    break;
                case Key.Space:
                    ActivateQuickSendNavigationCell(data);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    ExitQuickSendKeyboardNavigation();
                    e.Handled = true;
                    break;
            }
        }

        private void QuickSendTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ExitQuickSendKeyboardNavigation();
            quickSendExplicitEditMode = false;
        }

        private static bool IsQuickSendNavigationKey(Key key)
        {
            return key == Key.Up ||
                key == Key.Down ||
                key == Key.Left ||
                key == Key.Right;
        }

        private static int GetWrappedQuickSendRowIndex(int currentIndex, int delta, int rowCount)
        {
            if (rowCount <= 0)
                return -1;

            var normalizedIndex = currentIndex;
            if (normalizedIndex < 0 || normalizedIndex >= rowCount)
                normalizedIndex = 0;

            return (normalizedIndex + delta % rowCount + rowCount) % rowCount;
        }

        private static int GetClampedQuickSendNavigationColumn(int currentColumn, int delta)
        {
            return Math.Max(
                QuickSendNavigationFirstColumn,
                Math.Min(QuickSendNavigationLastColumn, currentColumn + delta));
        }

        private void BeginQuickSendKeyboardNavigation(ToSendData data, int column)
        {
            var rowIndex = data == null ? -1 : toSendListItems.IndexOf(data);
            if (rowIndex < 0)
                return;

            quickSendExplicitEditMode = false;
            quickSendKeyboardNavigationMode = true;
            quickSendNavigationRowIndex = rowIndex;
            quickSendNavigationColumn = Math.Max(
                QuickSendNavigationFirstColumn,
                Math.Min(QuickSendNavigationLastColumn, column));
            FocusQuickSendNavigationCell();
        }

        private void MoveQuickSendNavigationRow(int delta)
        {
            var nextRow = GetWrappedQuickSendRowIndex(
                quickSendNavigationRowIndex,
                delta,
                toSendListItems.Count);
            if (nextRow < 0)
                return;

            quickSendNavigationRowIndex = nextRow;
            FocusQuickSendNavigationCell();
        }

        private void MoveQuickSendNavigationColumn(int delta)
        {
            var nextColumn = GetClampedQuickSendNavigationColumn(
                quickSendNavigationColumn,
                delta);
            if (nextColumn == quickSendNavigationColumn)
                return;

            quickSendNavigationColumn = nextColumn;
            FocusQuickSendNavigationCell();
        }

        private void ActivateQuickSendNavigationCell(ToSendData data)
        {
            switch (quickSendNavigationColumn)
            {
                case 0:
                    var textBox = GetQuickSendNavigationElement(
                        quickSendNavigationRowIndex,
                        QuickSendNavigationFirstColumn) as TextBox;
                    ExitQuickSendKeyboardNavigation();
                    quickSendExplicitEditMode = true;
                    if (textBox != null)
                    {
                        textBox.IsReadOnly = false;
                        textBox.Focus();
                        Keyboard.Focus(textBox);
                        textBox.CaretIndex = textBox.Text?.Length ?? 0;
                    }
                    break;
                case 1:
                    SendQuickSendItem(data);
                    break;
                case 2:
                    var settingsButton = GetQuickSendNavigationElement(quickSendNavigationRowIndex, 2);
                    OpenQuickSendItemSettings(data, settingsButton);
                    break;
            }
        }

        private void FocusQuickSendNavigationCell()
        {
            RestoreQuickSendNavigationTextBox();
            if (quickSendNavigationRowIndex < 0 ||
                quickSendNavigationRowIndex >= toSendListItems.Count)
            {
                return;
            }

            toSendList.SelectedIndex = quickSendNavigationRowIndex;
            var target = GetQuickSendNavigationElement(
                quickSendNavigationRowIndex,
                quickSendNavigationColumn);
            if (target == null)
                return;

            if (target is TextBox textBox)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                textBox.IsReadOnly = true;
                textBox.Cursor = Cursors.Arrow;
                quickSendNavigationTextBox = textBox;
            }

            target.Focus();
            Keyboard.Focus(target);
        }

        private FrameworkElement GetQuickSendNavigationElement(int rowIndex, int column)
        {
            if (rowIndex < 0 || rowIndex >= toSendListItems.Count)
                return null;

            var item = toSendListItems[rowIndex];
            toSendList.ScrollIntoView(item);
            toSendList.UpdateLayout();
            var container = toSendList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
            if (container == null)
                return null;

            container.ApplyTemplate();
            var elementName = column == 0
                ? "QuickSendRowTextBox"
                : column == 1
                    ? "QuickSendRowSendButton"
                    : "QuickSendRowSettingsButton";
            return container.Template.FindName(elementName, container) as FrameworkElement;
        }

        private static ToSendData GetQuickSendDataFromSource(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element &&
                    element.DataContext is ToSendData data)
                {
                    return data;
                }

                current = GetQuickSendNavigationParent(current);
            }

            return null;
        }

        private static int GetQuickSendNavigationColumnFromSource(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element)
                {
                    switch (element.Name)
                    {
                        case "QuickSendRowTextBox":
                            return 0;
                        case "QuickSendRowSendButton":
                            return 1;
                        case "QuickSendRowSettingsButton":
                            return 2;
                    }
                }

                current = GetQuickSendNavigationParent(current);
            }

            return -1;
        }

        private void ExitQuickSendKeyboardNavigation()
        {
            RestoreQuickSendNavigationTextBox();
            quickSendKeyboardNavigationMode = false;
            quickSendExplicitEditMode = false;
            quickSendNavigationRowIndex = -1;
            quickSendNavigationColumn = QuickSendNavigationFirstColumn;
        }

        private static DependencyObject GetQuickSendNavigationParent(DependencyObject child)
        {
            if (child is Visual ||
                child is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(child);
            }

            if (child is ContentElement contentElement)
                return ContentOperations.GetParent(contentElement) ??
                    (contentElement as FrameworkContentElement)?.Parent;

            return null;
        }

        private void RestoreQuickSendNavigationTextBox()
        {
            if (quickSendNavigationTextBox == null)
                return;

            quickSendNavigationTextBox.IsReadOnly = false;
            quickSendNavigationTextBox.ClearValue(CursorProperty);
            quickSendNavigationTextBox = null;
        }

        private void QuickSendTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (IsCtrlKeyDown() && e.Key == Key.X && sender is TextBox textBox)
            {
                textBox.Cut();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Return && e.Key != Key.Enter)
                return;

            if (!(sender is TextBox quickSendTextBox) || !(quickSendTextBox.DataContext is ToSendData data))
                return;

            quickSendTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            SendQuickSendItem(data);
        }

        private void QuickSendTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is TextBox textBox) || !(textBox.DataContext is ToSendData data))
                return;

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            var title = TryFindResource("QuickSendEditTitle") as string ?? "编辑快捷发送内容";
            var promptTemplate = TryFindResource("QuickSendEditPrompt") as string
                ?? "快捷发送 {0} 的内容（支持多行）：";
            var dialog = new InputDialogWindow(
                string.Format(promptTemplate, data.id),
                data.text ?? string.Empty,
                title)
            {
                Owner = this
            };
            dialog.EnableMultilineEditor();

            if (dialog.ShowDialog() == true)
                data.text = dialog.Value ?? string.Empty;

            e.Handled = true;
        }

        private void SendQuickSendItem(ToSendData data)
        {
            if (data == null)
                return;

            var sendText = data.text ?? string.Empty;
            var sendData = data.hex ? Global.Hex2Byte(sendText) : Global.GetEncoding().GetBytes(sendText);
            if (IsMainSendTargetSelected())
            {
                var targetData = PrepareMainSendTargetBytes(sendData, data.appendCrlf);
                if (targetData.Length > 0)
                    Tools.Global.SendToMainSendTarget(targetData);
                return;
            }

            var receiveScriptName = recvScriptBackup;

            // 如果有指定接收脚本，则切换；导入或旧配置中的路径不能越过脚本目录。
            if (!string.IsNullOrWhiteSpace(data.recvScriptPath))
            {
                if (!Tools.Global.TryGetProfileScriptPath(
                        "user_script_recv_convert",
                        data.recvScriptPath,
                        out var normalizedScriptName,
                        out var receiveScriptPath) ||
                    !File.Exists(receiveScriptPath))
                {
                    data.recvScriptPath = "";
                    if (Tools.Global.TryGetProfileScriptPath(
                            "user_script_recv_convert",
                            "default",
                            out _,
                            out var defaultScriptPath) &&
                        !File.Exists(defaultScriptPath))
                    {
                        File.Create(defaultScriptPath).Close();
                    }
                }
                else
                {
                    data.recvScriptPath = normalizedScriptName;
                    receiveScriptName = normalizedScriptName;
                }
            }

            SetReceiveScriptContext(receiveScriptName, data.recvScriptPara ?? "", sendData);
            sendUartData(
                sendData,
                data.hex,
                true,
                data.hex ? data.text : null,
                data.appendCrlf,
                sendText);
        }

        private byte[] PrepareMainSendTargetBytes(byte[] data, bool appendCrlf)
        {
            return AppendCrlf(data, appendCrlf);
        }

        private void QuickSendButton_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(((Button)sender).Tag is ToSendData data))
                return;

            var defaultButtonText = TryFindResource("QuickSendButton") as string ?? "发送";
            var ret = Tools.InputDialog.OpenDialog(
                TryFindResource("QuickSendSetButton") as string ?? "输入你想显示的内容",
                string.IsNullOrWhiteSpace(data.commit) ? defaultButtonText : data.commit,
                TryFindResource("QuickSendChangeButton") as string ?? "更改发送按键显示内容");
            if (!ret.Item1)
                return;

            data.commit = string.IsNullOrWhiteSpace(ret.Item2) ? defaultButtonText : ret.Item2.Trim();
            SaveSendList(null, EventArgs.Empty);
            e.Handled = true;
        }

        /// <summary>
        /// 检查并更正快捷发送区序号
        /// </summary>
        public void CheckToSendListId()
        {
            //当序号不对时，更正序号
            for (int i = 0; i < toSendListItems.Count; i++)
            {
                if (toSendListItems[i].id != i + 1)
                {
                    var item = toSendListItems[i];
                    toSendListItems.RemoveAt(i);//元素删掉重新加进去
                    item.id = i + 1;
                    toSendListItems.Insert(i, item);
                }
            }
        }

        public void SaveSendList(object sender, EventArgs e)
        {
            if (!canSaveSendList)
                return;
            CheckToSendListId();
            //保存当前的所有数据
            var newList = new List<ToSendData>();
            foreach (ToSendData i in toSendListItems)
            {
                newList.Add(i);
            }
            Tools.Global.setting.quickSend = newList;
        }

        private void NewScriptButton_Click(object sender, RoutedEventArgs e)
        {
            newScriptFileWrapPanel.Visibility = Visibility.Visible;
        }

        private void RunScriptButton_Click(object sender, RoutedEventArgs e)
        {
            if (scriptFileList.SelectedItem == null || fileLoading)
            {
                Tools.MessageBox.Show("请先选择一个脚本文件");
                return;
            }

            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);

            scriptLogTextBox.Clear();
            scriptEditorGrid.Visibility = Visibility.Collapsed;
            scriptLogShowGrid.Visibility = Visibility.Visible;
            scriptLogPrintable = true;

            if (!ScriptEnv.JavaScriptRunEnv.New($"user_script_run/{scriptFileList.SelectedItem as string}.js"))
                return;

            ScriptEnv.JavaScriptRunEnv.canRun = true;
        }

        private void NewScriptFileButton_Click(object sender, RoutedEventArgs e)
        {
            var scriptName = Tools.Global.NormalizeScriptFileName(newScriptFileNameTextBox.Text);
            if (!Tools.Global.TryGetProfileScriptPath(
                    "user_script_run",
                    scriptName,
                    out scriptName,
                    out var scriptPath))
            {
                Tools.MessageBox.Show(string.IsNullOrWhiteSpace(scriptName)
                    ? TryFindResource("ScriptNoName") as string ?? "?!"
                    : TryFindResource("ScriptInvalidName") as string ?? "?!");
                return;
            }

            newScriptFileNameTextBox.Text = scriptName;
            if (File.Exists(scriptPath))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptExist") as string ?? "?!");
                return;
            }

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(scriptPath));
                File.Create(scriptPath).Close();
                loadScriptFile(scriptName);
            }
            catch
            {
                Tools.MessageBox.Show(TryFindResource("ScriptCreateFail") as string ?? "?!");
                return;
            }
            newScriptFileWrapPanel.Visibility = Visibility.Collapsed;
        }

        private void NewScriptFileCancelButton_Click(object sender, RoutedEventArgs e)
        {
            newScriptFileWrapPanel.Visibility = Visibility.Collapsed;
        }

        //重载锁，防止逻辑卡死
        private static bool fileLoading = false;
        //上次打开文件名
        private static string lastScriptFile = "";
        //最后打开文件的时间
        private static DateTime lastScriptFileTime = DateTime.Now;
        //最后修改文件的时间
        private static DateTime lastScriptChangeTime = DateTime.Now;
        /// <summary>
        /// 加载脚本文件
        /// </summary>
        /// <param name="fileName">文件名，不带.js</param>
        private void loadScriptFile(string fileName)
        {
            //检查文件是否存在
            if (!File.Exists(Tools.Global.ProfilePath + $"user_script_run/{fileName}.js"))
            {
                Tools.Global.setting.runScript = "example";
                if (!File.Exists(Tools.Global.ProfilePath + $"user_script_run/{Tools.Global.setting.runScript}.js"))
                {
                    File.Create(Tools.Global.ProfilePath + $"user_script_run/{Tools.Global.setting.runScript}.js").Close();
                }
            }
            else
            {
                Tools.Global.setting.runScript = fileName;
            }

            //文件内容显示出来
            try
            {
                textEditor.Text = File.ReadAllText(Tools.Global.ProfilePath + $"user_script_run/{Tools.Global.setting.runScript}.js");
            }
            catch
            {
                Tools.MessageBox.Show("File load failed.\r\n" +
                    "Do not open this file in other application!");
                return;
            }
            
            //记录最后时间
            lastScriptFileTime = File.GetLastWriteTime(Tools.Global.ProfilePath + $"user_script_run/{Tools.Global.setting.runScript}.js");
            //加载文件,修改时间使用文件时间
            lastScriptChangeTime = lastScriptFileTime;

            RefreshScriptList();
        }

        /// <summary>
        /// 保存脚本文件
        /// </summary>
        /// <param name="fileName">文件名，不带.js</param>
        private void saveScriptFile(string fileName)
        {
            try
            {
                //如果修改时间大于文件时间才执行保存操作
                if (lastScriptChangeTime > lastScriptFileTime)
                {
                    File.WriteAllText(Tools.Global.ProfilePath + $"user_script_run/{fileName}.js", textEditor.Text);
                    //记录最后时间
                    lastScriptFileTime = File.GetLastWriteTime(Tools.Global.ProfilePath + $"user_script_run/{fileName}.js");
                }
            }
            catch { }
        }

        private void ScriptFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (scriptFileList.SelectedItem != null && !fileLoading)
            {
                if (lastScriptFile != "")
                    saveScriptFile(lastScriptFile);
                string fileName = scriptFileList.SelectedItem as string;
                loadScriptFile(fileName);
            }
        }
        private void TextEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            //自动保存脚本
            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);
        }
        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            Tools.TaskbarIntegration.ConfigureWindow(this);
            var darkMode = Tools.Global.setting?.darkMode ?? Tools.Global.IsDarkTheme;
            Tools.Win32.ApplyWindowTheme(this, darkMode, false);
        }
        private void Window_Deactivated(object sender, EventArgs e)
        {
            CloseQuickSendItemSettings();
            CloseNotificationPopup();
            //窗口变为后台,可能在切换编辑器,自动保存脚本
            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);
        }
        private void Window_Activated(object sender, EventArgs e)
        {
            if (lastScriptFile != "")
            {
                //当前文件最后时间
                DateTime fileTime = File.GetLastWriteTime(Tools.Global.ProfilePath + $"user_script_run/{lastScriptFile}.js");
                if (fileTime > lastScriptFileTime)//代码在外部被修改
                {
                    loadScriptFile(lastScriptFile);
                }
            }
        }

        //是否可打印标记
        private bool _scriptLogPrintable = true;
        private bool scriptLogPrintable
        {
            get
            {
                return _scriptLogPrintable;
            }
            set
            {
                this.Dispatcher.Invoke(new Action(delegate
                {
                    if (value)
                    {
                        pauseScriptPrintButton.ToolTip = TryFindResource("ScriptPause") as string ?? "?!";
                        pauseScriptPrintIcon.Icon = FontAwesomeIcon.Pause;
                    }
                    else
                    {
                        pauseScriptPrintButton.ToolTip = TryFindResource("ScriptContinue") as string ?? "?!";
                        pauseScriptPrintIcon.Icon = FontAwesomeIcon.Play;
                    }
                }));
                _scriptLogPrintable = value;
            }
        }

        //脚本日志打印次数
        private int scriptLogCount = 0;
        /// <summary>
        /// 消息来的信号量
        /// </summary>
        private EventWaitHandle scriptWaitQueue = new AutoResetEvent(false);
        private List<string> scriptLogsBuff = new List<string>();
        private void ScriptApis_PrintScriptLog(object sender, EventArgs e)
        {
            if(sender is string && sender != null)
            { 
                lock(scriptLogsBuff)
                {
                    if (scriptLogsBuff.Count > 500)
                    {
                        scriptLogsBuff.Clear();
                        scriptLogsBuff.Add("too many logs!");
                        //延时0.5秒，防止卡住ui线程
                        Thread.Sleep(500);
                    }
                    else
                        scriptLogsBuff.Add(sender as string);
                }
                scriptWaitQueue.Set();
            }
        }

        private void ScriptLogPrintTask()
        {
            scriptWaitQueue.Reset();
            Tools.Global.ProgramClosedEvent += (_, _) =>
            {
                scriptWaitQueue.Set();
            };
            while (true)
            {
                scriptWaitQueue.WaitOne();
                if (Tools.Global.isMainWindowsClosed)
                    return;
                var logsb = new StringBuilder();
                lock (scriptLogsBuff)
                {
                    for(int i=0;i<scriptLogsBuff.Count;i++)
                    {
                        logsb.AppendLine(scriptLogsBuff[i]);
                        scriptLogCount++;
                    }
                    scriptLogsBuff.Clear();
                }

                if (!scriptLogPrintable)
                    continue;
                if (logsb.Length == 0)
                    continue;
                var logs = logsb.ToString();
                DoInvoke(()=>
                {
                    scriptLogTextBox.IsEnabled = false;//确保文字不再被选中，防止wpf卡死
                    if (scriptLogCount >= 1000)
                    {
                        scriptLogTextBox.Clear();
                        scriptLogTextBox.AppendText("JavaScript log too long, auto clear.\r\n" +
                            "more logs see JavaScript log file.\r\n");
                        scriptLogCount = 0;
                    }
                    scriptLogTextBox.AppendText(logs);
                    scriptLogTextBox.ScrollToEnd();
                    if (!scriptLogTextBox.IsMouseOver)
                        scriptLogTextBox.IsEnabled = true;
                });
                //正常就延时10ms，防止卡住ui线程
                Thread.Sleep(10);
            }
        }


        private void scriptLogTextBox_MouseLeave(object sender, MouseEventArgs e)
        {
            scriptLogTextBox.IsEnabled = true;
        }

        private void StopScriptButton_Click(object sender, RoutedEventArgs e)
        {
            var wasRunning = ScriptEnv.JavaScriptRunEnv.isRunning;
            scriptLogCount = 0;
            lock(scriptLogsBuff)
                scriptLogsBuff.Clear();
            if (!ScriptEnv.JavaScriptRunEnv.isRunning)
            {
                scriptLogTextBox.Clear();
                scriptEditorGrid.Visibility = Visibility.Visible;
                scriptLogShowGrid.Visibility = Visibility.Collapsed;
                scriptLogPrintable = true;
                
                stopScriptOrExitIcon.Icon = FontAwesomeIcon.Stop;
                stopScriptButton.ToolTip = TryFindResource("ScriptStop") as string ?? "?!";
            }
            else
            {
                stopScriptOrExitIcon.Icon = FontAwesomeIcon.SignOut;
                stopScriptButton.ToolTip = TryFindResource("ScriptQuit") as string ?? "?!";
            }
            scriptLogPrintable = true;
            ScriptEnv.JavaScriptRunEnv.StopScript("");

            pauseScriptPrintButton.ToolTip = TryFindResource("ScriptReload") as string ?? "?!";
            pauseScriptPrintIcon.Icon = FontAwesomeIcon.Refresh;
            if (wasRunning)
            {
                AddNotification(
                    DateTime.Now,
                    string.Format(
                        TryFindResource("NotificationStoppedTitleFormat") as string ?? "{0} 已停止",
                        TryFindResource("NotificationScriptSource") as string ?? "脚本"),
                    Tools.Global.setting.runScript ?? string.Empty,
                    AppNotificationLevel.Warning,
                    AppNotificationCategory.Task);
            }
        }

        private void JavaScriptRunEnv_ScriptRunError(object sender, EventArgs e)
        {
            scriptLogPrintable = true;
            Dispatcher.BeginInvoke(new Action(() =>
                AddNotification(
                    DateTime.Now,
                    string.Format(
                        TryFindResource("NotificationOperationFailedTitleFormat") as string ?? "{0} 失败",
                        TryFindResource("NotificationScriptSource") as string ?? "脚本"),
                    Tools.Global.setting.runScript ?? string.Empty,
                    AppNotificationLevel.Error,
                    AppNotificationCategory.Task)));
        }

        private void PauseScriptPrintButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ScriptEnv.JavaScriptRunEnv.isRunning)
            {
                stopScriptOrExitIcon.Icon = FontAwesomeIcon.Stop;
                stopScriptButton.ToolTip = TryFindResource("ScriptStop") as string ?? "?!";
                if (scriptFileList.SelectedItem != null &&
                    ScriptEnv.JavaScriptRunEnv.New($"user_script_run/{scriptFileList.SelectedItem as string}.js"))
                {
                    ScriptEnv.JavaScriptRunEnv.canRun = true;
                    scriptLogPrintable = true;
                }
            }
            else {
                scriptLogPrintable = !scriptLogPrintable;
            }
        }

        private void SendScriptCommandButton_Click(object sender, RoutedEventArgs e)
        {
            ScriptEnv.JavaScriptRunEnv.RunCommand(runOneLineScriptTextBox.Text);
            //runOneLineScriptTextBox.Clear();
        }

        private void RunOneLineScriptTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if(e.Key == Key.Enter)
                ScriptEnv.JavaScriptRunEnv.RunCommand(runOneLineScriptTextBox.Text);
        }

        private void sentCountTextBlock_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            Tools.Global.setting.SentCount = 0;
        }

        private void receivedCountTextBlock_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            Tools.Global.setting.ReceivedCount = 0;
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            Tools.Global.setting.language = ((MenuItem)sender).Tag.ToString();
            RefreshQuickSendDefaultButtonLabels();
            RefreshQuickSendPageSelector();
            if (IsSerialSplitModeActive())
                UpdateSelectedSplitSlotControls();
            else
                UpdateMainSerialConnectionStatus();
            RefreshToolModulesLocalization();
            SetRightToolsCollapsed(rightToolsCollapsed);
            UpdateMainSendTargetUi();
            UpdateThemeToggleMenu();
            updateCheckController.RefreshIndicatorText();
            RefreshNotificationFilterOptions();
        }

        private void Global_SerialPinStatusChangedEvent(object sender, SerialPinStatusSnapshot snapshot)
        {
            if (snapshot == null || Tools.Global.isMainWindowsClosed)
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Global_SerialPinStatusChangedEvent(sender, snapshot)));
                return;
            }

            var portName = string.IsNullOrWhiteSpace(snapshot.PortName)
                ? (TryFindResource("SerialPinUnknownPort") as string ?? "串口")
                : snapshot.PortName;
            var changedLines = snapshot.ChangedLines == null || snapshot.ChangedLines.Count == 0
                ? "PIN"
                : string.Join(" / ", snapshot.ChangedLines);
            var titleFormat = TryFindResource("SerialPinNotificationTitleFormat") as string ??
                "{0} 引脚变化：{1}";
            var messageFormat = TryFindResource("SerialPinNotificationMessageFormat") as string ??
                "CTS:{0}  DSR:{1}  DCD:{2}  RI:{3}";

            var title = string.Format(titleFormat, portName, changedLines);
            var message = string.Format(
                messageFormat,
                FormatSerialPinState(snapshot.Cts, false, snapshot),
                FormatSerialPinState(snapshot.Dsr, false, snapshot),
                FormatSerialPinState(snapshot.Dcd, false, snapshot),
                FormatSerialPinState(snapshot.Ri, true, snapshot));

            AddPortNotification(
                snapshot.Timestamp,
                title,
                message,
                AppNotificationLevel.Info,
                AppNotificationCategory.SerialPin,
                snapshot.PortName);
            Tools.Logger.AddUartLogDebug(
                $"[SerialPinChanged]{portName} {changedLines} {message}");
        }

        private void Global_AppNotificationEvent(object sender, AppNotificationEventArgs notification)
        {
            if (notification == null || windowIsClosing || Tools.Global.isMainWindowsClosed)
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Global_AppNotificationEvent(sender, notification)));
                return;
            }

            if (string.IsNullOrWhiteSpace(notification.PortName))
            {
                AddNotification(
                    notification.Timestamp,
                    notification.Title,
                    notification.Message,
                    notification.Level,
                    notification.Category);
            }
            else
            {
                AddPortNotification(
                    notification.Timestamp,
                    notification.Title,
                    notification.Message,
                    notification.Level,
                    notification.Category,
                    notification.PortName);
            }
        }

        private void AddSerialConnectionNotification(string portName, bool reconnected)
        {
            var displayName = string.IsNullOrWhiteSpace(portName)
                ? (TryFindResource("SerialPinUnknownPort") as string ?? "串口")
                : portName;
            var baudMessage = string.Format(
                TryFindResource("NotificationSerialOpenedMessageFormat") as string ?? "{0} baud",
                Tools.Global.setting?.baudRate ?? 0);
            if (reconnected)
            {
                var reconnectMessage = TryFindResource("NotificationSerialReconnectedMessage") as string ??
                    "自动重连成功。";
                baudMessage = reconnectMessage + " " + baudMessage;
            }

            AddPortNotification(
                DateTime.Now,
                string.Format(
                    TryFindResource("NotificationConnectedTitleFormat") as string ?? "{0} 已连接",
                    displayName),
                baudMessage,
                AppNotificationLevel.Success,
                AppNotificationCategory.Connection,
                portName);
        }

        private void AddSerialDisconnectedNotification(string portName)
        {
            var displayName = string.IsNullOrWhiteSpace(portName)
                ? (TryFindResource("SerialPinUnknownPort") as string ?? "串口")
                : portName;
            AddPortNotification(
                DateTime.Now,
                string.Format(
                    TryFindResource("NotificationDisconnectedTitleFormat") as string ?? "{0} 已断开",
                    displayName),
                string.Empty,
                AppNotificationLevel.Info,
                AppNotificationCategory.Connection,
                portName);
        }

        private string FormatSerialPinState(bool? state, bool isRingIndicator, SerialPinStatusSnapshot snapshot)
        {
            if (state.HasValue)
                return state.Value ? "1" : "0";

            if (isRingIndicator &&
                snapshot?.ChangedLines != null &&
                snapshot.ChangedLines.Any(line => string.Equals(line, "RI", StringComparison.OrdinalIgnoreCase)))
            {
                return TryFindResource("SerialPinTriggered") as string ?? "触发";
            }

            return "?";
        }

        private void AddNotification(
            DateTime timestamp,
            string title,
            string message,
            AppNotificationLevel level = AppNotificationLevel.Info,
            AppNotificationCategory category = AppNotificationCategory.General)
        {
            AddNotificationCore(timestamp, title, message, level, category, string.Empty);
        }

        private void AddPortNotification(
            DateTime timestamp,
            string title,
            string message,
            AppNotificationLevel level,
            AppNotificationCategory category,
            string portName)
        {
            AddNotificationCore(timestamp, title, message, level, category, portName);
        }

        private void AddNotificationCore(
            DateTime timestamp,
            string title,
            string message,
            AppNotificationLevel level,
            AppNotificationCategory category,
            string portName)
        {
            var effectiveTimestamp = timestamp == default(DateTime) ? DateTime.Now : timestamp;
            notificationItems.Insert(0, new AppNotificationItem
            {
                Timestamp = effectiveTimestamp,
                Title = title ?? string.Empty,
                Message = message ?? string.Empty,
                Level = level,
                Category = category,
                PortName = portName ?? string.Empty,
                IndicatorBrush = GetNotificationIndicatorBrush(level)
            });

            if (!string.IsNullOrWhiteSpace(portName))
            {
                Tools.Logger.RecordPortNotification(effectiveTimestamp, portName, title, message);
                mainSplitPortPage?.WritePortNotificationToSession(
                    effectiveTimestamp,
                    portName,
                    title,
                    message);
            }

            while (notificationItems.Count > MaxNotificationItems)
                notificationItems.RemoveAt(notificationItems.Count - 1);

            if (NotificationPopup?.IsOpen != true && notificationDetachedWindow?.IsVisible != true)
                unreadNotificationCount++;
            UpdateNotificationUi();
        }

        private System.Windows.Media.Brush GetNotificationIndicatorBrush(AppNotificationLevel level)
        {
            string resourceKey;
            switch (level)
            {
                case AppNotificationLevel.Success:
                    resourceKey = "AppSuccessBrush";
                    break;
                case AppNotificationLevel.Warning:
                    resourceKey = "AppWarningBrush";
                    break;
                case AppNotificationLevel.Error:
                    resourceKey = "AppDangerBrush";
                    break;
                default:
                    resourceKey = "AppAccentBrush";
                    break;
            }

            return TryFindResource(resourceKey) as System.Windows.Media.Brush ??
                System.Windows.Media.Brushes.DodgerBlue;
        }

        private bool FilterNotification(object value)
        {
            if (!(value is AppNotificationItem item))
                return false;

            switch (selectedNotificationFilter)
            {
                case NotificationFilter.Info:
                    return item.Level == AppNotificationLevel.Info;
                case NotificationFilter.Success:
                    return item.Level == AppNotificationLevel.Success;
                case NotificationFilter.Warning:
                    return item.Level == AppNotificationLevel.Warning;
                case NotificationFilter.Error:
                    return item.Level == AppNotificationLevel.Error;
                default:
                    return true;
            }
        }

        private void RefreshNotificationFilterOptions()
        {
            if (NotificationFilterComboBox == null)
                return;

            var options = new[]
            {
                new NotificationFilterOption
                {
                    Filter = NotificationFilter.All,
                    Text = GetResourceText("NotificationFilterAll", "全部"),
                    IndicatorBrush = TryFindResource("AppGlassMutedBrush") as System.Windows.Media.Brush ??
                        System.Windows.Media.Brushes.Gray
                },
                new NotificationFilterOption
                {
                    Filter = NotificationFilter.Info,
                    Text = GetResourceText("NotificationFilterInfo", "提示"),
                    IndicatorBrush = GetNotificationIndicatorBrush(AppNotificationLevel.Info)
                },
                new NotificationFilterOption
                {
                    Filter = NotificationFilter.Success,
                    Text = GetResourceText("NotificationFilterSuccess", "成功"),
                    IndicatorBrush = GetNotificationIndicatorBrush(AppNotificationLevel.Success)
                },
                new NotificationFilterOption
                {
                    Filter = NotificationFilter.Warning,
                    Text = GetResourceText("NotificationFilterWarning", "警告"),
                    IndicatorBrush = GetNotificationIndicatorBrush(AppNotificationLevel.Warning)
                },
                new NotificationFilterOption
                {
                    Filter = NotificationFilter.Error,
                    Text = GetResourceText("NotificationFilterError", "错误"),
                    IndicatorBrush = GetNotificationIndicatorBrush(AppNotificationLevel.Error)
                }
            };

            refreshingNotificationFilters = true;
            try
            {
                NotificationFilterComboBox.ItemsSource = options;
                NotificationFilterComboBox.SelectedItem =
                    options.First(option => option.Filter == selectedNotificationFilter);
            }
            finally
            {
                refreshingNotificationFilters = false;
            }
        }

        private void NotificationFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (refreshingNotificationFilters ||
                !(NotificationFilterComboBox.SelectedItem is NotificationFilterOption option))
            {
                return;
            }

            selectedNotificationFilter = option.Filter;
            notificationView?.Refresh();
            UpdateNotificationUi();
        }

        private void UpdateNotificationUi()
        {
            if (NotificationBadge == null)
                return;

            NotificationBadge.Visibility = unreadNotificationCount > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            NotificationBadgeText.Text = unreadNotificationCount > 99
                ? "99+"
                : unreadNotificationCount.ToString();
            var filteredItemsEmpty = notificationView?.IsEmpty ?? notificationItems.Count == 0;
            NotificationEmptyText.Visibility = filteredItemsEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
            NotificationEmptyText.SetResourceReference(
                TextBlock.TextProperty,
                notificationItems.Count == 0 ? "NotificationEmpty" : "NotificationFilterEmpty");
            NotificationClearButton.IsEnabled = notificationItems.Count > 0;
        }

        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            await updateCheckController.CheckAsync();
        }

        private void NotificationCenterButton_Click(object sender, RoutedEventArgs e)
        {
            if (notificationDetachedWindow?.IsVisible == true)
            {
                notificationDetachedWindow.Activate();
                unreadNotificationCount = 0;
                UpdateNotificationUi();
                e.Handled = true;
                return;
            }

            var shouldOpen = GetNotificationPopupStateAfterButtonClick(NotificationPopup.IsOpen);
            if (!shouldOpen)
            {
                NotificationPopup.IsOpen = false;
            }
            else
            {
                NotificationPopup.PlacementTarget = NotificationCenterButton;
                PositionNotificationPopup();
                NotificationPopup.IsOpen = true;
            }
            e.Handled = true;
        }

        private void NotificationDetachButton_Click(object sender, RoutedEventArgs e)
        {
            if (notificationDetachedWindow?.IsVisible == true)
            {
                notificationDetachedWindow.Activate();
                return;
            }

            NotificationPopup.IsOpen = false;
            NotificationPopup.Child = null;
            NotificationDetachButton.Visibility = Visibility.Collapsed;
            NotificationDetachedCloseButton.Visibility = Visibility.Visible;

            var workArea = SystemParameters.WorkArea;
            // 使用只承载 Content 的裸窗口模板，彻底绕过 AdonisUI 的默认
            // Window 模板；透明阴影留白因此不会再被画成灰色圆角外框。
            var floatingWindowStyle = TryFindResource("FloatingNotificationWindowStyle") as Style;
            var floatingWindow = new Window
            {
                Style = floatingWindowStyle,
                Title = GetResourceText("NotificationCenterTitle", "消息中心"),
                Width = NotificationPopupRoot.Width,
                MaxHeight = Math.Max(300, workArea.Height - 24),
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ShowInTaskbar = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = NotificationPopupRoot
            };
            Tools.Win32.ConfigureTransparentToolWindow(floatingWindow);
            floatingWindow.SourceInitialized += NotificationDetachedWindow_SourceInitialized;
            floatingWindow.Left = Math.Max(
                workArea.Left,
                Math.Min(Left + ActualWidth - floatingWindow.Width - 20, workArea.Right - floatingWindow.Width));
            floatingWindow.Top = Math.Max(
                workArea.Top,
                Math.Min(Top + 72, workArea.Bottom - 300));
            floatingWindow.Closed += NotificationDetachedWindow_Closed;
            notificationDetachedWindow = floatingWindow;
            unreadNotificationCount = 0;
            UpdateNotificationUi();
            floatingWindow.Show();
            floatingWindow.Activate();
            e.Handled = true;
        }

        private void NotificationDetachedCloseButton_Click(object sender, RoutedEventArgs e)
        {
            notificationDetachedWindow?.Close();
            e.Handled = true;
        }

        private void NotificationDetachedWindow_SourceInitialized(object sender, EventArgs e)
        {
            if (sender is Window window)
                Tools.Win32.ConfigureTransparentToolWindow(window);
        }

        private void NotificationDetachedWindow_Closed(object sender, EventArgs e)
        {
            if (sender is Window window)
            {
                window.SourceInitialized -= NotificationDetachedWindow_SourceInitialized;
                window.Closed -= NotificationDetachedWindow_Closed;
                window.Content = null;
            }

            notificationDetachedWindow = null;
            NotificationDetachButton.Visibility = Visibility.Visible;
            NotificationDetachedCloseButton.Visibility = Visibility.Collapsed;
            if (NotificationPopup.Child == null)
                NotificationPopup.Child = NotificationPopupRoot;
            NotificationPopup.IsOpen = false;
        }

        private void NotificationDragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (notificationDetachedWindow?.IsVisible != true || e.LeftButton != MouseButtonState.Pressed)
                return;

            var source = e.OriginalSource as DependencyObject;
            while (source != null && !ReferenceEquals(source, NotificationPopupRoot))
            {
                if (source is ButtonBase ||
                    source is Selector ||
                    source is TextBoxBase ||
                    source is RangeBase ||
                    source is ScrollBar ||
                    source is ScrollViewer ||
                    source is Thumb ||
                    source is Hyperlink)
                {
                    return;
                }
                source = source is System.Windows.Media.Visual
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }

            try
            {
                notificationDetachedWindow.DragMove();
                e.Handled = true;
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static bool GetNotificationPopupStateAfterButtonClick(bool isOpen)
        {
            return !isOpen;
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ShouldCloseNotificationPopup(
                NotificationPopup?.IsOpen == true,
                NotificationCenterButton?.IsMouseOver == true,
                NotificationPopupRoot?.IsMouseOver == true,
                NotificationFilterComboBox?.IsDropDownOpen == true))
            {
                CloseNotificationPopup();
            }
        }

        private static bool ShouldCloseNotificationPopup(
            bool isOpen,
            bool isPointerOverButton,
            bool isPointerOverPopup,
            bool isFilterDropDownOpen)
        {
            return isOpen &&
                !isPointerOverButton &&
                !isPointerOverPopup &&
                !isFilterDropDownOpen;
        }

        private void CloseNotificationPopup()
        {
            if (NotificationPopup?.IsOpen == true)
                NotificationPopup.IsOpen = false;
        }

        private void NotificationPopup_Opened(object sender, EventArgs e)
        {
            PositionNotificationPopup();
            unreadNotificationCount = 0;
            UpdateNotificationUi();
        }

        private void PositionNotificationPopup()
        {
            if (MainGrid == null ||
                NotificationCenterButton == null ||
                NotificationPopupRoot == null ||
                NotificationPopupSurface == null ||
                MainGrid.ActualWidth <= 0)
            {
                return;
            }

            var buttonLeft = NotificationCenterButton.TranslatePoint(new System.Windows.Point(0, 0), MainGrid).X;
            var surfaceWidth = NotificationPopupSurface.ActualWidth > 0
                ? NotificationPopupSurface.ActualWidth
                : Math.Max(
                    0,
                    NotificationPopupRoot.Width -
                    NotificationPopupSurface.Margin.Left -
                    NotificationPopupSurface.Margin.Right);
            var popupAlignmentWidth = NotificationPopupSurface.Margin.Left + surfaceWidth;
            NotificationPopup.HorizontalOffset = CalculateNotificationPopupOffset(
                MainGrid.ActualWidth,
                buttonLeft,
                popupAlignmentWidth);
        }

        private static double CalculateNotificationPopupOffset(
            double mainWidth,
            double buttonLeft,
            double popupWidth)
        {
            var desiredLeft = Math.Max(0, mainWidth - popupWidth);
            return Math.Round(desiredLeft - buttonLeft);
        }

        private void NotificationClearButton_Click(object sender, RoutedEventArgs e)
        {
            notificationItems.Clear();
            Tools.Logger.ClearPortNotificationLogs();
            unreadNotificationCount = 0;
            UpdateNotificationUi();
        }

        private void LanguageMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null)
                return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.HorizontalOffset = 0;
            button.ContextMenu.VerticalOffset = 6;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        private void ThemeToggleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Tools.Global.setting.darkMode = !Tools.Global.setting.darkMode;
            Tools.Global.ApplyTheme(Tools.Global.setting.darkMode);
            UpdateThemeToggleMenu();
        }

        private void Global_ThemeChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Global_ThemeChanged(sender, e)));
                return;
            }

            if (scriptEditorInitialized)
                Tools.EditorTheme.Apply(textEditor);
            UpdateThemeToggleMenu();
        }

        private void UpdateThemeToggleMenu()
        {
            if (ThemeToggleMenuItem == null)
                return;

            ThemeToggleMenuItem.ToolTip = Tools.Global.setting.darkMode ?
                GetResourceText("LightMode", "白天模式") :
                GetResourceText("DarkMode", "黑夜模式");

            if (ThemeToggleIcon != null)
            {
                ThemeToggleIcon.Icon = Tools.Global.setting.darkMode
                    ? FontAwesomeIcon.SunOutline
                    : FontAwesomeIcon.MoonOutline;
                ThemeToggleIcon.SetResourceReference(Control.ForegroundProperty, "AppAccentBrush");
            }
        }

        //id序号右击事件
        private void TextBlock_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            ToSendData data;
            try
            {
                data = ((TextBlock)sender).Tag as ToSendData;
            }
            catch
            {
                data = ((Grid)sender).Tag as ToSendData;
            }
            if (data == null)
                return;
            Tuple<bool, string> ret = Tools.InputDialog.OpenDialog(TryFindResource("QuickSendChangeIdButton") as string ?? "?!",
                data.id.ToString(), (TryFindResource("QuickSendChangeIdTitle") as string ?? "?!") + data.id.ToString());

            if (!ret.Item1)
                return;
            CheckToSendListId();
            if (data.id <= 0 || data.id > toSendListItems.Count)
                return;
            if (ret.Item2.Trim().Length == 0)//留空删除该项目
            {
                Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "pre-delete-item");
                if (toSendListItems.Count <= 1)
                    ClearQuickSendItem(data, 1);
                else
                    toSendListItems.RemoveAt(data.id-1);
            }
            else
            {
                int index = -1;
                int.TryParse(ret.Item2, out index);
                if (index == data.id || index <= 0 || index > toSendListItems.Count) return;
                //移动到指定位置
                var item = toSendListItems[data.id-1];
                toSendListItems.RemoveAt(data.id-1);
                toSendListItems.Insert(index - 1, item);
            }
            SaveSendList(null, EventArgs.Empty);
        }

        private void MenuItem_Click_QuickSendList(object sender, RoutedEventArgs e)
        {
            int select = int.Parse((string)((MenuItem)sender).Tag);
            SelectQuickSendPage(select);
        }

        private void QuickListSelectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (quickListSelectorRefreshing || QuickListSelectComboBox.SelectedIndex < 0)
                return;

            SelectQuickSendPage(QuickListSelectComboBox.SelectedIndex);
        }

        private void QuickListNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (quickListSelectorRefreshing || QuickListNameTextBox == null)
                return;

            Global.setting.SetQuickListNameNow(QuickListNameTextBox.Text);
            quickListSelectorRefreshing = true;
            try
            {
                RefreshQuickSendPageSelectorItemsOnly();
            }
            finally
            {
                quickListSelectorRefreshing = false;
            }
        }

        private void SelectQuickSendPage(int select)
        {
            if (select < 0 || select >= Global.setting.GetQuickSendListCount() ||
                select == Global.setting.quickSendSelect)
                return;

            SaveSendList(null, EventArgs.Empty);
            canSaveSendList = false;
            toSendListItems.Clear();
            Global.setting.quickSendSelect = select;
            LoadQuickSendList();
            canSaveSendList = true;
        }

        private void AddQuickSendPageButton_Click(object sender, RoutedEventArgs e)
        {
            QuickListSelectComboBox.IsDropDownOpen = false;
            SaveSendList(null, EventArgs.Empty);
            canSaveSendList = false;
            toSendListItems.Clear();
            Global.setting.AddQuickSendPage();
            LoadQuickSendList();
            canSaveSendList = true;
            QuickListNameTextBox.Focus();
            QuickListNameTextBox.SelectAll();
        }

        private void DeleteQuickSendPageButton_Click(object sender, RoutedEventArgs e)
        {
            QuickListSelectComboBox.IsDropDownOpen = false;
            if (Global.setting.GetQuickSendListCount() <= 1)
            {
                Tools.MessageBox.Show(TryFindResource("QuickSendDeletePageBlocked") as string ?? "?!");
                return;
            }

            if (toSendListItems.Any(HasQuickSendContent))
            {
                var ret = Tools.InputDialog.OpenDialog(
                    TryFindResource("QuickSendDeletePageConfirmMsg") as string ?? "?!",
                    "",
                    TryFindResource("DeleteConfirmation") as string ?? "?!");
                if (!ret.Item1 || ret.Item2 != "YES")
                    return;
            }

            SaveSendList(null, EventArgs.Empty);
            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "pre-delete-page");
            canSaveSendList = false;
            toSendListItems.Clear();
            Global.setting.RemoveQuickSendPage(Global.setting.quickSendSelect);
            LoadQuickSendList();
            canSaveSendList = true;
        }

        private async void QuickSendImportButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = TryFindResource("QuickSendLlcomPlusFile") as string ?? "?!"
            };
            if (openFileDialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            CancelQuickSendImport();
            var importPath = openFileDialog.FileName;
            var importButton = sender as Button;
            var cts = new CancellationTokenSource();
            quickSendImportCts = cts;
            var runId = Interlocked.Increment(ref nextQuickSendImportRunId);
            Interlocked.Exchange(ref activeQuickSendImportRunId, runId);
            if (importButton != null)
                importButton.IsEnabled = false;

            try
            {
                // Reject oversized files before a reader or JSON object graph is created.
                CheckQuickSendImportFileSize(importPath);
                var result = await Task.Run(
                    () => ParseQuickSendImportFile(importPath, cts.Token),
                    cts.Token);
                if (!IsCurrentQuickSendImport(runId, cts))
                    return;

                ApplyQuickSendImportResult(result);
                if (result.ImportsAllPages)
                    Tools.MessageBox.Show(TryFindResource("QuickSendImportAllDone") as string ?? "?!");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception err)
            {
                if (IsCurrentQuickSendImport(runId, cts))
                    Tools.MessageBox.Show(err.GetBaseException().Message);
            }
            finally
            {
                var detached = ReferenceEquals(
                    Interlocked.CompareExchange(ref quickSendImportCts, null, cts),
                    cts);
                Interlocked.CompareExchange(ref activeQuickSendImportRunId, 0, runId);
                cts.Dispose();
                if (detached && !windowIsClosing && importButton != null)
                    importButton.IsEnabled = true;
            }
        }

        private bool IsCurrentQuickSendImport(long runId, CancellationTokenSource cts)
        {
            return !windowIsClosing &&
                   runId != 0 &&
                   runId == Interlocked.Read(ref activeQuickSendImportRunId) &&
                   ReferenceEquals(Interlocked.CompareExchange(ref quickSendImportCts, null, null), cts);
        }

        private void CancelQuickSendImport()
        {
            Interlocked.Exchange(ref activeQuickSendImportRunId, 0);
            var cts = Interlocked.Exchange(ref quickSendImportCts, null);
            if (cts == null)
                return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        private static QuickSendImportResult ParseQuickSendImportFile(
            string path,
            CancellationToken cancellationToken)
        {
            CheckQuickSendImportFileSize(path);
            cancellationToken.ThrowIfCancellationRequested();

            JToken root;
            try
            {
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.SequentialScan))
                using (var textReader = new StreamReader(
                    stream,
                    new UTF8Encoding(false, true),
                    true,
                    4096))
                using (var jsonReader = new JsonTextReader(textReader)
                {
                    CloseInput = false,
                    MaxDepth = MaxQuickSendImportJsonDepth,
                    DateParseHandling = DateParseHandling.None
                })
                {
                    root = JToken.Load(jsonReader);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (jsonReader.Read())
                        throw QuickSendImportError("JSON 根值之后存在多余内容。");
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw QuickSendImportError("JSON 格式无效或嵌套超过限制：" + ex.Message);
            }

            if (root == null)
                throw QuickSendImportError("文件中没有可导入的数据。");

            ValidateQuickSendJsonStringBudget(root, cancellationToken);
            var allQuickSendToken = GetAllQuickSendListToken(root);
            if (allQuickSendToken != null)
            {
                var pageTokens = (JArray)allQuickSendToken;
                if (pageTokens.Count == 0 || pageTokens.Count > MaxQuickSendImportPages)
                {
                    throw QuickSendImportError(
                        $"页数必须为 1 到 {MaxQuickSendImportPages} 页。");
                }

                var pages = new List<List<QuickSendImportItem>>(pageTokens.Count);
                foreach (var pageToken in pageTokens)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pages.Add(ParseQuickSendImportPage((JArray)pageToken, cancellationToken));
                }

                List<string> names = null;
                if (root is JObject package &&
                    (package["quickSendListNames"] != null || package["quickListNames"] != null))
                {
                    var namesToken = package["quickSendListNames"] ?? package["quickListNames"];
                    if (!(namesToken is JArray namesArray) ||
                        namesArray.Count != pages.Count ||
                        namesArray.Any(value => value.Type != JTokenType.String))
                    {
                        throw QuickSendImportError("页面名称列表必须与导入页数完全对应。");
                    }
                    names = namesArray.Select(value => (string)value).ToList();
                }

                return new QuickSendImportResult
                {
                    ImportsAllPages = true,
                    Pages = pages,
                    PageNames = names
                };
            }

            if (!(root is JArray singlePage))
                throw QuickSendImportError("当前页导入数据必须是 JSON 数组。");

            return new QuickSendImportResult
            {
                ImportsAllPages = false,
                Pages = new List<List<QuickSendImportItem>>
                {
                    ParseQuickSendImportPage(singlePage, cancellationToken)
                }
            };
        }

        private static void CheckQuickSendImportFileSize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw QuickSendImportError("未选择导入文件。");

            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
                throw QuickSendImportError("导入文件不存在。");
            if (fileInfo.Length > MaxQuickSendImportFileBytes)
            {
                throw QuickSendImportError(
                    $"文件超过 {MaxQuickSendImportFileBytes / (1024 * 1024)} MiB 硬上限。");
            }
        }

        private static void ValidateQuickSendJsonStringBudget(
            JToken root,
            CancellationToken cancellationToken)
        {
            long totalCharacters = 0;
            var visited = 0;
            foreach (var token in EnumerateQuickSendJsonTokens(root))
            {
                if ((visited++ & 0xff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                string value = null;
                if (token is JProperty property)
                    value = property.Name;
                else if (token is JValue scalar && scalar.Type == JTokenType.String)
                    value = (string)scalar;

                if (value == null)
                    continue;
                if (value.Length > MaxQuickSendFieldCharacters)
                {
                    throw QuickSendImportError(
                        $"单个字段超过 {MaxQuickSendFieldCharacters:N0} 个字符。");
                }

                totalCharacters += value.Length;
                if (totalCharacters > MaxQuickSendTotalCharacters)
                {
                    throw QuickSendImportError(
                        $"所有文本字段合计超过 {MaxQuickSendTotalCharacters:N0} 个字符。");
                }
            }
        }

        private static IEnumerable<JToken> EnumerateQuickSendJsonTokens(JToken root)
        {
            var pending = new Stack<JToken>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                yield return current;
                if (!(current is JContainer container))
                    continue;

                foreach (var child in container.Children().Reverse())
                    pending.Push(child);
            }
        }

        private static List<QuickSendImportItem> ParseQuickSendImportPage(
            JArray pageToken,
            CancellationToken cancellationToken)
        {
            if (pageToken == null)
                throw QuickSendImportError("快捷发送页不能为空。");
            if (pageToken.Count > MaxQuickSendItemsPerPage)
            {
                throw QuickSendImportError(
                    $"单页条数超过 {MaxQuickSendItemsPerPage:N0} 条。");
            }

            var items = new List<QuickSendImportItem>(pageToken.Count);
            foreach (var itemToken in pageToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!(itemToken is JObject))
                    throw QuickSendImportError("每条快捷发送数据都必须是 JSON 对象。");

                QuickSendImportItem item;
                try
                {
                    item = itemToken.ToObject<QuickSendImportItem>();
                }
                catch (JsonException ex)
                {
                    throw QuickSendImportError("快捷发送字段类型无效：" + ex.Message);
                }
                if (item == null)
                    throw QuickSendImportError("快捷发送条目为空。");

                item.text = item.text ?? string.Empty;
                item.commit = item.commit ?? string.Empty;
                item.recvScriptPath = item.recvScriptPath ?? string.Empty;
                item.recvScriptPara = item.recvScriptPara ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(item.recvScriptPath))
                {
                    if (!Tools.Global.TryGetProfileScriptPath(
                            "user_script_recv_convert",
                            item.recvScriptPath,
                            out var normalizedName,
                            out _))
                    {
                        throw QuickSendImportError("接收脚本名称无效或越过脚本目录。");
                    }
                    item.recvScriptPath = normalizedName;
                }
                items.Add(item);
            }
            return items;
        }

        private static InvalidDataException QuickSendImportError(string detail)
        {
            return new InvalidDataException("快捷发送导入失败：" + detail);
        }

        private void ApplyQuickSendImportResult(QuickSendImportResult result)
        {
            if (result?.Pages == null || result.Pages.Count == 0)
                throw QuickSendImportError("解析结果为空。");

            SaveSendList(null, EventArgs.Empty);
            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "pre-import");
            var previousCanSave = canSaveSendList;
            canSaveSendList = false;
            try
            {
                var pages = result.Pages
                    .Select(page => page.Select(CreateImportedQuickSendItem).ToList())
                    .ToList();
                if (result.ImportsAllPages)
                {
                    ValidateImportedQuickSendLists(pages);
                    ApplyImportedQuickSendLists(pages, result.PageNames);
                }
                else
                {
                    ValidateImportedQuickSendList(pages[0]);
                    ApplyImportedQuickSendList(pages[0]);
                }
            }
            finally
            {
                canSaveSendList = previousCanSave;
            }
        }

        private static ToSendData CreateImportedQuickSendItem(QuickSendImportItem source)
        {
            return new ToSendData
            {
                id = source.id,
                text = source.text ?? string.Empty,
                hex = source.hex,
                commit = source.commit ?? string.Empty,
                recvScriptPath = source.recvScriptPath ?? string.Empty,
                recvScriptPara = source.recvScriptPara ?? string.Empty,
                appendCrlf = source.appendCrlf,
                disableSuggestion = source.disableSuggestion
            };
        }

        private void ValidateImportedQuickSendLists(List<List<ToSendData>> lists)
        {
            if (lists == null || lists.Count == 0 ||
                lists.Count > MaxQuickSendImportPages ||
                lists.Any(list => list == null))
            {
                throw QuickSendImportError($"页数必须为 1 到 {MaxQuickSendImportPages} 页。");
            }

            long totalCharacters = 0;
            foreach (var list in lists)
                ValidateImportedQuickSendList(list, ref totalCharacters);
        }

        private void ValidateImportedQuickSendList(List<ToSendData> list)
        {
            long totalCharacters = 0;
            ValidateImportedQuickSendList(list, ref totalCharacters);
        }

        private void ValidateImportedQuickSendList(
            List<ToSendData> list,
            ref long totalCharacters)
        {
            if (list == null || list.Count > MaxQuickSendItemsPerPage || list.Any(item => item == null))
            {
                throw QuickSendImportError(
                    $"单页条数不能超过 {MaxQuickSendItemsPerPage:N0} 条，且条目不能为空。");
            }

            foreach (var item in list)
            {
                ValidateImportedQuickSendField(item.text, ref totalCharacters);
                ValidateImportedQuickSendField(item.commit, ref totalCharacters);
                ValidateImportedQuickSendField(item.recvScriptPath, ref totalCharacters);
                ValidateImportedQuickSendField(item.recvScriptPara, ref totalCharacters);

                if (string.IsNullOrWhiteSpace(item.recvScriptPath))
                {
                    item.recvScriptPath = string.Empty;
                    continue;
                }

                if (!Tools.Global.TryGetProfileScriptPath(
                        "user_script_recv_convert",
                        item.recvScriptPath,
                        out var normalizedName,
                        out _))
                {
                    throw QuickSendImportError("接收脚本名称无效或越过脚本目录。");
                }
                item.recvScriptPath = normalizedName;
            }
        }

        private static void ValidateImportedQuickSendField(
            string value,
            ref long totalCharacters)
        {
            var length = value?.Length ?? 0;
            if (length > MaxQuickSendFieldCharacters)
            {
                throw QuickSendImportError(
                    $"单个字段超过 {MaxQuickSendFieldCharacters:N0} 个字符。");
            }
            totalCharacters += length;
            if (totalCharacters > MaxQuickSendTotalCharacters)
            {
                throw QuickSendImportError(
                    $"所有文本字段合计超过 {MaxQuickSendTotalCharacters:N0} 个字符。");
            }
        }

        private void ApplyImportedQuickSendLists(
            List<List<ToSendData>> allData,
            List<string> allNames)
        {
            var previousLists = Tools.Global.setting.GetAllQuickSendLists();
            var previousNames = Tools.Global.setting.GetAllQuickListNames();
            var previousCanSave = canSaveSendList;
            canSaveSendList = false;
            try
            {
                Tools.Global.setting.SetAllQuickSendLists(allData);
                if (allNames != null)
                    Tools.Global.setting.SetAllQuickListNames(allNames);
                toSendListItems.Clear();
                LoadQuickSendList();
            }
            catch
            {
                try
                {
                    Tools.Global.setting.SetAllQuickSendLists(previousLists);
                    Tools.Global.setting.SetAllQuickListNames(previousNames);
                    toSendListItems.Clear();
                    LoadQuickSendList();
                }
                catch { }
                throw;
            }
            finally
            {
                canSaveSendList = previousCanSave;
            }
        }

        private void ApplyImportedQuickSendList(List<ToSendData> data)
        {
            var previousData = Tools.Global.setting.quickSend;
            var previousCanSave = canSaveSendList;
            canSaveSendList = false;
            try
            {
                Tools.Global.setting.quickSend = data;
                toSendListItems.Clear();
                LoadQuickSendList();
            }
            catch
            {
                try
                {
                    Tools.Global.setting.quickSend = previousData;
                    toSendListItems.Clear();
                    LoadQuickSendList();
                }
                catch { }
                throw;
            }
            finally
            {
                canSaveSendList = previousCanSave;
            }
        }

        private void QuickSendExportButton_Click(object sender, RoutedEventArgs e)
        {
            ExportQuickSend(false);
        }

        private void QuickSendExportAllButton_Click(object sender, RoutedEventArgs e)
        {
            ExportQuickSend(true);
        }

        private void QuickSendCommandMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.Placement = PlacementMode.Bottom;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void QuickSendBackupButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSendList(null, EventArgs.Empty);
            Tools.QuickSendBackupService.FlushPending();

            var backupWindow = new QuickSendBackupWindow { Owner = this };
            if (backupWindow.ShowDialog() != true || backupWindow.SelectedSnapshot == null)
                return;

            if (!Tools.QuickSendBackupService.TryLoad(
                    backupWindow.SelectedSnapshot,
                    out var state,
                    out var error))
            {
                Tools.MessageBox.Show(error);
                return;
            }

            var answer = System.Windows.MessageBox.Show(
                this,
                TryFindResource("QuickSendBackupRestoreConfirm") as string ??
                    "Restoring replaces all current quick send pages. Continue?",
                TryFindResource("QuickSendBackupWindowTitle") as string ??
                    "Quick send backup and restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;

            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "pre-restore");
            var previousCanSave = canSaveSendList;
            canSaveSendList = false;
            try
            {
                Tools.Global.setting.SetAllQuickSendState(
                    state.CreateModelLists(),
                    state.Names,
                    state.SelectedIndex);
                toSendListItems.Clear();
                LoadQuickSendList();
            }
            finally
            {
                canSaveSendList = previousCanSave;
            }

            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "restore");
            Tools.MessageBox.Show(
                TryFindResource("QuickSendBackupRestoreDone") as string ??
                "Quick send data was restored from the snapshot.");
        }

        private void OfferLegacyQuickSendRecovery(Tools.QuickSendBackupState candidate)
        {
            if (candidate == null || windowIsClosing)
                return;
            var answer = System.Windows.MessageBox.Show(
                this,
                TryFindResource("QuickSendBackupLegacyCandidatePrompt") as string ??
                    "The current quick send data is empty, but an older backup still contains data. Restore it now?",
                TryFindResource("QuickSendBackupWindowTitle") as string ??
                    "Quick send backup and restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "pre-legacy-recovery");
            var previousCanSave = canSaveSendList;
            canSaveSendList = false;
            try
            {
                Tools.Global.setting.SetAllQuickSendState(
                    candidate.CreateModelLists(),
                    candidate.Names,
                    candidate.SelectedIndex);
                toSendListItems.Clear();
                LoadQuickSendList();
            }
            finally
            {
                canSaveSendList = previousCanSave;
            }
            Tools.QuickSendBackupService.CreateNow(Tools.Global.setting, "legacy-recovery");
            Tools.MessageBox.Show(
                TryFindResource("QuickSendBackupRecoveredLegacy") as string ??
                "Quick send data was restored from the legacy backup.");
        }

        private void ExportQuickSend(bool exportAll)
        {
            SaveSendList(null, EventArgs.Empty);
            System.Windows.Forms.SaveFileDialog SaveFileDialog = new System.Windows.Forms.SaveFileDialog();
            var fileName = exportAll
                ? TryFindResource("QuickSendExportAllFileName") as string ?? "all-quick-send-data"
                : Global.setting.GetQuickListNameNow();
            SaveFileDialog.FileName = System.Text.RegularExpressions.Regex.Replace(fileName, "[<>/\\|:\"?*]", "-");
            SaveFileDialog.Filter = TryFindResource("QuickSendLlcomPlusFile") as string ?? "?!";
            if (SaveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    object data = exportAll
                        ? (object)new
                        {
                            type = "llcom_plus.quickSend.all",
                            version = 1,
                            quickSendList = Tools.Global.setting.GetAllQuickSendLists(),
                            quickSendListNames = Tools.Global.setting.GetAllQuickListNames(),
                        }
                        : toSendListItems.ToList();
                    File.WriteAllText(SaveFileDialog.FileName, JsonConvert.SerializeObject(data));
                    Tools.MessageBox.Show(TryFindResource(exportAll ? "QuickSendSaveAllFileDone" : "QuickSendSaveFileDone") as string ?? "?!");
                }
                catch(Exception err)
                {
                    Tools.MessageBox.Show(err.Message);
                }
            }
        }

        private static JToken GetAllQuickSendListToken(JToken token)
        {
            if (token is JObject obj && obj["quickSendList"] is JArray packageList)
                return IsAllQuickSendListToken(packageList) ? packageList : null;
            return IsAllQuickSendListToken(token) ? token : null;
        }

        private static bool IsAllQuickSendListToken(JToken token)
        {
            if (!(token is JArray array) || array.Count == 0)
                return false;
            return array.All(item => item is JArray);
        }

        private void pauseScriptPrintButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            scriptLogTextBox.Clear();
        }

        private void textEditor_TextChanged(object sender, EventArgs e)
        {
            lastScriptChangeTime = DateTime.Now;
        }

        private void uartDataFlowDocument_GotFocus(object sender, RoutedEventArgs e)
        {
            if (Tools.Global.setting.terminal)
                dataShowFrame.SetResourceReference(Control.BorderBrushProperty, "AppGlassBorderBrush");
        }

        private void uartDataFlowDocument_LostFocus(object sender, RoutedEventArgs e)
        {
            dataShowFrame.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }

        private void uartDataFlowDocument_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.TextComposition.Text.Length < 1 || !Tools.Global.setting.terminal)
                return;
            if (IsSerialSplitModeActive())
            {
                _ = SendPreparedDataToSplitTargetsAsync(
                    Encoding.ASCII.GetBytes(e.TextComposition.Text),
                    autoOpen: false);
                e.Handled = true;
                return;
            }
            if (IsSelectedMainSerialPortOpen())
                try
                {
                    _ = SendMainSerialAsync(
                        Tools.Global.uart.CaptureConnectionLease(),
                        Encoding.ASCII.GetBytes(e.TextComposition.Text), showErrors: false);
                    e.Handled = true;
                }
                catch { }
        }

        private void uartDataFlowDocument_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsCtrlKeyDown() || !Tools.Global.setting.terminal)
                return;

            if (e.Key == Key.A)
            {
                SelectAllUartLog();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.C || e.Key == Key.V || e.Key == Key.X)
                return;

            if (e.Key >= Key.A && e.Key <= Key.Z && IsSerialSplitModeActive())
                try
                {
                    _ = SendPreparedDataToSplitTargetsAsync(
                        new byte[] { (byte)((int)e.Key - (int)Key.A + 1) },
                        autoOpen: false);
                    e.Handled = true;
                    return;
                }
                catch { }

            if (e.Key >= Key.A && e.Key <= Key.Z && IsSelectedMainSerialPortOpen())
                try
                {
                    _ = SendMainSerialAsync(
                        Tools.Global.uart.CaptureConnectionLease(),
                        new byte[] { (byte)((int)e.Key - (int)Key.A + 1) }, showErrors: false);
                    e.Handled = true;
                }
                catch { }
        }

        private bool IsCtrlKeyDown()
        {
            return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        }

        private void SelectAllUartLog()
        {
            if (dataShowFrame.Content is Pages.DataShowPage dataShowPage)
                dataShowPage.SelectAllLog();
        }

        private void QuickSendRowSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement button)
                OpenQuickSendItemSettings(button.Tag as ToSendData, button);
        }

        private void OpenQuickSendItemSettings(ToSendData item, FrameworkElement anchor)
        {
            if (item == null || anchor == null || !toSendListItems.Contains(item))
                return;
            ExitQuickSendKeyboardNavigation();
            CloseQuickSendItemSettings();
            WaitRuntimeFilesReady();
            quickSendSettingsAnchor = anchor;
            QuickSendItemSettingsEditor.SetItem(item);
            QuickSendItemSettingsPopup.PlacementTarget = anchor;
            QuickSendItemSettingsPopup.IsOpen = true;
        }

        private void QuickSendItemSettingsPopup_Opened(object sender, EventArgs e)
        {
            var anchor = quickSendSettingsAnchor;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (QuickSendItemSettingsPopup.IsOpen && ReferenceEquals(anchor, quickSendSettingsAnchor))
                    (QuickSendItemSettingsEditor.FindName("HexCheckBox") as CheckBox)?.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void QuickSendItemSettingsPopup_Closed(object sender, EventArgs e)
        {
            QuickSendItemSettingsEditor.SetItem(null);
            quickSendSettingsAnchor = null;
        }

        private void CloseQuickSendItemSettings(bool restoreFocus = false)
        {
            if (QuickSendItemSettingsPopup == null || QuickSendItemSettingsEditor == null)
                return;
            var anchor = quickSendSettingsAnchor;
            QuickSendItemSettingsPopup.IsOpen = false;
            QuickSendItemSettingsEditor.SetItem(null);
            quickSendSettingsAnchor = null;
            if (restoreFocus && anchor?.IsVisible == true && !windowIsClosing)
                anchor.Focus();
        }

        private void QuickSendItemSettingsEditor_CloseRequested(object sender, EventArgs e)
        {
            CloseQuickSendItemSettings(restoreFocus: true);
        }

        private void QuickSendItemSettingsEditor_DeleteRequested(object sender, EventArgs e)
        {
            var item = QuickSendItemSettingsEditor.Item;
            CloseQuickSendItemSettings();
            RemoveQuickSendItem(item);
        }

        private void QuickSendItemSettingsEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape &&
                !(QuickSendItemSettingsEditor.FindName("ScriptComboBox") as ComboBox).IsDropDownOpen)
            {
                CloseQuickSendItemSettings(restoreFocus: true);
                e.Handled = true;
            }
        }

        private void QuickSendRowSettingsButton_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (!(sender is FrameworkElement button) || !(button.Tag is ToSendData item))
                return;
            var parts = new List<string>
            {
                item.hex ? "HEX" : (TryFindResource("QuickSendSettingsTextMode") as string ?? "Text"),
                item.appendCrlf ? "CRLF" : (TryFindResource("QuickSendSettingsNoCrlf") as string ?? "No CRLF")
            };
            if (item.disableSuggestion)
                parts.Add(TryFindResource("QuickSendSettingsExclude") as string ?? "Excluded from suggestions");
            if (!string.IsNullOrWhiteSpace(item.recvScriptPath))
                parts.Add((TryFindResource("QuickSendSettingsScript") as string ?? "Receive script") + ": " + item.recvScriptPath);
            if (!string.IsNullOrWhiteSpace(item.recvScriptPara))
                parts.Add(TryFindResource("QuickSendSettingsHasParameters") as string ?? "Parameters configured");
            button.ToolTip = string.Format(TryFindResource("QuickSendSettingsTitle") as string ?? "Command {0} settings", item.id) +
                Environment.NewLine + string.Join(" · ", parts);
        }
    }
}
