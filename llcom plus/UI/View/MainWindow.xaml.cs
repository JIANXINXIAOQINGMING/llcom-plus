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

        public MainWindow()
        {
            StartupProfiler.Mark("MainWindow ctor enter");
            StartupProfiler.Measure("MainWindow.InitializeComponent", InitializeComponent);
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
        private bool forcusClosePort = true;
        private bool canSaveSendList = true;
        private bool isOpeningPort = false;
        private bool applyingSendSuggestion = false;
        private bool lazyLoadReady = false;
        private bool scriptEditorInitialized = false;
        private Task runtimeFilesTask = null;
        private readonly object sessionSendStringLock = new object();
        private readonly Queue<string> sessionSendStringOverrides = new Queue<string>();
        private readonly object receiveScriptContextLock = new object();
        private ReceiveScriptContext currentReceiveScriptContext = new ReceiveScriptContext();
        private readonly List<ToolModule> toolModules = new List<ToolModule>();
        private bool toolsInitialized = false;
        private bool rightToolsCollapsed = false;
        private double expandedRightToolsWidth = ExpandedRightToolsDefaultWidth;
        private double mainGridLayoutWidth;
        private Pages.MultiPortPage mainSplitPortPage = null;
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
        private string lastMainSendTargetDisplayName = string.Empty;
        private bool windowIsClosing;
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

        private sealed class AppNotificationItem
        {
            public DateTime Timestamp { get; set; }
            public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            public string Title { get; set; }
            public string Message { get; set; }
            public AppNotificationLevel Level { get; set; }
            public AppNotificationCategory Category { get; set; }
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
                        Tools.Global.uart.UartDataRawSent += Uart_UartDataRawSent;
                        Tools.Global.SendRawDataRequest += Global_SendRawDataRequest;
                        Tools.Global.SendDataRequest += Global_SendDataRequest;
                        Tools.Global.MainSendTargetChangedEvent += Global_MainSendTargetChangedEvent;
                        Tools.Global.ThemeChanged += Global_ThemeChanged;
                        Tools.Global.UartPortClosedEvent += Global_UartPortClosedEvent;
                        Tools.Global.SerialSplitScreenChangedEvent += Global_SerialSplitScreenChangedEvent;
                        Tools.Global.SerialPinStatusChangedEvent += Global_SerialPinStatusChangedEvent;
                        Tools.Global.AppNotificationEvent += Global_AppNotificationEvent;
                        Tools.Global.IsActiveSerialTargetOpenRequest = IsActiveSerialTargetOpenForTools;
                        Tools.Global.EnsureActiveSerialTargetOpenRequest = EnsureActiveSerialTargetOpenForTools;
                        Tools.Global.SendRawDataToActiveTargetRequest = SendRawDataToActiveTargetForTools;
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
                        ToSendData.DataChanged += SaveSendList;
                        LoadQuickSendList();
                        canSaveSendList = true;
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

            if (MainTabControl.SelectedItem == ScriptTab)
                EnsureScriptEditorInitialized();
            else if (MainTabControl.SelectedItem == ToolsTab)
                EnsureToolModulesInitialized();
            else if (MainTabControl.SelectedItem == AboutTab)
                NavigateFrameOnce(aboutFrame, "UI/Pages/AboutPage.xaml");
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

        private int GetSerialSplitScreenCount()
        {
            return Math.Max(1, Math.Min(4, Tools.Global.setting?.serialSplitScreenCount ?? 1));
        }

        private bool IsSerialSplitModeRequested()
        {
            return GetSerialSplitScreenCount() > 1;
        }

        private bool IsSerialSplitModeActive()
        {
            return mainSplitPortPage != null && appliedSerialSplitScreenCount > 1;
        }

        private void ApplySerialSplitLayout()
        {
            if (dataShowFrame == null || serialSplitSendTargetPanel == null)
                return;

            var count = GetSerialSplitScreenCount();
            UpdateMainPaneMinimum();
            RefreshSerialSplitTargetSelector(count);

            if (count <= 1)
            {
                if (appliedSerialSplitScreenCount == 1 && dataShowFrame.Content is Pages.DataShowPage)
                {
                    serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                    serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
                    SetMainSerialControlsEnabled(true);
                    return;
                }

                serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
                if (mainSplitPortPage != null)
                {
                    mainSplitPortPage.ActiveSlotChanged -= MainSplitPortPage_ActiveSlotChanged;
                    // 必须在切换回单屏页面之前同步关闭并 Dispose 所有分屏串口，
                    // 不能再依赖旧页面稍后触发的 Unloaded。
                    mainSplitPortPage.ReleaseAllPortsForLayoutChange();
                }
                mainSplitPortPage = null;
                appliedSerialSplitScreenCount = 1;
                if (!(dataShowFrame.Content is Pages.DataShowPage))
                    dataShowFrame.Navigate(new Uri("UI/Pages/DataShowPage.xaml", UriKind.Relative));
                SetMainSerialControlsEnabled(true);
                UpdateMainSerialConnectionStatus();
                return;
            }

            serialSplitSendTargetPanel.Visibility = Visibility.Visible;
            serialSplitSendTargetComboBox.IsEnabled = ShouldEnableSendTargetSelector(count);
            var initialFirstPortName = mainSplitPortPage?.GetSlotPortName(1);
            if (string.IsNullOrWhiteSpace(initialFirstPortName))
                initialFirstPortName = GetSelectedPortName();

            // 分屏串口全部使用独立 SerialPort。进入分屏前必须释放主大屏的
            // Global.uart，否则它仍会占用旧端口并把收发事件错误投到窗口 1。
            if (!ReleaseMainSerialPortBeforeSplit())
                return;

            if (mainSplitPortPage != null && appliedSerialSplitScreenCount != count)
            {
                mainSplitPortPage.ActiveSlotChanged -= MainSplitPortPage_ActiveSlotChanged;
                mainSplitPortPage.ReleaseAllPortsForLayoutChange();
                mainSplitPortPage = null;
            }

            if (mainSplitPortPage == null || appliedSerialSplitScreenCount != count)
            {
                mainSplitPortPage = new Pages.MultiPortPage(count, false, initialFirstPortName);
                mainSplitPortPage.ActiveSlotChanged += MainSplitPortPage_ActiveSlotChanged;
                dataShowFrame.Navigate(mainSplitPortPage);
                appliedSerialSplitScreenCount = count;
            }

            if (!IsMainSendTargetSelected())
                mainSplitPortPage.SetActiveSlot(GetSelectedSerialSplitSlot());
            UpdateSelectedSplitSlotControls();
        }

        private bool ReleaseMainSerialPortBeforeSplit()
        {
            if (!Tools.Global.uart.IsOpen())
                return true;

            try
            {
                CloseMainSerialPortForSwitch();
                return true;
            }
            catch (Exception ex)
            {
                Tools.Logger.AddUartLogDebug($"[SplitMode]release main uart failed:{ex}");
                ShowOpenPortFailed(ex.Message);
                return false;
            }
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
            if (serialPortsListComboBox != null)
                // 端口下拉框始终保持可选。选中端口只是记录“待使用端口”，
                // 真正的关闭旧端口/打开新端口由发送或状态按钮触发。
                serialPortsListComboBox.IsEnabled = serialPortsListComboBox.Items.Count > 0;
            if (connectionStatusButton != null)
                connectionStatusButton.IsEnabled = enabled &&
                    (serialPortsListComboBox.Items.Count > 0 || Tools.Global.uart.IsOpen());
            if (baudRateComboBox != null)
                baudRateComboBox.IsEnabled = enabled;
            if (FlowControlButton != null)
                FlowControlButton.IsEnabled = enabled;
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

                serialPortsListComboBox.IsEnabled = serialPortsListComboBox.Items.Count > 0;
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
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(new Func<bool>(IsActiveSerialTargetOpenForTools));

            if (IsSerialSplitModeActive())
            {
                if (mainSplitPortPage == null)
                    return false;
                if (IsAllSerialTargetsSelected())
                    return Enumerable.Range(1, mainSplitPortPage.SlotCount)
                        .All(mainSplitPortPage.IsSlotSelectedPortOpen);
                return mainSplitPortPage.IsSlotSelectedPortOpen(GetSelectedSerialSplitSlot());
            }

            return IsSelectedMainSerialPortOpen();
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

            if (Tools.Global.uart.IsOpen() && !IsSelectedMainSerialPortOpen())
            {
                try
                {
                    CloseMainSerialPortForSwitch();
                }
                catch (Exception ex)
                {
                    ShowOpenPortFailed(ex.Message);
                    return false;
                }
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
                serialPortsListComboBox.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                connectionStatusButton.IsEnabled = true;
                UpdateMainSerialConnectionStatus();
                AddSerialConnectionNotification(port, reconnected: false);
                return true;
            }
            catch (Exception ex)
            {
                Tools.Logger.AddUartLogDebug($"[OpenSelectedPortBlocking]open error:{ex}");
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

            if (Dispatcher.CheckAccess())
            {
                if (IsSerialSplitModeActive())
                {
                    if (mainSplitPortPage == null)
                        return false;
                    if (IsAllSerialTargetsSelected())
                    {
                        var allSent = true;
                        for (var broadcastSlot = 1; broadcastSlot <= mainSplitPortPage.SlotCount; broadcastSlot++)
                        {
                            var broadcastDisplayAsHex = mainSplitPortPage.IsSlotHexMode(broadcastSlot);
                            allSent = mainSplitPortPage.SendBytesBlocking(
                                broadcastSlot,
                                data,
                                broadcastDisplayAsHex,
                                token) && allSent;
                        }
                        return allSent;
                    }

                    var slot = GetSelectedSerialSplitSlot();
                    var displayAsHex = mainSplitPortPage?.IsSlotHexMode(slot) ?? Tools.Global.setting.hexSend;
                    return mainSplitPortPage?.SendBytesBlocking(slot, data, displayAsHex, token) == true;
                }

                if (!IsSelectedMainSerialPortOpen())
                    return false;

                Tools.Global.uart.SendDataCancelable(data, token, null, raiseEvents: false);
                return true;
            }

            Pages.MultiPortPage page = null;
            var targetSlot = 1;
            var splitMode = false;
            var splitDisplayAsHex = false;
            var allSerialTargets = false;
            var allSplitTargets = new List<Tuple<int, bool>>();
            var mainReady = false;
            Dispatcher.Invoke(new Action(() =>
            {
                splitMode = IsSerialSplitModeActive();
                page = mainSplitPortPage;
                targetSlot = GetSelectedSerialSplitSlot();
                splitDisplayAsHex = page?.IsSlotHexMode(targetSlot) ?? Tools.Global.setting.hexSend;
                allSerialTargets = IsAllSerialTargetsSelected();
                if (splitMode && allSerialTargets && page != null)
                {
                    for (var slot = 1; slot <= page.SlotCount; slot++)
                        allSplitTargets.Add(Tuple.Create(slot, page.IsSlotHexMode(slot)));
                }
                mainReady = IsSelectedMainSerialPortOpen();
            }));

            if (splitMode)
            {
                if (allSerialTargets)
                {
                    var allSent = true;
                    foreach (var target in allSplitTargets)
                        allSent = page?.SendBytesBlocking(target.Item1, data, target.Item2, token) == true && allSent;
                    return allSent;
                }
                return page?.SendBytesBlocking(targetSlot, data, splitDisplayAsHex, token) == true;
            }

            if (!mainReady)
                return false;

            Tools.Global.uart.SendDataCancelable(data, token, null, raiseEvents: false);
            return true;
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
                        item.appendCrlf = false;
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
                DeleteQuickSendPageButton.IsEnabled = Global.setting.GetQuickSendListCount() > 1;
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
            Tools.Logger.ShowData(sender as byte[], true, DequeueSessionSendStringOverride());
        }

        private string RawSentTitle = null;
        private void Uart_UartDataRawSent(object sender, EventArgs e)
        {
            if(RawSentTitle is null)
                RawSentTitle = TryFindResource("RawDataSentTitle") as string ?? "?!";
            Tools.Logger.ShowRawData(RawSentTitle, sender as byte[], true);
        }

        private void Uart_UartDataRecived(object sender, EventArgs e)
        {
            var data = sender as byte[];
            Tools.Logger.ShowData(data, false, null, GetReceiveScriptContext());
            if (!IsSerialSplitModeActive() || GetSelectedSerialSplitSlot() == 1)
                Tools.Global.NotifyActiveSerialTargetReceived(data);
        }

        private void Global_SendRawDataRequest(byte[] data)
        {
            Dispatcher.Invoke(new Action(delegate
            {
                SetReceiveScriptContext(recvScriptBackup, "", data);
                sendUartData(data, true, false);
            }));
        }

        private void Global_SendDataRequest(Tools.UartSendRequest request)
        {
            if (request?.Data == null)
                return;

            Dispatcher.Invoke(new Action(delegate
            {
                SetReceiveScriptContext(recvScriptBackup, "", request.Data);
                sendUartData(
                    request.Data,
                    request.IsHex,
                    request.ApplySendProcessing,
                    request.SessionStringLogOverride);
            }));
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
                statusTextBlock.Text = TryFindResource("OpenPort_close") as string ?? "?!";
                refreshPortList(string.IsNullOrWhiteSpace(portName) ? null : portName);
            }));
        }

        private void EnqueueSessionSendStringOverride(string value)
        {
            lock (sessionSendStringLock)
                sessionSendStringOverrides.Enqueue(value);
        }

        private string DequeueSessionSendStringOverride()
        {
            lock (sessionSendStringLock)
                return sessionSendStringOverrides.Count > 0 ? sessionSendStringOverrides.Dequeue() : null;
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

            // 这里只更新待使用端口及其配置，不触碰当前已打开的串口。
            ApplySelectedUartProfile();
            UpdateMainSerialConnectionStatus();
        }

        private bool IsSelectedMainSerialPortOpen()
        {
            var selectedPort = GetSelectedPortName();
            return Tools.Global.uart.IsOpen() &&
                !string.IsNullOrWhiteSpace(selectedPort) &&
                string.Equals(Tools.Global.uart.GetName(), selectedPort, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateMainSerialConnectionStatus()
        {
            if (statusTextBlock == null || IsSerialSplitModeActive())
                return;

            statusTextBlock.Text = TryFindResource(
                IsSelectedMainSerialPortOpen() ? "OpenPort_open" : "OpenPort_close") as string ?? "?!";
            if (connectionStatusButton != null)
                connectionStatusButton.IsEnabled = serialPortsListComboBox.Items.Count > 0 || Tools.Global.uart.IsOpen();
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
                statusTextBlock.Text = TryFindResource("OpenPort_close") as string ?? "?!";
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
                        foreach (string p in SerialPort.GetPortNames())//加上缺少的com口
                        {
                            //有些人遇到了微软库的bug，所以需要手动从0x00截断
                            var pp = p;
                            if (p.IndexOf("\0") > 0)
                                pp = p.Substring(0, p.IndexOf("\0"));
                            bool notMatch = true;
                            foreach (string n in strs)
                            {
                                if (n.Contains($"({pp})"))//如果和选中项目匹配
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
                        ApplyUartProfileForPort(ExtractPortName(selectedItem));
                    }
                    else
                    {
                        serialPortsListComboBox.IsEnabled = false;
                        connectionStatusButton.IsEnabled = Tools.Global.uart.IsOpen();
                    }
                    refreshLock = false;

                    if (!string.IsNullOrWhiteSpace(preferredPort) &&
                        selectedItem != null &&
                        selectedItem.Contains($"({preferredPort})") &&
                        !forcusClosePort &&
                        Tools.Global.setting.autoReconnect &&
                        !isOpeningPort)
                    {
                        Task.Run(() =>
                        {
                            isOpeningPort = true;
                            try
                            {
                                Tools.Global.uart.Open();
                                Tools.Logger.StartSessionLog(Tools.Global.uart.GetName());
                                Dispatcher.Invoke(new Action(delegate
                                {
                                    serialPortsListComboBox.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                                    connectionStatusButton.IsEnabled = true;
                                    statusTextBlock.Text = (TryFindResource("OpenPort_open") as string ?? "?!");
                                    AddSerialConnectionNotification(Tools.Global.uart.GetName(), reconnected: true);
                                }));
                            }
                            catch (Exception ex)
                            {
                                Tools.Logger.AddUartLogDebug($"[autoReconnect]open error:{ex}");
                                ShowOpenPortFailed(ex.Message);
                            }
                            isOpeningPort = false;
                        });
                    }
                })));
                StartupProfiler.Mark($"refreshPortList worker exit, ports={strs.Count}");
            });
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
                statusTextBlock.Text = (TryFindResource("OpenPort_close") as string ?? "?!");
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
                statusTextBlock.Text = TryFindResource("OpenPort_close") as string ?? "?!";
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
            ClearPendingSendAfterOpenFailure();
            sendUartData(
                data,
                isHex,
                applySendProcessing,
                sessionStringLogOverride,
                extraEnterOverride);
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
            ApplySelectedUartProfile();
            if (serialPortsListComboBox.SelectedItem == null)
            {
                Tools.Logger.AddUartLogDebug("[openPort]no selected port");
                ShowOpenPortFailed("未选择串口。");
                return;
            }

            if (Tools.Global.uart.IsOpen())
            {
                if (IsSelectedMainSerialPortOpen())
                {
                    UpdateMainSerialConnectionStatus();
                    return;
                }

                try
                {
                    CloseMainSerialPortForSwitch();
                }
                catch (Exception ex)
                {
                    ShowOpenPortFailed(ex.Message);
                    return;
                }
            }

            isOpeningPort = true;
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
                if ((serialPortsListComboBox.SelectedItem as string).Contains($"({pp})"))//如果和选中项目匹配
                {
                    port = pp;
                    break;
                }
            }
            Tools.Logger.AddUartLogDebug($"[openPort]PortName:{port},isOpeningPort:{isOpeningPort}");
            if (port == "")
            {
                isOpeningPort = false;
                ShowOpenPortFailed("当前选择的串口不在系统串口列表中，请刷新串口后重试。");
                return;
            }

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
                        serialPortsListComboBox.IsEnabled = serialPortsListComboBox.Items.Count > 0;
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
                    statusTextBlock.Text = (TryFindResource("OpenPort_close") as string ?? "?!");
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
                category: AppNotificationCategory.Connection);
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
        private void sendUartData(byte[] data, bool? is_hex = null, bool applySendProcessing = true, string sessionStringLogOverride = null, bool? extraEnterOverride = null)
        {
            if (data == null)
                return;

            if (IsSerialSplitModeActive())
            {
                if (IsAllSerialTargetsSelected())
                {
                    _ = SendToAllSplitSlotsAsync(
                        data,
                        is_hex,
                        applySendProcessing,
                        extraEnterOverride);
                    return;
                }

                var targetSlot = GetSelectedSerialSplitSlot();
                var targetHexMode = mainSplitPortPage?.IsSlotHexMode(targetSlot) ?? Tools.Global.setting.hexSend;
                var displayAsHex = is_hex ?? targetHexMode;
                var splitData = PrepareUartSendData(data, is_hex, applySendProcessing, targetHexMode, extraEnterOverride);
                if (splitData == null || splitData.Length == 0)
                    return;

                _ = SendToSelectedSplitSlotAsync(splitData, displayAsHex);
                return;
            }

            if (!IsSelectedMainSerialPortOpen())
            {
                toSendData = (byte[])data.Clone();//带发送数据缓存起来，连上串口后发出去
                toSendDataIsHex = is_hex;
                toSendDataApplySendProcessing = applySendProcessing;
                toSendDataSessionStringLogOverride = sessionStringLogOverride;
                toSendDataExtraEnterOverride = extraEnterOverride;
                openPort();
                return;
            }

            if (Tools.Global.uart.IsOpen())
            {
                byte[] dataConvert = PrepareUartSendData(data, is_hex, applySendProcessing, null, extraEnterOverride);
                if (dataConvert == null)
                    return;

                if (dataConvert.Length == 0)
                    return;

                var overrideQueued = false;
                try
                {
                    if (sessionStringLogOverride != null && Tools.Global.setting.showSend)
                    {
                        EnqueueSessionSendStringOverride(sessionStringLogOverride);
                        overrideQueued = true;
                    }
                    Tools.Global.uart.SendData(dataConvert, applySendProcessing ? data : null);
                }
                catch(Exception ex)
                {
                    if (overrideQueued)
                        DequeueSessionSendStringOverride();
                    Tools.MessageBox.Show($"{TryFindResource("ErrorSendFail") as string ?? "?!"}\r\n"+ ex.ToString());
                    return;
                }
            }
        }

        private byte[] PrepareUartSendData(byte[] data, bool? isHex, bool applySendProcessing, bool? defaultHexSend = null, bool? extraEnterOverride = null)
        {
            byte[] dataConvert = data;
            if (!applySendProcessing)
                return dataConvert;

            try
            {
                WaitRuntimeFilesReady();
                dataConvert = ScriptEnv.JavaScriptLoader.Run(
                    $"{Tools.Global.setting.sendScript}.js",
                    new System.Collections.ArrayList
                    {
                        "uartData",
                        isHex == null ?
                        ((defaultHexSend ?? Tools.Global.setting.hexSend) ? Tools.Global.Hex2Byte(Tools.Global.Byte2String(data)) : data) : data
                    });
            }
            catch (Exception ex)
            {
                Tools.MessageBox.Show($"{TryFindResource("ErrorScript") as string ?? "?!"}\r\n" + ex.ToString());
                return null;
            }

            if (dataConvert == null)
                return null;

            return AppendCrlf(dataConvert, extraEnterOverride ?? Tools.Global.setting.extraEnter);
        }

        private async Task SendToAllSplitSlotsAsync(
            byte[] sourceData,
            bool? isHex,
            bool applySendProcessing,
            bool? extraEnterOverride)
        {
            try
            {
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();
                var page = mainSplitPortPage;
                if (page == null)
                    return;

                var preparedTargets = new List<Tuple<int, byte[], bool>>();
                var failures = new List<string>();
                for (var slot = 1; slot <= page.SlotCount; slot++)
                {
                    var targetHexMode = page.IsSlotHexMode(slot);
                    var displayAsHex = isHex ?? targetHexMode;
                    var data = PrepareUartSendData(
                        sourceData,
                        isHex,
                        applySendProcessing,
                        targetHexMode,
                        extraEnterOverride);
                    if (data == null || data.Length == 0)
                        continue;

                    if (!page.EnsureSlotOpen(slot))
                    {
                        failures.Add(FormatSplitTargetFailure(slot, page.GetSlotLastError(slot)));
                        continue;
                    }

                    preparedTargets.Add(Tuple.Create(slot, data, displayAsHex));
                }

                var pendingSends = preparedTargets
                    .Select(target => Tuple.Create(
                        target.Item1,
                        page.SendBytesAsync(target.Item1, target.Item2, target.Item3)))
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
                ShowSplitBroadcastFailures(failures);
            }
            catch (Exception ex)
            {
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

        private async Task SendToSelectedSplitSlotAsync(byte[] data, bool displayAsHex, bool autoOpen = true)
        {
            try
            {
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();

                if (mainSplitPortPage == null)
                    return;

                var slot = GetSelectedSerialSplitSlot();
                if (autoOpen)
                {
                    if (!mainSplitPortPage.EnsureSlotOpen(slot))
                    {
                        UpdateSelectedSplitSlotControls();
                        var detail = mainSplitPortPage.GetSlotLastError(slot);
                        var message = TryFindResource("ErrorOpenPort") as string ?? "串口打开失败！";
                        if (!string.IsNullOrWhiteSpace(detail))
                            message += "\r\n" + detail;
                        Tools.MessageBox.Show(message);
                        return;
                    }
                    UpdateSelectedSplitSlotControls();
                }
                else if (!mainSplitPortPage.IsSlotSelectedPortOpen(slot))
                {
                    UpdateSelectedSplitSlotControls();
                    return;
                }

                await mainSplitPortPage.SendBytesAsync(slot, data, displayAsHex);
            }
            catch (Exception ex)
            {
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFail") as string ?? "?!"}\r\n" + ex.ToString());
            }
        }

        private async Task SendPreparedDataToSplitTargetsAsync(
            byte[] data,
            bool displayAsHex,
            bool autoOpen)
        {
            if (!IsAllSerialTargetsSelected())
            {
                await SendToSelectedSplitSlotAsync(data, displayAsHex, autoOpen);
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
                .Select(slot => page.SendBytesAsync(slot, data, displayAsHex))
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

            var data = Global.GetEncoding().GetBytes(toSendDataTextBox.Text);
            SetReceiveScriptContext(recvScriptBackup, "", data);
            sendUartData(data, null, true, Tools.Global.setting.hexSend ? toSendDataTextBox.Text : null);
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
            toSendListItems.Add(CreateBlankQuickSendItem(toSendListItems.Count + 1));
            SaveSendList(null, EventArgs.Empty);
        }

        private void RemoveSendListItemButton_Click(object sender, RoutedEventArgs e)
        {
            var item = ((Button)sender).Tag as ToSendData;
            if (item == null)
                return;

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
                    appendCrlf = false,
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
                item.appendCrlf = false;
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
                    item.appendCrlf ||
                    !string.IsNullOrWhiteSpace(item.recvScriptPath) ||
                    !string.IsNullOrWhiteSpace(item.recvScriptPara));
        }

        private void knowSendDataButton_click(object sender, RoutedEventArgs e)
        {
            SendQuickSendItem(((Button)sender).Tag as ToSendData);
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

            // 如果有指定接收脚本，则切换
            if (!string.IsNullOrEmpty(data.recvScriptPath))
            {
                //检查文件是否存在
                if (!File.Exists(Tools.Global.ProfilePath + $"user_script_recv_convert/{data.recvScriptPath}.js"))
                {
                    data.recvScriptPath = "";
                    if (!File.Exists(Tools.Global.ProfilePath + "user_script_recv_convert/default.js"))
                    {
                        File.Create(Tools.Global.ProfilePath + "user_script_recv_convert/default.js").Close();
                    }
                }
                else
                {
                    receiveScriptName = data.recvScriptPath;
                }
            }

            SetReceiveScriptContext(receiveScriptName, data.recvScriptPara ?? "", sendData);
            sendUartData(sendData, true, true, data.hex ? data.text : null, data.appendCrlf);
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
            if (string.IsNullOrWhiteSpace(newScriptFileNameTextBox.Text))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptNoName") as string ?? "?!");
                return;
            }
            if (File.Exists(Tools.Global.ProfilePath + $"user_script_run/{newScriptFileNameTextBox.Text}.js"))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptExist") as string ?? "?!");
                return;
            }

            try
            {
                File.Create(Tools.Global.ProfilePath + $"user_script_run/{newScriptFileNameTextBox.Text}.js").Close();
                loadScriptFile(newScriptFileNameTextBox.Text);
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
            var darkMode = Tools.Global.setting?.darkMode ?? Tools.Global.IsDarkTheme;
            Tools.Win32.ApplyWindowTheme(this, darkMode, false);
        }
        private void Window_Deactivated(object sender, EventArgs e)
        {
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

        private void RefreshPortButton_Click(object sender, RoutedEventArgs e)
        {
            refreshPortList();
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

            AddNotification(
                snapshot.Timestamp,
                title,
                message,
                AppNotificationLevel.Info,
                AppNotificationCategory.SerialPin);
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

            AddNotification(
                notification.Timestamp,
                notification.Title,
                notification.Message,
                notification.Level,
                notification.Category);
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

            AddNotification(
                DateTime.Now,
                string.Format(
                    TryFindResource("NotificationConnectedTitleFormat") as string ?? "{0} 已连接",
                    displayName),
                baudMessage,
                AppNotificationLevel.Success,
                AppNotificationCategory.Connection);
        }

        private void AddSerialDisconnectedNotification(string portName)
        {
            var displayName = string.IsNullOrWhiteSpace(portName)
                ? (TryFindResource("SerialPinUnknownPort") as string ?? "串口")
                : portName;
            AddNotification(
                DateTime.Now,
                string.Format(
                    TryFindResource("NotificationDisconnectedTitleFormat") as string ?? "{0} 已断开",
                    displayName),
                string.Empty,
                AppNotificationLevel.Info,
                AppNotificationCategory.Connection);
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
            notificationItems.Insert(0, new AppNotificationItem
            {
                Timestamp = timestamp == default(DateTime) ? DateTime.Now : timestamp,
                Title = title ?? string.Empty,
                Message = message ?? string.Empty,
                Level = level,
                Category = category,
                IndicatorBrush = GetNotificationIndicatorBrush(level)
            });

            while (notificationItems.Count > MaxNotificationItems)
                notificationItems.RemoveAt(notificationItems.Count - 1);

            if (NotificationPopup?.IsOpen != true)
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

        private void NotificationCenterButton_Click(object sender, RoutedEventArgs e)
        {
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
            if (select == Global.setting.quickSendSelect)
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
            SaveSendList(null, EventArgs.Empty);
            canSaveSendList = false;
            toSendListItems.Clear();
            Global.setting.AddQuickSendPage();
            LoadQuickSendList();
            canSaveSendList = true;
            QuickListNameTextBox.Focus();
        }

        private void DeleteQuickSendPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (Global.setting.GetQuickSendListCount() <= 1)
            {
                Tools.MessageBox.Show(TryFindResource("QuickSendDeletePageBlocked") as string ?? "?!");
                return;
            }

            if (toSendListItems.Count(HasQuickSendContent) > 1)
            {
                var ret = Tools.InputDialog.OpenDialog(
                    TryFindResource("QuickSendDeletePageConfirmMsg") as string ?? "?!",
                    "",
                    TryFindResource("DeleteConfirmation") as string ?? "?!");
                if (!ret.Item1 || ret.Item2 != "YES")
                    return;
            }

            canSaveSendList = false;
            toSendListItems.Clear();
            Global.setting.RemoveQuickSendPage(Global.setting.quickSendSelect);
            LoadQuickSendList();
            canSaveSendList = true;
        }

        private void QuickSendImportButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.OpenFileDialog OpenFileDialog = new System.Windows.Forms.OpenFileDialog();
            OpenFileDialog.Filter = TryFindResource("QuickSendLlcomPlusFile") as string ?? "?!";
            if (OpenFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                JToken token = null;
                try
                {
                    token = JToken.Parse(File.ReadAllText(OpenFileDialog.FileName));
                    if (token == null)
                        throw new Exception(TryFindResource("QuickSendLoadError") as string ?? "?!");
                }
                catch (Exception err)
                {
                    Tools.MessageBox.Show(err.Message);
                    return;
                }

                var allQuickSendToken = GetAllQuickSendListToken(token);
                if (allQuickSendToken != null)
                {
                    var allData = allQuickSendToken.ToObject<List<List<ToSendData>>>();
                    if (allData == null)
                    {
                        Tools.MessageBox.Show(TryFindResource("QuickSendLoadError") as string ?? "?!");
                        return;
                    }
                    var allNames = (token as JObject)?["quickSendListNames"]?.ToObject<List<string>>();
                    this.Dispatcher.Invoke(new Action(delegate
                    {
                        canSaveSendList = false;
                        Tools.Global.setting.SetAllQuickSendLists(allData);
                        Tools.Global.setting.SetAllQuickListNames(allNames);
                        toSendListItems.Clear();
                        LoadQuickSendList();
                        canSaveSendList = true;
                        Tools.MessageBox.Show(TryFindResource("QuickSendImportAllDone") as string ?? "?!");
                    }));
                    return;
                }

                List<ToSendData> data = null;
                try
                {
                    data = token.ToObject<List<ToSendData>>();
                    if (data == null)
                        throw new Exception(TryFindResource("QuickSendLoadError") as string ?? "?!");
                }
                catch (Exception err)
                {
                    Tools.MessageBox.Show(err.Message);
                    return;
                }

                this.Dispatcher.Invoke(new Action(delegate
                {
                    canSaveSendList = false;
                    toSendListItems.Clear();
                    foreach(var d in data)
                    {
                        toSendListItems.Add(d);
                    }
                    canSaveSendList = true;
                    SaveSendList(0, EventArgs.Empty);//保存并刷新数据列表
                }));
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
                    false,
                    autoOpen: false);
                e.Handled = true;
                return;
            }
            if (IsSelectedMainSerialPortOpen())
                try
                {
                    Tools.Global.uart.SendData(Encoding.ASCII.GetBytes(e.TextComposition.Text));
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
                        false,
                        autoOpen: false);
                    e.Handled = true;
                    return;
                }
                catch { }

            if (e.Key >= Key.A && e.Key <= Key.Z && IsSelectedMainSerialPortOpen())
                try
                {
                    Tools.Global.uart.SendData(new byte[] { (byte)((int)e.Key - (int)Key.A + 1) });
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

        private void ScriptIcon_Click(object sender, MouseButtonEventArgs e)
        {
            WaitRuntimeFilesReady();
            // 点击📜图标时配置接收脚本
            TextBlock icon = sender as TextBlock;
            ToSendData data = icon.Tag as ToSendData;
            recvScriptCombo.ItemsSource = Directory.GetFiles(Global.ProfilePath + "user_script_recv_convert", "*.js")
                                                   .Select(System.IO.Path.GetFileNameWithoutExtension).ToList();
            recvScriptPopup.PlacementTarget = icon;
            recvScriptCombo.Tag = data;
            recvScriptCombo.SelectedItem = data.recvScriptPath ?? "";
            recvScriptCombo.IsDropDownOpen = true;
            recvScriptPopup.IsOpen = false;
            recvScriptPopup.IsOpen = true;

            // 打开对话框，选择接收脚本
            //System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog();
            //dialog.Filter = "Lua脚本文件 (*.js)|*.js|所有文件 (*.*)|*.*";
            //dialog.InitialDirectory = System.IO.Path.Combine(Tools.Global.ProfilePath, "user_script_recv_convert");

            //if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            //{
            //    data.recvScriptPath = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            //    //SaveSendList(null, EventArgs.Empty);
            //}
        }

        private void ScriptIcon_RightClick(object sender, MouseButtonEventArgs e)
        {
            // 右击📜图标时清除接收脚本
            TextBlock icon = sender as TextBlock;
            ToSendData data = icon.Tag as ToSendData;

            // 清除接收脚本项
            if (!string.IsNullOrEmpty(data.recvScriptPath))
            {
                data.recvScriptPath = "";
                //SaveSendList(null, EventArgs.Empty);
            }
        }

        private void recvScriptCombo_DropDownClosed(object sender, EventArgs e)
        {
            ComboBox me = sender as ComboBox;
            ToSendData data = me.Tag as ToSendData;
            string newItem = me.SelectedItem as string;
            if(data.recvScriptPath != newItem) data.recvScriptPath = newItem;
            recvScriptPopup.IsOpen = false;
            me.SelectedItem = null;
        }

        [DllImport("user32")]
        public static extern IntPtr SetFocus(IntPtr hWnd);
        private async void ScriptParaIcon_Click(object sender, MouseButtonEventArgs e)
        {
            TextBlock icon = sender as TextBlock;
            ToSendData data = icon.Tag as ToSendData;

            recvScriptParaBox.Tag = data;
            recvScriptParaBox.Text = data.recvScriptPara;
            recvScriptParaBox.ScrollToEnd();
            recvScriptParaPopup.PlacementTarget = icon;
            recvScriptParaPopup.IsOpen = false;
            await Task.Yield();
            recvScriptParaPopup.IsOpen = true;
            await Task.Yield();
            var source = (HwndSource)PresentationSource.FromVisual(recvScriptParaPopup.Child);
            SetFocus(source.Handle);
            await Task.Yield();
            Keyboard.Focus(recvScriptParaBox);
        }
        private void ScriptParaIcon_RightClick(object sender, MouseButtonEventArgs e)
        {
            TextBlock icon = sender as TextBlock;
            ToSendData data = icon.Tag as ToSendData;

            if (!string.IsNullOrEmpty(data.recvScriptPara))
            {
                data.recvScriptPara = "";
                //SaveSendList(null, EventArgs.Empty);
            }
        }
        private void ScriptParaConfirm_Click(object sender, MouseButtonEventArgs e)
        {
            TextBlock icon = sender as TextBlock;
            TextBox t = icon.Tag as TextBox;
            ToSendData data = t.Tag as ToSendData;

            data.recvScriptPara = t.Text;
            //SaveSendList(null, EventArgs.Empty);
            recvScriptParaPopup.IsOpen = false;
        }
        private void ScriptParaCancel_Click(object sender, MouseButtonEventArgs e)
        {
            recvScriptParaPopup.IsOpen = false;
        }
    }
}
