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
        private const double PreferredWindowWidth = 1660;
        private const double PreferredWindowHeight = 820;
        private const double ExpandedRightToolsMinWidth = 980;
        private const double ExpandedRightToolsDefaultWidth = 1040;

        public MainWindow()
        {
            StartupProfiler.Mark("MainWindow ctor enter");
            StartupProfiler.Measure("MainWindow.InitializeComponent", InitializeComponent);
            StartupProfiler.Measure("Global.LoadSetting", Tools.Global.LoadSetting);
            StartupProfiler.Measure("MainWindow restore placement", () =>
            {
                if (Tools.Global.setting.windowHeight != 0 &&
                    Tools.Global.setting.windowLeft > 0 &&
                    Tools.Global.setting.windowTop > 0 &&
                    Tools.Global.setting.windowTop < SystemParameters.FullPrimaryScreenHeight &&
                    Tools.Global.setting.windowLeft < SystemParameters.FullPrimaryScreenWidth)
                {
                    this.Left = Tools.Global.setting.windowLeft;
                    this.Top = Tools.Global.setting.windowTop;
                    this.Width = Math.Max(Tools.Global.setting.windowWidth, this.MinWidth);
                    this.Height = Math.Max(Tools.Global.setting.windowHeight, this.MinHeight);
                }
                else
                {
                    this.Width = Math.Max(PreferredWindowWidth, this.MinWidth);
                    this.Height = Math.Max(PreferredWindowHeight, this.MinHeight);
                }
            });
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
        private Pages.MultiPortPage mainSplitPortPage = null;
        private int appliedSerialSplitScreenCount = -1;
        private bool syncingSerialSplitControls = false;
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
                        Tools.Global.UartPortClosedEvent += Global_UartPortClosedEvent;
                        Tools.Global.SerialSplitScreenChangedEvent += Global_SerialSplitScreenChangedEvent;
                        Tools.Global.IsActiveSerialTargetOpenRequest = IsActiveSerialTargetOpenForTools;
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
                        this.showHexFormatComboBox.DataContext = Tools.Global.setting;
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
                        this.Title += $" - {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()}";
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
                NavigateFrameOnce(aboutFrame, "Pages/AboutPage.xaml");
        }

        private void EnsureScriptEditorInitialized()
        {
            if (scriptEditorInitialized)
                return;
            scriptEditorInitialized = true;
            WaitRuntimeFilesReady();

            SearchPanel.Install(textEditor.TextArea);

            textEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");

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
            AddFrameTool("EncodingTools", GetResourceText("EncodingToolsTab", "编码转换工具"), "Pages/ConvertPage.xaml");
            AddFrameTool("Mqtt", "MQTT", "Pages/MqttTestPage.xaml");
            AddFrameTool("SerialMonitor", GetResourceText("SerialMonitorHeader", "串口监听"), "Pages/SerialMonitorPage.xaml");
            AddFrameTool("LogReplay", GetResourceText("LogReplayToolTab", "日志回放"), "Pages/LogReplayPage.xaml");
            AddFrameTool("CircularSend", GetResourceText("CircularSendToolTab", "循环发送"), "Pages/CircularSendPage.xaml");
            AddFrameTool("EncodingFix", GetResourceText("EncodingFixHeader", "乱码修复"), "Pages/EncodingFixPage.xaml");
            AddFrameTool("Plot", GetResourceText("PlotHeader", "曲线"), "Pages/PlotPage.xaml");
            AddFrameTool("WinUsb", "WinUSB", "Pages/WinUSBPage.xaml");
            AddContentTool("HttpTool", GetResourceText("HttpToolTab", "HTTP工具"), () => new HttpToolWindow());
            AddContentTool("DataCalc", GetResourceText("DataCalcToolTab", "数据计算/文件发送"), () => new Pages.DataCalcFileSendView());
            AddFrameTool("TcpClient", GetResourceText("TcpClientTitle", "socket客户端"), "Pages/SocketClientPage.xaml");
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
            RefreshSerialSplitTargetSelector(count);

            if (count <= 1)
            {
                if (appliedSerialSplitScreenCount == 1 && dataShowFrame.Content is Pages.DataShowPage)
                {
                    serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                    serialSplitSendTargetComboBox.IsEnabled = false;
                    SetMainSerialControlsEnabled(true);
                    return;
                }

                serialSplitSendTargetPanel.Visibility = Visibility.Visible;
                serialSplitSendTargetComboBox.IsEnabled = false;
                mainSplitPortPage = null;
                appliedSerialSplitScreenCount = 1;
                if (!(dataShowFrame.Content is Pages.DataShowPage))
                    dataShowFrame.Navigate(new Uri("Pages/DataShowPage.xaml", UriKind.Relative));
                SetMainSerialControlsEnabled(true);
                statusTextBlock.Text = Tools.Global.uart.IsOpen()
                    ? (TryFindResource("OpenPort_open") as string ?? "?!")
                    : (TryFindResource("OpenPort_close") as string ?? "?!");
                return;
            }

            serialSplitSendTargetPanel.Visibility = Visibility.Visible;
            serialSplitSendTargetComboBox.IsEnabled = count > 1;
            if (mainSplitPortPage == null || appliedSerialSplitScreenCount != count)
            {
                mainSplitPortPage = new Pages.MultiPortPage(count, false);
                mainSplitPortPage.ActiveSlotChanged += MainSplitPortPage_ActiveSlotChanged;
                dataShowFrame.Navigate(mainSplitPortPage);
                appliedSerialSplitScreenCount = count;
            }

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

            var index = Math.Max(0, Math.Min(slotNumber - 1, serialSplitSendTargetComboBox.Items.Count - 1));
            if (serialSplitSendTargetComboBox.SelectedIndex != index)
                serialSplitSendTargetComboBox.SelectedIndex = index;
            else
                UpdateSelectedSplitSlotControls();
        }

        private void SetMainSerialControlsEnabled(bool enabled)
        {
            if (openClosePortButton != null)
                openClosePortButton.IsEnabled = enabled && serialPortsListComboBox.Items.Count > 0;
            if (serialPortsListComboBox != null)
                serialPortsListComboBox.IsEnabled = enabled && !Tools.Global.uart.IsOpen();
            if (baudRateComboBox != null)
                baudRateComboBox.IsEnabled = enabled;
            if (FlowControlButton != null)
                FlowControlButton.IsEnabled = enabled;
        }

        private void RefreshSerialSplitTargetSelector(int count)
        {
            if (serialSplitSendTargetComboBox == null)
                return;

            var oldIndex = Math.Max(0, serialSplitSendTargetComboBox.SelectedIndex);
            serialSplitSendTargetComboBox.Items.Clear();
            for (int i = 1; i <= Math.Max(1, count); i++)
            {
                serialSplitSendTargetComboBox.Items.Add(string.Format(
                    TryFindResource("SplitSendTargetItem") as string ?? "窗口 {0}",
                    i));
            }

            serialSplitSendTargetComboBox.SelectedIndex = Math.Min(oldIndex, serialSplitSendTargetComboBox.Items.Count - 1);
            serialSplitSendTargetComboBox.IsEnabled = count > 1;
        }

        private int GetSelectedSerialSplitSlot()
        {
            if (serialSplitSendTargetComboBox == null || serialSplitSendTargetComboBox.SelectedIndex < 0)
                return 1;
            return serialSplitSendTargetComboBox.SelectedIndex + 1;
        }

        private void SerialSplitSendTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (mainSplitPortPage == null)
                return;

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
                var slot = GetSelectedSerialSplitSlot();
                var isOpen = mainSplitPortPage.IsSlotOpen(slot);
                var slotPort = mainSplitPortPage.GetSlotPortName(slot);
                SelectSerialPortComboBoxItem(slotPort);
                SetBaudRateComboBoxValue(mainSplitPortPage.GetSlotBaudRate(slot));

                openClosePortButton.IsEnabled = serialPortsListComboBox.Items.Count > 0;
                serialPortsListComboBox.IsEnabled = !isOpen;
                baudRateComboBox.IsEnabled = true;
                FlowControlButton.IsEnabled = false;
                openClosePortTextBlock.Text = TryFindResource(isOpen ? "OpenPort_close" : "OpenPort_open") as string ?? "?!";
                statusTextBlock.Text = string.Format(
                    TryFindResource("SplitModeStatus") as string ?? "分屏模式 {0}",
                    slot);
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
                return mainSplitPortPage?.IsSlotOpen(GetSelectedSerialSplitSlot()) == true;

            return Tools.Global.uart.IsOpen();
        }

        private bool SendRawDataToActiveTargetForTools(byte[] data, CancellationToken token)
        {
            if (data == null || data.Length == 0)
                return false;

            if (Dispatcher.CheckAccess())
            {
                if (IsSerialSplitModeActive())
                {
                    var slot = GetSelectedSerialSplitSlot();
                    var displayAsHex = mainSplitPortPage?.IsSlotHexMode(slot) ?? Tools.Global.setting.hexSend;
                    return mainSplitPortPage?.SendBytesBlocking(slot, data, displayAsHex, token) == true;
                }

                if (!Tools.Global.uart.IsOpen())
                    return false;

                Tools.Global.uart.SendDataCancelable(data, token, null, raiseEvents: false);
                return true;
            }

            Pages.MultiPortPage page = null;
            var targetSlot = 1;
            var splitMode = false;
            var splitDisplayAsHex = false;
            Dispatcher.Invoke(new Action(() =>
            {
                splitMode = IsSerialSplitModeActive();
                page = mainSplitPortPage;
                targetSlot = GetSelectedSerialSplitSlot();
                splitDisplayAsHex = page?.IsSlotHexMode(targetSlot) ?? Tools.Global.setting.hexSend;
            }));

            if (splitMode)
                return page?.SendBytesBlocking(targetSlot, data, splitDisplayAsHex, token) == true;

            if (!Tools.Global.uart.IsOpen())
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
                RightToolsColumn.MinWidth = ExpandedRightToolsMinWidth;
                RightToolsColumn.Width = new GridLength(Math.Max(ExpandedRightToolsMinWidth, expandedRightToolsWidth));
                RightToolsPanel.Visibility = Visibility.Visible;
                RightTopActions.Visibility = Visibility.Visible;
                RightToolsGridSplitter.Visibility = Visibility.Visible;
                RightToolsToggleIcon.Icon = FontAwesomeIcon.AngleDoubleRight;
                RightToolsToggleButton.ToolTip = GetResourceText("CollapseRightTools", "收起右侧工具");
            }

            rightToolsCollapsed = collapsed;
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
                    QuickListNameTextBox.Text = Global.setting.GetQuickListNameNow();
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
                if (string.IsNullOrWhiteSpace(name))
                    name = $"未命名{i}";
                items.Add($"{i + 1}. {name}");
            }
            return items;
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

        private void Global_UartPortClosedEvent(object sender, string portName)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (Tools.Global.isMainWindowsClosed)
                    return;
                if (IsSerialSplitModeRequested())
                {
                    ApplySerialSplitLayout();
                    return;
                }

                Tools.Logger.StopSessionLog();
                openClosePortTextBlock.Text = TryFindResource("OpenPort_open") as string ?? "?!";
                serialPortsListComboBox.IsEnabled = true;
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
            if (syncingSerialSplitControls)
                return;

            if (IsSerialSplitModeActive())
            {
                mainSplitPortPage?.SetSlotPortName(GetSelectedSerialSplitSlot(), GetSelectedPortName());
                UpdateSelectedSplitSlotControls();
                return;
            }

            ApplySelectedUartProfile();
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
                    var preferredPort = string.IsNullOrEmpty(lastPort) ? Tools.Global.uart.GetName() : lastPort;
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
                        openClosePortButton.IsEnabled = true;
                        serialPortsListComboBox.SelectedItem = selectedItem;
                        ApplyUartProfileForPort(ExtractPortName(selectedItem));
                    }
                    else
                    {
                        openClosePortButton.IsEnabled = false;
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
                                    openClosePortTextBlock.Text = (TryFindResource("OpenPort_close") as string ?? "?!");
                                    serialPortsListComboBox.IsEnabled = false;
                                    statusTextBlock.Text = (TryFindResource("OpenPort_open") as string ?? "?!");
                                }));
                            }
                            catch
                            {
                                //MessageBox.Show("串口打开失败！");
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
                openClosePortTextBlock.Text = (TryFindResource("OpenPort_open") as string ?? "?!");
                serialPortsListComboBox.IsEnabled = true;
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
            Tools.Global.setting.windowLeft = this.Left;
            Tools.Global.setting.windowTop = this.Top;
            Tools.Global.setting.windowWidth = this.Width;
            Tools.Global.setting.windowHeight = this.Height;
            //自动保存脚本
            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);
            Tools.Global.isMainWindowsClosed = true;
            foreach (Window win in App.Current.Windows)
            {
                if (win != this)
                {
                    win.Close();
                }
            }
            e.Cancel = false;//正常关闭
        }



        Window settingPage = new SettingWindow();
        private void MoreSettingButton_Click(object sender, RoutedEventArgs e)
        {
            settingPage.Show();
        }

        Window flowControlPage = new FlowControlWindow();
        private void FlowControlButton_Click(object sender, RoutedEventArgs e)
        {
            flowControlPage.Owner = this;
            flowControlPage.Show();
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
        private void openPort()
        {
            Tools.Logger.AddUartLogDebug($"[openPort]{isOpeningPort},{serialPortsListComboBox.SelectedItem}");
            if (IsSerialSplitModeRequested())
                return;
            if (isOpeningPort)
                return;
            ApplySelectedUartProfile();
            if (serialPortsListComboBox.SelectedItem != null)
            {
                string[] ports;//获取所有串口列表
                try
                {
                    Tools.Logger.AddUartLogDebug($"[openPort]GetPortNames");
                    ports = SerialPort.GetPortNames();
                    Tools.Logger.AddUartLogDebug($"[openPort]GetPortNames{ports.Length}");
                }
                catch(Exception e)
                {
                    ports = new string[0];
                    Tools.Logger.AddUartLogDebug($"[openPort]GetPortNames Exception:{e.Message}");
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
                if (port != "")
                {
                    Task.Run(() =>
                    {
                        isOpeningPort = true;
                        try
                        {
                            forcusClosePort = false;//不再强制关闭串口
                            Tools.Logger.AddUartLogDebug($"[openPort]SetName");
                            Tools.Global.uart.SetName(port);
                            Tools.Logger.AddUartLogDebug($"[openPort]open");
                            Tools.Global.uart.Open();
                            Tools.Logger.StartSessionLog(port);
                            Tools.Logger.AddUartLogDebug($"[openPort]change show");
                            this.Dispatcher.Invoke(new Action(delegate
                            {
                                openClosePortTextBlock.Text = (TryFindResource("OpenPort_close") as string ?? "?!");
                                serialPortsListComboBox.IsEnabled = false;
                                statusTextBlock.Text = (TryFindResource("OpenPort_open") as string ?? "?!");
                            }));
                            Tools.Logger.AddUartLogDebug($"[openPort]check to send");
                            if (toSendData != null)
                            {
                                sendUartData(toSendData, toSendDataIsHex, toSendDataApplySendProcessing, toSendDataSessionStringLogOverride);
                                toSendData = null;
                                toSendDataIsHex = null;
                                toSendDataApplySendProcessing = true;
                                toSendDataSessionStringLogOverride = null;
                            }
                            Tools.Logger.AddUartLogDebug($"[openPort]done");
                        }
                        catch(Exception e)
                        {
                            Tools.Logger.AddUartLogDebug($"[openPort]open error:{e.Message}");
                            //串口打开失败！
                            Tools.MessageBox.Show(TryFindResource("ErrorOpenPort") as string ?? "?!");
                        }
                        isOpeningPort = false;
                        Tools.Logger.AddUartLogDebug($"[openPort]all done");
                    });

                }
            }
        }
        private void OpenClosePortButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsSerialSplitModeRequested())
            {
                if (mainSplitPortPage == null)
                    ApplySerialSplitLayout();
                mainSplitPortPage?.ToggleSlotOpen(GetSelectedSerialSplitSlot());
                UpdateSelectedSplitSlotControls();
                return;
            }
            Tools.Logger.AddUartLogDebug($"[OpenClosePortButton]now:{Tools.Global.uart.IsOpen()}");
            if (!Tools.Global.uart.IsOpen())//打开串口逻辑
            {
                openPort();
            }
            else//关闭串口逻辑
            {
                string lastPort = null;//记录一下上次的串口号
                try
                {
                    Tools.Logger.AddUartLogDebug($"[OpenClosePortButton]close");
                    forcusClosePort = true;//不再重新开启串口
                    lastPort = Tools.Global.uart.GetName();//串口号
                    Tools.Global.uart.Close();
                    Tools.Logger.StopSessionLog();
                    Tools.Logger.AddUartLogDebug($"[OpenClosePortButton]close done");
                }
                catch
                {
                    //串口关闭失败！
                    Tools.MessageBox.Show(TryFindResource("ErrorClosePort") as string ?? "?!");
                }
                Tools.Logger.AddUartLogDebug($"[OpenClosePortButton]change show");
                openClosePortTextBlock.Text = (TryFindResource("OpenPort_open") as string ?? "?!");
                serialPortsListComboBox.IsEnabled = true;
                statusTextBlock.Text = (TryFindResource("OpenPort_close") as string ?? "?!");
                Tools.Logger.AddUartLogDebug($"[OpenClosePortButton]change show done");
                refreshPortList(lastPort);
            }

        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsSerialSplitModeActive())
                mainSplitPortPage?.ClearAllLogs();
            else
                Tools.Logger.ClearData();
        }

        private int lastBaudRateSelectedIndex = -1;
        private void BaudRateComboBox_Changed(object sender, EventArgs e)
        {
            if (syncingSerialSplitControls)
                return;

            //选的没变
            if(lastBaudRateSelectedIndex == baudRateComboBox.SelectedIndex)
                return;

            if (baudRateComboBox.SelectedItem != null)
            {
                lastBaudRateSelectedIndex = baudRateComboBox.SelectedIndex;
                if (baudRateComboBox.SelectedIndex == baudRateComboBox.Items.Count - 1)
                {
                    int br = 0;
                    Tuple<bool, string> ret = Tools.InputDialog.OpenDialog(TryFindResource("ShowBaudRate") as string ?? "?!",
                        "115200", TryFindResource("OtherRate") as string ?? "?!");
                    if (!ret.Item1 || !int.TryParse(ret.Item2,out br))//啥都没选
                    {
                        Tools.MessageBox.Show(TryFindResource("OtherRateFail") as string ?? "?!");
                    }
                    if (IsSerialSplitModeActive())
                    {
                        mainSplitPortPage?.SetSlotBaudRate(GetSelectedSerialSplitSlot(), br);
                        baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = br.ToString();
                        baudRateComboBox.Text = br.ToString();
                        return;
                    }
                    Tools.Global.setting.baudRate = br;
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
                        return;
                    }
                    Tools.Global.setting.baudRate =
                        int.Parse((baudRateComboBox.SelectedItem as ComboBoxItem).Content.ToString());
                    baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = TryFindResource("OtherRate") as string ?? "?!";
                }
            }
        }

        /// <summary>
        /// 发串口数据
        /// </summary>
        /// <param name="data"></param>
        private void sendUartData(byte[] data, bool? is_hex = null, bool applySendProcessing = true, string sessionStringLogOverride = null)
        {
            if (data == null)
                return;

            if (IsSerialSplitModeActive())
            {
                var targetSlot = GetSelectedSerialSplitSlot();
                var targetHexMode = mainSplitPortPage?.IsSlotHexMode(targetSlot) ?? Tools.Global.setting.hexSend;
                var displayAsHex = is_hex ?? targetHexMode;
                var splitData = PrepareUartSendData(data, is_hex, applySendProcessing, targetHexMode);
                if (splitData == null || splitData.Length == 0)
                    return;

                _ = SendToSelectedSplitSlotAsync(splitData, displayAsHex);
                return;
            }

            if (!Tools.Global.uart.IsOpen())
            {
                toSendData = (byte[])data.Clone();//带发送数据缓存起来，连上串口后发出去
                toSendDataIsHex = is_hex;
                toSendDataApplySendProcessing = applySendProcessing;
                toSendDataSessionStringLogOverride = sessionStringLogOverride;
                openPort();
                return;
            }

            if (Tools.Global.uart.IsOpen())
            {
                byte[] dataConvert = PrepareUartSendData(data, is_hex, applySendProcessing);
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

        private byte[] PrepareUartSendData(byte[] data, bool? isHex, bool applySendProcessing, bool? defaultHexSend = null)
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

            if (Tools.Global.setting.extraEnter)
            {
                var temp = dataConvert.ToList();
                temp.Add(0x0d);
                temp.Add(0x0a);
                dataConvert = temp.ToArray();
            }

            return dataConvert;
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
                if (!mainSplitPortPage.IsSlotOpen(slot))
                {
                    if (!autoOpen || !mainSplitPortPage.EnsureSlotOpen(slot))
                    {
                        UpdateSelectedSplitSlotControls();
                        return;
                    }
                    UpdateSelectedSplitSlotControls();
                }

                await mainSplitPortPage.SendBytesAsync(slot, data, displayAsHex);
            }
            catch (Exception ex)
            {
                Tools.MessageBox.Show($"{TryFindResource("ErrorSendFail") as string ?? "?!"}\r\n" + ex.ToString());
            }
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
            var data = Global.GetEncoding().GetBytes(toSendDataTextBox.Text);
            SetReceiveScriptContext(recvScriptBackup, "", data);
            sendUartData(data, null, true, Tools.Global.setting.hexSend ? toSendDataTextBox.Text : null);
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

        private void QuickSendTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return && e.Key != Key.Enter)
                return;

            if (!(sender is TextBox textBox) || !(textBox.DataContext is ToSendData data))
                return;

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            SendQuickSendItem(data);
        }

        private void SendQuickSendItem(ToSendData data)
        {
            if (data == null)
                return;

            var sendData = data.hex ? Global.Hex2Byte(data.text) : Global.GetEncoding().GetBytes(data.text);
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
            sendUartData(sendData, true, true, data.hex ? data.text : null);
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
        private void Window_Deactivated(object sender, EventArgs e)
        {
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
        }

        private void JavaScriptRunEnv_ScriptRunError(object sender, EventArgs e)
        {
            scriptLogPrintable = true;
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
            RefreshToolModulesLocalization();
            SetRightToolsCollapsed(rightToolsCollapsed);
            UpdateThemeToggleMenu();
        }

        private void ThemeToggleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Tools.Global.setting.darkMode = !Tools.Global.setting.darkMode;
            Tools.Global.ApplyTheme(Tools.Global.setting.darkMode);
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
                ThemeToggleIcon.Foreground = TryFindResource("AppAccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.DeepSkyBlue;
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
                dataShowFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 148, 0));
        }

        private void uartDataFlowDocument_LostFocus(object sender, RoutedEventArgs e)
        {
            dataShowFrame.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        private void uartDataFlowDocument_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.TextComposition.Text.Length < 1 || !Tools.Global.setting.terminal)
                return;
            if (IsSerialSplitModeActive())
            {
                _ = SendToSelectedSplitSlotAsync(Encoding.ASCII.GetBytes(e.TextComposition.Text), false, autoOpen: false);
                e.Handled = true;
                return;
            }
            if (Tools.Global.uart.IsOpen())
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

            if (e.Key == Key.C || e.Key == Key.V)
                return;

            if (e.Key >= Key.A && e.Key <= Key.Z && IsSerialSplitModeActive())
                try
                {
                    _ = SendToSelectedSplitSlotAsync(new byte[] { (byte)((int)e.Key - (int)Key.A + 1) }, false, autoOpen: false);
                    e.Handled = true;
                    return;
                }
                catch { }

            if (e.Key >= Key.A && e.Key <= Key.Z && Tools.Global.uart.IsOpen())
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
