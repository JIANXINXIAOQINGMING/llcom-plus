using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace llcom_plus.Pages
{
    /// <summary>
    /// Serial monitor UI and the managed half of native ABI version 3.
    /// </summary>
    public partial class SerialMonitorPage : Page
    {
        private const uint WireMagic = 0x334D534C;
        private const ushort AbiVersion = 3;
        private const ushort WireHeaderSize = 64;
        private const int MaxFragmentData = 8192;
        private const int WirePacketSize = WireHeaderSize + MaxFragmentData;
        private const uint FlagFirst = 0x0001;
        private const uint FlagLast = 0x0002;
        private const uint FlagTruncated = 0x0004;

        private const int MaxQueuedPacketCount = 512;
        private const long MaxQueuedPayloadBytes = 2L * 1024 * 1024;
        private const int MaxDrainPacketCount = 64;
        private const int MaxDrainPayloadBytes = 256 * 1024;
        private const string QueueDropPolicy = "DropNewest";

        private const int NativeStarted = 1;
        private const int NativeStoppedModuleRetained = 2;
        private const int NativeStopPendingModuleRetained = 3;
        private const int NativeStoppedModuleStateUnknown = 4;

        private const ushort ImageFileMachineUnknown = 0;
        private const ushort ImageFileMachineI386 = 0x014c;
        private const ushort ImageFileMachineAmd64 = 0x8664;
        private const ushort ImageFileMachineArm64 = 0xaa64;

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime,
            out long kernelTime, out long userTime);

        [DllImport("kernel32.dll")]
        private static extern void GetNativeSystemInfo(out NativeSystemInfo information);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSystemInfo
        {
            public ushort ProcessorArchitecture;
            public ushort Reserved;
            public uint PageSize;
            public IntPtr MinimumAddress;
            public IntPtr MaximumAddress;
            public UIntPtr ActiveProcessorMask;
            public uint ProcessorCount;
            public uint ProcessorType;
            public uint AllocationGranularity;
            public ushort ProcessorLevel;
            public ushort ProcessorRevision;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        private enum State : uint
        {
            Disconnect = 2,
            Receive = 3,
            Send = 4,
        }

        private enum ProcessArchitecture
        {
            Unknown,
            X86,
            X64,
            Arm64,
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct MonitorPacket
        {
            public uint Magic;
            public ushort Version;
            public ushort HeaderSize;
            public ulong Generation;
            public ulong Sequence;
            public uint ComPort;
            public uint CommState;
            public ulong FileHandle;
            public uint TotalLength;
            public uint FragmentOffset;
            public uint DataLength;
            public uint Flags;
            public ulong NativeDroppedPackets;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxFragmentData)]
            public byte[] Data;
        }

        private sealed class ProcessChoice
        {
            public uint Pid { get; set; }
            public string Name { get; set; }
            public ProcessArchitecture Architecture { get; set; }
            public string ArchitectureDetail { get; set; }
            public long StartTimeUtcTicks { get; set; }

            public override string ToString()
            {
                return $"{Name}[{Pid}] — {ArchitectureDetail}";
            }
        }

        private sealed class MonitorRecord
        {
            public int CallbackGeneration { get; set; }
            public ulong ProtocolGeneration { get; set; }
            public ulong Sequence { get; set; }
            public uint ComPort { get; set; }
            public uint CommState { get; set; }
            public uint TotalLength { get; set; }
            public uint FragmentOffset { get; set; }
            public uint Flags { get; set; }
            public byte[] Data { get; set; }
        }

        private readonly object monitorLifecycleGate = new object();
        private readonly object callbackQueueGate = new object();
        private readonly Queue<MonitorRecord> callbackQueue = new Queue<MonitorRecord>();

        private bool firstLoad = true;
        private bool connected;
        private bool starting;
        private bool stopping;
        private volatile bool pageActive;
        private Tools.SerialMonitorBrokerClient broker;
        private CancellationTokenSource monitorCancellation;
        private Task stopTask = Task.CompletedTask;
        private Task startRequestSettled = Task.CompletedTask;
        private bool subscribedProgramClosed;
        private int callbacksEnabled;
        private int callbackGeneration;
        private int selectedComPort;
        private long queuedPayloadBytes;
        private long droppedPacketCount;
        private long droppedPayloadBytes;
        private ulong nativeDroppedPacketCount;
        private ulong nativeProtocolGeneration;
        private bool drainScheduled;
        private string lifecycleStatus = string.Empty;

        public SerialMonitorPage()
        {
            InitializeComponent();
        }

        private void BrokerPacketReceived(byte[] frame, int generation)
        {
            if (frame == null || frame.Length != WirePacketSize ||
                generation != Volatile.Read(ref callbackGeneration) || Volatile.Read(ref callbacksEnabled) == 0)
                return;
            var pinned = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try { NativeCallback(pinned.AddrOfPinnedObject(), generation); }
            finally { pinned.Free(); }
        }

        private int NativeCallback(IntPtr parameter, int generation)
        {
            try
            {
                if (parameter == IntPtr.Zero || Volatile.Read(ref callbacksEnabled) == 0 ||
                    generation != Volatile.Read(ref callbackGeneration))
                    return 0;

                var packet = Marshal.PtrToStructure<MonitorPacket>(parameter);
                if (!IsPacketMetadataValid(packet, Volatile.Read(ref selectedComPort)))
                    return 0;

                var payload = new byte[checked((int)packet.DataLength)];
                if (payload.Length > 0)
                    Buffer.BlockCopy(packet.Data, 0, payload, 0, payload.Length);

                bool scheduleDrain = false;
                lock (callbackQueueGate)
                {
                    if (Volatile.Read(ref callbacksEnabled) == 0 ||
                        generation != Volatile.Read(ref callbackGeneration))
                    {
                        return 1;
                    }

                    if (nativeProtocolGeneration == 0)
                        nativeProtocolGeneration = packet.Generation;
                    if (nativeProtocolGeneration != packet.Generation)
                        return 1;

                    nativeDroppedPacketCount = SaturatingAdd(
                        nativeDroppedPacketCount,
                        packet.NativeDroppedPackets);

                    if (!IsWithinCallbackQueueLimits(
                        callbackQueue.Count,
                        queuedPayloadBytes,
                        payload.Length))
                    {
                        droppedPacketCount++;
                        droppedPayloadBytes += payload.Length;
                    }
                    else
                    {
                        callbackQueue.Enqueue(new MonitorRecord
                        {
                            CallbackGeneration = generation,
                            ProtocolGeneration = packet.Generation,
                            Sequence = packet.Sequence,
                            ComPort = packet.ComPort,
                            CommState = packet.CommState,
                            TotalLength = packet.TotalLength,
                            FragmentOffset = packet.FragmentOffset,
                            Flags = packet.Flags,
                            Data = payload,
                        });
                        queuedPayloadBytes += payload.Length;
                    }

                    if (!drainScheduled)
                    {
                        drainScheduled = true;
                        scheduleDrain = true;
                    }
                }

                if (scheduleDrain)
                    ScheduleDrain(generation);
                return 1;
            }
            catch (Exception ex)
            {
                // Managed exceptions must never cross the native callback ABI.
                Debug.WriteLine($"Serial monitor native callback failed: {ex.Message}");
                return 0;
            }
        }

        private static ulong SaturatingAdd(ulong left, ulong right)
        {
            ulong result = left + right;
            return result < left ? ulong.MaxValue : result;
        }

        private static bool IsPacketMetadataValid(MonitorPacket packet, int selectedCom)
        {
            if (Marshal.SizeOf<MonitorPacket>() != WirePacketSize ||
                packet.Magic != WireMagic ||
                packet.Version != AbiVersion ||
                packet.HeaderSize != WireHeaderSize ||
                packet.Generation == 0 ||
                packet.ComPort != (uint)selectedCom ||
                packet.CommState < (uint)State.Disconnect ||
                packet.CommState > (uint)State.Send ||
                packet.DataLength > MaxFragmentData ||
                packet.Data == null ||
                packet.Data.Length < checked((int)packet.DataLength))
            {
                return false;
            }

            ulong fragmentEnd = (ulong)packet.FragmentOffset + packet.DataLength;
            if (fragmentEnd > packet.TotalLength)
                return false;
            if (packet.CommState == (uint)State.Disconnect)
            {
                return packet.DataLength == 0 &&
                    packet.TotalLength == 0 &&
                    packet.FragmentOffset == 0;
            }
            return packet.TotalLength > 0;
        }

        private static bool IsWithinCallbackQueueLimits(
            int queuedCount,
            long queuedBytes,
            int incomingBytes)
        {
            return incomingBytes >= 0 &&
                queuedCount < MaxQueuedPacketCount &&
                queuedBytes >= 0 &&
                queuedBytes <= MaxQueuedPayloadBytes - incomingBytes;
        }

        private static int GetMonitorPacketSize()
        {
            return Marshal.SizeOf<MonitorPacket>();
        }

        private static bool ProbePacketValidationBehavior()
        {
            var packet = new MonitorPacket
            {
                Magic = WireMagic,
                Version = AbiVersion,
                HeaderSize = WireHeaderSize,
                Generation = 44,
                Sequence = 7,
                ComPort = 9,
                CommState = (uint)State.Receive,
                TotalLength = 3,
                FragmentOffset = 0,
                DataLength = 3,
                Flags = FlagFirst | FlagLast,
                Data = new byte[MaxFragmentData],
            };
            bool validAccepted = IsPacketMetadataValid(packet, 9);
            bool wrongComRejected = !IsPacketMetadataValid(packet, 10);
            packet.DataLength = MaxFragmentData + 1;
            bool oversizedRejected = !IsPacketMetadataValid(packet, 9);
            return validAccepted && wrongComRejected && oversizedRejected;
        }

        private void ScheduleDrain(int generation)
        {
            var dispatcher = Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)

            {
                lock (callbackQueueGate)
                {
                    if (generation == callbackGeneration)
                        drainScheduled = false;
                }
                return;
            }

            try
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => DrainCallbackQueue(generation)));
            }
            catch (Exception ex)
            {
                lock (callbackQueueGate)
                {
                    if (generation == callbackGeneration)
                        drainScheduled = false;
                }
                Debug.WriteLine($"Scheduling serial monitor drain failed: {ex.Message}");
            }
        }

        private void DrainCallbackQueue(int generation)
        {
            var batch = new List<MonitorRecord>(MaxDrainPacketCount);
            bool hasMore;
            lock (callbackQueueGate)
            {
                if (generation != callbackGeneration || callbacksEnabled == 0)
                    return;

                int batchBytes = 0;
                while (callbackQueue.Count > 0 && batch.Count < MaxDrainPacketCount)
                {
                    var next = callbackQueue.Peek();
                    if (batch.Count > 0 && batchBytes + next.Data.Length > MaxDrainPayloadBytes)
                        break;
                    callbackQueue.Dequeue();
                    queuedPayloadBytes -= next.Data.Length;
                    batchBytes += next.Data.Length;
                    batch.Add(next);
                }

                hasMore = callbackQueue.Count > 0;
                if (!hasMore)
                    drainScheduled = false;
            }

            foreach (var record in batch)
            {
                if (record.CallbackGeneration != generation ||
                    generation != Volatile.Read(ref callbackGeneration) || Volatile.Read(ref callbacksEnabled) == 0)
                    continue;
                try
                {
                    ShowMonitorData(record);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Serial monitor log drain failed: {ex.Message}");
                }
            }
            UpdateDropStatus();

            if (hasMore)
                ScheduleDrain(generation);
        }

        private static void ShowMonitorData(MonitorRecord record)
        {
            var color = Tools.Logger.GetThemeBrush("AppGlassTextBrush", SystemColors.ControlTextBrush);
            string direction = "unknown";
            switch ((State)record.CommState)
            {
                case State.Send:
                    direction = "→";
                    color = Tools.Logger.GetThemeBrush("AppDataSentBrush", Brushes.IndianRed);
                    break;
                case State.Receive:
                    direction = "←";
                    color = Tools.Logger.GetThemeBrush("AppDataReceivedBrush", Brushes.SeaGreen);
                    break;
                case State.Disconnect:
                    direction = "❌";
                    color = Tools.Logger.GetThemeBrush("AppDangerBrush", Brushes.OrangeRed);
                    break;
            }

            string fragment = string.Empty;
            if (record.TotalLength != (uint)record.Data.Length || record.FragmentOffset != 0)
            {
                fragment = $" [{record.FragmentOffset}+{record.Data.Length}/{record.TotalLength}]";
            }
            if ((record.Flags & FlagTruncated) != 0)
                fragment += " [truncated]";
            if ((record.Flags & FlagFirst) == 0 || (record.Flags & FlagLast) == 0)
                fragment += $" [seq {record.Sequence}]";

            Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
            {
                title = $"monitor COM{record.ComPort} {direction}{fragment}",
                data = record.Data,
                color = color,
            });
        }

        private int ResetCallbackQueueForStart(uint comPort)
        {
            int generation = Interlocked.Increment(ref callbackGeneration);
            lock (callbackQueueGate)
            {
                callbackQueue.Clear();
                queuedPayloadBytes = 0;
                droppedPacketCount = 0;
                droppedPayloadBytes = 0;
                nativeDroppedPacketCount = 0;
                nativeProtocolGeneration = 0;
                drainScheduled = false;
            }
            Volatile.Write(ref selectedComPort, checked((int)comPort));
            Volatile.Write(ref callbacksEnabled, 1);
            Debug.WriteLine($"Serial monitor callback generation {generation} started.");
            UpdateDropStatus();
            return generation;
        }

        private void StopAndClearCallbackQueue()
        {
            Volatile.Write(ref callbacksEnabled, 0);
            Interlocked.Increment(ref callbackGeneration);
            Volatile.Write(ref selectedComPort, 0);
            lock (callbackQueueGate)
            {
                callbackQueue.Clear();
                queuedPayloadBytes = 0;
                nativeProtocolGeneration = 0;
                drainScheduled = false;
            }
        }

        private void UpdateDropStatus()
        {
            long managedPackets;
            long managedBytes;
            ulong nativePackets;
            lock (callbackQueueGate)
            {
                managedPackets = droppedPacketCount;
                managedBytes = droppedPayloadBytes;
                nativePackets = nativeDroppedPacketCount;
            }
            MonitorDropTextBlock.Text =
                $"Queue: ≤{MaxQueuedPacketCount} packets / {MaxQueuedPayloadBytes / 1024} KiB, " +
                $"policy={QueueDropPolicy}; dropped managed={managedPackets} packets/{managedBytes} bytes, " +
                $"native={nativePackets} packets.";
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            pageActive = true;
            if (!subscribedProgramClosed)
            {
                Tools.Global.ProgramClosedEvent += Global_ProgramClosedEvent;
                subscribedProgramClosed = true;
            }

            if (firstLoad)
            {
                firstLoad = false;
                Refresh();
            }

            UpdateMonitorControls();
            UpdateDropStatus();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            pageActive = false;
            _ = StopMonitoringAsync();
            if (subscribedProgramClosed)
            {
                Tools.Global.ProgramClosedEvent -= Global_ProgramClosedEvent;
                subscribedProgramClosed = false;
            }
        }

        private void Global_ProgramClosedEvent(object sender, EventArgs e)
        {
            pageActive = false;
            _ = StopMonitoringAsync();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void PidComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!connected && !starting && !stopping) lifecycleStatus = string.Empty;
            UpdateMonitorControls();
        }

        private void SerialPortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!connected && !starting && !stopping) lifecycleStatus = string.Empty;
            UpdateMonitorControls();
        }

        private async void MonitorButton_Click(object sender, RoutedEventArgs e)
        {
            bool isConnected;
            lock (monitorLifecycleGate)
            {
                if (stopping) return;
                isConnected = connected || starting;
            }

            if (isConnected)
            {
                await StopMonitoringAsync();
                return;
            }

            var process = PidComboBox.SelectedItem as ProcessChoice;
            var portName = SerialPortComboBox.SelectedItem as string;
            if (process == null || !TryParseComPort(portName, out uint comPort))
                return;
            if (!IsTargetCompatible(process, out string compatibilityReason))
            {
                lifecycleStatus = compatibilityReason;
                UpdateMonitorControls();
                return;
            }

            try
            {
                await StartMonitoringAsync(process, comPort);
            }
            catch (Exception ex)
            {
                await StopMonitoringAsync();
                lifecycleStatus = "加载失败 / start failed: " + ex.Message;
            }
            UpdateMonitorControls();
        }

        private async Task<bool> StartMonitoringAsync(ProcessChoice target, uint comPort)
        {
            ValidateTargetSnapshot(target);
            var client = new Tools.SerialMonitorBrokerClient(Tools.Global.AppPath);
            var cancellation = new CancellationTokenSource();
            var requestSettled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int generation;
            lock (monitorLifecycleGate)
            {
                if (!pageActive || starting || stopping || broker != null)
                {
                    client.Dispose();
                    cancellation.Dispose();
                    return false;
                }
                broker = client;
                monitorCancellation = cancellation;
                startRequestSettled = requestSettled.Task;
                starting = true;
                connected = false;
                generation = ResetCallbackQueueForStart(comPort);
                lifecycleStatus = "正在启动串口监听…";
            }
            UpdateMonitorControls();
            try
            {
                int status;
                try
                {
                    status = await client.StartAsync(target.Pid, comPort, target.StartTimeUtcTicks,
                        ArchitectureName(target.Architecture), data => BrokerPacketReceived(data, generation),
                        message => BrokerExited(client, generation, message), cancellation.Token).ConfigureAwait(false);
                }
                finally { requestSettled.TrySetResult(true); }
                lock (monitorLifecycleGate)
                {
                    if (!ReferenceEquals(broker, client) || generation != callbackGeneration ||
                        cancellation.IsCancellationRequested || !pageActive)
                        return false;
                    starting = false;
                    if (status == NativeStarted && client.IsRunning)
                    {
                        connected = true;
                        lifecycleStatus = $"正在监听 COM{comPort}；已自动匹配 {ArchitectureName(target.Architecture)} 监听程序。请在目标程序重新打开所选串口。";
                        return true;
                    }
                }
                await StopMonitoringAsync(status == NativeStarted
                    ? "监听辅助程序已退出，未建立监听。" : DescribeStartFailure(status));
                return false;
            }
            catch (OperationCanceledException)
            {
                if (IsCurrentBroker(client, generation))
                    await StopMonitoringAsync("监听启动已取消。");
                return false;
            }
            catch (Exception ex)
            {
                if (IsCurrentBroker(client, generation))
                    await StopMonitoringAsync("监听启动失败：" + ex.GetBaseException().Message);
                return false;
            }
            finally
            {
                requestSettled.TrySetResult(true);
                UpdateMonitorControls();
            }
        }

        private bool IsCurrentBroker(Tools.SerialMonitorBrokerClient client, int generation)
        {
            lock (monitorLifecycleGate)
                return ReferenceEquals(broker, client) && generation == callbackGeneration;
        }

        private void BrokerExited(Tools.SerialMonitorBrokerClient client, int generation, string message)
        {
            if (!IsCurrentBroker(client, generation)) return;
            try
            {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    _ = StopMonitoringAsync();
                    return;
                }
                Dispatcher.InvokeAsync(new Action(async () =>
                {
                    if (IsCurrentBroker(client, generation))
                        await StopMonitoringAsync("监听已结束：" + (message ?? "辅助程序已退出。"));
                }));
            }
            catch (InvalidOperationException) { _ = StopMonitoringAsync(); }
        }

        private Task StopMonitoringAsync(string reason = null)
        {
            Tools.SerialMonitorBrokerClient client;
            CancellationTokenSource cancellation;
            bool wasStarting;
            Task pendingStart;
            int generation;
            TaskCompletionSource<bool> completion;
            lock (monitorLifecycleGate)
            {
                if (stopping) return stopTask;
                StopAndClearCallbackQueue();
                connected = false;
                client = broker;
                cancellation = monitorCancellation;
                wasStarting = starting;
                starting = false;
                broker = null;
                monitorCancellation = null;
                pendingStart = startRequestSettled;
                startRequestSettled = Task.CompletedTask;
                if (client == null)
                {
                    if (reason != null) lifecycleStatus = reason;
                    return Task.CompletedTask;
                }
                stopping = true;
                generation = callbackGeneration;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                stopTask = completion.Task;
            }
            _ = CompleteStopAsync(client, cancellation, pendingStart, wasStarting, generation, reason, completion);
            UpdateMonitorControls();
            return completion.Task;
        }

        private async Task CompleteStopAsync(Tools.SerialMonitorBrokerClient client, CancellationTokenSource cancellation,
            Task pendingStart, bool wasStarting, int generation, string reason, TaskCompletionSource<bool> completion)
        {
            string status;
            try
            {
                cancellation?.Cancel();
                status = DescribeStopStatus(await client.StopAsync().ConfigureAwait(false));
                if (wasStarting && reason == null) status = "监听启动已取消。";
            }
            catch (Exception ex) { status = "监听回调已停止，辅助程序清理失败：" + ex.GetBaseException().Message; }
            finally
            {
                try { client.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex); }
                // The client registers against this token while its startup
                // handshake is pending. Do not race its registration cleanup.
                await pendingStart.ConfigureAwait(false);
                cancellation?.Dispose();
            }
            lock (monitorLifecycleGate)
            {
                stopping = false;
                if (generation == callbackGeneration) lifecycleStatus = reason ?? status;
            }
            completion.TrySetResult(true);
            UpdateMonitorControls();
        }

        private static string DescribeStartFailure(int status)
        {
            switch (status)
            {
                case -1:
                    return "参数无效 / invalid monitor arguments.";
                case -2:
                    return "当前监听组件不支持目标架构，请使用完整的匹配版本。";
                case -3:
                    return "无法可靠识别目标进程位数，已拒绝注入。";
                case -4:
                    return "监听辅助程序与目标进程位数不匹配，已停止启动。";
                case -5:
                    return "监听后台通信初始化失败，请稍后重试。";
                case -6:
                    return "内置监听组件校验或载入失败，请重新解压完整版本包。";
                case -7:
                    return "无法访问目标进程或启动监听，请检查运行权限和目标程序的保护设置。";
                case -8:
                    return "监听启动超时，已取消本次启动并请求后台清理。";
                case -9:
                    return "目标进程未能载入监听组件。";
                case -10:
                case -11:
                    return "目标监听初始化失败或超时，已请求后台清理。";
                case -12:
                    return "后台接收未能启动，已请求停止监听。";
                case -13:
                    return "目标中已有其他版本的监听组件，请关闭并重新打开目标程序后再试。";
                default:
                    if (status < -100)
                        return $"监听初始化失败（错误码 {status}）。";
                    return $"监听启动失败（状态 {status}）。";
            }
        }

        private static string DescribeStopStatus(int status)
        {
            switch (status)
            {
                case NativeStoppedModuleRetained:
                    return "监听已停止。";
                case NativeStopPendingModuleRetained:
                    return "监听显示已停止，正在清理后台操作。";
                case NativeStoppedModuleStateUnknown:
                    return "监听显示已停止，后台清理状态暂无法确认。";
                case 0:
                    return "没有活动监听会话。";
                default:
                    return $"监听显示已停止（状态 {status}）。";
            }
        }

        private void UpdateMonitorControls()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.InvokeAsync(new Action(UpdateMonitorControls)); }
                catch (InvalidOperationException) { }
                return;
            }
            bool isConnected, isStarting, isStopping;
            lock (monitorLifecycleGate)
            {
                isConnected = connected;
                isStarting = starting;
                isStopping = stopping;
            }

            bool compatible = false;
            string compatibility = lifecycleStatus;
            if (!isConnected && !isStarting && !isStopping)
            {
                var target = PidComboBox.SelectedItem as ProcessChoice;
                compatible = target != null && IsTargetCompatible(target, out compatibility);
                compatible = compatible && TryParseComPort(
                    SerialPortComboBox.SelectedItem as string,
                    out _);
            }

            RefreshButton.IsEnabled = !isConnected && !isStarting && !isStopping;
            PidComboBox.IsEnabled = RefreshButton.IsEnabled;
            SerialPortComboBox.IsEnabled = RefreshButton.IsEnabled;
            MonitorButton.IsEnabled = !isStopping && (isStarting || isConnected || compatible);
            MonitorButton.Content = isStarting ? "取消启动" : isConnected
                ? TryFindResource("SerialMonitorStop") as string ?? "Stop"
                : TryFindResource("SerialMonitorStart") as string ?? "Start";
            MonitorStatusTextBlock.Text = isStopping ? "正在停止监听…" : isConnected || isStarting
                ? lifecycleStatus
                : (string.IsNullOrWhiteSpace(lifecycleStatus) ? compatibility : lifecycleStatus);
        }

        private static bool IsTargetCompatible(ProcessChoice target, out string reason)
        {
            if (target == null || target.Pid == 0 || target.StartTimeUtcTicks <= 0)
            {
                reason = "无法可靠读取目标进程身份，请刷新列表或检查访问权限。";
                return false;
            }
            if (target.Pid == GetCurrentProcessId() ||
                string.Equals(target.Name, "SerialMonitorBroker", StringComparison.OrdinalIgnoreCase))
            {
                reason = "请选择实际使用串口的软件，不能监听 llcom plus 自身或监听辅助程序。";
                return false;
            }
            if (!IsTargetArchitectureSupported(target.Architecture, Environment.Is64BitOperatingSystem))
            {
                reason = target.Architecture == ProcessArchitecture.Unknown
                    ? "无法可靠检测目标位数，请检查进程访问权限。"
                    : "当前系统不支持该目标架构的串口监听。";
                return false;
            }
            var architecture = ArchitectureName(target.Architecture);
            if (!Tools.SerialMonitorBrokerClient.IsHelperAvailable(Tools.Global.AppPath, architecture))
            {
                reason = $"缺少 {architecture} 监听组件，请重新解压完整版本包。";
                return false;
            }
            reason = $"可监听 {architecture} 程序，将自动匹配监听组件。请选择实际打开串口的进程。";
            return true;
        }

        private static bool IsTargetArchitectureSupported(ProcessArchitecture architecture, bool operatingSystem64Bit)
        {
            return architecture == ProcessArchitecture.X86 ||
                architecture == ProcessArchitecture.X64 && operatingSystem64Bit;
        }

        private static string ArchitectureName(ProcessArchitecture architecture)
        {
            return architecture == ProcessArchitecture.X86 ? "x86" :
                architecture == ProcessArchitecture.X64 ? "x64" : "unknown";
        }

        private static void ValidateTargetSnapshot(ProcessChoice target)
        {
            if (target == null || target.Pid == 0 || target.StartTimeUtcTicks <= 0 ||
                target.Pid == GetCurrentProcessId())
                throw new InvalidOperationException("目标进程身份无效，请重新选择实际使用串口的软件。");
            if (!TryReadProcessIdentity(target.Pid, out var architecture, out var startTime) ||
                !ProcessSnapshotMatches(target, architecture, startTime))
                throw new InvalidOperationException("目标进程已退出、被替换或无法访问，请刷新列表后重试。");
        }

        private static bool ProcessSnapshotMatches(ProcessChoice target, ProcessArchitecture architecture,
            long startTimeUtcTicks)
        {
            return target != null && target.StartTimeUtcTicks > 0 &&
                target.StartTimeUtcTicks == startTimeUtcTicks && target.Architecture == architecture;
        }

        private static ProcessArchitecture GetProcessArchitecture(Process process)
        {
            return TryReadProcessIdentity(checked((uint)process.Id), out var architecture, out _)
                ? architecture : ProcessArchitecture.Unknown;
        }

        private static bool TryReadProcessIdentity(uint processId, out ProcessArchitecture architecture,
            out long startTimeUtcTicks)
        {
            architecture = ProcessArchitecture.Unknown;
            startTimeUtcTicks = 0;
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (!GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
                startTimeUtcTicks = DateTime.FromFileTimeUtc(created).Ticks;
                try
                {
                    if (IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
                    {
                        ushort effectiveMachine = processMachine == ImageFileMachineUnknown
                            ? nativeMachine
                            : processMachine;
                        architecture = MachineToArchitecture(effectiveMachine);
                        return architecture != ProcessArchitecture.Unknown;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    // Windows versions before IsWow64Process2 use the fallback.
                }

                if (!IsWow64Process(handle, out bool wow64)) return false;
                if (!Environment.Is64BitOperatingSystem)
                    architecture = ProcessArchitecture.X86;
                else
                {
                    GetNativeSystemInfo(out var native);
                    // The old API cannot distinguish all ARM64 emulation modes.
                    architecture = native.ProcessorArchitecture == 9
                        ? wow64 ? ProcessArchitecture.X86 : ProcessArchitecture.X64
                        : ProcessArchitecture.Unknown;
                }
                return architecture != ProcessArchitecture.Unknown;
            }
            catch
            {
                architecture = ProcessArchitecture.Unknown;
                startTimeUtcTicks = 0;
                return false;
            }
            finally { CloseHandle(handle); }
        }

        private static ProcessArchitecture MachineToArchitecture(ushort machine)
        {
            switch (machine)
            {
                case ImageFileMachineI386:
                    return ProcessArchitecture.X86;
                case ImageFileMachineAmd64:
                    return ProcessArchitecture.X64;
                case ImageFileMachineArm64:
                    return ProcessArchitecture.Arm64;
                default:
                    return ProcessArchitecture.Unknown;
            }
        }

        private static string ArchitectureText(ProcessArchitecture architecture)
        {
            switch (architecture)
            {
                case ProcessArchitecture.X86:
                    return "x86";
                case ProcessArchitecture.X64:
                    return "x64";
                case ProcessArchitecture.Arm64:
                    return "ARM64 (disabled)";
                default:
                    return "unknown (disabled)";
            }
        }

        private static bool TryParseComPort(string value, out uint port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(value) ||
                !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return uint.TryParse(value.Substring(3), out port) && port > 0 && port <= 4096;
        }

        private void Refresh()
        {
            if (connected || starting || stopping) return;
            uint previousPid = (PidComboBox.SelectedItem as ProcessChoice)?.Pid ?? 0;
            string previousPort = SerialPortComboBox.SelectedItem as string;

            var processChoices = new List<ProcessChoice>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        TryReadProcessIdentity(checked((uint)process.Id), out var architecture, out var startTime);
                        processChoices.Add(new ProcessChoice
                        {
                            Pid = checked((uint)process.Id),
                            Name = process.ProcessName,
                            Architecture = architecture,
                            ArchitectureDetail = ArchitectureText(architecture),
                            StartTimeUtcTicks = startTime,
                        });
                    }
                    catch
                    {
                        // Process exited or denied access while refreshing.
                    }
                }
            }
            processChoices.Sort((left, right) =>
                string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase));

            PidComboBox.Items.Clear();
            foreach (var choice in processChoices)
                PidComboBox.Items.Add(choice);
            PidComboBox.SelectedItem = processChoices.FirstOrDefault(choice => choice.Pid == previousPid)
                ?? processChoices.FirstOrDefault();

            var ports = SerialPort.GetPortNames()
                .Where(port => TryParseComPort(port, out _))
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SerialPortComboBox.Items.Clear();
            foreach (var port in ports)
                SerialPortComboBox.Items.Add(port);
            SerialPortComboBox.SelectedItem = ports.FirstOrDefault(port =>
                string.Equals(port, previousPort, StringComparison.OrdinalIgnoreCase))
                ?? ports.FirstOrDefault();

            lifecycleStatus = string.Empty;
            UpdateMonitorControls();
        }
    }
}
