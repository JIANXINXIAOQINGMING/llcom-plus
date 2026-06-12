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
using llcom.Model;
using System.Text.RegularExpressions;
using llcom.Tools;
using ICSharpCode.AvalonEdit.Folding;
using System.Threading;
using System.Windows.Interop;
using System.Drawing;
using ICSharpCode.AvalonEdit;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using llcom.ScriptEnv;
using System.Web.UI.WebControls.WebParts;
using Color = System.Windows.Media.Color;

namespace llcom
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
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
                    this.Width = Tools.Global.setting.windowWidth;
                    this.Height = Tools.Global.setting.windowHeight;
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
        public static string recvScriptBackup = "";
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
                    StartupProfiler.Measure("Navigate DataShowPage", () =>
                        dataShowFrame.Navigate(new Uri("Pages/DataShowPage.xaml", UriKind.Relative)));

                    StartupProfiler.Measure("Loaded baud rate init", () =>
                    {
                        //加载初始波特率
                        var br = Tools.Global.setting.baudRate.ToString();
                        if(baudRateComboBox.Items.Contains(br))
                            baudRateComboBox.Text = Tools.Global.setting.baudRate.ToString();
                        else
                        {
                            lastBaudRateSelectedIndex = baudRateComboBox.Items.Count - 1;//防止弹窗提示
                            baudRateComboBox.Items[baudRateComboBox.Items.Count - 1] = br;
                            baudRateComboBox.Text = br;
                        }
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
                LoadSelectedToolPage();
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

        private void ToolsSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!lazyLoadReady)
                return;
            if (!ReferenceEquals(e.OriginalSource, ToolsSelectorComboBox))
                return;

            LoadSelectedToolPage();
        }

        private void LoadSelectedToolPage()
        {
            var selected = (ToolsSelectorComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "EncodingTools";
            HideAllToolPages();

            switch (selected)
            {
                case "Mqtt":
                    ShowToolPage(MqttTestFrame);
                    NavigateFrameOnce(MqttTestFrame, "Pages/MqttTestPage.xaml");
                    break;
                case "SerialMonitor":
                    ShowToolPage(SerialMonitorFrame);
                    NavigateFrameOnce(SerialMonitorFrame, "Pages/SerialMonitorPage.xaml");
                    break;
                case "LogReplay":
                    ShowToolPage(LogReplayFrame);
                    NavigateFrameOnce(LogReplayFrame, "Pages/LogReplayPage.xaml");
                    break;
                case "EncodingFix":
                    ShowToolPage(EncodingFixFrame);
                    NavigateFrameOnce(EncodingFixFrame, "Pages/EncodingFixPage.xaml");
                    break;
                case "Plot":
                    ShowToolPage(PlotFrame);
                    NavigateFrameOnce(PlotFrame, "Pages/PlotPage.xaml");
                    break;
                case "WinUsb":
                    ShowToolPage(WinUSBFrame);
                    NavigateFrameOnce(WinUSBFrame, "Pages/WinUSBPage.xaml");
                    break;
                case "HttpTool":
                    ShowToolPage(HttpToolPanel);
                    break;
                case "DataCalc":
                    ShowToolPage(DataCalcPanel);
                    break;
                case "TcpTest":
                    ShowToolPage(tcpTestFrame);
                    NavigateFrameOnce(tcpTestFrame, "Pages/tcpTest.xaml");
                    break;
                case "TcpLocal":
                    ShowToolPage(tcpLocalTestFrame);
                    NavigateFrameOnce(tcpLocalTestFrame, "Pages/TcpLocalPage.xaml");
                    break;
                case "UdpLocal":
                    ShowToolPage(udpLocalTestFrame);
                    NavigateFrameOnce(udpLocalTestFrame, "Pages/UdpLocalPage.xaml");
                    break;
                case "TcpClient":
                    ShowToolPage(tcpClientFrame);
                    NavigateFrameOnce(tcpClientFrame, "Pages/SocketClientPage.xaml");
                    break;
                default:
                    ShowToolPage(EncodingToolsFrame);
                    NavigateFrameOnce(EncodingToolsFrame, "Pages/ConvertPage.xaml");
                    break;
            }
        }

        private void HideAllToolPages()
        {
            foreach (var page in new FrameworkElement[]
            {
                EncodingToolsFrame,
                MqttTestFrame,
                SerialMonitorFrame,
                LogReplayFrame,
                EncodingFixFrame,
                PlotFrame,
                WinUSBFrame,
                HttpToolPanel,
                DataCalcPanel,
                tcpTestFrame,
                tcpLocalTestFrame,
                udpLocalTestFrame,
                tcpClientFrame
            })
            {
                page.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowToolPage(FrameworkElement page)
        {
            page.Visibility = Visibility.Visible;
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
            if (Tools.Global.setting.quickSend.Count == 0 &&
                Tools.Global.setting.GetAllQuickSendLists().All(list => list == null || list.Count == 0))
            {
                Tools.Global.setting.quickSend = new List<ToSendData>
                        {
                            new ToSendData{id = 1,text="example string",commit="右击更改此处文字",hex=false},
                            new ToSendData{id = 2,text="JavaScript可通过接口获取此处数据",hex=false},
                            new ToSendData{id = 3,text="aa 01 02 0d 0a",commit="Hex数据也能发",hex=true},
                            new ToSendData{id = 4,text="此处数据会被JavaScript处理",hex=false},
                            new ToSendData{id = 5,text="右击序号可以更改这一行的位置",hex=false},
                            new ToSendData{id = 6,text="",hex=false},
                        };
            }
            foreach (var i in Tools.Global.setting.quickSend)
            {
                if (i.commit == null)
                    i.commit = TryFindResource("QuickSendButton") as string ?? "?!";
                toSendListItems.Add(i);
            }
            CheckToSendListId();
            RefreshQuickSendPageSelector();
        }

        private void RefreshQuickSendPageSelector()
        {
            if (QuickListSelectComboBox == null)
                return;

            quickListSelectorRefreshing = true;
            try
            {
                var names = Global.setting.GetAllQuickListNames()
                    .Select((name, index) => $"{index + 1}. {name}")
                    .ToList();
                QuickListSelectComboBox.ItemsSource = null;
                QuickListSelectComboBox.ItemsSource = names;
                QuickListSelectComboBox.SelectedIndex = Global.setting.quickSendSelect;
                DeleteQuickSendPageButton.IsEnabled = Global.setting.GetQuickSendListCount() > 1;
            }
            finally
            {
                quickListSelectorRefreshing = false;
            }
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
            Tools.Logger.ShowData(sender as byte[], false);
        }

        private void Global_SendRawDataRequest(byte[] data)
        {
            Dispatcher.Invoke(new Action(delegate
            {
                Global.setting.recvScript = recvScriptBackup;
                sendUartData(data, true, false);
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
                    foreach (string i in strs)
                        serialPortsListComboBox.Items.Add(i);
                    if (strs.Count >= 1)
                    {
                        openClosePortButton.IsEnabled = true;
                        serialPortsListComboBox.SelectedIndex = 0;
                    }
                    else
                    {
                        openClosePortButton.IsEnabled = false;
                    }
                    refreshLock = false;

                    if (string.IsNullOrEmpty(lastPort))
                        lastPort = Tools.Global.uart.GetName();
                    //选定上次的com口
                    foreach (string c in serialPortsListComboBox.Items)
                    {
                        if (c.Contains($"({lastPort})"))
                        {
                            serialPortsListComboBox.Text = c;
                            //自动重连，不管结果
                            if (!forcusClosePort && Tools.Global.setting.autoReconnect && !isOpeningPort)
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
                            break;
                        }
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
                    string name = file.Name.Substring(0, file.Name.Length - 4);
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

        Window dataCalcWindow = new DataCalcWindow();
        private void DataCalcWindowButton_Click(object sender, RoutedEventArgs e)
        {
            dataCalcWindow.Owner = this;
            if (dataCalcWindow.WindowState == WindowState.Minimized)
                dataCalcWindow.WindowState = WindowState.Normal;
            dataCalcWindow.Show();
            dataCalcWindow.Activate();
        }

        Window httpToolWindow = new HttpToolWindow();
        private void HttpToolWindowButton_Click(object sender, RoutedEventArgs e)
        {
            httpToolWindow.Owner = this;
            if (httpToolWindow.WindowState == WindowState.Minimized)
                httpToolWindow.WindowState = WindowState.Normal;
            httpToolWindow.Show();
            httpToolWindow.Activate();
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
            if (isOpeningPort)
                return;
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
            Tools.Logger.ClearData();
        }

        private int lastBaudRateSelectedIndex = -1;
        private void BaudRateComboBox_Changed(object sender, EventArgs e)
        {
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
                byte[] dataConvert = data;
                if (applySendProcessing)
                {
                    try
                    {
                        WaitRuntimeFilesReady();
                        dataConvert = ScriptEnv.JavaScriptLoader.Run(
                            $"{Tools.Global.setting.sendScript}.js",
                            new System.Collections.ArrayList
                            {
                                "uartData",
                                is_hex == null ?
                                (Tools.Global.setting.hexSend ? Tools.Global.Hex2Byte(Tools.Global.Byte2String(data)) : data) : data
                            });
                    }
                    catch (Exception ex)
                    {
                        Tools.MessageBox.Show($"{TryFindResource("ErrorScript") as string ?? "?!"}\r\n" + ex.ToString());
                        return;
                    }

                    if (dataConvert == null)
                        return;

                    if (Tools.Global.setting.extraEnter)
                    {
                        var temp = dataConvert.ToList();
                        temp.Add(0x0d);
                        temp.Add(0x0a);
                        dataConvert = temp.ToArray();
                    }
                }

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
            Global.setting.recvScript = recvScriptBackup;
            var data = Global.GetEncoding().GetBytes(toSendDataTextBox.Text);
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
            if (matchPrefix.Length == 0 || !matchPrefix.StartsWith("A", StringComparison.OrdinalIgnoreCase))
            {
                sendSuggestPopup.IsOpen = false;
                return;
            }

            var items = GetQuickSendAtCommands()
                .Where(i => i.StartsWith(matchPrefix, StringComparison.OrdinalIgnoreCase))
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

        private IEnumerable<string> GetQuickSendAtCommands()
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

            return allItems
                .Where(i => i != null && !i.hex && !string.IsNullOrWhiteSpace(i.text))
                .Select(i => i.text.Trim())
                .Where(i => i.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
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
            var selected = sendSuggestListBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selected))
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
                toSendDataTextBox.Text = text.Remove(replaceStart, replaceLength).Insert(replaceStart, selected);
                toSendDataTextBox.CaretIndex = replaceStart + selected.Length;
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
            toSendListItems.Add(new ToSendData() { id = toSendListItems.Count + 1, text = "", hex = false , commit = TryFindResource("QuickSendButton") as string ?? "?!" });
            SaveSendList(null, EventArgs.Empty);
        }

        private void RemoveSendListItemButton_Click(object sender, RoutedEventArgs e)
        {
            var item = ((Button)sender).Tag as ToSendData;
            if (item == null)
                return;

            toSendListItems.Remove(item);
            CheckToSendListId();
            SaveSendList(null, EventArgs.Empty);
        }

        private void knowSendDataButton_click(object sender, RoutedEventArgs e)
        {
            ToSendData data = ((Button)sender).Tag as ToSendData;

            // 如果有指定接收脚本，则切换
            if (!string.IsNullOrEmpty(data.recvScriptPath))
            {
                //检查文件是否存在
                if (!File.Exists(Tools.Global.ProfilePath + $"user_script_recv_convert/{data.recvScriptPath}.js"))
                {
                    Tools.Global.setting.recvScript = "default";
                    data.recvScriptPath = "";
                    if (!File.Exists(Tools.Global.ProfilePath + $"user_script_recv_convert/{Tools.Global.setting.recvScript}.js"))
                    {
                        File.Create(Tools.Global.ProfilePath + $"user_script_recv_convert/{Tools.Global.setting.recvScript}.js").Close();
                    }
                }
                else
                {
                    Tools.Global.setting.recvScript = data.recvScriptPath;
                }
            }
            else
            {
                Tools.Global.setting.recvScript = recvScriptBackup;
            }

            var sendData = data.hex ? Global.Hex2Byte(data.text) : Global.GetEncoding().GetBytes(data.text);
            sendUartData(sendData, true, true, data.hex ? data.text : null);
        }

        private void Button_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 恢复原有的双击改名功能
            ToSendData data = ((Button)sender).Tag as ToSendData;
            Tuple<bool, string> ret = Tools.InputDialog.OpenDialog(
                TryFindResource("QuickSendSetButton") as string ?? "?!",
                data.commit, 
                TryFindResource("QuickSendChangeButton") as string ?? "?!");
            if(ret.Item1)
            {
                ((Button)sender).Content = data.commit = ret.Item2;
            }
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
            if (scriptFileList.SelectedItem != null && !fileLoading)
            {
                scriptLogTextBox.Clear();
                ScriptEnv.JavaScriptRunEnv.New($"user_script_run/{scriptFileList.SelectedItem as string}.js");
                scriptEditorGrid.Visibility = Visibility.Collapsed;
                scriptLogShowGrid.Visibility = Visibility.Visible;
                scriptLogPrintable = true;
            }
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
                ScriptEnv.JavaScriptRunEnv.New($"user_script_run/{scriptFileList.SelectedItem as string}.js");
                ScriptEnv.JavaScriptRunEnv.canRun = true;
                scriptLogPrintable = true;
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
            QuickListSelectComboBox.Focus();
        }

        private void DeleteQuickSendPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (Global.setting.GetQuickSendListCount() <= 1)
            {
                Tools.MessageBox.Show(TryFindResource("QuickSendDeletePageBlocked") as string ?? "?!");
                return;
            }

            var ret = Tools.InputDialog.OpenDialog(
                TryFindResource("QuickSendDeletePageConfirmMsg") as string ?? "?!",
                "",
                TryFindResource("DeleteConfirmation") as string ?? "?!");
            if (!ret.Item1 || ret.Item2 != "YES")
                return;

            canSaveSendList = false;
            toSendListItems.Clear();
            Global.setting.RemoveQuickSendPage(Global.setting.quickSendSelect);
            LoadQuickSendList();
            canSaveSendList = true;
        }

        private void QuickSendImportButton_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Forms.OpenFileDialog OpenFileDialog = new System.Windows.Forms.OpenFileDialog();
            OpenFileDialog.Filter = TryFindResource("QuickSendLLCOMFile") as string ?? "?!";
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
            SaveFileDialog.Filter = TryFindResource("QuickSendLLCOMFile") as string ?? "?!";
            if (SaveFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    object data = exportAll
                        ? (object)new
                        {
                            type = "llcom.quickSend.all",
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

        private void QuickListSelectComboBox_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            RenameQuickSendPage();
        }

        private void QuickListNameStackPanel_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            RenameQuickSendPage();
        }

        private void RenameQuickSendPage()
        {
            Tuple<bool, string> ret = Tools.InputDialog.OpenDialog("↓↓↓↓↓↓",
                Global.setting.GetQuickListNameNow(), TryFindResource("QuickSendListNameChangeTip") as string ?? "?!");

            if (!ret.Item1)
                return;

            Global.setting.SetQuickListNameNow(ret.Item2);
            RefreshQuickSendPageSelector();
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
