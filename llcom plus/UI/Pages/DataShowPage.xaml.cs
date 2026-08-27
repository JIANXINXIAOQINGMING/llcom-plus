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
        internal const string SentColorRole = "sent";
        internal const string ReceivedColorRole = "received";
        internal const string ErrorColorRole = "error";

        internal sealed class LogSnapshot
        {
            public bool PackedMode { get; set; }
            public string PlainText { get; set; } = string.Empty;
            public List<DataShow> Items { get; set; } = new List<DataShow>();

            public string ToPlainText()
            {
                if (!PackedMode)
                    return PlainText ?? string.Empty;

                var text = new StringBuilder();
                foreach (var item in Items ?? new List<DataShow>())
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
        }

        private sealed class PlainLogSegment
        {
            public string Text { get; set; } = string.Empty;
            public bool Sent { get; set; }
        }

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
        private readonly List<DataShow> packedLogItems = new List<DataShow>();
        private readonly List<PlainLogSegment> plainLogSegments = new List<PlainLogSegment>();
        private Paragraph plainLogParagraph;
        private int plainLogCharCount;
        private Window ownerWindow;
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            StartupProfiler.Mark("DataShowPage.Loaded enter");
            if (loaded)
                return;
            loaded = true;
            StartupProfiler.Measure("DataShowPage.Loaded init", () =>
            {
                Unloaded += DataShowPage_Unloaded;
                //添加待显示数据到缓冲区
                Tools.Logger.DataShowTask += Logger_DataShowTask;
                Tools.Logger.DataClearEvent += Logger_DataClearEvent;
                Tools.Global.LogColorsChanged += Global_LogColorsChanged;
                LockIcon.DataContext = this;
                UnLockIcon.DataContext = this;
                UnLockText.DataContext = this;
                LockLogButton.DataContext = this;
                RTSCheckBox.DataContext = this;
                DTRCheckBox.DataContext = this;
                Rts = Tools.Global.uart.Rts;
                Dtr = Tools.Global.uart.Dtr;
                Tools.Global.UartProfileChangedEvent += Global_UartProfileChangedEvent;

                LogOptionsButton.DataContext = Tools.Global.setting;
                HexSendCheckBox.DataContext = Tools.Global.setting;
                this.ExtraEnterCheckBox.DataContext = Tools.Global.setting;
                EnterSendCheckBox.DataContext = Tools.Global.setting;
                DisableLogCheckBox.DataContext = Tools.Global.setting;
                EnableSymbolCheckBox.DataContext = Tools.Global.setting;
                SessionLogCheckBox.DataContext = Tools.Global.setting;
                SessionLogFolderButton.DataContext = Tools.Global.setting;

                ownerWindow = Window.GetWindow(this);
                if (ownerWindow != null)
                {
                    ownerWindow.PreviewMouseDown += OwnerWindow_PreviewMouseDown;
                    ownerWindow.Deactivated += OwnerWindow_Deactivated;
                }

                lastPackShowMode = Tools.Global.setting.timeout >= 0;
                MainPackedTextBox.Visibility = lastPackShowMode ? Visibility.Visible : Visibility.Collapsed;
                MainTextBox.Visibility = lastPackShowMode ? Visibility.Collapsed : Visibility.Visible;
            });
            StartupProfiler.Mark("DataShowPage.Loaded exit");
        }

        private void DataShowPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= DataShowPage_Unloaded;
            Tools.Logger.DataShowTask -= Logger_DataShowTask;
            Tools.Logger.DataClearEvent -= Logger_DataClearEvent;
            Tools.Global.LogColorsChanged -= Global_LogColorsChanged;
            Tools.Global.UartProfileChangedEvent -= Global_UartProfileChangedEvent;
            if (ownerWindow != null)
            {
                ownerWindow.PreviewMouseDown -= OwnerWindow_PreviewMouseDown;
                ownerWindow.Deactivated -= OwnerWindow_Deactivated;
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

        private void OwnerWindow_Deactivated(object sender, EventArgs e)
        {
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
                ClearLogDisplay();
            });
        }

        private void Global_LogColorsChanged(object sender, EventArgs e)
        {
            DoInvoke(() =>
            {
                RebuildPlainLogDocument();
                RebuildPackedLogDocument();
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
            if (lastPackShowMode)
            {
                MainPackedTextBox.Focus();
                MainPackedTextBox.SelectAll();
            }
            else
            {
                MainTextBox.Focus();
                MainTextBox.SelectAll();
            }
        }

        public string GetLogTextSnapshot()
        {
            return GetLogSnapshot().ToPlainText();
        }

        internal LogSnapshot GetLogSnapshot()
        {
            return new LogSnapshot
            {
                PackedMode = lastPackShowMode,
                PlainText = lastPackShowMode ? string.Empty : GetPlainLogText(),
                Items = lastPackShowMode
                    ? packedLogItems.Select(item => item.Clone()).ToList()
                    : new List<DataShow>()
            };
        }

        internal void SetLogSnapshot(LogSnapshot snapshot)
        {
            if (snapshot == null)
            {
                SetLogTextSnapshot(string.Empty);
                return;
            }

            var needPack = Tools.Global.setting?.timeout >= 0;
            if (snapshot.PackedMode != needPack)
            {
                SetLogTextSnapshot(snapshot.ToPlainText());
                return;
            }

            lastPackShowMode = needPack;
            ClearLogDisplay();
            if (needPack)
            {
                foreach (var item in snapshot.Items ?? new List<DataShow>())
                {
                    if (item?.IsVisible == true)
                        AppendPackedLogItem(item.Clone());
                }
                MainPackedTextBox.Visibility = Visibility.Visible;
                MainTextBox.Visibility = Visibility.Collapsed;
                if (!LockLog)
                    MainPackedTextBox.ScrollToEnd();
                return;
            }

            var text = snapshot.PlainText ?? string.Empty;
            if (text.Length > MaxPlainTextLogChars)
                text = text.Substring(text.Length - MaxPlainTextLogChars);
            SetPlainLogText(text);
            MainPackedTextBox.Visibility = Visibility.Collapsed;
            MainTextBox.Visibility = Visibility.Visible;
            if (!LockLog)
                MainTextBox.ScrollToEnd();
        }

        public void SetLogTextSnapshot(string text)
        {
            var snapshot = text ?? string.Empty;
            if (snapshot.Length > MaxPlainTextLogChars)
                snapshot = snapshot.Substring(snapshot.Length - MaxPlainTextLogChars);

            var needPack = Tools.Global.setting?.timeout >= 0;
            lastPackShowMode = needPack;
            ClearLogDisplay();

            if (needPack)
            {
                if (snapshot.Length > 0)
                    AppendPackedLogItem(new DataShow(snapshot));
                MainPackedTextBox.Visibility = Visibility.Visible;
                MainTextBox.Visibility = Visibility.Collapsed;
                if (!LockLog)
                    MainPackedTextBox.ScrollToEnd();
            }
            else
            {
                SetPlainLogText(snapshot);
                MainPackedTextBox.Visibility = Visibility.Collapsed;
                MainTextBox.Visibility = Visibility.Visible;
                if (!LockLog)
                    MainTextBox.ScrollToEnd();
            }
        }

        private string BuildPackedLogText()
        {
            var text = new StringBuilder();
            foreach (var item in packedLogItems)
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

        private void ClearLogDisplay()
        {
            packedLogItems.Clear();
            plainLogSegments.Clear();
            plainLogParagraph = null;
            plainLogCharCount = 0;
            MainPackedTextBox.Document.Blocks.Clear();
            MainTextBox.Document.Blocks.Clear();
        }

        private void AppendPackedLogItem(DataShow item)
        {
            if (item?.IsVisible != true)
                return;

            packedLogItems.Add(item);
            MainPackedTextBox.Document.Blocks.Add(CreateLogParagraph(item));
            TrimPackedLog();
        }

        private void RebuildPackedLogDocument()
        {
            if (MainPackedTextBox == null)
                return;

            MainPackedTextBox.Document.Blocks.Clear();
            foreach (var item in packedLogItems)
                MainPackedTextBox.Document.Blocks.Add(CreateLogParagraph(item));
        }

        internal static Paragraph CreateLogParagraph(DataShow item)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                FontFamily = new FontFamily("Consolas,Microsoft YaHei,微软雅黑"),
                FontSize = 12
            };
            paragraph.Inlines.Add(new Run(item.TimeText ?? string.Empty)
            {
                Foreground = ResourceBrush("AppGlassMutedBrush", SystemColors.GrayTextBrush)
            });
            paragraph.Inlines.Add(new Run(item.ArrowText ?? string.Empty)
            {
                Foreground = ResourceBrush("AppGlassMutedBrush", SystemColors.GrayTextBrush)
            });

            var dataBrush = item.IsSerialData
                ? Tools.Logger.GetLogDataBrush(item.IsSent)
                : item.DataTextColor ?? ResourceBrush("AppGlassTextBrush", SystemColors.ControlTextBrush);
            AppendLogTextRuns(
                paragraph,
                item.DataText,
                dataBrush,
                item.IsSerialData && !item.IsSent,
                15,
                item.IsSerialData
                    ? (item.IsSent ? SentColorRole : ReceivedColorRole)
                    : null);
            paragraph.Inlines.Add(new Run(item.RawTitle ?? string.Empty)
            {
                FontWeight = FontWeights.Bold,
                Foreground = ResourceBrush("AppGlassTextBrush", SystemColors.ControlTextBrush)
            });
            AppendLogTextRuns(paragraph, item.RawText, item.RawTextColor, false, 15);
            AppendLogTextRuns(paragraph, item.HexText, item.HexTextColor, false, null);
            return paragraph;
        }

        internal static void AppendLogTextRuns(
            Paragraph paragraph,
            string text,
            Brush defaultBrush,
            bool highlightErrors,
            double? fontSize,
            string colorRole = null)
        {
            if (paragraph == null || string.IsNullOrEmpty(text))
                return;

            var lines = text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split(new[] { '\n' });
            for (var i = 0; i < lines.Length; i++)
            {
                var isError = highlightErrors && Tools.Logger.IsCommonSerialErrorLine(lines[i]);
                var run = new Run(lines[i])
                {
                    Foreground = isError
                        ? Tools.Logger.GetLogErrorBrush()
                        : defaultBrush ?? ResourceBrush("AppGlassTextBrush", SystemColors.ControlTextBrush),
                    Tag = isError ? ErrorColorRole : colorRole
                };
                if (fontSize.HasValue)
                    run.FontSize = fontSize.Value;
                paragraph.Inlines.Add(run);
                if (i < lines.Length - 1)
                    paragraph.Inlines.Add(new LineBreak());
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
                    ClearLogDisplay();
                    MainPackedTextBox.Visibility = needPack ? Visibility.Visible : Visibility.Collapsed;
                    MainTextBox.Visibility = needPack ? Visibility.Collapsed : Visibility.Visible;
                });
            }

            //如果不开回显，就别打印
            if(!Tools.Global.setting.showSend && e is DataShowPara para && para.send)
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
                    AppendPlainLogSegment(DataText, e is DataShowPara serialData && serialData.send);
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
                        AppendPackedLogItem(data);
                        if (!LockLog)
                            MainPackedTextBox.ScrollToEnd();
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

        private string GetPlainLogText()
        {
            return string.Concat(plainLogSegments.Select(segment => segment.Text));
        }

        private void SetPlainLogText(string text)
        {
            plainLogSegments.Clear();
            plainLogCharCount = 0;
            var value = text ?? string.Empty;
            if (value.Length > MaxPlainTextLogChars)
                value = value.Substring(value.Length - MaxPlainTextLogChars);
            if (value.Length > 0)
            {
                plainLogSegments.Add(new PlainLogSegment { Text = value, Sent = false });
                plainLogCharCount = value.Length;
            }
            RebuildPlainLogDocument();
        }

        private void AppendPlainLogSegment(string text, bool sent)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var segment = new PlainLogSegment { Text = text, Sent = sent };
            plainLogSegments.Add(segment);
            plainLogCharCount += text.Length;
            EnsurePlainLogParagraph();
            AppendLogTextRuns(
                plainLogParagraph,
                text,
                Tools.Logger.GetLogDataBrush(sent),
                !sent,
                15,
                sent ? SentColorRole : ReceivedColorRole);
            TrimPlainTextLog();
        }

        private void EnsurePlainLogParagraph()
        {
            if (plainLogParagraph != null)
                return;
            plainLogParagraph = new Paragraph
            {
                Margin = new Thickness(0),
                FontFamily = new FontFamily("Consolas,Microsoft YaHei,微软雅黑"),
                FontSize = 15
            };
            MainTextBox.Document.Blocks.Add(plainLogParagraph);
        }

        private void RebuildPlainLogDocument()
        {
            if (MainTextBox == null)
                return;

            MainTextBox.Document.Blocks.Clear();
            plainLogParagraph = null;
            if (plainLogSegments.Count == 0)
                return;

            EnsurePlainLogParagraph();
            foreach (var segment in plainLogSegments)
            {
                AppendLogTextRuns(
                    plainLogParagraph,
                    segment.Text,
                    Tools.Logger.GetLogDataBrush(segment.Sent),
                    !segment.Sent,
                    15,
                    segment.Sent ? SentColorRole : ReceivedColorRole);
            }
        }

        private void TrimPlainTextLog()
        {
            if (plainLogCharCount <= MaxPlainTextLogChars)
                return;

            var charsToRemove = Math.Max(
                PlainTextTrimChars,
                plainLogCharCount - MaxPlainTextLogChars);
            while (charsToRemove > 0 && plainLogSegments.Count > 0)
            {
                var first = plainLogSegments[0];
                if (first.Text.Length <= charsToRemove)
                {
                    charsToRemove -= first.Text.Length;
                    plainLogCharCount -= first.Text.Length;
                    plainLogSegments.RemoveAt(0);
                    continue;
                }

                first.Text = first.Text.Substring(charsToRemove);
                plainLogCharCount -= charsToRemove;
                charsToRemove = 0;
            }
            RebuildPlainLogDocument();
        }

        private void TrimPackedLog()
        {
            var removeCount = packedLogItems.Count - MaxPackedLogItems;
            for (var i = 0; i < removeCount; i++)
            {
                packedLogItems.RemoveAt(0);
                if (MainPackedTextBox.Document.Blocks.FirstBlock != null)
                    MainPackedTextBox.Document.Blocks.Remove(MainPackedTextBox.Document.Blocks.FirstBlock);
            }
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

        private void ControlLineCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.CheckBox checkBox))
                return;

            var lineName = ReferenceEquals(checkBox, RTSCheckBox) ? "RTS" : "DTR";
            var portName = Tools.Global.uart.GetName();
            if (string.IsNullOrWhiteSpace(portName))
                portName = TryFindResource("SerialPinUnknownPort") as string ?? "串口";

            var title = string.Format(
                TryFindResource("NotificationControlLineTitleFormat") as string ??
                    "{0} 手动切换 {1}",
                portName,
                lineName);
            var message = string.Format(
                TryFindResource("NotificationControlLineMessageFormat") as string ??
                    "RTS:{0}  DTR:{1}",
                RTSCheckBox.IsChecked == true ? "1" : "0",
                DTRCheckBox.IsChecked == true ? "1" : "0");
            Tools.Global.PublishNotification(
                title,
                message,
                AppNotificationLevel.Info,
                category: AppNotificationCategory.SerialPin);
            Tools.Logger.AddUartLogDebug(
                $"[ControlLineManual]{portName} {lineName} {message}");
        }

        /// <summary>
        /// 显示要用到的数据结构
        /// </summary>
        public class DataShow
        {
            public bool IsVisible { get; private set; }
            public bool IsRestoredSnapshot { get; private set; }
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
            internal bool IsSerialData { get; set; }
            internal bool IsSent { get; set; }


            internal DataShow Clone()
            {
                return new DataShow
                {
                    IsVisible = IsVisible,
                    IsRestoredSnapshot = IsRestoredSnapshot,
                    TimeText = TimeText,
                    ArrowText = ArrowText,
                    DataText = DataText,
                    DataTextColor = DataTextColor,
                    RawTitle = RawTitle,
                    RawText = RawText,
                    RawTextColor = RawTextColor,
                    HexText = HexText,
                    HexTextColor = HexTextColor,
                    IsSerialData = IsSerialData,
                    IsSent = IsSent
                };
            }

            internal static DataShow CreateStatus(string timeText, string direction, string text, SolidColorBrush color)
            {
                return new DataShow
                {
                    IsVisible = true,
                    TimeText = timeText ?? string.Empty,
                    ArrowText = direction ?? string.Empty,
                    DataText = text ?? string.Empty,
                    DataTextColor = color
                };
            }

            private DataShow()
            {
            }


            internal DataShow(string restoredSnapshot)
            {
                RawText = restoredSnapshot ?? string.Empty;
                RawTextColor = ResourceBrush("AppGlassTextBrush", Brushes.Black);
                IsRestoredSnapshot = RawText.Length > 0;
                IsVisible = IsRestoredSnapshot;
            }


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
                IsSerialData = true;
                IsSent = sent;
                DataTextColor = Tools.Logger.GetLogDataBrush(sent);
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
                        sw.Write(GetPlainLogText());
                    }
                    else
                    {
                        foreach (var item in packedLogItems)
                        {
                            if (item.IsRestoredSnapshot)
                            {
                                sw.Write(item.RawText);
                                continue;
                            }

                            if (string.IsNullOrEmpty(item.RawTitle))
                                sw.WriteLine(item.TimeText + (item.IsSent ? " [send] " : " [recv] ") + item.DataText);
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
                {
                    Tools.Global.setting.sessionLogFolder = dialog.SelectedPath;
                    if (Tools.Global.setting.sessionLogEnabled && Tools.Global.uart.IsOpen())
                        Tools.Logger.StartSessionLog(Tools.Global.uart.GetName());
                }
            }
        }

        private void SessionLogCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (Tools.Global.setting == null)
                return;

            var enabled = SessionLogCheckBox.IsChecked == true;
            if (Tools.Global.setting.sessionLogEnabled != enabled)
                Tools.Global.setting.sessionLogEnabled = enabled;

            if (enabled && Tools.Global.uart.IsOpen())
                Tools.Logger.StartSessionLog(Tools.Global.uart.GetName());
            else if (!enabled)
                Tools.Logger.StopSessionLog();
        }
    }
}
