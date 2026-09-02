using CoAP.Net;
using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using llcom_plus.ScriptEnv;
using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace llcom_plus.Pages
{
    /// <summary>
    /// WinUSB page with a generation-bound, byte/count-bounded send lifecycle.
    /// </summary>
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public partial class WinUSBPage : Page
    {
        private const long MaxQueuedSendBytes = 4L * 1024 * 1024;
        private const int MaxQueuedSendRequests = 1024;
        private const int MaxConsecutiveZeroWrites = 3;

        private bool loaded;
        private IDisposable sendChannelRegistration;
        private long connectionGeneration;
        private readonly UsbSendLifecycle sendLifecycle = new UsbSendLifecycle();

        public WinUSBPage()
        {
            InitializeComponent();
            Unloaded += Page_Unloaded;
        }

        public bool IsConnected { get; set; }
        public bool HexMode { get; set; }

        private sealed class SendRequest
        {
            public SendRequest(long generation, byte[] data)
            {
                Generation = generation;
                Data = data;
            }

            public long Generation { get; }
            public byte[] Data { get; }
        }

        private sealed class UsbConnection
        {
            public UsbConnection(long generation)
            {
                Generation = generation;
            }

            public long Generation { get; }
            public Queue<SendRequest> SendQueue { get; } = new Queue<SendRequest>();
            public long QueuedBytes;
            public bool CloseRequested;
        }

        private sealed class UsbSendLifecycle
        {
            // Every active check, capacity reservation, enqueue, dequeue and
            // completion transition uses this one gate. There is no state in
            // which enqueue can report success after close while losing data.
            private readonly object gate = new object();
            private UsbConnection active;

            public bool TryActivate(UsbConnection connection)
            {
                if (connection == null)
                    return false;
                lock (gate)
                {
                    if (active != null || connection.CloseRequested)
                        return false;
                    active = connection;
                    return true;
                }
            }

            public bool HasActiveConnection
            {
                get
                {
                    lock (gate)
                        return active != null && !active.CloseRequested;
                }
            }

            public bool IsActive(UsbConnection connection)
            {
                lock (gate)
                    return IsActiveUnderGate(connection);
            }

            public bool TryEnqueue(byte[] data)
            {
                if (data == null || data.LongLength > MaxQueuedSendBytes)
                    return false;

                lock (gate)
                {
                    var connection = active;
                    if (!IsActiveUnderGate(connection))
                        return false;
                    if (data.Length == 0)
                        return true;
                    if (connection.SendQueue.Count >= MaxQueuedSendRequests ||
                        connection.QueuedBytes > MaxQueuedSendBytes - data.Length)
                    {
                        return false;
                    }

                    // Copy ownership while still holding the lifecycle gate.
                    // Close cannot drain between reservation and publication.
                    var copy = new byte[data.Length];
                    Buffer.BlockCopy(data, 0, copy, 0, data.Length);
                    connection.SendQueue.Enqueue(
                        new SendRequest(connection.Generation, copy));
                    connection.QueuedBytes += copy.Length;
                    return true;
                }
            }

            public bool TryDequeue(UsbConnection connection, out SendRequest request)
            {
                lock (gate)
                {
                    request = null;
                    if (!IsActiveUnderGate(connection) || connection.SendQueue.Count == 0)
                        return false;

                    request = connection.SendQueue.Dequeue();
                    connection.QueuedBytes -= request.Data.Length;
                    if (connection.QueuedBytes < 0)
                        throw new InvalidOperationException("WinUSB queued byte accounting underflow.");
                    return true;
                }
            }

            public bool RequestClose()
            {
                lock (gate)
                {
                    if (active == null)
                        return false;
                    var connection = active;
                    active = null;
                    CloseAndDrainUnderGate(connection);
                    return true;
                }
            }

            public bool Complete(UsbConnection connection)
            {
                if (connection == null)
                    return !HasActiveConnection;
                lock (gate)
                {
                    if (ReferenceEquals(active, connection))
                        active = null;
                    CloseAndDrainUnderGate(connection);
                    return active == null;
                }
            }

            public Tuple<int, long, bool> GetSnapshot(UsbConnection connection)
            {
                lock (gate)
                {
                    return Tuple.Create(
                        connection?.SendQueue.Count ?? 0,
                        connection?.QueuedBytes ?? 0,
                        connection == null || connection.CloseRequested);
                }
            }

            private bool IsActiveUnderGate(UsbConnection connection)
            {
                return connection != null &&
                    !connection.CloseRequested &&
                    ReferenceEquals(active, connection);
            }

            private static void CloseAndDrainUnderGate(UsbConnection connection)
            {
                connection.CloseRequested = true;
                connection.SendQueue.Clear();
                connection.QueuedBytes = 0;
            }
        }

        // Reflection regression probe: validates that accepted work is owned,
        // closing drains accounting, and every enqueue after close is rejected.
        private static bool ProbeSendLifecycleCloseBehavior()
        {
            var lifecycle = new UsbSendLifecycle();
            var connection = new UsbConnection(17);
            var original = new byte[] { 1, 2, 3 };
            if (!lifecycle.TryActivate(connection) || !lifecycle.TryEnqueue(original))
                return false;
            original[0] = 99;
            if (!lifecycle.RequestClose() || lifecycle.TryEnqueue(new byte[] { 4 }))
                return false;
            var closedSnapshot = lifecycle.GetSnapshot(connection);
            var replacement = new UsbConnection(18);
            if (!lifecycle.TryActivate(replacement) || lifecycle.Complete(connection))
                return false;
            if (!lifecycle.TryEnqueue(new byte[] { 5 }) || !lifecycle.RequestClose())
                return false;
            var replacementSnapshot = lifecycle.GetSnapshot(replacement);
            return closedSnapshot.Item1 == 0 &&
                closedSnapshot.Item2 == 0 &&
                closedSnapshot.Item3 &&
                replacementSnapshot.Item1 == 0 &&
                replacementSnapshot.Item2 == 0 &&
                replacementSnapshot.Item3;
        }

        private void ShowData(string title, byte[] data = null, bool send = false)
        {
            Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
            {
                title = $"🔌 WinUSB: {title}",
                data = data ?? new byte[0],
                color = send
                    ? Tools.Logger.GetThemeBrush("AppDataSentBrush", Brushes.IndianRed)
                    : Tools.Logger.GetThemeBrush("AppDataReceivedBrush", Brushes.SeaGreen),
            });
        }

        private List<DeviceInfo> GetUsbList()
        {
            var list = new List<DeviceInfo>();
            using (var context = new UsbContext())
            using (var allDevices = context.List())
            {
                foreach (var usbRegistry in allDevices)
                {
                    if (!usbRegistry.TryOpen())
                        continue;
                    try
                    {
                        UsbConfigInfo configInfo = usbRegistry.Configs[0];
                        ReadOnlyCollection<UsbInterfaceInfo> interfaceList = configInfo.Interfaces;
                        for (int interfaceIndex = 0; interfaceIndex < interfaceList.Count; interfaceIndex++)
                        {
                            UsbInterfaceInfo interfaceInfo = interfaceList[interfaceIndex];
                            if (interfaceInfo.Class != ClassCode.VendorSpec)
                                continue;
                            var info = new DeviceInfo
                            {
                                Vid = usbRegistry.VendorId,
                                Pid = usbRegistry.ProductId,
                                SerialNumber = usbRegistry.Info.SerialNumber,
                                Interface = interfaceIndex,
                                Name = string.IsNullOrEmpty(interfaceInfo.Interface)
                                    ? "Device"
                                    : interfaceInfo.Interface,
                            };

                            foreach (var endpoint in interfaceInfo.Endpoints)
                            {
                                var address = endpoint.EndpointAddress;
                                if ((WriteEndpointID)address <= WriteEndpointID.Ep15)
                                {
                                    info.SendEP.Add(((WriteEndpointID)address, endpoint.MaxPacketSize));
                                }
                                else
                                {
                                    info.RecvEP.Add(((ReadEndpointID)address, endpoint.MaxPacketSize));
                                }
                            }
                            if (info.SendEP.Count > 0 || info.RecvEP.Count > 0)
                                list.Add(info);
                        }
                    }
                    catch
                    {
                        // An individual descriptor failure must not abort refresh.
                    }
                    finally
                    {
                        usbRegistry.Close();
                    }
                }
            }
            return list;
        }

        private async System.Threading.Tasks.Task RefreshUsbList()
        {
            List<DeviceInfo> result = null;
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    result = GetUsbList();
                }
                catch
                {
                    // Device discovery errors are represented by an empty list.
                }
            });
            UsbListComboBox.Items.Clear();
            if (result != null)
            {
                foreach (var item in result)
                    UsbListComboBox.Items.Add(item);
            }
            UsbListComboBox.SelectedIndex = UsbListComboBox.Items.Count > 0 ? 0 : -1;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (loaded)
                return;
            loaded = true;

            if (Tools.Global.IsMSIX())
            {
                Tools.MessageBox.Show(
                    "微软商店版无法使用该功能\r\nCan't use this in MS Store Version");
                MainGrid.IsEnabled = false;
            }

            MainGrid.DataContext = this;
            var registration = ScriptApis.SendChannelsRegister(
                "winusb",
                (data, _) => TryEnqueueSend(data));
            var previous = Interlocked.Exchange(ref sendChannelRegistration, registration);
            previous?.Dispose();
            await RefreshUsbList();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            loaded = false;
            var registration = Interlocked.Exchange(ref sendChannelRegistration, null);
            registration?.Dispose();
            RequestClose();
        }

        private async void RefreshUsbButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshUsbList();
        }

        private bool TryEnqueueSend(byte[] data)
        {
            return sendLifecycle.TryEnqueue(data);
        }

        private bool IsConnectionActive(UsbConnection connection)
        {
            return sendLifecycle.IsActive(connection);
        }

        private void SetConnected(bool value)
        {
            if (Dispatcher.CheckAccess())
            {
                IsConnected = value;
                return;
            }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() => IsConnected = value));
            }
            catch
            {
                // The page is shutting down.
            }
        }

        private void RequestClose()
        {
            if (sendLifecycle.RequestClose())
                SetConnected(false);
        }

        private void CompleteConnection(UsbConnection connection)
        {
            if (sendLifecycle.Complete(connection))
                SetConnected(false);
        }

        private bool WriteAll(
            UsbConnection connection,
            UsbEndpointWriter writer,
            byte[] data,
            int maxPacketSize)
        {
            int sent = 0;
            int consecutiveZeroWrites = 0;
            int packetSize = Math.Max(1, maxPacketSize);
            while (sent < data.Length)
            {
                if (!IsConnectionActive(connection))
                    return false;

                int requestedLength = Math.Min(data.Length - sent, packetSize);
                int realSent;
                Error error;
                try
                {
                    error = writer.Write(
                        data,
                        sent,
                        requestedLength,
                        1000,
                        out realSent);
                }
                catch (Exception ex)
                {
                    ShowData($"send error:\r\n{ex}");
                    return false;
                }

                if (realSent < 0 || realSent > requestedLength)
                {
                    ShowData($"send error: invalid transferred length {realSent}/{requestedLength}");
                    return false;
                }
                if (realSent > 0)
                {
                    var actualData = new byte[realSent];
                    Buffer.BlockCopy(data, sent, actualData, 0, realSent);
                    ShowData($"sent {realSent} bytes", actualData, true);
                    sent += realSent;
                    consecutiveZeroWrites = 0;
                }
                else
                {
                    consecutiveZeroWrites++;
                }

                if (error != Error.Success)
                {
                    ShowData($"send error: {error}");
                    return false;
                }
                if (realSent == 0 && consecutiveZeroWrites >= MaxConsecutiveZeroWrites)
                {
                    ShowData($"send error: no progress after {MaxConsecutiveZeroWrites} writes");
                    return false;
                }
            }
            return true;
        }

        private bool SendAll(UsbConnection connection, UsbEndpointWriter writer, int maxPacketSize)
        {
            while (sendLifecycle.TryDequeue(connection, out var request))
            {
                if (request.Generation != connection.Generation)
                    continue;
                if (!WriteAll(connection, writer, request.Data, maxPacketSize))
                    return false;
            }
            return IsConnectionActive(connection);
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (UsbListComboBox.SelectedItem == null ||
                IsConnected ||
                sendLifecycle.HasActiveConnection)
            {
                return;
            }

            var target = (DeviceInfo)UsbListComboBox.SelectedItem;
            string notificationSource = TryFindResource("NotificationWinUsbSource") as string ?? "WinUSB";
            string connectedTitle = string.Format(
                TryFindResource("NotificationConnectedTitleFormat") as string ?? "{0} 已连接",
                notificationSource);
            string disconnectedTitle = string.Format(
                TryFindResource("NotificationDisconnectedTitleFormat") as string ?? "{0} 已断开",
                notificationSource);
            string connectionLostTitle = string.Format(
                TryFindResource("NotificationConnectionLostTitleFormat") as string ?? "{0} 连接中断",
                notificationSource);
            string failedTitle = string.Format(
                TryFindResource("NotificationOperationFailedTitleFormat") as string ?? "{0} 失败",
                notificationSource);
            string targetSummary =
                $"VID:0x{target.Vid:X04} PID:0x{target.Pid:X04} IF:{target.Interface}";

            var context = new UsbContext();
            var allDevices = context.List();
            bool workerStarted = false;
            foreach (var device in allDevices)
            {
                if (device.ProductId != target.Pid || device.VendorId != target.Vid)
                    continue;
                if (!device.TryOpen() || device.Info.SerialNumber != target.SerialNumber)
                    continue;

                try
                {
                    device.ResetDevice();
                    if (!device.TryOpen())
                        break;
                    UsbConfigInfo configInfo = device.Configs[0];
                    ReadOnlyCollection<UsbInterfaceInfo> interfaces = configInfo.Interfaces;
                    if (target.Interface < 0 || target.Interface >= interfaces.Count)
                        continue;
                    device.SetConfiguration(1);
                    device.ClaimInterface(target.Interface);
                    device.SetAltInterface(0);
                }
                catch (Exception error)
                {
                    ShowData($"open failed:\r\n{error}");
                    break;
                }

                if (UsbInComboBox.SelectedItem == null || UsbOutComboBox.SelectedItem == null)
                {
                    ShowData("open failed: endpoint not selected");
                    try { device.Close(); } catch { }
                    break;
                }
                var readEndpoint = (ReadEndpointID)UsbInComboBox.SelectedItem;
                var writeEndpoint = (WriteEndpointID)UsbOutComboBox.SelectedItem;
                int maxPacket = 64;
                foreach (var endpoint in target.SendEP)
                {
                    if (writeEndpoint == endpoint.Item1)
                    {
                        maxPacket = endpoint.Item2;
                        break;
                    }
                }

                var connection = new UsbConnection(
                    Interlocked.Increment(ref connectionGeneration));
                if (!sendLifecycle.TryActivate(connection))
                {
                    ShowData("open failed: another connection is active");
                    try { device.Close(); } catch { }
                    break;
                }

                SetConnected(true);
                ShowData("open success");
                Tools.Global.PublishNotification(
                    connectedTitle,
                    targetSummary,
                    AppNotificationLevel.Success,
                    category: AppNotificationCategory.Connection);

                var worker = new Thread(() =>
                {
                    bool unexpectedDisconnect = false;
                    Exception disconnectException = null;
                    try
                    {
                        const int timeout = 50;
                        var temp = new byte[1024];
                        var reader = device.OpenEndpointReader(readEndpoint, 1024);
                        var writer = device.OpenEndpointWriter(writeEndpoint);
                        while (IsConnectionActive(connection) && !Tools.Global.isMainWindowsClosed)
                        {
                            int readLength;
                            var error = reader.Read(temp, timeout, out readLength);
                            switch (error)
                            {
                                case Error.Success:
                                case Error.Timeout:
                                case Error.Busy:
                                case Error.Pipe:
                                    break;
                                default:
                                    unexpectedDisconnect = true;
                                    return;
                            }

                            if (readLength > 0)
                            {
                                var data = temp.Take(readLength).ToArray();
                                ShowData($"recv {readLength} bytes", data);
                                ScriptApis.SendChannelsReceived("winusb", data);
                            }
                            if (!IsConnectionActive(connection) || Tools.Global.isMainWindowsClosed)
                                return;
                            if (!SendAll(connection, writer, maxPacket))
                            {
                                unexpectedDisconnect = IsConnectionActive(connection);
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        unexpectedDisconnect = true;
                        disconnectException = ex;
                    }
                    finally
                    {
                        try { device.Close(); } catch { }
                        try { allDevices.Dispose(); } catch { }
                        try { context.Dispose(); } catch { }
                        CompleteConnection(connection);

                        if (disconnectException != null)
                        {
                            ShowData($"disconnect by exception:\r\n{disconnectException}");
                            Tools.Global.PublishNotification(
                                connectionLostTitle,
                                disconnectException.GetBaseException().Message,
                                AppNotificationLevel.Error,
                                category: AppNotificationCategory.Connection);
                        }
                        else if (unexpectedDisconnect)
                        {
                            ShowData("disconnect");
                            Tools.Global.PublishNotification(
                                connectionLostTitle,
                                targetSummary,
                                AppNotificationLevel.Warning,
                                category: AppNotificationCategory.Connection);
                        }
                        else
                        {
                            ShowData("disconnect");
                            Tools.Global.PublishNotification(
                                disconnectedTitle,
                                targetSummary,
                                AppNotificationLevel.Info,
                                category: AppNotificationCategory.Connection);
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = $"WinUSB-{connection.Generation}",
                };

                try
                {
                    worker.Start();
                    workerStarted = true;
                }
                catch (Exception ex)
                {
                    CompleteConnection(connection);
                    try { device.Close(); } catch { }
                    ShowData($"open failed:\r\n{ex}");
                }
                break;
            }

            if (!workerStarted)
            {
                try { allDevices.Dispose(); } catch { }
                try { context.Dispose(); } catch { }
                Tools.Global.PublishNotification(
                    failedTitle,
                    targetSummary,
                    AppNotificationLevel.Error,
                    category: AppNotificationCategory.Connection);
            }
        }

        private void DisonnectButton_Click(object sender, RoutedEventArgs e)
        {
            RequestClose();
        }

        private void SendDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsConnected)
                return;
            byte[] data = HexMode
                ? Tools.Global.Hex2Byte(toSendDataTextBox.Text)
                : Tools.Global.GetEncoding().GetBytes(toSendDataTextBox.Text);
            if (!TryEnqueueSend(data))
                ShowData("send queue full or disconnected");
        }

        private void UsbListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UsbListComboBox.SelectedItem == null || IsConnected)
                return;
            var target = (DeviceInfo)UsbListComboBox.SelectedItem;
            UsbInComboBox.Items.Clear();
            UsbOutComboBox.Items.Clear();
            foreach (var endpoint in target.RecvEP)
                UsbInComboBox.Items.Add(endpoint.Item1);
            foreach (var endpoint in target.SendEP)
                UsbOutComboBox.Items.Add(endpoint.Item1);
            if (UsbInComboBox.Items.Count > 0)
                UsbInComboBox.SelectedIndex = 0;
            if (UsbOutComboBox.Items.Count > 0)
                UsbOutComboBox.SelectedIndex = 0;
        }
    }

    internal sealed class DeviceInfo
    {
        public string Name { get; set; }
        public ushort Pid { get; set; }
        public ushort Vid { get; set; }
        public string SerialNumber { get; set; }
        public int Interface { get; set; } = -1;
        public List<(WriteEndpointID, int)> SendEP { get; } = new List<(WriteEndpointID, int)>();
        public List<(ReadEndpointID, int)> RecvEP { get; } = new List<(ReadEndpointID, int)>();

        public override string ToString()
        {
            if (Name == null)
                return $"VID: 0x{Vid:X04}, PID: 0x{Pid:X04}, Interface: {Interface}\r\n{SerialNumber}";
            return $"{Name}\r\nVID: 0x{Vid:X04}, PID: 0x{Pid:X04}, Interface: {Interface}\r\n{SerialNumber}";
        }
    }
}
