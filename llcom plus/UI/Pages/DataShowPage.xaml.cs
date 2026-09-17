using llcom_plus.Tools;
using llcom_plus.Model;
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
                if (Items == null || Items.Count == 0)
                    return PlainText ?? string.Empty;

                var text = new StringBuilder();
                foreach (var item in Items ?? new List<DataShow>())
                {
                    text.Append(item.ToLogText());
                }
                return text.ToString();
            }
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
        private Paragraph plainLogParagraph;
        private int plainLogCharCount;
        private bool displayedShowSend = true;
        private bool displayedLineEndings = true;
        private Settings subscribedSettings;
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
                subscribedSettings = Tools.Global.setting;
                subscribedSettings.UartProcessingSettingsChanged += Settings_UartProcessingSettingsChanged;
                displayedShowSend = subscribedSettings.showSend;
                displayedLineEndings = subscribedSettings.ShowLineEndings;

                LogOptionsButton.DataContext = Tools.Global.setting;
                HexSendCheckBox.DataContext = Tools.Global.setting;
                this.ExtraEnterCheckBox.DataContext = Tools.Global.setting;
                EnterSendCheckBox.DataContext = Tools.Global.setting;
                DisableLogCheckBox.DataContext = Tools.Global.setting;
                EnableSymbolCheckBox.DataContext = Tools.Global.setting;
                ShowLineEndingsCheckBox.DataContext = Tools.Global.setting;
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
                // The cached page can be unloaded while split-pane settings change.
                // Reapply the current filter when it is shown again without discarding history.
                RebuildPackedLogDocument();
                RebuildPlainLogDocument();
            });
            StartupProfiler.Mark("DataShowPage.Loaded exit");
        }

        private void DataShowPage_Unloaded(object sender, RoutedEventArgs e)
        {
            CloseLogSearch();
            Unloaded -= DataShowPage_Unloaded;
            Tools.Logger.DataShowTask -= Logger_DataShowTask;
            Tools.Logger.DataClearEvent -= Logger_DataClearEvent;
            Tools.Global.LogColorsChanged -= Global_LogColorsChanged;
            Tools.Global.UartProfileChangedEvent -= Global_UartProfileChangedEvent;
            if (subscribedSettings != null)
                subscribedSettings.UartProcessingSettingsChanged -= Settings_UartProcessingSettingsChanged;
            subscribedSettings = null;
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
                RefreshTxVisibility();
            });
        }

        private void Settings_UartProcessingSettingsChanged(object sender, EventArgs e)
        {
            DoInvoke(RefreshTxVisibility);
        }

        private void RefreshTxVisibility()
        {
            var showSend = Tools.Global.setting?.showSend != false;
            var lineEndings = Tools.Global.setting?.ShowLineEndings != false;
            var packed = Tools.Global.setting?.timeout >= 0;
            if (displayedShowSend == showSend && displayedLineEndings == lineEndings && lastPackShowMode == packed)
                return;
            displayedShowSend = showSend;
            displayedLineEndings = lineEndings;
            lastPackShowMode = packed;
            MainPackedTextBox.Visibility = packed ? Visibility.Visible : Visibility.Collapsed;
            MainTextBox.Visibility = packed ? Visibility.Collapsed : Visibility.Visible;
            RebuildPackedLogDocument();
            RebuildPlainLogDocument();
            if (IsSearchActive)
                ShowLogSearch();
        }

        //记录一下上次是不是分包显示的
        bool lastPackShowMode = false;

        public bool IsSearchActive => LogSearchBar?.IsSearchActive == true;

        public void ShowLogSearch()
        {
            LogSearchBar.Open(lastPackShowMode ? MainPackedTextBox : MainTextBox);
        }

        public void CloseLogSearch()
        {
            LogSearchBar?.Close();
        }

        private void LogSearchStateChanged(object sender, EventArgs e)
        {
            if (!IsSearchActive && !LockLog)
                (lastPackShowMode ? MainPackedTextBox : MainTextBox)?.ScrollToEnd();
        }

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
                Items = packedLogItems.Select(item => item.Clone()).ToList()
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
            if (snapshot.Items == null || snapshot.Items.Count == 0)
            {
                SetLogTextSnapshot(snapshot.ToPlainText());
                return;
            }

            lastPackShowMode = needPack;
            ClearLogDisplay();
            foreach (var item in snapshot.Items)
                if (item?.IsVisible == true)
                    AppendPackedLogItem(item.Clone());
            MainPackedTextBox.Visibility = needPack ? Visibility.Visible : Visibility.Collapsed;
            MainTextBox.Visibility = needPack ? Visibility.Collapsed : Visibility.Visible;
            if (!LockLog && !IsSearchActive)
                (needPack ? MainPackedTextBox : MainTextBox).ScrollToEnd();
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
                if (!LockLog && !IsSearchActive)
                    MainPackedTextBox.ScrollToEnd();
            }
            else
            {
                if (snapshot.Length > 0)
                    AppendPackedLogItem(new DataShow(snapshot));
                MainPackedTextBox.Visibility = Visibility.Collapsed;
                MainTextBox.Visibility = Visibility.Visible;
                if (!LockLog && !IsSearchActive)
                    MainTextBox.ScrollToEnd();
            }
        }

        private string BuildPackedLogText()
        {
            return string.Concat(packedLogItems.Select(item => item.ToLogText()));
        }

        private void ClearLogDisplay()
        {
            packedLogItems.Clear();
            plainLogParagraph = null;
            plainLogCharCount = 0;
            MainPackedTextBox.Document.Blocks.Clear();
            MainTextBox.Document.Blocks.Clear();
        }

        private void AppendPackedLogItem(DataShow item)
        {
            if (item?.IsVisible != true)
                return;

            item = item.LimitHistoryText(MaxPlainTextLogChars);
            packedLogItems.Add(item);
            plainLogCharCount += item.RetainedCharacterCount;
            if (Tools.Global.setting?.showSend != false || !item.IsSent)
                AppendHistoryItem(lastPackShowMode ? MainPackedTextBox : MainTextBox, item, ref plainLogParagraph,
                    Tools.Global.setting?.ShowLineEndings != false);
            TrimPackedLog();
        }

        private void RebuildPackedLogDocument()
        {
            if (MainPackedTextBox == null)
                return;

            MainPackedTextBox.Document.Blocks.Clear();
            if (!lastPackShowMode)
                return;
            plainLogParagraph = null;
            foreach (var item in packedLogItems)
                if (Tools.Global.setting?.showSend != false || !item.IsSent)
                    AppendHistoryItem(MainPackedTextBox, item, ref plainLogParagraph, Tools.Global.setting?.ShowLineEndings != false);
        }

        // Render from retained, direction-aware history. Hiding TX never mutates history.
        internal static void AppendHistoryItem(System.Windows.Controls.RichTextBox target, DataShow item, ref Paragraph plainParagraph,
            bool showLineEndings = true)
        {
            if (!item.IsPlainText)
            {
                plainParagraph = null;
                target.Document.Blocks.Add(CreateLogParagraph(item, showLineEndings));
                return;
            }
            if (plainParagraph == null)
            {
                plainParagraph = new Paragraph { Margin = new Thickness(0), FontFamily = new FontFamily("Consolas,Microsoft YaHei,微软雅黑"), FontSize = 15 };
                target.Document.Blocks.Add(plainParagraph);
            }
            AppendLogTextRuns(plainParagraph, item.GetDisplayData(showLineEndings), Tools.Logger.GetLogDataBrush(item.IsSent), !item.IsSent, 15,
                item.IsSent ? SentColorRole : ReceivedColorRole);
        }

        internal static Paragraph CreateLogParagraph(DataShow item, bool showLineEndings = true)
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
                item.GetDisplayData(showLineEndings),
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
            AppendLogTextRuns(paragraph, item.GetDisplayRaw(showLineEndings), item.RawTextColor, false, 15);
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
            // Keep direction-aware history even when TX is hidden. View changes never
            // rerun receive scripts, touch the serial connection, or rewrite session files.
            var data = e is DataShowRaw raw
                ? new DataShow(raw.title, raw.data, raw.time, raw.color)
                : new DataShow(e as DataShowPara);
            if (data?.IsVisible == true)
            {
                if (Tools.Global.setting.timeout < 0 && e is DataShowPara serialData)
                {
                    data = data.AsPlainText(Tools.Global.setting.showHexFormat == 2);
                }
                DoInvoke(() =>
                {
                    RefreshTxVisibility();
                    AppendPackedLogItem(data);
                    if (!LockLog && !IsSearchActive)
                        (lastPackShowMode ? MainPackedTextBox : MainTextBox).ScrollToEnd();
                });
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
            return BuildPackedLogText();
        }

        private void RebuildPlainLogDocument()
        {
            if (MainTextBox == null)
                return;

            MainTextBox.Document.Blocks.Clear();
            if (lastPackShowMode)
                return;
            plainLogParagraph = null;
            foreach (var item in packedLogItems)
                if (Tools.Global.setting?.showSend != false || !item.IsSent)
                    AppendHistoryItem(MainTextBox, item, ref plainLogParagraph, Tools.Global.setting?.ShowLineEndings != false);
        }

        private void TrimPackedLog()
        {
            if (!TrimHistory(packedLogItems, ref plainLogCharCount, MaxPlainTextLogChars, PlainTextTrimChars))
                return;
            RebuildPackedLogDocument();
            RebuildPlainLogDocument();
        }

        internal static bool TrimHistory(List<DataShow> items, ref int characters, int limit, int trimSize)
        {
            if (items.Count <= MaxPackedLogItems && characters <= limit)
                return false;
            var target = characters > limit ? Math.Max(0, limit - trimSize) : limit;
            while (items.Count > 1 && (items.Count > MaxPackedLogItems || characters > target))
            {
                // Evict whole records to retain direction and ordering; disk logs are independent.
                characters -= items[0].RetainedCharacterCount;
                items.RemoveAt(0);
            }
            // A single large packet must not make the display/history completely empty.
            // Retain its bounded tail with its TX/RX identity; full bytes remain on disk.
            if (items.Count == 1 && characters > limit)
            {
                items[0] = items[0].LimitHistoryText(limit);
                characters = items[0].RetainedCharacterCount;
            }
            return true;
        }

        private static byte[] ApplyReceiveScript(
            byte[] data,
            DataShowPara source,
            UartPortProfile profile = null)
        {
            var temp = data?.ToArray() ?? new byte[0];
            if (source?.send ?? false)
                return temp;

            try
            {
                var context = source?.receiveScriptContext;
                var scriptName = ResolveReceiveScriptName(
                    context?.ScriptName,
                    profile?.recvScript);
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

        private static string ResolveReceiveScriptName(
            string requestedScriptName,
            string profileScriptName = null)
        {
            var scriptName = !string.IsNullOrWhiteSpace(requestedScriptName)
                ? requestedScriptName.Trim()
                : !string.IsNullOrWhiteSpace(profileScriptName)
                    ? profileScriptName.Trim()
                    : Tools.Global.setting?.recvScript;
            if (string.IsNullOrWhiteSpace(scriptName))
                scriptName = "default";

            if (Tools.Global.TryGetProfileScriptPath(
                    "user_script_recv_convert",
                    scriptName,
                    out var normalizedName,
                    out var scriptPath) &&
                File.Exists(scriptPath))
            {
                return normalizedName;
            }

            return "default";
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
                category: AppNotificationCategory.SerialPin,
                portName: portName);
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
            internal bool IsPlainText { get; private set; }
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
            // Preserve both display projections; never remove literal backslash text,
            // rerun receive scripts, or change the canonical export/session record.
            private string dataWithLineEndings;
            private string dataWithoutLineEndings;
            private string rawWithLineEndings;
            private string rawWithoutLineEndings;

            internal string GetDisplayData(bool show) => (show ? dataWithLineEndings : dataWithoutLineEndings) ?? DataText;
            internal string GetDisplayRaw(bool show) => (show ? rawWithLineEndings : rawWithoutLineEndings) ?? RawText;

            // Charge the largest projection to the display budget, including markers.
            // Compute lengths without allocating another full copy of every record.
            internal int RetainedCharacterCount
            {
                get
                {
                    if (IsRestoredSnapshot) return RawText?.Length ?? 0;
                    var dataLength = Math.Max(DataText?.Length ?? 0,
                        Math.Max(GetDisplayData(true)?.Length ?? 0, GetDisplayData(false)?.Length ?? 0));
                    if (IsPlainText) return dataLength;
                    var rawLength = Math.Max(RawText?.Length ?? 0,
                        Math.Max(GetDisplayRaw(true)?.Length ?? 0, GetDisplayRaw(false)?.Length ?? 0));
                    return dataLength + rawLength + (TimeText?.Length ?? 0) + (ArrowText?.Length ?? 0) +
                        (RawTitle?.Length ?? 0) + (HexText?.Length ?? 0) + Environment.NewLine.Length;
                }
            }

            internal DataShow AsPlainText(bool hexSpace)
            {
                var result = Clone();
                result.IsPlainText = true;
                result.TimeText = result.ArrowText = result.HexText = null;
                if (hexSpace && !string.IsNullOrEmpty(result.DataText)) result.DataText += " ";
                return result;
            }

            internal string ToLogText()
            {
                if (IsPlainText)
                    return DataText ?? string.Empty;
                if (IsRestoredSnapshot)
                    return RawText ?? string.Empty;
                return (TimeText ?? string.Empty) + (ArrowText ?? string.Empty) +
                    (DataText ?? string.Empty) + (RawTitle ?? string.Empty) +
                    (RawText ?? string.Empty) + (HexText ?? string.Empty) + Environment.NewLine;
            }

            internal static DataShow CreatePlain(string text, bool sent)
            {
                return new DataShow
                {
                    IsVisible = !string.IsNullOrEmpty(text),
                    IsPlainText = true,
                    IsSerialData = true,
                    IsSent = sent,
                    DataText = text ?? string.Empty
                };
            }

            internal DataShow Clone()
            {
                return new DataShow
                {
                    IsVisible = IsVisible,
                    IsRestoredSnapshot = IsRestoredSnapshot,
                    IsPlainText = IsPlainText,
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
                    IsSent = IsSent,
                    dataWithLineEndings = dataWithLineEndings,
                    dataWithoutLineEndings = dataWithoutLineEndings,
                    rawWithLineEndings = rawWithLineEndings,
                    rawWithoutLineEndings = rawWithoutLineEndings
                };
            }

            internal DataShow LimitHistoryText(int limit)
            {
                if (RetainedCharacterCount <= limit)
                    return this;
                var text = ToLogText();
                var start = Math.Max(0, text.Length - limit);
                if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
                    start++;
                var retained = CreatePlain(text.Substring(start), IsSent);
                retained.IsSerialData = IsSerialData;
                if (dataWithLineEndings != null || rawWithLineEndings != null)
                {
                    retained.dataWithLineEndings = TailDisplayText(true, limit);
                    retained.dataWithoutLineEndings = TailDisplayText(false, limit);
                }
                return retained;
            }

            private string TailDisplayText(bool show, int limit)
            {
                var value = IsPlainText ? GetDisplayData(show) :
                    (TimeText ?? "") + (ArrowText ?? "") + (GetDisplayData(show) ?? "") +
                    (RawTitle ?? "") + (GetDisplayRaw(show) ?? "") + (HexText ?? "") + Environment.NewLine;
                if (value.Length <= limit) return value;
                var start = value.Length - limit;
                if (start > 0 && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1])) start++;
                return value.Substring(start);
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
                : this(source, null)
            {
            }

            internal DataShow(DataShowPara source, UartPortProfile profile)
            {
                var data = source?.data ?? new byte[0];
                var time = source?.time ?? DateTime.Now;
                var sent = source?.send ?? false;
                if (data == null || data.Length == 0)
                    return;
                byte[] temp = ApplyReceiveScript(data, source, profile);
                if (temp == null || temp.Length == 0)
                    return;

                TimeText = time.ToString("[yyyy/MM/dd HH:mm:ss.fff]");
                ArrowText = sent ? " ← " : " → ";
                IsSerialData = true;
                IsSent = sent;
                DataTextColor = Tools.Logger.GetLogDataBrush(sent);
                HexTextColor = sent ? ResourceBrush("AppDataSentSoftBrush", Brushes.IndianRed) : ResourceBrush("AppDataReceivedSoftBrush", Brushes.ForestGreen);

                var showHexFormat = profile?.showHexFormat ?? Tools.Global.setting.showHexFormat;
                var encoding = profile?.encoding ?? Tools.Global.setting.encoding;
                var enableSymbol = profile?.enableSymbol ?? Tools.Global.setting.EnableSymbol;
                var len = temp.Length;
                //主要数据
                if (temp != null && temp.Length > 0)
                {
                    DataText = showHexFormat switch
                    {
                        2 => Tools.Global.Byte2Hex(temp, " ", len),
                        _ => Tools.Global.Byte2Readable(temp, len, encoding, enableSymbol),
                    };
                    if (showHexFormat != 2)
                    {
                        dataWithLineEndings = Tools.Global.Byte2LogDisplay(temp, len, encoding, enableSymbol, true);
                        dataWithoutLineEndings = Tools.Global.Byte2LogDisplay(temp, len, encoding, enableSymbol, false);
                    }
                    //同时显示模式时，才显示小字hex
                    if (showHexFormat == 0)
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
                    RawText = "\n" + (Tools.Global.setting.showHexFormat switch
                    {
                        2 => Tools.Global.Byte2Hex(temp, " ", len),
                        _ => Tools.Global.Byte2Readable(temp, len),
                    });
                    if (Tools.Global.setting.showHexFormat != 2)
                    {
                        rawWithLineEndings = "\n" + Tools.Global.Byte2LogDisplay(temp, len,
                            Tools.Global.setting.encoding, Tools.Global.setting.EnableSymbol, true);
                        rawWithoutLineEndings = "\n" + Tools.Global.Byte2LogDisplay(temp, len,
                            Tools.Global.setting.encoding, Tools.Global.setting.EnableSymbol, false);
                    }
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
                    WriteLogSnapshot(sw, needPack);
                }
            }
        }

        internal void WriteLogSnapshot(TextWriter sw, bool needPack)
        {
                    if (!needPack)
                    {
                        sw.Write(GetPlainLogText());
                    }
                    else
                    {
                        foreach (var item in packedLogItems)
                        {
                            if (item.IsPlainText)
                            {
                                sw.Write(item.DataText);
                                continue;
                            }
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

                    var notificationText = Tools.Logger.GetPortNotificationLogText(Tools.Global.uart.GetName());
                    if (!string.IsNullOrWhiteSpace(notificationText))
                    {
                        sw.WriteLine();
                        sw.WriteLine("===== NOTIFICATIONS =====");
                        sw.Write(notificationText);
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
