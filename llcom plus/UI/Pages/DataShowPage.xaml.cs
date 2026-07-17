using llcom_plus.Tools;
using ScottPlot.Drawing.Colormaps;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace llcom_plus.Pages
{
    /// <summary>
    /// DataShowPage.xaml 的交互逻辑
    /// </summary>
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public partial class DataShowPage : Page
    {
        private const int MaxPackedLogItems = 3000;
        private const int MaxPlainTextLogChars = 1024 * 1024;
        private const int PlainTextTrimChars = 256 * 1024;

        public DataShowPage()
        {
            StartupProfiler.Mark("DataShowPage ctor enter");
            StartupProfiler.Measure("DataShowPage.InitializeComponent", InitializeComponent);
            StartupProfiler.Mark("DataShowPage ctor exit");
        }

        /// <summary>
        /// 禁止自动滚动？
        /// </summary>
        public bool LockLog { get; set; } = false;
        private bool loaded = false;
        private bool packedLogSelectionMode = false;
        private Window ownerWindow;
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            StartupProfiler.Mark("DataShowPage.Loaded enter");
            if (loaded)
                return;
            loaded = true;
            StartupProfiler.Measure("DataShowPage.Loaded init", () =>
            {
                MainTextBox.LostFocus += MainTextBox_LostFocus;
                Unloaded += DataShowPage_Unloaded;
                //添加待显示数据到缓冲区
                Tools.Logger.DataShowTask += Logger_DataShowTask;
                Tools.Logger.DataClearEvent += Logger_DataClearEvent;
                LockIcon.DataContext = this;
                UnLockIcon.DataContext = this;
                UnLockText.DataContext = this;
                LockLogButton.DataContext = this;
                RTSCheckBox.DataContext = this;
                DTRCheckBox.DataContext = this;
                Rts = Tools.Global.uart.Rts;
                Dtr = Tools.Global.uart.Dtr;
                Tools.Global.UartProfileChangedEvent += Global_UartProfileChangedEvent;

                MainList.DataContext = Tools.Global.setting;
                MainTextBox.DataContext = Tools.Global.setting;

                HexSendCheckBox.DataContext = Tools.Global.setting;
                this.ExtraEnterCheckBox.DataContext = Tools.Global.setting;
                EnterSendCheckBox.DataContext = Tools.Global.setting;
                DisableLogCheckBox.DataContext = Tools.Global.setting;
                EnableSymbolCheckBox.DataContext = Tools.Global.setting;
                SessionLogCheckBox.DataContext = Tools.Global.setting;
                SessionLogFolderButton.DataContext = Tools.Global.setting;

                ownerWindow = Window.GetWindow(this);
                if (ownerWindow != null)
                    ownerWindow.PreviewMouseDown += OwnerWindow_PreviewMouseDown;

                lastPackShowMode = Tools.Global.setting.timeout >= 0;
                MainListScrollViewer.Visibility = lastPackShowMode ? Visibility.Visible : Visibility.Collapsed;
                MainTextBox.Visibility = lastPackShowMode ? Visibility.Collapsed : Visibility.Visible;
            });
            StartupProfiler.Mark("DataShowPage.Loaded exit");
        }

        private void DataShowPage_Unloaded(object sender, RoutedEventArgs e)
        {
            MainTextBox.LostFocus -= MainTextBox_LostFocus;
            Unloaded -= DataShowPage_Unloaded;
            Tools.Logger.DataShowTask -= Logger_DataShowTask;
            Tools.Logger.DataClearEvent -= Logger_DataClearEvent;
            Tools.Global.UartProfileChangedEvent -= Global_UartProfileChangedEvent;
            if (ownerWindow != null)
            {
                ownerWindow.PreviewMouseDown -= OwnerWindow_PreviewMouseDown;
                ownerWindow = null;
            }
            loaded = false;
        }

        private void OwnerWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!LogOptionsPopup.IsOpen)
                return;

            var popupChild = LogOptionsPopup.Child as UIElement;
            var placementTarget = LogOptionsPopup.PlacementTarget as UIElement;
            if (LogOptionsButton.IsMouseOver || (placementTarget?.IsMouseOver ?? false) || (popupChild?.IsMouseOver ?? false))
                return;

            LogOptionsButton.IsChecked = false;
        }

        public void ToggleOptions(UIElement placementTarget)
        {
            if (placementTarget == null)
                return;

            LogOptionsPopup.PlacementTarget = placementTarget;
            LogOptionsPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            LogOptionsPopup.HorizontalOffset = 0;
            LogOptionsPopup.VerticalOffset = -6;
            LogOptionsButton.IsChecked = LogOptionsButton.IsChecked != true;
        }

        private void Logger_DataClearEvent(object sender, EventArgs e)
        {
            DoInvoke(() =>
            {
                MainList.Items.Clear();
                MainTextBox.Clear();
            });
        }

        private void Global_UartProfileChangedEvent(object sender, EventArgs e)
        {
            DoInvoke(() =>
            {
                Rts = Tools.Global.uart.Rts;
                Dtr = Tools.Global.uart.Dtr;
            });
        }

        //记录一下上次是不是分包显示的
        bool lastPackShowMode = false;

        public void SelectAllLog()
        {
            if (!lastPackShowMode)
            {
                MainTextBox.Focus();
                MainTextBox.SelectAll();
                return;
            }

            var text = BuildPackedLogText();
            if (string.IsNullOrEmpty(text))
                return;

            packedLogSelectionMode = true;
            MainTextBox.Text = text;
            MainListScrollViewer.Visibility = Visibility.Collapsed;
            MainTextBox.Visibility = Visibility.Visible;
            MainTextBox.Focus();
            MainTextBox.SelectAll();
        }

        private void MainTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (packedLogSelectionMode)
                RestorePackedLogView();
        }

        private string BuildPackedLogText()
        {
            var text = new StringBuilder();
            foreach (var item in MainList.Items.OfType<DataShow>())
            {
                text.Append(item.TimeText);
                text.Append(item.ArrowText);
                text.Append(item.DataText);
                text.Append(item.RawTitle);
                text.Append(item.RawText);
                text.Append(item.HexText);
                text.AppendLine();
            }
            return text.ToString();
        }

        private void RestorePackedLogView()
        {
            packedLogSelectionMode = false;
            if (lastPackShowMode)
            {
                MainTextBox.Clear();
                MainTextBox.Visibility = Visibility.Collapsed;
                MainListScrollViewer.Visibility = Visibility.Visible;
            }
        }

        private void Logger_DataShowTask(object sender, Tools.DataShow e)
        {
            //先判断下要不要清空
            var needPack = Tools.Global.setting.timeout >= 0;
            if (lastPackShowMode != needPack)
            {
                lastPackShowMode = needPack;
                DoInvoke(() =>
                {
                    packedLogSelectionMode = false;
                    MainList.Items.Clear();
                    MainTextBox.Clear();
                    MainListScrollViewer.Visibility = needPack ? Visibility.Visible : Visibility.Collapsed;
                    MainTextBox.Visibility = needPack ? Visibility.Collapsed : Visibility.Visible;
                });
            }

            //如果不开回显，就别打印
            if(!Tools.Global.setting.showSend && !Tools.Global.setting.showSendRaw && e is DataShowPara para && para.send)
                return;

            //显示到列表
            if (!needPack && e is not DataShowRaw)//不分包模式
            {
                var displayData = e.data;
                if (e is DataShowPara showPara && !showPara.send)
                {
                    displayData = ApplyReceiveScript(displayData, showPara);
                    if (displayData == null || displayData.Length == 0)
                        return;
                }

                var DataText = Tools.Global.setting.showHexFormat switch
                {
                    2 => Tools.Global.Byte2Hex(displayData, " ", displayData.Length) + " ",
                    _ => Tools.Global.Byte2Readable(displayData, displayData.Length),
                };
                DoInvoke(() =>
                {
                    MainTextBox.AppendText(DataText);
                    TrimPlainTextLog();
                    if (!LockLog)
                        MainTextBox.ScrollToEnd();
                });
            }
            else//分包模式
            {
                var data = e is DataShowRaw ? 
                    new DataShow((e as DataShowRaw).title, e.data, e.time, (e as DataShowRaw).color) :
                    new DataShow(e as DataShowPara);
                if (data != null && data.IsVisible)
                {
                    DoInvoke(() =>
                    {
                        if (packedLogSelectionMode)
                            RestorePackedLogView();
                        MainList.Items.Add(data);
                        TrimPackedLog();
                        if (!LockLog)
                            MainListScrollViewer.ScrollToEnd();
                    });
                }
            }
        }

        private bool DoInvoke(Action action)
        {
            if (Tools.Global.isMainWindowsClosed)
                return false;
            try
            {
                if (Dispatcher.CheckAccess())
                    action();
                else
                    Dispatcher.Invoke(action);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void TrimPlainTextLog()
        {
            if (MainTextBox.Text.Length <= MaxPlainTextLogChars)
                return;

            var trimLength = Math.Max(PlainTextTrimChars, MainTextBox.Text.Length - MaxPlainTextLogChars);
            trimLength = Math.Min(trimLength, MainTextBox.Text.Length);
            MainTextBox.Text = MainTextBox.Text.Substring(trimLength);
            MainTextBox.SelectionStart = MainTextBox.Text.Length;
        }

        private void TrimPackedLog()
        {
            var removeCount = MainList.Items.Count - MaxPackedLogItems;
            for (var i = 0; i < removeCount; i++)
                MainList.Items.RemoveAt(0);
        }

        private static byte[] ApplyReceiveScript(byte[] data, DataShowPara source)
        {
            var temp = data?.ToArray() ?? new byte[0];
            if (source?.send ?? false)
                return temp;

            try
            {
                var context = source?.receiveScriptContext;
                var scriptName = ResolveReceiveScriptName(context?.ScriptName);
                var uartPara = context?.Parameter ?? "";
                var uartSendRaw = context?.SendRaw ?? new byte[0];
                return ScriptEnv.JavaScriptLoader.Run(
                    $"{scriptName}.js",
                    new System.Collections.ArrayList { "uartData", temp, "uartPara", uartPara, "uartSendRaw", uartSendRaw },
                    "user_script_recv_convert/");
            }
            catch (Exception ex)
            {
                var message = System.Windows.Application.Current?.TryFindResource("ErrorRecvScript") as string
                    ?? "Receive conversion JavaScript script error:";
                Tools.MessageBox.Show(message + "\r\n" + ex.ToString());
                return null;
            }
        }

        private static string ResolveReceiveScriptName(string requestedScriptName)
        {
            var scriptName = string.IsNullOrWhiteSpace(requestedScriptName)
                ? Tools.Global.setting.recvScript
                : requestedScriptName.Trim();
            if (string.IsNullOrWhiteSpace(scriptName))
                scriptName = "default";

            var scriptPath = System.IO.Path.Combine(Tools.Global.ProfilePath, "user_script_recv_convert", scriptName + ".js");
            return File.Exists(scriptPath) ? scriptName : "default";
        }


        private void LockLogButton_Click(object sender, RoutedEventArgs e)
        {
            LockLog = !LockLog;
        }


        public bool Rts {
            get
            {
                return Tools.Global.uart.Rts;
            }
            set
            {
                if (Tools.Global.uart.Rts == value)
                    return;
                Tools.Global.uart.Rts = value;
                Tools.Global.setting?.SaveActiveUartProfile();
            }
        }
        public bool Dtr
        {
            get
            {
                return Tools.Global.uart.Dtr;
            }
            set
            {
                if (Tools.Global.uart.Dtr == value)
                    return;
                Tools.Global.uart.Dtr = value;
                Tools.Global.setting?.SaveActiveUartProfile();
            }
        }

        /// <summary>
        /// 显示要用到的数据结构
        /// </summary>
        public class DataShow
        {
            public bool IsVisible { get; private set; }
            public string TimeText { get; set; }
            public string ArrowText { get; set; }
            public string DataText { get; set; }
            public SolidColorBrush DataTextColor { get; set; }
            public string RawTitle { get; set; }
            /// <summary>
            /// 前面要加换行符
            /// </summary>
            public string RawText { get; set; }
            public SolidColorBrush RawTextColor { get; set; }
            /// <summary>
            /// 前面要加换行符
            /// </summary>
            public string HexText { get; set; }
            public SolidColorBrush HexTextColor { get; set; }


            internal DataShow(DataShowPara source)
            {
                var data = source?.data ?? new byte[0];
                var time = source?.time ?? DateTime.Now;
                var sent = source?.send ?? false;
                if (data == null || data.Length == 0)
                    return;
                byte[] temp = ApplyReceiveScript(data, source);
                if (temp == null || temp.Length == 0)
                    return;

                TimeText = time.ToString("[yyyy/MM/dd HH:mm:ss.fff]");
                ArrowText = sent ? " ← " : " → ";
                DataTextColor = sent ? ResourceBrush("AppDataSentBrush", Brushes.IndianRed) : ResourceBrush("AppDataReceivedBrush", Brushes.SeaGreen);
                HexTextColor = sent ? ResourceBrush("AppDataSentSoftBrush", Brushes.IndianRed) : ResourceBrush("AppDataReceivedSoftBrush", Brushes.ForestGreen);

                var len = temp.Length;
                //主要数据
                if (temp != null && temp.Length > 0)
                {
                    DataText = Tools.Global.setting.showHexFormat switch
                    {
                        2 => Tools.Global.Byte2Hex(temp, " ", len),
                        _ => Tools.Global.Byte2Readable(temp, len),
                    };
                    //同时显示模式时，才显示小字hex
                    if (Tools.Global.setting.showHexFormat == 0)
                        HexText = "\nHex: " + Tools.Global.Byte2Hex(temp, " ", len);
                }
                IsVisible = true;
            }

            public DataShow(string title, byte[] data, DateTime time, SolidColorBrush color)
            {
                byte[] temp = data?.ToArray() ?? new byte[0];

                TimeText = time.ToString("[yyyy/MM/dd HH:mm:ss.fff]");

                var len = temp.Length;
                //主要数据
                if (temp != null && temp.Length > 0)
                {
                    RawText = "\n" + Tools.Global.setting.showHexFormat switch
                    {
                        2 => Tools.Global.Byte2Hex(temp, " ", len),
                        _ => Tools.Global.Byte2Readable(temp, len),
                    };
                    //同时显示模式时，才显示小字hex
                    if (Tools.Global.setting.showHexFormat == 0)
                        HexText = "\nHex: " + Tools.Global.Byte2Hex(temp, " ", len);
                }

                RawTitle = title;
                RawTextColor = color;
                HexTextColor = color;
                IsVisible = true;
            }
        }

        private static SolidColorBrush ResourceBrush(string key, SolidColorBrush fallback)
        {
            return System.Windows.Application.Current?.Resources[key] as SolidColorBrush ?? fallback;
        }

        private void SaveLogButton_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "Log files(*.log)|*.log";
            if(saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                string saveFilePath = saveFileDialog.FileName;
                var needPack = Tools.Global.setting.timeout >= 0;
                using (var fs = new FileStream(saveFilePath, FileMode.Create))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    if (!needPack)
                    {
                        sw.Write(MainTextBox.Text);
                    }
                    else
                    {
                        int iCount = MainList.Items.Count - 1;
                        for (int i = 0; i <= iCount; i++)
                        {
                            var item = MainList.Items[i] as DataShow;
                            if (item == null)
                                continue;

                            if (string.IsNullOrEmpty(item.RawTitle))
                                sw.WriteLine(item.TimeText + (item.ArrowText == " ← " ? " [send] " : " [recv] ") + item.DataText);
                            else
                                sw.WriteLine(item.TimeText + " [" + item.RawTitle + "] " + item.RawText);
                        }
                    }
                }
            }
        }

        private void SessionLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = TryFindResource("SessionLogFolderTip") as string ?? "Select log folder";
                if (!string.IsNullOrWhiteSpace(Tools.Global.setting.sessionLogFolder) &&
                    Directory.Exists(Tools.Global.setting.sessionLogFolder))
                {
                    dialog.SelectedPath = Tools.Global.setting.sessionLogFolder;
                }
                else
                {
                    dialog.SelectedPath = Tools.Global.ProfilePath;
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                    Tools.Global.setting.sessionLogFolder = dialog.SelectedPath;
            }
        }
    }
}
