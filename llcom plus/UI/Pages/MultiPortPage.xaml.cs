using llcom_plus.Tools;
using llcom_plus.Model;
using System;
using System.Collections;
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
        private readonly string pageIdentity = Guid.NewGuid().ToString("N");
        private int slotCount;
        private readonly bool showSlotSendPanel;
        private readonly List<PortSlot> slots = new List<PortSlot>();
        private Settings subscribedSettings;
        private bool slotsCreated;
        private bool subscribedProgramClosed;
        private bool updatingExternalControls;
        private bool suppressSlotProfileSave;
        private bool lockLogs;
        private bool portsReleased;
        private int activeSlotNumber = 1;
        private Window ownerWindow;
        private string initialFirstPortName;
        private DataShowPage.LogSnapshot initialFirstLogSnapshot;

        public event Action<int> ActiveSlotChanged;
        public event Action<int> SlotCountChanged;
        public int SlotCount => slotsCreated ? slots.Count : slotCount;

        public MultiPortPage() : this(MaxSlotCount, true, null)
        {
        }

        public MultiPortPage(int slotCount, bool showSlotSendPanel = true, string initialFirstPortName = null)
            : this(slotCount, showSlotSendPanel, initialFirstPortName, null)
        {
        }

        internal MultiPortPage(
            int slotCount,
            bool showSlotSendPanel,
            string initialFirstPortName,
            DataShowPage.LogSnapshot initialFirstLogSnapshot)
        {
            this.slotCount = Math.Max(1, Math.Min(MaxSlotCount, slotCount));
            this.showSlotSendPanel = showSlotSendPanel;
            this.initialFirstPortName = NormalizePortName(initialFirstPortName);
            this.initialFirstLogSnapshot = initialFirstLogSnapshot;
            InitializeComponent();
            ToolbarPanel.Visibility = showSlotSendPanel ? Visibility.Visible : Visibility.Collapsed;
            ExternalOptionsButton.Visibility = Visibility.Collapsed;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!slotsCreated)
            {
                slotsCreated = true;
                for (var i = 0; i < slotCount; i++)
                {
                    // 主界面分屏的窗口 1 继续复用 Global.uart，确保进入/退出分屏
                    // 都不会关闭主串口，已有打印和后续收发也沿用同一条事件链。
                    var slot = new PortSlot(this, i + 1, !showSlotSendPanel && i == 0);
                    slots.Add(slot);
                }
                RebuildGridLayout();
            }

            if (!subscribedProgramClosed)
            {
                Global.ProgramClosedEvent += Global_ProgramClosedEvent;
                Global.LogColorsChanged += Global_LogColorsChanged;
                subscribedProgramClosed = true;
            }
            if (!ReferenceEquals(subscribedSettings, Global.setting))
            {
                if (subscribedSettings != null)
                    subscribedSettings.UartProcessingSettingsChanged -= Settings_UartProcessingSettingsChanged;
                subscribedSettings = Global.setting;
                if (subscribedSettings != null)
                    subscribedSettings.UartProcessingSettingsChanged += Settings_UartProcessingSettingsChanged;
            }

            BindExternalGlobalOptions();
            ownerWindow = Window.GetWindow(this);
            if (ownerWindow != null)
            {
                ownerWindow.PreviewMouseDown += OwnerWindow_PreviewMouseDown;
                ownerWindow.Deactivated += OwnerWindow_Deactivated;
            }
            RefreshPorts();
            ApplyInitialFirstState();
            ActivateSettingsProfileForActiveSlot();
            UpdateStatus();
            ActiveSlotChanged?.Invoke(activeSlotNumber);
        }

        private void ApplyInitialFirstState()
        {
            var slot = GetSlot(1);
            if (slot != null &&
                !slot.IsOpen &&
                !string.IsNullOrWhiteSpace(initialFirstPortName) &&
                slot.HasAvailablePort(initialFirstPortName))
            {
                slot.SetPortName(initialFirstPortName);
                ApplyPortProfile(slot);
                var currentPorts = GetPortNames();
                foreach (var otherSlot in slots.Skip(1))
                {
                    otherSlot.RefreshPorts(currentPorts);
                    ApplyPortProfile(otherSlot);
                }
            }

            if (slot != null && initialFirstLogSnapshot != null)
                slot.SetLogSnapshot(initialFirstLogSnapshot);

            initialFirstPortName = string.Empty;
            initialFirstLogSnapshot = null;
        }

        public bool AddSlot()
        {
            if (showSlotSendPanel || SlotCount >= MaxSlotCount)
                return false;

            AddSlotCore();
            SlotCountChanged?.Invoke(SlotCount);
            SetActiveSlot(SlotCount, true);
            return true;
        }

        public bool RemoveSlot(int slotNumber)
        {
            if (showSlotSendPanel || SlotCount <= 1)
                return false;

            if (!RemoveSlotCore(slotNumber))
                return false;

            SlotCountChanged?.Invoke(SlotCount);
            ActiveSlotChanged?.Invoke(activeSlotNumber);
            return true;
        }

        public void ResizeSlotCount(int requestedCount)
        {
            var normalizedCount = Math.Max(1, Math.Min(MaxSlotCount, requestedCount));
            if (!slotsCreated)
            {
                slotCount = normalizedCount;
                return;
            }

            while (slots.Count < normalizedCount)
                AddSlotCore();
            while (slots.Count > normalizedCount)
                RemoveSlotCore(slots.Count);

            activeSlotNumber = Math.Max(1, Math.Min(activeSlotNumber, SlotCount));
            RefreshExternalControls();
            UpdateStatus();
        }

        private void AddSlotCore()
        {
            var slot = new PortSlot(this, slots.Count + 1, false);
            slots.Add(slot);
            slotCount = slots.Count;
            slot.RefreshPorts(GetPortNames());
            ApplyPortProfile(slot);
            if (Global.setting?.sessionLogEnabled == true)
                slot.RestartSessionLog();
            RebuildGridLayout();
            UpdateStatus();
        }

        private bool RemoveSlotCore(int slotNumber)
        {
            var index = slotNumber - 1;
            if (index < 0 || index >= slots.Count || slots.Count <= 1)
                return false;

            var slot = slots[index];
            slot.Close(waitForDispose: true);
            slot.DisposeOwnedPort();
            SlotsGrid.Children.Remove(slot.Root);
            slots.RemoveAt(index);

            for (var i = 0; i < slots.Count; i++)
                slots[i].SetIndex(i + 1);

            slotCount = slots.Count;
            if (activeSlotNumber > slotNumber)
                activeSlotNumber--;
            else if (activeSlotNumber == slotNumber)
                activeSlotNumber = Math.Min(slotNumber, slotCount);
            activeSlotNumber = Math.Max(1, activeSlotNumber);

            RebuildGridLayout();
            ActivateSettingsProfileForActiveSlot();
            RefreshExternalControls();
            UpdateStatus();
            return true;
        }

        private void BindExternalGlobalOptions()
        {
            if (showSlotSendPanel)
                return;

            ExternalOptionsButton.DataContext = Global.setting;
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

        private void RebuildGridLayout()
        {
            ConfigureGridLayout();
            foreach (var slot in slots)
            {
                AddSlotToGrid(slot);
                slot.UpdateCloseButtonVisibility();
            }
        }

        private void AddSlotToGrid(PortSlot slot)
        {
            Grid.SetRowSpan(slot.Root, 1);
            Grid.SetColumnSpan(slot.Root, 1);
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
                ownerWindow.Deactivated -= OwnerWindow_Deactivated;
                ownerWindow = null;
            }
            if (subscribedProgramClosed)
            {
                Global.ProgramClosedEvent -= Global_ProgramClosedEvent;
                Global.LogColorsChanged -= Global_LogColorsChanged;
                subscribedProgramClosed = false;
            }
            if (subscribedSettings != null)
            {
                subscribedSettings.UartProcessingSettingsChanged -= Settings_UartProcessingSettingsChanged;
                subscribedSettings = null;
            }
        }

        private void Settings_UartProcessingSettingsChanged(object sender, EventArgs e)
        {
            RunOnUi(() =>
            {
                var settings = Global.setting;
                if (settings == null)
                    return;

                var slot = GetSlot(activeSlotNumber);
                if (slot == null || string.IsNullOrWhiteSpace(slot.SelectedPortName))
                    return;
                if (!string.Equals(
                        settings.ActiveUartProfileName,
                        slot.SelectedPortName,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                var merged = settings.MergeCurrentUartProcessingSettings(slot.GetProfileSnapshot());
                slot.ApplyProcessingSettings(merged);
                settings.SaveUartProfileSnapshot(slot.SelectedPortName, merged);
            });
        }

        private void Global_LogColorsChanged(object sender, EventArgs e)
        {
            RunOnUi(() =>
            {
                foreach (var slot in slots)
                    slot.RefreshLogColors();
            });
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

        private void OwnerWindow_Deactivated(object sender, EventArgs e)
        {
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
                    var notificationText = Logger.GetPortNotificationLogText(slot.SelectedPortName);
                    if (!string.IsNullOrWhiteSpace(notificationText))
                    {
                        writer.WriteLine("===== NOTIFICATIONS =====");
                        writer.Write(notificationText);
                    }
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

            var mainSlot = slots.FirstOrDefault(slot => slot.UsesMainUart);
            if (mainSlot != null && !string.IsNullOrWhiteSpace(mainSlot.SelectedPortName))
                Global.setting?.SetActiveUartProfile(mainSlot.SelectedPortName, usesMainUart: true);

            portsReleased = true;
            CloseAll(
                closeMainUart: false,
                detachMainUart: true,
                waitForDispose: true,
                disposeOwnedPorts: true);
            Global.uart?.ClearRuntimeProfileOverride();
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
            ActivateSettingsProfileForActiveSlot();
            UpdateStatus();
        }

        public void RefreshSlotPorts(string[] ports)
        {
            foreach (var slot in slots)
            {
                slot.RefreshPorts(ports);
                ApplyPortProfile(slot);
            }
            ActivateSettingsProfileForActiveSlot();
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
            ActivateSettingsProfileForActiveSlot();
            RefreshExternalControls();
            if (notify)
                ActiveSlotChanged?.Invoke(activeSlotNumber);
        }

        private void ActivateSlotFromPane(int slotNumber)
        {
            SetActiveSlot(slotNumber, true);
        }

        private void ActivateSettingsProfileForActiveSlot()
        {
            var slot = GetSlot(activeSlotNumber);
            if (slot == null || string.IsNullOrWhiteSpace(slot.SelectedPortName))
                return;

            Global.setting?.SetActiveUartProfile(
                slot.SelectedPortName,
                usesMainUart: slot.UsesMainUart);
            if (Global.setting != null)
                llcom_plus.MainWindow.recvScriptBackup = Global.setting.recvScript;
        }

        public string GetSlotPortName(int slotNumber)
        {
            var slot = GetSlot(slotNumber);
            return slot?.SelectedPortName ?? "";
        }

        internal DataShowPage.LogSnapshot GetSlotLogSnapshot(int slotNumber)
        {
            return GetSlot(slotNumber)?.GetLogSnapshot();
        }

        public void SetSlotPortName(int slotNumber, string portName)
        {
            var slot = GetSlot(slotNumber);
            if (slot == null)
                return;

            // 选择端口只更新待使用端口；发送或状态按钮才切换实际连接。
            slot.SetPortName(portName);
            ApplyPortProfile(slot);
            if (slotNumber == activeSlotNumber)
                ActivateSettingsProfileForActiveSlot();
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

        public Task<bool> SendBytesAsync(int slotNumber, byte[] data)
        {
            if (data == null || data.Length == 0)
                return Task.FromResult(false);

            var index = Math.Max(1, Math.Min(slotCount, slotNumber)) - 1;
            if (index < 0 || index >= slots.Count)
                return Task.FromResult(false);

            return slots[index].SendBytesAsync(data);
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

        public bool SendBytesBlocking(int slotNumber, byte[] data, CancellationToken token)
        {
            if (data == null || data.Length == 0)
                return false;

            var target = CaptureSerialTarget(slotNumber);
            return target?.IsOpen == true && target.Send(data, token, null);
        }

        internal ActiveSerialTarget CaptureSerialTarget(int slotNumber)
        {
            return GetSlot(slotNumber)?.CaptureSerialTarget();
        }

        internal UartPortProfile GetSlotProfileSnapshot(int slotNumber)
        {
            return GetSlot(slotNumber)?.GetProfileSnapshot();
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

        private void ExternalControlCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (updatingExternalControls ||
                !(sender is CheckBox checkBox))
            {
                return;
            }

            var slot = GetSlot(activeSlotNumber);
            if (slot == null)
                return;

            PublishControlLineNotification(
                slot,
                ReferenceEquals(checkBox, ExternalRTSCheckBox) ? "RTS" : "DTR");
        }

        private void PublishControlLineNotification(PortSlot slot, string lineName)
        {
            if (slot == null)
                return;

            var portName = string.IsNullOrWhiteSpace(slot.SelectedPortName)
                ? FindText("SerialPinUnknownPort", "串口")
                : slot.SelectedPortName;
            var title = string.Format(
                FindText("NotificationControlLineTitleFormat", "{0} 手动切换 {1}"),
                portName,
                lineName);
            var message = string.Format(
                FindText("NotificationControlLineMessageFormat", "RTS:{0}  DTR:{1}"),
                slot.Rts ? "1" : "0",
                slot.Dtr ? "1" : "0");
            Global.PublishNotification(
                title,
                message,
                AppNotificationLevel.Info,
                category: AppNotificationCategory.SerialPin,
                portName: portName);
            Logger.AddUartLogDebug(
                $"[SplitControlLineManual]slot={slot.Index},port={portName} {lineName} {message}");
        }

        private void ApplyPortProfile(PortSlot slot)
        {
            if (slot == null)
                return;

            if (slot.IsOpen &&
                !string.Equals(
                    slot.PortName,
                    slot.SelectedPortName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var profile = Global.setting?.GetUartProfileSnapshot(slot.SelectedPortName);
            if (profile == null)
                return;

            suppressSlotProfileSave = true;
            try
            {
                slot.ApplyProfile(profile);
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

            var profile = slot.GetProfileSnapshot() ??
                Global.setting?.GetUartProfileSnapshot(slot.SelectedPortName);
            if (profile == null)
                return;

            profile.baudRate = slot.BaudRate;
            profile.hexSend = slot.HexMode;
            profile.rts = slot.Rts;
            profile.dtr = slot.Dtr;
            slot.UpdateProfileSnapshot(profile);
            Global.setting?.SaveUartProfileSnapshot(slot.SelectedPortName, profile);
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

        public void WritePortNotificationToSession(
            DateTime timestamp,
            string portName,
            string title,
            string message)
        {
            var normalizedPortName = NormalizePortName(portName);
            foreach (var slot in slots.Where(slot =>
                string.Equals(slot.SelectedPortName, normalizedPortName, StringComparison.OrdinalIgnoreCase)))
            {
                slot.WriteNotificationToSession(timestamp, title, message);
            }
        }

        private bool IsPortSelectedInEarlierSlot(PortSlot target, string portName)
        {
            return slots.Any(slot =>
                slot.Index < target.Index &&
                string.Equals(slot.SelectedPortName, portName, StringComparison.OrdinalIgnoreCase));
        }

        private string GetFirstAvailablePort(PortSlot target, IEnumerable<string> ports)
        {
            var assignedPorts = new HashSet<string>(
                slots
                    .Where(slot => !ReferenceEquals(slot, target))
                    .Select(slot => slot.SelectedPortName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            return ports.FirstOrDefault(port => !assignedPorts.Contains(port)) ?? string.Empty;
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
            private readonly Button closeButton = new Button();
            private readonly RichTextBox logTextBox = new RichTextBox();
            private readonly TextBox sendTextBox = new TextBox();
            private readonly TextBlock titleTextBlock = new TextBlock();
            private readonly object serialLock = new object();
            private readonly object serialLifecycleLock = new object();
            private readonly object sendLock = new object();
            private readonly object receiveBufferLock = new object();
            private readonly object sessionLogLock = new object();
            private readonly List<byte> pendingReceiveData = new List<byte>();
            private readonly Dictionary<Block, DataShowPage.DataShow> packedLogItems = new Dictionary<Block, DataShowPage.DataShow>();
            private SerialPinMonitor pinMonitor;
            private Timer receiveFlushTimer;
            private bool receiveFlushScheduled;
            private bool serialTransition;
            private bool serialDisposed;
            private bool applyingProfile;
            private long connectionGeneration;
            private string selectedPortName = "";
            private UartPortProfile profileSnapshot = new UartPortProfile();
            private StreamWriter sessionStringLogWriter;
            private StreamWriter sessionHexLogWriter;
            private int logCharCount;
            private bool lastPackedLogMode;
            private Paragraph plainDataParagraph;

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
                    Global.uart.SetDirectReceiveMode(true);
                }
                else
                {
                    serial.DataReceived += Serial_DataReceived;
                    serial.WriteTimeout = 5000;
                    serial.ReadTimeout = 500;
                    receiveFlushTimer = new Timer(FlushReceivedData, null, Timeout.Infinite, Timeout.Infinite);
                    pinMonitor = new SerialPinMonitor(serial, Global.NotifySerialPinStatusChanged);
                }
            }

            public int Index { get; private set; }
            public Border Root { get; }
            public string DisplayTitle => titleTextBlock.Text ?? string.Format("Port {0}", Index);
            public bool UsesMainUart => useMainUart;
            public bool IsOpen
            {
                get
                {
                    if (useMainUart)
                        return Global.uart.IsOpen();

                    lock (serialLock)
                    {
                        if (serialDisposed || serialTransition)
                            return false;
                        try { return serial.IsOpen; }
                        catch (Exception ex) when (IsClosedSerialException(ex)) { return false; }
                    }
                }
            }
            public string PortName
            {
                get
                {
                    if (useMainUart)
                        return NormalizePortName(Global.uart.GetName());

                    lock (serialLock)
                    {
                        try { return NormalizePortName(serial.PortName); }
                        catch (Exception ex) when (IsClosedSerialException(ex)) { return SelectedPortName; }
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
                set { rtsCheckBox.IsChecked = value; }
            }
            public bool Dtr
            {
                get { return dtrCheckBox.IsChecked == true; }
                set { dtrCheckBox.IsChecked = value; }
            }
            public bool HexMode
            {
                get { return hexCheckBox.IsChecked == true; }
                set { hexCheckBox.IsChecked = value; }
            }

            public void ApplyProfile(UartPortProfile profile)
            {
                if (profile == null)
                    return;

                applyingProfile = true;
                try
                {
                    profileSnapshot = CloneProfile(profile);
                    if (useMainUart)
                        Global.uart.SetRuntimeProfileOverride(profileSnapshot);
                    SetBaudRateText(profileSnapshot.baudRate);
                    hexCheckBox.IsChecked = profileSnapshot.hexSend;
                    dtrCheckBox.IsChecked = profileSnapshot.dtr;
                    rtsCheckBox.IsChecked = profileSnapshot.rts;
                    lastPackedLogMode = profileSnapshot.timeout >= 0;
                }
                finally
                {
                    applyingProfile = false;
                }
            }

            public void UpdateProfileSnapshot(UartPortProfile profile)
            {
                if (profile != null)
                {
                    profileSnapshot = CloneProfile(profile);
                    if (useMainUart)
                        Global.uart.SetRuntimeProfileOverride(profileSnapshot);
                }
            }

            public void ApplyProcessingSettings(UartPortProfile profile)
            {
                if (profile == null)
                    return;

                UpdateProfileSnapshot(profile);
                lastPackedLogMode = profileSnapshot.timeout >= 0;
                if (useMainUart)
                    return;

                lock (serialLock)
                {
                    if (serialDisposed)
                        return;

                    try
                    {
                        serial.DataBits = profileSnapshot.dataBits;
                        serial.Parity = (Parity)profileSnapshot.parity;
                        serial.StopBits = (StopBits)profileSnapshot.stopBit;
                        serial.Handshake = GetHandshake(profileSnapshot.flowControl);
                        if (serial.Handshake != Handshake.RequestToSend)
                            serial.RtsEnable = profileSnapshot.rts;
                    }
                    catch (Exception ex)
                    {
                        Logger.AddUartLogDebug(
                            $"[SplitSettings]slot={Index},port={SelectedPortName},apply failed:{ex.Message}");
                    }
                }
            }

            public UartPortProfile GetProfileSnapshot()
            {
                return CloneProfile(profileSnapshot);
            }

            public ActiveSerialTarget CaptureSerialTarget()
            {
                var displayName = DisplayTitle;
                var profile = GetProfileSnapshot();
                if (useMainUart)
                {
                    var connection = Global.uart.CaptureConnectionLease();
                    return new ActiveSerialTarget(
                        $"split:{owner.pageIdentity}:slot:{Index}:{connection.Identity}",
                        displayName,
                        () => connection.IsOpen,
                        (data, token, committedBytes) =>
                            SendCapturedMainUart(connection, data, token, committedBytes));
                }

                long generation;
                string portName;
                lock (serialLock)
                {
                    generation = connectionGeneration;
                    try { portName = NormalizePortName(serial.PortName); }
                    catch (Exception ex) when (IsClosedSerialException(ex)) { portName = SelectedPortName; }
                }

                return new ActiveSerialTarget(
                    $"split:{owner.pageIdentity}:slot:{Index}:{portName}:{generation}",
                    displayName,
                    () => IsDirectConnectionOpen(generation),
                    (data, token, committedBytes) =>
                        SendCapturedDirect(generation, portName, profile, data, token, committedBytes));
            }

            private static UartPortProfile CloneProfile(UartPortProfile profile)
            {
                profile = profile ?? new UartPortProfile();
                return new UartPortProfile
                {
                    baudRate = profile.baudRate,
                    autoReconnect = profile.autoReconnect,
                    showHexFormat = profile.showHexFormat,
                    hexSend = profile.hexSend,
                    showSend = profile.showSend,
                    showSendRaw = profile.showSendRaw,
                    parity = profile.parity,
                    timeout = profile.timeout,
                    dataBits = profile.dataBits,
                    stopBit = profile.stopBit,
                    flowControl = profile.flowControl,
                    sendThrottlePacketSize = profile.sendThrottlePacketSize,
                    sendThrottleDelayMs = profile.sendThrottleDelayMs,
                    bitDelay = profile.bitDelay,
                    maxLength = profile.maxLength,
                    sendScript = profile.sendScript,
                    recvScript = profile.recvScript,
                    terminal = profile.terminal,
                    encoding = profile.encoding,
                    extraEnter = profile.extraEnter,
                    enterSend = profile.enterSend,
                    enableSymbol = profile.enableSymbol,
                    rts = profile.rts,
                    dtr = profile.dtr
                };
            }

            private bool IsDirectConnectionOpen(long generation)
            {
                lock (serialLock)
                {
                    if (serialDisposed || serialTransition || generation != connectionGeneration)
                        return false;
                    try { return serial.IsOpen; }
                    catch (Exception ex) when (IsClosedSerialException(ex)) { return false; }
                }
            }

            private long BeginSerialTransition()
            {
                lock (serialLock)
                {
                    connectionGeneration = unchecked(connectionGeneration + 1);
                    if (connectionGeneration == 0)
                        connectionGeneration = 1;
                    serialTransition = true;
                    return connectionGeneration;
                }
            }

            private void CompleteSerialTransition(long generation)
            {
                lock (serialLock)
                {
                    if (generation == connectionGeneration)
                        serialTransition = false;
                }
            }

            public void SetIndex(int index)
            {
                Index = Math.Max(1, index);
                UpdateTitle();
            }

            public void UpdateCloseButtonVisibility()
            {
                closeButton.Visibility =
                    !owner.showSlotSendPanel && owner.SlotCount > 1
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }

            public void RefreshPorts(string[] ports)
            {
                ports = (ports ?? new string[0])
                    .Select(NormalizePortName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var selected = SelectedPortName;
                if (string.IsNullOrWhiteSpace(selected))
                    selected = PortName;

                portComboBox.Items.Clear();
                foreach (var port in ports)
                    portComboBox.Items.Add(port);

                if (!string.IsNullOrWhiteSpace(selected) &&
                    portComboBox.Items.Contains(selected) &&
                    (IsOpen || !owner.IsPortSelectedInEarlierSlot(this, selected)))
                {
                    selectedPortName = selected;
                    portComboBox.Text = selected;
                }
                else if (portComboBox.Items.Count > 0)
                {
                    var availablePort = owner.GetFirstAvailablePort(this, ports);
                    selectedPortName = !string.IsNullOrWhiteSpace(availablePort)
                        ? availablePort
                        : NormalizePortName(portComboBox.Items[0]?.ToString());
                    portComboBox.SelectedItem = selectedPortName;
                }
                else if (!IsOpen)
                {
                    selectedPortName = string.Empty;
                    portComboBox.Text = string.Empty;
                }

                SyncOpenStateUi();
            }

            public bool HasAvailablePort(string portName)
            {
                var normalizedPortName = NormalizePortName(portName);
                return !string.IsNullOrWhiteSpace(normalizedPortName) &&
                    portComboBox.Items.Cast<object>().Any(item =>
                        string.Equals(
                            NormalizePortName(item?.ToString()),
                            normalizedPortName,
                            StringComparison.OrdinalIgnoreCase));
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
                if (useMainUart)
                {
                    try
                    {
                        if (closeMainUart && Global.uart.IsOpen())
                        {
                            Global.uart.Close(waitForDispose);
                            Logger.StopSessionLog();
                        }
                    }
                    catch (Exception ex)
                    {
                        closeError = ex;
                    }
                }
                else
                {
                    var generation = BeginSerialTransition();
                    string portName;
                    try { portName = serial.PortName; }
                    catch { portName = SelectedPortName; }
                    pinMonitor?.Disarm();
                    Logger.AddUartLogDebug($"[SplitUartClose]slot={Index},port={portName},generation={generation}");
                    try
                    {
                        lock (serialLifecycleLock)
                        {
                            if (serial.IsOpen)
                                serial.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        closeError = ex;
                    }
                    finally
                    {
                        CompleteSerialTransition(generation);
                    }
                }

                FlushReceivedData(null);
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

                Global.uart.SetDirectReceiveMode(false);
                Global.uart.UartDataRecived -= MainUart_UartDataRecived;
                Global.uart.UartDataSent -= MainUart_UartDataSent;
            }

            public void DisposeOwnedPort()
            {
                if (useMainUart)
                    return;

                BeginSerialTransition();
                lock (serialLock)
                    serialDisposed = true;

                try { pinMonitor?.Dispose(); }
                catch { }
                pinMonitor = null;
                try { serial.DataReceived -= Serial_DataReceived; }
                catch { }
                lock (serialLifecycleLock)
                {
                    try { serial.Close(); }
                    catch { }
                    try { serial.Dispose(); }
                    catch { }
                }
                lock (receiveBufferLock)
                {
                    pendingReceiveData.Clear();
                    receiveFlushScheduled = false;
                    try { receiveFlushTimer?.Dispose(); }
                    catch { }
                    receiveFlushTimer = null;
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

            public void WriteNotificationToSession(DateTime timestamp, string title, string message)
            {
                if (useMainUart || !IsOpen)
                    return;

                EnsureSessionLogOpen();
                var effectiveTimestamp = timestamp == default(DateTime) ? DateTime.Now : timestamp;
                var line = $"[{effectiveTimestamp:yyyy/MM/dd HH:mm:ss.fff}] [notice] {title ?? string.Empty}";
                if (!string.IsNullOrWhiteSpace(message))
                    line += " | " + message;
                lock (sessionLogLock)
                {
                    sessionStringLogWriter?.WriteLine(line);
                    sessionHexLogWriter?.WriteLine(line);
                }
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

                var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleTextBlock.FontWeight = FontWeights.SemiBold;
                titleTextBlock.VerticalAlignment = VerticalAlignment.Center;
                header.Children.Add(titleTextBlock);

                closeButton.Content = "×";
                closeButton.HorizontalAlignment = HorizontalAlignment.Right;
                closeButton.VerticalAlignment = VerticalAlignment.Top;
                closeButton.Margin = new Thickness(0, -16, -16, 0);
                closeButton.ToolTip = owner.FindText("RemoveSerialSplitPaneTip", "删除这个分屏");
                closeButton.SetResourceReference(FrameworkElement.StyleProperty, "SplitPaneCloseButtonStyle");
                closeButton.Click += CloseButton_Click;
                Grid.SetRow(header, 0);
                grid.Children.Add(header);
                Grid.SetRow(closeButton, 0);
                Grid.SetRowSpan(closeButton, 4);
                Panel.SetZIndex(closeButton, 40);
                grid.Children.Add(closeButton);
                UpdateCloseButtonVisibility();

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
                hexCheckBox.IsChecked = profileSnapshot.hexSend;
                dtrCheckBox.IsChecked = useMainUart && Global.uart.Dtr;
                rtsCheckBox.IsChecked = useMainUart && Global.uart.Rts;
                dtrCheckBox.Checked += ControlLineCheckBox_Changed;
                dtrCheckBox.Unchecked += ControlLineCheckBox_Changed;
                dtrCheckBox.Click += ControlLineCheckBox_Click;
                rtsCheckBox.Checked += ControlLineCheckBox_Changed;
                rtsCheckBox.Unchecked += ControlLineCheckBox_Changed;
                rtsCheckBox.Click += ControlLineCheckBox_Click;

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
                logTextBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                logTextBox.FontFamily = new FontFamily("Consolas,Microsoft YaHei,微软雅黑");
                logTextBox.FontSize = 12;
                logTextBox.Background = Brushes.Transparent;
                logTextBox.BorderBrush = Brushes.Transparent;
                logTextBox.BorderThickness = new Thickness(0);
                logTextBox.FocusVisualStyle = null;
                var logContextMenu = new ContextMenu();
                logContextMenu.Items.Add(new MenuItem
                {
                    Header = owner.FindText("LogCopySelection", "复制选中内容"),
                    Command = ApplicationCommands.Copy,
                    CommandTarget = logTextBox
                });
                logContextMenu.Items.Add(new MenuItem
                {
                    Header = owner.FindText("LogSelectAll", "全选日志"),
                    Command = ApplicationCommands.SelectAll,
                    CommandTarget = logTextBox
                });
                logTextBox.ContextMenu = logContextMenu;
                lastPackedLogMode = GetProfileSnapshot().timeout >= 0;
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
                if (Index == owner.activeSlotNumber)
                    owner.ActivateSettingsProfileForActiveSlot();
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
                    else
                    {
                        lock (serialLock)
                        {
                            if (!serialDisposed && !serialTransition && serial.IsOpen)
                                serial.BaudRate = baudRate;
                        }
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

                var profile = GetProfileSnapshot();
                profile.baudRate = baudRate;
                profile.hexSend = HexMode;
                profile.rts = Rts;
                profile.dtr = Dtr;
                UpdateProfileSnapshot(profile);
                long generation = 0;

                try
                {
                    if (useMainUart)
                    {
                        Global.setting?.SetActiveUartProfile(portName);
                        if (Global.setting != null)
                            Global.setting.baudRate = baudRate;
                        Global.uart.SetName(portName);
                        Global.uart.Rts = profile.rts;
                        Global.uart.Dtr = profile.dtr;
                        Global.uart.Open();
                        Logger.StartSessionLog(portName);
                    }
                    else
                    {
                        lock (serialLock)
                        {
                            if (serialDisposed)
                                throw new ObjectDisposedException(nameof(PortSlot));
                        }

                        ResetReceiveBuffer();
                        generation = BeginSerialTransition();
                        var handshake = GetHandshake(profile.flowControl);
                        Logger.AddUartLogDebug(
                            $"[SplitUartOpen]slot={Index},port={portName},baud={baudRate}," +
                            $"parity={(Parity)profile.parity},dataBits={profile.dataBits},stopBits={(StopBits)profile.stopBit}," +
                            $"handshake={handshake},dtr={profile.dtr},rts={profile.rts},generation={generation}");
                        lock (serialLifecycleLock)
                        {
                            serial.PortName = portName;
                            serial.BaudRate = baudRate;
                            serial.DataBits = profile.dataBits;
                            serial.Parity = (Parity)profile.parity;
                            serial.StopBits = (StopBits)profile.stopBit;
                            serial.Handshake = handshake;
                            serial.DtrEnable = profile.dtr;
                            if (serial.Handshake != Handshake.RequestToSend)
                                serial.RtsEnable = profile.rts;
                            serial.Open();
                        }

                        lock (serialLock)
                        {
                            if (serialDisposed || generation != connectionGeneration)
                                throw new IOException("The split serial connection changed while opening.");
                            serialTransition = false;
                        }
                        pinMonitor?.Arm();
                        EnsureSessionLogOpen();

                        try
                        {
                            if (serial.BytesToRead > 0)
                                ThreadPool.QueueUserWorkItem(_ => Serial_DataReceived(serial, null));
                        }
                        catch { }
                    }

                    SyncOpenStateUi();
                    UpdateTitle();
                    owner.UpdateStatus();
                    AppendLog("SYS", owner.FindText("MultiPortOpened", "已打开。"));
                    Global.PublishNotification(
                        string.Format(
                            owner.FindText("NotificationConnectedTitleFormat", "{0} 已连接"),
                            portName),
                        string.Format(
                            owner.FindText("NotificationSerialOpenedMessageFormat", "{0} baud"),
                            baudRate),
                        AppNotificationLevel.Success,
                        category: AppNotificationCategory.Connection,
                        portName: portName);
                    LastErrorMessage = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    if (!useMainUart && generation != 0)
                    {
                        try
                        {
                            lock (serialLifecycleLock)
                            {
                                if (serial.IsOpen)
                                    serial.Close();
                            }
                        }
                        catch { }
                        CompleteSerialTransition(generation);
                    }

                    LastErrorMessage = ex.Message;
                    AppendLog("ERR", owner.FindText("MultiPortOpenFailed", "打开失败: ") + LastErrorMessage);
                    Global.PublishNotification(
                        string.Format(
                            owner.FindText("NotificationOperationFailedTitleFormat", "{0} 失败"),
                            portName),
                        LastErrorMessage,
                        AppNotificationLevel.Error,
                        category: AppNotificationCategory.Connection,
                        portName: portName);
                    try
                    {
                        if (useMainUart && Global.uart.IsOpen())
                            Global.uart.Close();
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
                packedLogItems.Clear();
                logCharCount = 0;
                plainDataParagraph = null;
            }

            public string GetLogText()
            {
                return new TextRange(logTextBox.Document.ContentStart, logTextBox.Document.ContentEnd).Text;
            }

            public DataShowPage.LogSnapshot GetLogSnapshot()
            {
                if (!lastPackedLogMode)
                {
                    return new DataShowPage.LogSnapshot
                    {
                        PackedMode = false,
                        PlainText = GetLogText()
                    };
                }

                var items = new List<DataShowPage.DataShow>();
                foreach (var block in logTextBox.Document.Blocks.ToList())
                {
                    if (packedLogItems.TryGetValue(block, out var item))
                    {
                        items.Add(item.Clone());
                        continue;
                    }

                    var text = new TextRange(block.ContentStart, block.ContentEnd).Text;
                    if (!string.IsNullOrEmpty(text))
                        items.Add(new DataShowPage.DataShow(text));
                }

                return new DataShowPage.LogSnapshot
                {
                    PackedMode = true,
                    Items = items
                };
            }

            public void SetLogSnapshot(DataShowPage.LogSnapshot snapshot)
            {
                if (snapshot == null)
                {
                    ClearLog();
                    return;
                }

                var packedMode = GetProfileSnapshot().timeout >= 0;
                lastPackedLogMode = packedMode;
                if (!packedMode || !snapshot.PackedMode)
                {
                    SetLogTextSnapshot(snapshot.ToPlainText());
                    return;
                }

                ClearLog();
                foreach (var item in snapshot.Items ?? new List<DataShowPage.DataShow>())
                {
                    if (item?.IsVisible == true)
                        AppendDataLog(item.Clone());
                }
            }

            public void SetLogTextSnapshot(string text)
            {
                var snapshot = text ?? string.Empty;
                if (snapshot.Length > MaxLogCharsPerSlot)
                    snapshot = snapshot.Substring(snapshot.Length - MaxLogCharsPerSlot);

                logTextBox.Document.Blocks.Clear();
                packedLogItems.Clear();
                logCharCount = snapshot.Length;
                plainDataParagraph = null;
                lastPackedLogMode = GetProfileSnapshot().timeout >= 0;
                if (snapshot.Length == 0)
                    return;

                if (lastPackedLogMode)
                {
                    logCharCount = 0;
                    AppendDataLog(new DataShowPage.DataShow(snapshot));
                    return;
                }

                var paragraph = new Paragraph
                {
                    Margin = new Thickness(0),
                    FontFamily = new FontFamily("Consolas,Microsoft YaHei,微软雅黑"),
                    FontSize = lastPackedLogMode ? 12 : 15
                };
                DataShowPage.AppendLogTextRuns(
                    paragraph,
                    snapshot,
                    Logger.GetLogDataBrush(false),
                    true,
                    15,
                    DataShowPage.ReceivedColorRole);
                logTextBox.Document.Blocks.Add(paragraph);
                plainDataParagraph = paragraph;
                logTextBox.ScrollToEnd();
            }

            private void CloseButton_Click(object sender, RoutedEventArgs e)
            {
                owner.RemoveSlot(Index);
                e.Handled = true;
            }

            private async void SendButton_Click(object sender, RoutedEventArgs e)
            {
                if (!EnsureOpen())
                {
                    AppendLog("ERR", owner.FindText("MultiPortNotOpen", "请先打开串口。"));
                    return;
                }

                byte[] data;
                try
                {
                    data = PrepareSlotSendData(sendTextBox.Text ?? string.Empty);
                }
                catch (Exception ex)
                {
                    AppendLog("ERR", owner.FindText("MultiPortSendFailed", "发送失败: ") + ex.Message);
                    return;
                }

                if (data == null || data.Length == 0)
                    return;

                await SendBytesAsync(data);
            }

            private byte[] PrepareSlotSendData(string text)
            {
                var profile = GetProfileSnapshot();
                var input = profile.hexSend
                    ? Global.Hex2Byte(text)
                    : Global.GetEncoding(profile.encoding).GetBytes(text ?? string.Empty);
                var scriptName = ResolveProfileScriptName(
                    "user_script_send_convert",
                    profile.sendScript,
                    "default");
                var converted = ScriptEnv.JavaScriptLoader.Run(
                    scriptName + ".js",
                    new ArrayList { "uartData", input });
                if (converted == null || !profile.extraEnter)
                    return converted;

                return converted.Concat(new byte[] { 0x0d, 0x0a }).ToArray();
            }

            private static string ResolveProfileScriptName(
                string directoryName,
                string requestedName,
                string fallbackName)
            {
                if (Global.TryGetProfileScriptPath(
                        directoryName,
                        requestedName,
                        out var normalizedName,
                        out var path) &&
                    File.Exists(path))
                {
                    return normalizedName;
                }
                return fallbackName;
            }

            public async Task<bool> SendBytesAsync(byte[] data)
            {
                var target = CaptureSerialTarget();
                if (target?.IsOpen != true)
                {
                    AppendLog("ERR", owner.FindText("MultiPortNotOpen", "请先打开串口。"));
                    return false;
                }

                sendButton.IsEnabled = false;
                try
                {
                    return await Task.Run(() => target.Send(data, CancellationToken.None, null));
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

            private bool SendCapturedMainUart(
                Uart.ConnectionLease connection,
                byte[] data,
                CancellationToken token,
                Action<int> committedBytes)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Keep the main UART on its normal event path. The split pane's
                    // MainUart_UartDataSent handler renders it, while MainWindow/Logger
                    // retain the ordinary session-log and subscriber behavior.
                    return connection.Send(data, token, committedBytes, raiseEvents: true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    owner.RunOnUi(() => AppendLog(
                        "ERR",
                        owner.FindText("MultiPortSendFailed", "发送失败: ") + ex.Message));
                    return false;
                }
            }

            private bool SendCapturedDirect(
                long generation,
                string portName,
                UartPortProfile profile,
                byte[] data,
                CancellationToken token,
                Action<int> committedBytes)
            {
                token.ThrowIfCancellationRequested();
                if (!IsDirectConnectionOpen(generation))
                    return false;

                try
                {
                    lock (sendLock)
                    {
                        WriteDirectSerial(
                            generation,
                            portName,
                            profile,
                            data,
                            token,
                            committedBytes);
                    }
                    owner.RunOnUi(() => WriteDataLog(data, true, profile));
                    return true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    owner.RunOnUi(() => AppendLog(
                        "ERR",
                        owner.FindText("MultiPortSendFailed", "发送失败: ") + ex.Message));
                    return false;
                }
            }

            private void ControlLineCheckBox_Changed(object sender, RoutedEventArgs e)
            {
                if (!applyingProfile)
                    ApplyControlLines();
            }

            private void ControlLineCheckBox_Click(object sender, RoutedEventArgs e)
            {
                if (applyingProfile || !(sender is CheckBox checkBox))
                    return;

                owner.PublishControlLineNotification(
                    this,
                    ReferenceEquals(checkBox, rtsCheckBox) ? "RTS" : "DTR");
            }

            private void ApplyControlLines()
            {
                if (applyingProfile)
                    return;

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
                    }
                    catch (Exception ex)
                    {
                        AppendLog("ERR", ex.Message);
                    }
                    owner.SaveSlotProfile(this);
                    return;
                }

                Exception applyError = null;
                try
                {
                    lock (serialLock)
                    {
                        if (!serialDisposed && !serialTransition && serial.IsOpen)
                        {
                            serial.DtrEnable = dtrCheckBox.IsChecked == true;
                            if (serial.Handshake != Handshake.RequestToSend)
                                serial.RtsEnable = rtsCheckBox.IsChecked == true;
                        }
                    }
                }
                catch (Exception ex) when (IsClosedSerialException(ex))
                {
                }
                catch (Exception ex)
                {
                    applyError = ex;
                }

                owner.SaveSlotProfile(this);
                if (applyError != null)
                    AppendLog("ERR", applyError.Message);
            }

            private void SlotProfileCheckBox_Changed(object sender, RoutedEventArgs e)
            {
                if (!applyingProfile)
                    owner.SaveSlotProfile(this);
            }

            private static Handshake GetHandshake(int flowControl)
            {
                switch (flowControl)
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
                long generation;
                lock (serialLock)
                {
                    if (serialDisposed ||
                        serialTransition ||
                        !ReferenceEquals(sender, serial) ||
                        !serial.IsOpen)
                    {
                        return;
                    }
                    generation = connectionGeneration;
                }

                try
                {
                    var result = new List<byte>();
                    while (true)
                    {
                        byte[] block;
                        int read;
                        lock (serialLock)
                        {
                            if (serialDisposed ||
                                serialTransition ||
                                generation != connectionGeneration ||
                                !serial.IsOpen)
                            {
                                break;
                            }

                            var length = serial.BytesToRead;
                            if (length <= 0)
                                break;
                            block = new byte[length];
                            read = serial.Read(block, 0, block.Length);
                        }

                        if (read <= 0)
                            break;
                        if (read == block.Length)
                            result.AddRange(block);
                        else
                            result.AddRange(block.Take(read));
                    }

                    if (result.Count > 0)
                        QueueReceivedData(result.ToArray());
                }
                catch (Exception ex) when (IsClosedSerialException(ex))
                {
                }
                catch (Exception ex)
                {
                    owner.RunOnUi(() => AppendLog("ERR", ex.Message));
                }
            }

            private void QueueReceivedData(byte[] data)
            {
                if (data == null || data.Length == 0)
                    return;

                var profile = GetProfileSnapshot();
                var flushImmediately = false;
                lock (receiveBufferLock)
                {
                    pendingReceiveData.AddRange(data);
                    var maxLength = Math.Max(1L, profile.maxLength);
                    if (pendingReceiveData.Count > maxLength)
                    {
                        receiveFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        receiveFlushScheduled = false;
                        flushImmediately = true;
                    }
                    else
                    {
                        var timeout = profile.timeout;
                        var delay = timeout > 0 ? timeout : 10;
                        var resetOnEveryReceive = timeout < 0 || profile.bitDelay;
                        if (!receiveFlushScheduled || resetOnEveryReceive)
                            receiveFlushTimer?.Change(delay, Timeout.Infinite);
                        receiveFlushScheduled = true;
                    }
                }

                if (flushImmediately)
                    FlushReceivedData(null);
            }

            private void FlushReceivedData(object state)
            {
                byte[] data;
                lock (receiveBufferLock)
                {
                    receiveFlushScheduled = false;
                    if (pendingReceiveData.Count == 0)
                        return;

                    data = pendingReceiveData.ToArray();
                    pendingReceiveData.Clear();
                }

                owner.RunOnUi(() =>
                {
                    WriteDataLog(data, false);
                    if (Index == owner.activeSlotNumber)
                        Global.NotifyActiveSerialTargetReceived(data);
                });
            }

            private void ResetReceiveBuffer()
            {
                lock (receiveBufferLock)
                {
                    receiveFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    receiveFlushScheduled = false;
                    pendingReceiveData.Clear();
                }
            }

            private void WaitForWriteDrain(
                long generation,
                SerialPort port,
                int byteCount,
                CancellationToken token)
            {
                var baudRate = Math.Max(1, port.BaudRate);
                var estimatedMilliseconds = (long)Math.Ceiling(byteCount * 11000d / baudRate);
                var timeoutMilliseconds = Math.Max(5000L, estimatedMilliseconds + 2000L);
                var stopwatch = Stopwatch.StartNew();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsDirectConnectionOpen(generation))
                        throw new IOException("The captured split serial connection changed while draining.");
                    if (port.BytesToWrite <= 0)
                        return;
                    if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
                        throw new TimeoutException("等待串口发送缓冲区清空超时。");
                    Thread.Sleep(2);
                }
            }

            private void WriteDirectSerial(
                long generation,
                string portName,
                UartPortProfile profile,
                byte[] data,
                CancellationToken token,
                Action<int> committedBytes)
            {
                var packetSize = Math.Max(0, profile?.sendThrottlePacketSize ?? 0);
                var delayMs = Math.Max(0, profile?.sendThrottleDelayMs ?? 0);
                if (packetSize == 0 || delayMs == 0)
                    packetSize = data.Length;

                Logger.AddUartLogDebug(
                    $"[SplitUartWrite]slot={Index},port={portName},bytes={data.Length},generation={generation}");
                for (var offset = 0; offset < data.Length; offset += packetSize)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(packetSize, data.Length - offset);
                    lock (serialLock)
                    {
                        if (serialDisposed ||
                            serialTransition ||
                            generation != connectionGeneration ||
                            !serial.IsOpen)
                        {
                            throw new IOException("The captured split serial connection is no longer open.");
                        }
                        serial.Write(data, offset, count);
                    }

                    committedBytes?.Invoke(count);
                    WaitForWriteDrain(generation, serial, count, token);
                    if (delayMs > 0 && offset + count < data.Length &&
                        token.WaitHandle.WaitOne(delayMs))
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
            }

            private void MainUart_UartDataSent(object sender, EventArgs e)
            {
                var data = sender as byte[];
                if (data == null || data.Length == 0)
                    return;

                owner.RunOnUi(() =>
                    WriteDataLog(data, true, updateCounters: false, writeSessionLog: false));
            }

            private void MainUart_UartDataRecived(object sender, EventArgs e)
            {
                var data = sender as byte[];
                if (data == null || data.Length == 0)
                    return;

                owner.RunOnUi(() =>
                    WriteDataLog(data, false, updateCounters: false, writeSessionLog: false));
            }

            private void WriteDataLog(
                byte[] data,
                bool sent,
                UartPortProfile profile = null,
                bool updateCounters = true,
                bool writeSessionLog = true)
            {
                if (data == null || data.Length == 0 || Global.setting == null || Global.setting.DisableLog)
                    return;

                profile = profile == null ? GetProfileSnapshot() : CloneProfile(profile);
                if (updateCounters)
                {
                    if (sent)
                        Global.setting.SentCount += data.Length;
                    else
                        Global.setting.ReceivedCount += data.Length;
                }

                if (writeSessionLog)
                    WriteSessionLog(sent ? "send" : "recv", data);

                // 与普通模式一致：关闭“显示实际发出的数据”时，不显示分屏发送回显。
                if (sent && !profile.showSend)
                    return;

                var displayItem = new DataShowPage.DataShow(
                    new DataShowPara
                    {
                        data = data,
                        send = sent,
                        receiveScriptContext = sent
                            ? null
                            : new ReceiveScriptContext { ScriptName = profile.recvScript }
                    },
                    profile);
                if (!displayItem.IsVisible)
                    return;

                var packedLogMode = profile.timeout >= 0;
                if (lastPackedLogMode != packedLogMode)
                {
                    lastPackedLogMode = packedLogMode;
                    ClearLog();
                }

                if (!packedLogMode)
                {
                    var text = displayItem.DataText ?? string.Empty;
                    if (profile.showHexFormat == 2 && text.Length > 0)
                        text += " ";
                    AppendPlainData(text, sent);
                    return;
                }

                AppendDataLog(displayItem);
            }

            private void AppendPlainData(string text, bool sent)
            {
                if (string.IsNullOrEmpty(text))
                    return;

                if (plainDataParagraph == null)
                {
                    plainDataParagraph = new Paragraph
                    {
                        Margin = new Thickness(0),
                        FontFamily = new FontFamily("Consolas,Microsoft YaHei,微软雅黑"),
                        FontSize = 15
                    };
                    logTextBox.Document.Blocks.Add(plainDataParagraph);
                }

                DataShowPage.AppendLogTextRuns(
                    plainDataParagraph,
                    text,
                    Logger.GetLogDataBrush(sent),
                    !sent,
                    15,
                    sent ? DataShowPage.SentColorRole : DataShowPage.ReceivedColorRole);
                logCharCount += text.Length;
                TrimPlainData();
                TrimLog();
                if (!owner.lockLogs)
                    logTextBox.ScrollToEnd();
            }

            private void TrimPlainData()
            {
                if (plainDataParagraph == null || logCharCount <= MaxLogCharsPerSlot)
                    return;

                var currentText = new TextRange(
                    plainDataParagraph.ContentStart,
                    plainDataParagraph.ContentEnd).Text;
                if (currentText.EndsWith(Environment.NewLine, StringComparison.Ordinal))
                    currentText = currentText.Substring(0, currentText.Length - Environment.NewLine.Length);
                var retainedLength = Math.Max(0, MaxLogCharsPerSlot - LogTrimChars);
                if (currentText.Length <= retainedLength)
                    return;

                var retainedText = currentText.Substring(currentText.Length - retainedLength);
                plainDataParagraph.Inlines.Clear();
                DataShowPage.AppendLogTextRuns(
                    plainDataParagraph,
                    retainedText,
                    Logger.GetLogDataBrush(false),
                    true,
                    15,
                    DataShowPage.ReceivedColorRole);
                logCharCount -= currentText.Length - retainedText.Length;
            }

            private void AppendDataLog(DataShowPage.DataShow item)
            {
                plainDataParagraph = null;
                var paragraph = DataShowPage.CreateLogParagraph(item);
                logTextBox.Document.Blocks.Add(paragraph);
                packedLogItems[paragraph] = item.Clone();
                logCharCount +=
                    (item.TimeText?.Length ?? 0) +
                    (item.ArrowText?.Length ?? 0) +
                    (item.DataText?.Length ?? 0) +
                    (item.RawTitle?.Length ?? 0) +
                    (item.RawText?.Length ?? 0) +
                    (item.HexText?.Length ?? 0) + 2;
                TrimLog();
                if (!owner.lockLogs)
                    logTextBox.ScrollToEnd();
            }

            private void AppendLog(string direction, string text)
            {
                AppendLog(direction, text, GetLogBrush(direction));
            }

            private void AppendLog(string direction, string text, Brush dataBrush)
            {
                var packedLogMode = GetProfileSnapshot().timeout >= 0;
                if (lastPackedLogMode != packedLogMode)
                {
                    lastPackedLogMode = packedLogMode;
                    ClearLog();
                }

                if (packedLogMode)
                {
                    AppendDataLog(DataShowPage.DataShow.CreateStatus(
                        $"[{DateTime.Now:HH:mm:ss.fff}] ",
                        direction + " ",
                        text,
                        dataBrush as SolidColorBrush ?? ResourceBrush("AppGlassTextBrush", SystemColors.ControlTextBrush)));
                    return;
                }

                plainDataParagraph = null;
                var linePrefix = $"[{DateTime.Now:HH:mm:ss.fff}] {direction} ";
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.Add(new Run(linePrefix) { Foreground = ResourceBrush("AppGlassMutedBrush", SystemColors.GrayTextBrush) });
                paragraph.Inlines.Add(new Run(text ?? "")
                {
                    Foreground = dataBrush,
                    Tag = direction == "TX"
                        ? DataShowPage.SentColorRole
                        : direction == "RX"
                            ? DataShowPage.ReceivedColorRole
                            : direction == "ERR"
                                ? DataShowPage.ErrorColorRole
                                : null
                });
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
                    if (ReferenceEquals(first, plainDataParagraph))
                        plainDataParagraph = null;
                    packedLogItems.Remove(first);
                    logTextBox.Document.Blocks.Remove(first);
                }
            }

            private static Brush GetLogBrush(string direction)
            {
                switch (direction)
                {
                    case "TX":
                        return Logger.GetLogDataBrush(true);
                    case "RX":
                        return Logger.GetLogDataBrush(false);
                    case "ERR":
                        return Logger.GetLogErrorBrush();
                    default:
                        return ResourceBrush("AppGlassTextBrush", SystemColors.ControlTextBrush);
                }
            }

            private static SolidColorBrush ResourceBrush(string key, SolidColorBrush fallback)
            {
                return Tools.Logger.GetThemeBrush(key, fallback);
            }

            public void RefreshLogColors()
            {
                if (!lastPackedLogMode)
                {
                    foreach (var paragraph in logTextBox.Document.Blocks.OfType<Paragraph>())
                    {
                        foreach (var run in paragraph.Inlines.OfType<Run>())
                        {
                            switch (run.Tag as string)
                            {
                                case DataShowPage.SentColorRole:
                                    run.Foreground = Logger.GetLogDataBrush(true);
                                    break;
                                case DataShowPage.ReceivedColorRole:
                                    run.Foreground = Logger.GetLogDataBrush(false);
                                    break;
                                case DataShowPage.ErrorColorRole:
                                    run.Foreground = Logger.GetLogErrorBrush();
                                    break;
                            }
                        }
                    }
                    return;
                }

                var snapshot = GetLogSnapshot();
                SetLogSnapshot(snapshot);
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

                        var startedAt = DateTime.Now;
                        var fileName = $"{startedAt:yyyyMMdd_HHmmss_fff}_slot{Index}.log";
                        var writers = Logger.CreateUniqueSessionLogWriters(
                            stringFolder,
                            hexFolder,
                            fileName);
                        sessionStringLogWriter = writers.StringWriter;
                        sessionHexLogWriter = writers.HexWriter;
                        var startLine = $"[START] {startedAt:yyyy-MM-dd HH:mm:ss.fff}  PORT={PortName}  SLOT={Index}";
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
                        var endLine = $"[END] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}";
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
                        var prefix = Logger.BuildSessionLogPrefix(DateTime.Now, direction);
                        Logger.WriteSessionStringLine(
                            sessionStringLogWriter,
                            prefix,
                            Byte2SessionString(data, GetProfileSnapshot().encoding));
                        sessionHexLogWriter?.WriteLine($"{prefix} │ {Global.Byte2Hex(data, " ", data.Length)}");
                    }
                    catch (Exception ex)
                    {
                        Logger.AddUartLogDebug($"[SplitSessionLog]write failed:{ex.Message}");
                    }
                }
            }

            private static string Byte2SessionString(byte[] data, int encodingCodePage)
            {
                var text = new StringBuilder();
                var plainBytes = new List<byte>();
                var encoding = Global.GetEncoding(encodingCodePage);
                foreach (var value in data ?? new byte[0])
                {
                    if (value <= 0x1f || value == 0x7f)
                    {
                        if (plainBytes.Count > 0)
                        {
                            text.Append(encoding.GetString(plainBytes.ToArray()));
                            plainBytes.Clear();
                        }
                        text.Append(Byte2SessionVisibleSymbol(value));
                    }
                    else
                    {
                        plainBytes.Add(value);
                    }
                }
                if (plainBytes.Count > 0)
                    text.Append(encoding.GetString(plainBytes.ToArray()));
                return text.ToString();
            }

            private static string Byte2SessionVisibleSymbol(byte value)
            {
                switch (value)
                {
                    case 0x00: return "\\0";
                    case 0x07: return "\\a";
                    case 0x08: return "\\b";
                    case 0x09: return "\\t";
                    case 0x0a: return "\\n" + Environment.NewLine;
                    case 0x0b: return "\\v";
                    case 0x0c: return "\\f";
                    case 0x0d: return "\\r";
                    case 0x1b: return "\\e";
                    default: return $"\\x{value:X2}";
                }
            }

            private static string MakeSafeFileName(string value)
            {
                foreach (var c in Path.GetInvalidFileNameChars())
                    value = value.Replace(c, '_');
                return value;
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
