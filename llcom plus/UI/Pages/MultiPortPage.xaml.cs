using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace llcom_plus.Pages
{
    /// <summary>
    /// 最多四路串口分屏收发。
    /// </summary>
    public partial class MultiPortPage : Page
    {
        private const int MaxSlotCount = 4;
        private const int MaxLogCharsPerSlot = 256 * 1024;
        private const int LogTrimChars = 64 * 1024;
        private readonly int slotCount;
        private readonly bool showSlotSendPanel;
        private readonly List<PortSlot> slots = new List<PortSlot>();
        private bool slotsCreated;
        private bool subscribedProgramClosed;
        private bool updatingExternalControls;
        private bool suppressSlotProfileSave;
        private bool lockLogs;
        private bool portsReleased;
        private int activeSlotNumber = 1;
        private Window ownerWindow;
        private string initialFirstPortName;

        public event Action<int> ActiveSlotChanged;
        public int SlotCount => slotCount;

        public MultiPortPage() : this(MaxSlotCount, true, null)
        {
        }

        public MultiPortPage(int slotCount, bool showSlotSendPanel = true, string initialFirstPortName = null)
        {
            this.slotCount = Math.Max(1, Math.Min(MaxSlotCount, slotCount));
            this.showSlotSendPanel = showSlotSendPanel;
            this.initialFirstPortName = NormalizePortName(initialFirstPortName);
            InitializeComponent();
            ToolbarPanel.Visibility = showSlotSendPanel ? Visibility.Visible : Visibility.Collapsed;
            ExternalOptionsButton.Visibility = Visibility.Collapsed;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!slotsCreated)
            {
                slotsCreated = true;
                ConfigureGridLayout();
                for (var i = 0; i < slotCount; i++)
                {
                    // 分屏中的每一路都拥有独立 SerialPort。串口 1 不再复用主大屏
                    // 的 Global.uart，避免继承旧端口的打开状态和收发事件。
                    var slot = new PortSlot(this, i + 1, false);
                    slots.Add(slot);
                    AddSlotToGrid(slot);
                }
            }

            if (!subscribedProgramClosed)
            {
                Global.ProgramClosedEvent += Global_ProgramClosedEvent;
                subscribedProgramClosed = true;
            }

            BindExternalGlobalOptions();
            ownerWindow = Window.GetWindow(this);
            if (ownerWindow != null)
                ownerWindow.PreviewMouseDown += OwnerWindow_PreviewMouseDown;
            RefreshPorts();
            ApplyInitialFirstPort();
            UpdateStatus();
            ActiveSlotChanged?.Invoke(activeSlotNumber);
        }

        private void ApplyInitialFirstPort()
        {
            if (string.IsNullOrWhiteSpace(initialFirstPortName))
                return;

            var slot = GetSlot(1);
            if (slot != null && !slot.IsOpen)
            {
                slot.SetPortName(initialFirstPortName);
                ApplyPortProfile(slot);
            }
            initialFirstPortName = string.Empty;
        }

        private void BindExternalGlobalOptions()
        {
            if (showSlotSendPanel)
                return;

            ExtraEnterCheckBox.DataContext = Global.setting;
            EnterSendCheckBox.DataContext = Global.setting;
            EnableSymbolCheckBox.DataContext = Global.setting;
            DisableLogCheckBox.DataContext = Global.setting;
            SessionLogCheckBox.DataContext = Global.setting;
            SessionLogFolderButton.DataContext = Global.setting;
        }

        private void SessionLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            ExternalOptionsButton.IsChecked = false;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = TryFindResource("SessionLogFolderTip") as string ?? "Select log folder";
                if (!string.IsNullOrWhiteSpace(Global.setting.sessionLogFolder) &&
                    Directory.Exists(Global.setting.sessionLogFolder))
                    dialog.SelectedPath = Global.setting.sessionLogFolder;
                else
                    dialog.SelectedPath = Global.ProfilePath;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Global.setting.sessionLogFolder = dialog.SelectedPath;
                    RestartSessionLogs();
                }
            }
        }

        private void SessionLogCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            var enabled = SessionLogCheckBox.IsChecked == true;
            if (Global.setting != null && Global.setting.sessionLogEnabled != enabled)
                Global.setting.sessionLogEnabled = enabled;

            if (enabled)
                RestartSessionLogs();
            else
                CloseSessionLogs();
        }

        private void RestartSessionLogs()
        {
            foreach (var slot in slots)
                slot.RestartSessionLog();
        }

        private void CloseSessionLogs()
        {
            foreach (var slot in slots)
                slot.CloseSessionLogWriter();
        }

        private void ConfigureGridLayout()
        {
            SlotsGrid.Children.Clear();
            SlotsGrid.RowDefinitions.Clear();
            SlotsGrid.ColumnDefinitions.Clear();

            var columns = slotCount == 1 ? 1 : 2;
            var rows = slotCount <= 2 ? 1 : 2;
            for (var column = 0; column < columns; column++)
                SlotsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < rows; row++)
                SlotsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        private void AddSlotToGrid(PortSlot slot)
        {
            if (slotCount == 1)
            {
                Grid.SetRow(slot.Root, 0);
                Grid.SetColumn(slot.Root, 0);
            }
            else if (slotCount == 2)
            {
                Grid.SetRow(slot.Root, 0);
                Grid.SetColumn(slot.Root, slot.Index - 1);
            }
            else if (slotCount == 3 && slot.Index == 3)
            {
                Grid.SetRow(slot.Root, 1);
                Grid.SetColumn(slot.Root, 0);
                Grid.SetColumnSpan(slot.Root, 2);
            }
            else
            {
                Grid.SetRow(slot.Root, (slot.Index - 1) / 2);
                Grid.SetColumn(slot.Root, (slot.Index - 1) % 2);
            }

            SlotsGrid.Children.Add(slot.Root);
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            ExternalOptionsButton.IsChecked = false;
            ReleaseAllPortsForLayoutChange();
            if (ownerWindow != null)
            {
                ownerWindow.PreviewMouseDown -= OwnerWindow_PreviewMouseDown;
                ownerWindow = null;
            }
            if (subscribedProgramClosed)
            {
                Global.ProgramClosedEvent -= Global_ProgramClosedEvent;
                subscribedProgramClosed = false;
            }
        }

        private void OwnerWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!ExternalOptionsPopup.IsOpen)
                return;

            var popupChild = ExternalOptionsPopup.Child as UIElement;
            var placementTarget = ExternalOptionsPopup.PlacementTarget as UIElement;
            if (ExternalOptionsButton.IsMouseOver || (placementTarget?.IsMouseOver ?? false) || (popupChild?.IsMouseOver ?? false))
                return;

            ExternalOptionsButton.IsChecked = false;
        }

        public void ToggleExternalOptions(UIElement placementTarget)
        {
            if (placementTarget == null || showSlotSendPanel)
                return;

            RefreshExternalControls();
            ExternalOptionsPopup.PlacementTarget = placementTarget;
            ExternalOptionsPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            ExternalOptionsPopup.HorizontalOffset = 0;
            ExternalOptionsPopup.VerticalOffset = -6;
            ExternalOptionsButton.IsChecked = ExternalOptionsButton.IsChecked != true;
        }

        private void ExternalLockLogsButton_Click(object sender, RoutedEventArgs e)
        {
            lockLogs = !lockLogs;
            ExternalLockLogsIcon.Icon = lockLogs
                ? FontAwesome.WPF.FontAwesomeIcon.Unlock
                : FontAwesome.WPF.FontAwesomeIcon.Lock;
            ExternalLockLogsText.Text = lockLogs
                ? FindText("UnlockLogAction", "取消锁定")
                : FindText("LockLogAction", "锁定日志");
            ExternalLockLogsIcon.SetResourceReference(
                TextBlock.ForegroundProperty,
                lockLogs ? "AppSuccessBrush" : "AppGlassTextBrush");
        }

        private void ExternalSaveLogsButton_Click(object sender, RoutedEventArgs e)
        {
            ExternalOptionsButton.IsChecked = false;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Log files(*.log)|*.log"
            };
            if (dialog.ShowDialog() != true)
                return;

            using (var writer = new StreamWriter(dialog.FileName, false, Encoding.UTF8))
            {
                foreach (var slot in slots)
                {
                    writer.WriteLine("===== " + slot.DisplayTitle + " =====");
                    writer.Write(slot.GetLogText());
                    writer.WriteLine();
                }
            }
        }

        private void Global_ProgramClosedEvent(object sender, EventArgs e)
        {
            ReleaseAllPortsForLayoutChange();
        }

        public void ReleaseAllPortsForLayoutChange()
        {
            if (portsReleased)
                return;

            portsReleased = true;
            CloseAll(
                closeMainUart: false,
                detachMainUart: true,
                waitForDispose: true,
                disposeOwnedPorts: true);
        }

        private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
        }

        private void CloseAllButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAll();
        }

        private void RefreshPorts()
        {
            var ports = GetPortNames();
            foreach (var slot in slots)
            {
                slot.RefreshPorts(ports);
                ApplyPortProfile(slot);
            }
            UpdateStatus();
        }

        public void RefreshSlotPorts(string[] ports)
        {
            foreach (var slot in slots)
            {
                slot.RefreshPorts(ports);
                ApplyPortProfile(slot);
            }
        }

        private void CloseAll(
            bool closeMainUart = true,
            bool detachMainUart = false,
            bool waitForDispose = false,
            bool disposeOwnedPorts = false)
        {
            foreach (var slot in slots)
            {
                slot.Close(closeMainUart, waitForDispose);
                if (disposeOwnedPorts)
                    slot.DisposeOwnedPort();
                if (detachMainUart)
                    slot.DetachMainUartEvents();
            }
            UpdateStatus();
        }

        public void ClearAllLogs()
        {
            foreach (var slot in slots)
                slot.ClearLog();
        }

        public void ClearSlotLog(int slotNumber)
        {
            GetSlot(slotNumber)?.ClearLog();
        }

        public void SetActiveSlot(int slotNumber)
        {
            SetActiveSlot(slotNumber, false);
        }

        private void SetActiveSlot(int slotNumber, bool notify)
        {
            activeSlotNumber = Math.Max(1, Math.Min(slotCount, slotNumber));
            RefreshExternalControls();
            if (notify)
                ActiveSlotChanged?.Invoke(activeSlotNumber);
        }

        private void ActivateSlotFromPane(int slotNumber)
        {
            SetActiveSlot(slotNumber, true);
        }

        public string GetSlotPortName(int slotNumber)
        {
            var slot = GetSlot(slotNumber);
            return slot?.SelectedPortName ?? "";
        }

        public void SetSlotPortName(int slotNumber, string portName)
        {
            var slot = GetSlot(slotNumber);
            if (slot == null)
                return;

            // 选择端口只更新待使用端口；发送或状态按钮才切换实际连接。
            slot.SetPortName(portName);
            ApplyPortProfile(slot);
            RefreshExternalControls();
            UpdateStatus();
        }

        public int GetSlotBaudRate(int slotNumber)
        {
            return GetSlot(slotNumber)?.BaudRate ?? 115200;
        }

        public void SetSlotBaudRate(int slotNumber, int baudRate)
        {
            var slot = GetSlot(slotNumber);
            if (slot == null)
                return;

            slot.SetBaudRate(baudRate);
            SaveSlotProfile(slot);
        }

        public bool ToggleSlotOpen(int slotNumber)
        {
            var slot = GetSlot(slotNumber);
            if (slot == null)
                return false;

            slot.ToggleOpen();
            SaveSlotProfile(slot);
            RefreshExternalControls();
            return slot.IsSelectedPortOpen;
        }

        public bool EnsureSlotOpen(int slotNumber)
        {
            var slot = GetSlot(slotNumber);
            if (slot == null)
                return false;

            var opened = slot.EnsureOpen();
            SaveSlotProfile(slot);
            RefreshExternalControls();
            UpdateStatus();
            return opened;
        }

        public Task<bool> SendBytesAsync(int slotNumber, byte[] data, bool displayAsHex)
        {
            if (data == null || data.Length == 0)
                return Task.FromResult(false);

            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            if (index < 0 || index >= slots.Count)
                return Task.FromResult(false);

            return slots[index].SendBytesAsync(data, displayAsHex);
        }

        public bool IsSlotHexMode(int slotNumber)
        {
            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            return index >= 0 && index < slots.Count && slots[index].HexMode;
        }

        public bool IsSlotOpen(int slotNumber)
        {
            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            return index >= 0 && index < slots.Count && slots[index].IsOpen;
        }

        public bool IsSlotSelectedPortOpen(int slotNumber)
        {
            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            return index >= 0 && index < slots.Count && slots[index].IsSelectedPortOpen;
        }

        public string GetSlotLastError(int slotNumber)
        {
            return GetSlot(slotNumber)?.LastErrorMessage ?? string.Empty;
        }

        public bool SendBytesBlocking(int slotNumber, byte[] data, bool displayAsHex, CancellationToken token)
        {
            if (data == null || data.Length == 0)
                return false;

            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            return index >= 0 && index < slots.Count && slots[index].SendBytesBlocking(data, displayAsHex, token);
        }

        private PortSlot GetSlot(int slotNumber)
        {
            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            return index >= 0 && index < slots.Count ? slots[index] : null;
        }

        private void RefreshExternalControls()
        {
            if (showSlotSendPanel)
                return;

            var slot = GetSlot(activeSlotNumber);
            if (slot == null)
                return;

            updatingExternalControls = true;
            try
            {
                var title = FindText("SendAndLogOptions", "发送与日志选项");
                ExternalOptionsTitle.Text = string.IsNullOrWhiteSpace(slot.SelectedPortName)
                    ? title
                    : title + " · " + slot.SelectedPortName;
                ExternalRTSCheckBox.IsChecked = slot.Rts;
                ExternalDTRCheckBox.IsChecked = slot.Dtr;
                ExternalHexCheckBox.IsChecked = slot.HexMode;
            }
            finally
            {
                updatingExternalControls = false;
            }
        }

        private void ExternalControlCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (updatingExternalControls)
                return;

            var slot = GetSlot(activeSlotNumber);
            if (slot == null)
                return;

            suppressSlotProfileSave = true;
            try
            {
                slot.HexMode = ExternalHexCheckBox.IsChecked == true;
                slot.Dtr = ExternalDTRCheckBox.IsChecked == true;
                slot.Rts = ExternalRTSCheckBox.IsChecked == true;
            }
            finally
            {
                suppressSlotProfileSave = false;
            }
            SaveSlotProfile(slot);
        }

        private void ApplyPortProfile(PortSlot slot)
        {
            if (slot == null || slot.IsOpen)
                return;

            var profile = Global.setting?.GetUartProfileForPort(slot.SelectedPortName);
            if (profile == null)
                return;

            suppressSlotProfileSave = true;
            try
            {
                slot.SetBaudRate(profile.baudRate);
                slot.HexMode = profile.hexSend;
                slot.Dtr = profile.dtr;
                slot.Rts = profile.rts;
            }
            finally
            {
                suppressSlotProfileSave = false;
            }
        }

        private void SaveSlotProfile(PortSlot slot)
        {
            if (suppressSlotProfileSave || slot == null)
                return;

            Global.setting?.SaveUartProfileForPort(
                slot.SelectedPortName,
                slot.BaudRate,
                slot.HexMode,
                slot.Rts,
                slot.Dtr);
        }

        private bool IsPortOpenInOtherSlot(PortSlot requester, string portName)
        {
            if (slots.Any(slot =>
                !ReferenceEquals(slot, requester) &&
                slot.IsOpen &&
                string.Equals(slot.PortName, portName, StringComparison.OrdinalIgnoreCase)))
                return true;

            try
            {
                if (requester?.UsesMainUart == true)
                    return false;

                return Global.uart != null &&
                    Global.uart.IsOpen() &&
                    string.Equals(NormalizePortName(Global.uart.GetName()), portName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateStatus()
        {
            if (Global.isMainWindowsClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            if (!Dispatcher.CheckAccess())
            {
                RunOnUi(UpdateStatus);
                return;
            }

            var openCount = slots.Count(slot => slot.IsOpen);
            StatusTextBlock.Text = string.Format(
                FindText("MultiPortStatus", "已打开 {0}/{1} 个串口"),
                openCount,
                slotCount);
        }

        private void RunOnUi(Action action)
        {
            if (Global.isMainWindowsClosed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            try
            {
                if (Dispatcher.CheckAccess())
                    action();
                else
                    Dispatcher.BeginInvoke(action);
            }
            catch
            {
            }
        }

        private string FindText(string key, string fallback)
        {
            return TryFindResource(key) as string ?? fallback;
        }

        private static string[] GetPortNames()
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Select(NormalizePortName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => GetPortSortValue(name))
                    .ThenBy(name => name)
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        private static string NormalizePortName(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
                return "";
            var value = portName.Trim();
            var zeroIndex = value.IndexOf('\0');
            if (zeroIndex >= 0)
                value = value.Substring(0, zeroIndex);
            return value.ToUpperInvariant();
        }

        private static int GetPortSortValue(string portName)
        {
            var match = Regex.Match(portName ?? "", @"^COM(\d+)$", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
        }

        private sealed class PortSlot
        {
            private readonly MultiPortPage owner;
            private readonly bool useMainUart;
            private readonly SerialPort serial = new SerialPort();
            private readonly ComboBox portComboBox = new ComboBox();
            private readonly ComboBox baudComboBox = new ComboBox();
            private readonly CheckBox hexCheckBox = new CheckBox();
            private readonly CheckBox dtrCheckBox = new CheckBox();
            private readonly CheckBox rtsCheckBox = new CheckBox();
            private readonly Button openButton = new Button();
            private readonly Button clearButton = new Button();
            private readonly Button sendButton = new Button();
            private readonly RichTextBox logTextBox = new RichTextBox();
            private readonly TextBox sendTextBox = new TextBox();
            private readonly TextBlock titleTextBlock = new TextBlock();
            private readonly object serialLock = new object();
            private readonly object sessionLogLock = new object();
            private string selectedPortName = "";
            private StreamWriter sessionStringLogWriter;
            private StreamWriter sessionHexLogWriter;
            private int logCharCount;

            public PortSlot(MultiPortPage owner, int index, bool useMainUart)
            {
                this.owner = owner;
                this.useMainUart = useMainUart;
                Index = index;
                Root = BuildView();
                if (useMainUart)
                {
                    Global.uart.UartDataRecived += MainUart_UartDataRecived;
                    Global.uart.UartDataSent += MainUart_UartDataSent;
                    Global.uart.UartDataRawSent += MainUart_UartDataRawSent;
                }
                else
                {
                    serial.DataReceived += Serial_DataReceived;
                    serial.WriteTimeout = 5000;
                    serial.ReadTimeout = 500;
                }
            }

            public int Index { get; }
            public Border Root { get; }
            public string DisplayTitle => titleTextBlock.Text ?? string.Format("Port {0}", Index);
            public bool UsesMainUart => useMainUart;
            public bool IsOpen
            {
                get
                {
                    try
                    {
                        return useMainUart ? Global.uart.IsOpen() : serial.IsOpen;
                    }
                    catch (Exception ex) when (IsClosedSerialException(ex))
                    {
                        return false;
                    }
                }
            }
            public string PortName
            {
                get
                {
                    try
                    {
                        return NormalizePortName(useMainUart ? Global.uart.GetName() : serial.PortName);
                    }
                    catch (Exception ex) when (IsClosedSerialException(ex))
                    {
                        return SelectedPortName;
                    }
                }
            }
            public string SelectedPortName => NormalizePortName(selectedPortName);
            public bool IsSelectedPortOpen => IsOpen &&
                !string.IsNullOrWhiteSpace(SelectedPortName) &&
                string.Equals(PortName, SelectedPortName, StringComparison.OrdinalIgnoreCase);
            public string LastErrorMessage { get; private set; } = string.Empty;
            public int BaudRate
            {
                get
                {
                    if (useMainUart && IsOpen)
                    {
                        try { return Global.uart.serial.BaudRate; }
                        catch { }
                    }

                    return int.TryParse(baudComboBox.Text, out var baudRate) && baudRate > 0 ? baudRate : 115200;
                }
            }
            public bool Rts
            {
                get { return rtsCheckBox.IsChecked == true; }
                set
                {
                    rtsCheckBox.IsChecked = value;
                    ApplyControlLines();
                }
            }
            public bool Dtr
            {
                get { return dtrCheckBox.IsChecked == true; }
                set
                {
                    dtrCheckBox.IsChecked = value;
                    ApplyControlLines();
                }
            }
            public bool HexMode
            {
                get { return hexCheckBox.IsChecked == true; }
                set { hexCheckBox.IsChecked = value; }
            }

            public void RefreshPorts(string[] ports)
            {
                var selected = SelectedPortName;
                if (string.IsNullOrWhiteSpace(selected))
                    selected = PortName;

                portComboBox.Items.Clear();
                foreach (var port in ports)
                    portComboBox.Items.Add(port);

                if (!string.IsNullOrWhiteSpace(selected) && portComboBox.Items.Contains(selected))
                {
                    selectedPortName = selected;
                    portComboBox.Text = selected;
                }
                else if (portComboBox.Items.Count > 0)
                {
                    portComboBox.SelectedIndex = Math.Min(Index - 1, portComboBox.Items.Count - 1);
                    selectedPortName = NormalizePortName(portComboBox.SelectedItem?.ToString());
                }

                SyncOpenStateUi();
            }

            public void SetPortName(string portName)
            {
                var normalizedPortName = NormalizePortName(portName);
                selectedPortName = normalizedPortName;
                portComboBox.Text = normalizedPortName;
                if (useMainUart && !IsOpen && !string.IsNullOrWhiteSpace(normalizedPortName))
                    Global.uart.SetName(normalizedPortName);
                UpdateTitle();
            }

            public void SetBaudRate(int baudRate)
            {
                if (baudRate <= 0)
                    return;

                var text = baudRate.ToString();
                if (!baudComboBox.Items.Contains(text))
                    baudComboBox.Items.Add(text);
                baudComboBox.Text = text;
                ApplyBaudRate(baudRate);
            }

            public void ToggleOpen()
            {
                if (IsSelectedPortOpen)
                    Close();
                else
                    EnsureOpen();
            }

            public bool EnsureOpen()
            {
                if (IsSelectedPortOpen)
                    return true;

                var selectedPort = SelectedPortName;
                if (string.IsNullOrWhiteSpace(selectedPort))
                {
                    LastErrorMessage = owner.FindText("MultiPortNoPort", "未选择串口。");
                    return false;
                }
                if (owner.IsPortOpenInOtherSlot(this, selectedPort))
                {
                    LastErrorMessage = owner.FindText("MultiPortPortInUse", "该串口已在其它分屏打开。");
                    AppendLog("ERR", LastErrorMessage);
                    return false;
                }

                if (IsOpen)
                    Close(waitForDispose: true);

                // 真正切换前再加载新端口自己的配置，避免选择阶段改动旧端口的控制线。
                owner.ApplyPortProfile(this);
                return Open();
            }

            public void Close(bool closeMainUart = true, bool waitForDispose = false)
            {
                Exception closeError = null;
                lock (serialLock)
                {
                    try
                    {
                        if (useMainUart)
                        {
                            if (closeMainUart && Global.uart.IsOpen())
                            {
                                Global.uart.Close(waitForDispose);
                                Logger.StopSessionLog();
                            }
                        }
                        else if (IsOpen)
                        {
                            Logger.AddUartLogDebug($"[SplitUartClose]slot={Index},port={serial.PortName}");
                            serial.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        closeError = ex;
                    }
                }

                CloseSessionLog();

                owner.RunOnUi(() =>
                {
                    if (closeError != null)
                        AppendLog("ERR", owner.FindText("MultiPortCloseFailed", "关闭失败: ") + closeError.Message);
                    SyncOpenStateUi();
                    UpdateTitle();
                    owner.UpdateStatus();
                });
            }

            public void DetachMainUartEvents()
            {
                if (!useMainUart)
                    return;

                Global.uart.UartDataRecived -= MainUart_UartDataRecived;
                Global.uart.UartDataSent -= MainUart_UartDataSent;
                Global.uart.UartDataRawSent -= MainUart_UartDataRawSent;
            }

            public void DisposeOwnedPort()
            {
                if (useMainUart)
                    return;

                lock (serialLock)
                {
                    try { serial.DataReceived -= Serial_DataReceived; }
                    catch { }
                    try { serial.Close(); }
                    catch { }
                    try { serial.Dispose(); }
                    catch { }
                }
            }

            public void RestartSessionLog()
            {
                CloseSessionLog();
                EnsureSessionLogOpen();
            }

            public void CloseSessionLogWriter()
            {
                CloseSessionLog();
            }

            private Border BuildView()
            {
                var root = new Border
                {
                    Margin = new Thickness(4),
                    Padding = new Thickness(8),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6)
                };
                if (Tools.Logger.GetThemeBrush("AppGlassBorderBrush", null) != null)
                    root.SetResourceReference(Border.BorderBrushProperty, "AppGlassBorderBrush");
                else
                    root.BorderBrush = SystemColors.ActiveBorderBrush;
                root.PreviewMouseDown += (sender, args) => owner.ActivateSlotFromPane(Index);

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                titleTextBlock.FontWeight = FontWeights.SemiBold;
                titleTextBlock.Margin = new Thickness(0, 0, 0, 6);
                Grid.SetRow(titleTextBlock, 0);
                grid.Children.Add(titleTextBlock);

                var options = new WrapPanel
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Visibility = owner.showSlotSendPanel ? Visibility.Visible : Visibility.Collapsed
                };
                portComboBox.MinWidth = 90;
                portComboBox.Margin = new Thickness(0, 0, 6, 4);
                portComboBox.SelectionChanged += PortComboBox_SelectionChanged;
                baudComboBox.Width = 92;
                baudComboBox.Margin = new Thickness(0, 0, 6, 4);
                foreach (var baud in new[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" })
                    baudComboBox.Items.Add(baud);
                baudComboBox.Text = "115200";

                openButton.MinWidth = 64;
                openButton.Margin = new Thickness(0, 0, 6, 4);
                openButton.Click += OpenButton_Click;
                clearButton.MinWidth = 64;
                clearButton.Margin = new Thickness(0, 0, 6, 4);
                clearButton.Click += ClearButton_Click;

                hexCheckBox.Margin = new Thickness(0, 3, 8, 4);
                dtrCheckBox.Margin = new Thickness(0, 3, 8, 4);
                rtsCheckBox.Margin = new Thickness(0, 3, 0, 4);
                dtrCheckBox.IsChecked = false;
                dtrCheckBox.Checked += ControlLineCheckBox_Changed;
                dtrCheckBox.Unchecked += ControlLineCheckBox_Changed;
                rtsCheckBox.Checked += ControlLineCheckBox_Changed;
                rtsCheckBox.Unchecked += ControlLineCheckBox_Changed;

                options.Children.Add(portComboBox);
                options.Children.Add(baudComboBox);
                options.Children.Add(openButton);
                options.Children.Add(clearButton);
                options.Children.Add(hexCheckBox);
                options.Children.Add(dtrCheckBox);
                options.Children.Add(rtsCheckBox);
                Grid.SetRow(options, 1);
                grid.Children.Add(options);

                logTextBox.IsReadOnly = true;
                logTextBox.Document = new FlowDocument { PagePadding = new Thickness(0) };
                logTextBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                logTextBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                logTextBox.FontFamily = new FontFamily("Consolas");
                logTextBox.FontSize = 13;
                logTextBox.Background = Brushes.Transparent;
                logTextBox.BorderBrush = Brushes.Transparent;
                logTextBox.BorderThickness = new Thickness(0);
                logTextBox.FocusVisualStyle = null;
                Grid.SetRow(logTextBox, 2);
                grid.Children.Add(logTextBox);

                var sendPanel = new Grid
                {
                    Margin = new Thickness(0, 6, 0, 0),
                    Visibility = owner.showSlotSendPanel ? Visibility.Visible : Visibility.Collapsed
                };
                sendPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                sendPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                sendTextBox.MinHeight = 30;
                sendTextBox.Margin = new Thickness(0, 0, 6, 0);
                sendButton.MinWidth = 64;
                sendButton.Click += SendButton_Click;
                sendPanel.Children.Add(sendTextBox);
                Grid.SetColumn(sendButton, 1);
                sendPanel.Children.Add(sendButton);
                Grid.SetRow(sendPanel, 3);
                grid.Children.Add(sendPanel);

                root.Child = grid;
                ApplyText();
                UpdateTitle();
                SyncOpenStateUi();
                return root;
            }

            private void SyncOpenStateUi()
            {
                var isOpen = IsSelectedPortOpen;
                portComboBox.IsEnabled = true;
                baudComboBox.IsEnabled = true;
                openButton.Content = owner.FindText(isOpen ? "MultiPortClose" : "MultiPortOpen", isOpen ? "关闭" : "打开");
                if (useMainUart && isOpen)
                    SetBaudRateText(BaudRate);
                UpdateTitle();
            }

            private void PortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
            {
                var selected = NormalizePortName(portComboBox.SelectedItem?.ToString() ?? portComboBox.Text);
                if (!string.IsNullOrWhiteSpace(selected))
                    selectedPortName = selected;
                owner.ApplyPortProfile(this);
                UpdateTitle();
                owner.UpdateStatus();
            }

            private void SetBaudRateText(int baudRate)
            {
                if (baudRate <= 0)
                    return;

                var text = baudRate.ToString();
                if (!baudComboBox.Items.Contains(text))
                    baudComboBox.Items.Add(text);
                baudComboBox.Text = text;
            }

            private void ApplyBaudRate(int baudRate)
            {
                try
                {
                    if (useMainUart)
                    {
                        if (Global.setting != null)
                            Global.setting.baudRate = baudRate;
                        else
                            Global.uart.SetBaudRate(baudRate);
                    }
                    else if (IsOpen)
                    {
                        serial.BaudRate = baudRate;
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("ERR", owner.FindText("MultiPortInvalidBaud", "波特率无效。") + ex.Message);
                }
            }

            private void ApplyText()
            {
                openButton.Content = owner.FindText("MultiPortOpen", "打开");
                clearButton.Content = owner.FindText("MultiPortClear", "清空");
                sendButton.Content = owner.FindText("MultiPortSend", "发送");
                hexCheckBox.Content = "HEX";
                dtrCheckBox.Content = "DTR";
                rtsCheckBox.Content = "RTS";
                hexCheckBox.Checked += SlotProfileCheckBox_Changed;
                hexCheckBox.Unchecked += SlotProfileCheckBox_Changed;
            }

            private void UpdateTitle()
            {
                var port = IsOpen ? PortName : SelectedPortName;
                titleTextBlock.Text = string.Format(
                    owner.FindText("MultiPortSlotTitle", "串口 {0}"),
                    Index) + (string.IsNullOrWhiteSpace(port) ? "" : $" - {port}");
            }

            private void OpenButton_Click(object sender, RoutedEventArgs e)
            {
                ToggleOpen();
            }

            private bool Open()
            {
                LastErrorMessage = string.Empty;
                var portName = SelectedPortName;
                if (string.IsNullOrWhiteSpace(portName))
                {
                    LastErrorMessage = owner.FindText("MultiPortNoPort", "未选择串口。");
                    return false;
                }
                if (owner.IsPortOpenInOtherSlot(this, portName))
                {
                    LastErrorMessage = owner.FindText("MultiPortPortInUse", "该串口已在其它分屏打开。");
                    AppendLog("ERR", LastErrorMessage);
                    return false;
                }
                if (!int.TryParse(baudComboBox.Text, out var baudRate) || baudRate <= 0)
                {
                    LastErrorMessage = owner.FindText("MultiPortInvalidBaud", "波特率无效。");
                    AppendLog("ERR", LastErrorMessage);
                    return false;
                }

                try
                {
                    if (useMainUart)
                    {
                        Global.setting?.SetActiveUartProfile(portName);
                        if (Global.setting != null)
                            Global.setting.baudRate = baudRate;
                        Global.uart.SetName(portName);
                        Global.uart.Rts = rtsCheckBox.IsChecked == true;
                        Global.uart.Dtr = dtrCheckBox.IsChecked == true;
                        Global.uart.Open();
                        Logger.StartSessionLog(portName);
                    }
                    else
                    {
                        serial.PortName = portName;
                        serial.BaudRate = baudRate;
                        serial.DataBits = Global.setting?.dataBits ?? 8;
                        serial.Parity = (Parity)(Global.setting?.parity ?? 0);
                        serial.StopBits = (StopBits)(Global.setting?.stopBit ?? 1);
                        serial.Handshake = GetHandshake();
                        serial.DtrEnable = dtrCheckBox.IsChecked == true;
                        if (serial.Handshake != Handshake.RequestToSend)
                            serial.RtsEnable = rtsCheckBox.IsChecked == true;
                        Logger.AddUartLogDebug(
                            $"[SplitUartOpen]slot={Index},port={serial.PortName},baud={serial.BaudRate}," +
                            $"parity={serial.Parity},dataBits={serial.DataBits},stopBits={serial.StopBits}," +
                            $"handshake={serial.Handshake},dtr={serial.DtrEnable},rts={serial.RtsEnable}");
                        serial.Open();
                        EnsureSessionLogOpen();
                    }

                    SyncOpenStateUi();
                    UpdateTitle();
                    owner.UpdateStatus();
                    AppendLog("SYS", owner.FindText("MultiPortOpened", "已打开。"));
                    LastErrorMessage = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    LastErrorMessage = ex.Message;
                    AppendLog("ERR", owner.FindText("MultiPortOpenFailed", "打开失败: ") + LastErrorMessage);
                    try
                    {
                        if (useMainUart)
                        {
                            if (Global.uart.IsOpen())
                                Global.uart.Close();
                        }
                        else if (IsOpen)
                        {
                            serial.Close();
                        }
                    }
                    catch { }
                    SyncOpenStateUi();
                    return false;
                }
            }

            private void ClearButton_Click(object sender, RoutedEventArgs e)
            {
                ClearLog();
            }

            public void ClearLog()
            {
                logTextBox.Document.Blocks.Clear();
                logCharCount = 0;
            }

            public string GetLogText()
            {
                return new TextRange(logTextBox.Document.ContentStart, logTextBox.Document.ContentEnd).Text;
            }

            private async void SendButton_Click(object sender, RoutedEventArgs e)
            {
                if (!EnsureOpen())
                {
                    AppendLog("ERR", owner.FindText("MultiPortNotOpen", "请先打开串口。"));
                    return;
                }

                byte[] data;
                var sendAsHex = hexCheckBox.IsChecked == true;
                try
                {
                    data = sendAsHex
                        ? Global.Hex2Byte(sendTextBox.Text)
                        : GetEncoding().GetBytes(sendTextBox.Text ?? "");
                }
                catch (Exception ex)
                {
                    AppendLog("ERR", owner.FindText("MultiPortSendFailed", "发送失败: ") + ex.Message);
                    return;
                }

                if (data.Length == 0)
                    return;

                await SendBytesAsync(data, sendAsHex);
            }

            public async Task<bool> SendBytesAsync(byte[] data, bool displayAsHex)
            {
                if (!IsSelectedPortOpen)
                {
                    AppendLog("ERR", owner.FindText("MultiPortNotOpen", "请先打开串口。"));
                    return false;
                }

                var notOpenText = owner.FindText("MultiPortNotOpen", "请先打开串口。");
                sendButton.IsEnabled = false;
                try
                {
                    await Task.Run(() =>
                    {
                        lock (serialLock)
                        {
                            if (!IsSelectedPortOpen)
                                throw new InvalidOperationException(notOpenText);
                            if (useMainUart)
                                Global.uart.SendData(data);
                            else
                                WriteDirectSerial(data, CancellationToken.None);
                        }
                    });
                    if (!useMainUart)
                        WriteDataLog("TX", data, displayAsHex, true);
                    return true;
                }
                catch (Exception ex)
                {
                    AppendLog("ERR", owner.FindText("MultiPortSendFailed", "发送失败: ") + ex.Message);
                    return false;
                }
                finally
                {
                    sendButton.IsEnabled = true;
                }
            }

            public bool SendBytesBlocking(byte[] data, bool displayAsHex, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    lock (serialLock)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!IsSelectedPortOpen)
                        {
                            owner.RunOnUi(() => AppendLog("ERR", owner.FindText("MultiPortNotOpen", "请先打开串口。")));
                            return false;
                        }

                        if (useMainUart)
                            Global.uart.SendDataCancelable(data, token, null, raiseEvents: false);
                        else
                            WriteDirectSerial(data, token);
                    }

                    owner.RunOnUi(() => WriteDataLog("TX", data, displayAsHex, true, updateCounters: !useMainUart));
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    owner.RunOnUi(() => AppendLog("ERR", owner.FindText("MultiPortSendFailed", "发送失败: ") + ex.Message));
                    return false;
                }
            }

            private void ControlLineCheckBox_Changed(object sender, RoutedEventArgs e)
            {
                ApplyControlLines();
            }

            private void ApplyControlLines()
            {
                // 配置加载、刷新端口时只更新并保存当前端口的 UI 配置。
                // 只有端口真正打开后才改变硬件控制线，避免分屏切换过程中
                // 对仍在使用的主串口重复产生 DTR/RTS 边沿。
                if (!IsOpen)
                {
                    owner.SaveSlotProfile(this);
                    return;
                }

                if (useMainUart)
                {
                    try
                    {
                        Global.uart.Dtr = dtrCheckBox.IsChecked == true;
                        Global.uart.Rts = rtsCheckBox.IsChecked == true;
                        owner.SaveSlotProfile(this);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("ERR", ex.Message);
                    }
                    return;
                }

                try
                {
                    serial.DtrEnable = dtrCheckBox.IsChecked == true;
                    if (serial.Handshake != Handshake.RequestToSend)
                        serial.RtsEnable = rtsCheckBox.IsChecked == true;
                    owner.SaveSlotProfile(this);
                }
                catch (Exception ex) when (IsClosedSerialException(ex))
                {
                    owner.SaveSlotProfile(this);
                }
                catch (Exception ex)
                {
                    AppendLog("ERR", ex.Message);
                }
            }

            private void SlotProfileCheckBox_Changed(object sender, RoutedEventArgs e)
            {
                owner.SaveSlotProfile(this);
            }

            private static Handshake GetHandshake()
            {
                switch (Global.setting?.flowControl ?? 0)
                {
                    case 1:
                        return Handshake.RequestToSend;
                    case 2:
                        return Handshake.XOnXOff;
                    default:
                        return Handshake.None;
                }
            }

            private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
            {
                try
                {
                    byte[] data;
                    lock (serialLock)
                    {
                        using (var buffer = new MemoryStream())
                        {
                            while (serial.IsOpen)
                            {
                                var length = serial.BytesToRead;
                                if (length <= 0)
                                    break;

                                var block = new byte[length];
                                var read = serial.Read(block, 0, block.Length);
                                if (read <= 0)
                                    break;
                                buffer.Write(block, 0, read);
                            }
                            data = buffer.ToArray();
                        }
                    }

                    if (data.Length == 0)
                        return;
                    owner.RunOnUi(() =>
                    {
                        WriteDataLog("RX", data, HexMode, false);
                        if (Index == owner.activeSlotNumber)
                            Global.NotifyActiveSerialTargetReceived(data);
                    });
                }
                catch (Exception ex) when (IsClosedSerialException(ex))
                {
                }
                catch (Exception ex)
                {
                    owner.RunOnUi(() => AppendLog("ERR", ex.Message));
                }
            }

            private static void WaitForWriteDrain(SerialPort port, int byteCount, CancellationToken token)
            {
                var baudRate = Math.Max(1, port.BaudRate);
                var estimatedMilliseconds = (long)Math.Ceiling(byteCount * 11000d / baudRate);
                var timeoutMilliseconds = Math.Max(5000L, estimatedMilliseconds + 2000L);
                var stopwatch = Stopwatch.StartNew();
                while (port.BytesToWrite > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
                        throw new TimeoutException("等待串口发送缓冲区清空超时。");
                    Thread.Sleep(2);
                }
            }

            private void WriteDirectSerial(byte[] data, CancellationToken token)
            {
                Logger.AddUartLogDebug(
                    $"[SplitUartWrite]slot={Index},port={serial.PortName},baud={serial.BaudRate},bytes={data.Length}");
                serial.Write(data, 0, data.Length);
                WaitForWriteDrain(serial, data.Length, token);
            }

            private void MainUart_UartDataSent(object sender, EventArgs e)
            {
                var data = sender as byte[];
                if (data == null || data.Length == 0)
                    return;

                owner.RunOnUi(() =>
                    WriteDataLog("TX", data, HexMode, true, updateCounters: false, writeSessionLog: false));
            }

            private void MainUart_UartDataRawSent(object sender, EventArgs e)
            {
                var data = sender as byte[];
                if (data == null || data.Length == 0)
                    return;

                owner.RunOnUi(() =>
                    WriteDataLog("TX", data, true, true, updateCounters: false, writeSessionLog: false));
            }

            private void MainUart_UartDataRecived(object sender, EventArgs e)
            {
                var data = sender as byte[];
                if (data == null || data.Length == 0)
                    return;

                owner.RunOnUi(() =>
                    WriteDataLog("RX", data, HexMode, false, updateCounters: false, writeSessionLog: false));
            }

            private string FormatData(byte[] data)
            {
                return FormatData(data, hexCheckBox.IsChecked == true);
            }

            private string FormatData(byte[] data, bool hex)
            {
                return hex
                    ? Global.Byte2Hex(data, " ", data.Length)
                    : Global.Byte2Readable(data, data.Length);
            }

            private void WriteDataLog(string direction, byte[] data, bool hex, bool sent, bool updateCounters = true, bool writeSessionLog = true)
            {
                if (Global.setting.DisableLog)
                    return;

                if (updateCounters)
                {
                    if (sent)
                        Global.setting.SentCount += data.Length;
                    else
                        Global.setting.ReceivedCount += data.Length;
                }

                if (writeSessionLog)
                    WriteSessionLog(sent ? "send" : "recv", data);
                AppendLog(direction, FormatData(data, hex), sent ? ResourceBrush("AppDataSentBrush", Brushes.IndianRed) : ResourceBrush("AppDataReceivedBrush", Brushes.SeaGreen));
            }

            private void AppendLog(string direction, string text)
            {
                AppendLog(direction, text, GetLogBrush(direction));
            }

            private void AppendLog(string direction, string text, Brush dataBrush)
            {
                var linePrefix = $"[{DateTime.Now:HH:mm:ss.fff}] {direction} ";
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.Add(new Run(linePrefix) { Foreground = ResourceBrush("AppGlassMutedBrush", SystemColors.GrayTextBrush) });
                paragraph.Inlines.Add(new Run(text ?? "") { Foreground = dataBrush });
                logTextBox.Document.Blocks.Add(paragraph);
                logCharCount += linePrefix.Length + (text?.Length ?? 0) + 2;
                TrimLog();
                if (!owner.lockLogs)
                    logTextBox.ScrollToEnd();
            }

            private void TrimLog()
            {
                if (logCharCount <= MaxLogCharsPerSlot)
                    return;

                while (logCharCount > MaxLogCharsPerSlot - LogTrimChars && logTextBox.Document.Blocks.FirstBlock != null)
                {
                    var first = logTextBox.Document.Blocks.FirstBlock;
                    logCharCount -= new TextRange(first.ContentStart, first.ContentEnd).Text.Length;
                    logTextBox.Document.Blocks.Remove(first);
                }
            }

            private static Brush GetLogBrush(string direction)
            {
                switch (direction)
                {
                    case "TX":
                        return ResourceBrush("AppDataSentBrush", Brushes.IndianRed);
                    case "RX":
                        return ResourceBrush("AppDataReceivedBrush", Brushes.SeaGreen);
                    case "ERR":
                        return ResourceBrush("AppDangerBrush", Brushes.OrangeRed);
                    default:
                        return ResourceBrush("AppGlassTextBrush", SystemColors.ControlTextBrush);
                }
            }

            private static SolidColorBrush ResourceBrush(string key, SolidColorBrush fallback)
            {
                return Tools.Logger.GetThemeBrush(key, fallback);
            }

            private void EnsureSessionLogOpen()
            {
                if (Global.setting == null || !Global.setting.sessionLogEnabled || !IsOpen)
                    return;

                lock (sessionLogLock)
                {
                    if (sessionStringLogWriter != null || sessionHexLogWriter != null)
                        return;

                    try
                    {
                        var folder = Global.setting.sessionLogFolder;
                        if (string.IsNullOrWhiteSpace(folder))
                        {
                            folder = Path.Combine(Global.ProfilePath, "session_logs");
                            Global.setting.sessionLogFolder = folder;
                        }

                        var safePortName = MakeSafeFileName(string.IsNullOrWhiteSpace(PortName) ? $"COM{Index}" : PortName);
                        var portFolder = Path.Combine(folder, safePortName);
                        var stringFolder = Path.Combine(portFolder, "STRING");
                        var hexFolder = Path.Combine(portFolder, "HEX");
                        Directory.CreateDirectory(stringFolder);
                        Directory.CreateDirectory(hexFolder);

                        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_slot{Index}.log";
                        sessionStringLogWriter = CreateSessionLogWriter(Path.Combine(stringFolder, fileName));
                        sessionHexLogWriter = CreateSessionLogWriter(Path.Combine(hexFolder, fileName));
                        var startLine = $"[START] {DateTime.Now:yyyy/MM/dd HH:mm:ss.fff} {PortName} slot {Index}";
                        sessionStringLogWriter.WriteLine(startLine);
                        sessionHexLogWriter.WriteLine(startLine);
                    }
                    catch (Exception ex)
                    {
                        CloseSessionLog();
                        Logger.AddUartLogDebug($"[SplitSessionLog]start failed:{ex.Message}");
                    }
                }
            }

            private void CloseSessionLog()
            {
                lock (sessionLogLock)
                {
                    try
                    {
                        var endLine = $"[END] {DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}";
                        sessionStringLogWriter?.WriteLine(endLine);
                        sessionHexLogWriter?.WriteLine(endLine);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        sessionStringLogWriter?.Dispose();
                        sessionHexLogWriter?.Dispose();
                        sessionStringLogWriter = null;
                        sessionHexLogWriter = null;
                    }
                }
            }

            private void WriteSessionLog(string direction, byte[] data)
            {
                if (data == null || data.Length == 0)
                    return;

                if (Global.setting == null || !Global.setting.sessionLogEnabled)
                {
                    CloseSessionLog();
                    return;
                }

                EnsureSessionLogOpen();
                lock (sessionLogLock)
                {
                    if (sessionStringLogWriter == null && sessionHexLogWriter == null)
                        return;

                    try
                    {
                        var prefix = $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}] [{direction}]";
                        sessionStringLogWriter?.WriteLine($"{prefix} {Byte2SessionString(data)}");
                        sessionHexLogWriter?.WriteLine($"{prefix} {Global.Byte2Hex(data, " ", data.Length)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.AddUartLogDebug($"[SplitSessionLog]write failed:{ex.Message}");
                    }
                }
            }

            private static StreamWriter CreateSessionLogWriter(string path)
            {
                return new StreamWriter(
                    new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }

            private static string MakeSafeFileName(string value)
            {
                foreach (var c in Path.GetInvalidFileNameChars())
                    value = value.Replace(c, '_');
                return value;
            }

            private static string Byte2SessionString(byte[] data)
            {
                return EscapeSessionString(Global.Byte2Readable(data, data.Length));
            }

            private static string EscapeSessionString(string text)
            {
                if (text == null)
                    return "";
                return text
                    .Replace("\\", "\\\\")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
            }

            private static Encoding GetEncoding()
            {
                try { return Global.GetEncoding(); }
                catch { return Encoding.UTF8; }
            }

            private static bool IsClosedSerialException(Exception ex)
            {
                return ex is ObjectDisposedException ||
                    ex is IOException ||
                    ex is InvalidOperationException;
            }
        }
    }
}
