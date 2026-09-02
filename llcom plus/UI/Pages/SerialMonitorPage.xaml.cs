using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CallbackDelegate(IntPtr packet);

        [DllImport("serial_monitor.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        private static extern int UnMonitorComm();

        [DllImport("serial_monitor.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        private static extern int MonitorComm(uint pid, uint comIndex, CallbackDelegate callback);

        [DllImport("serial_monitor.dll", CallingConvention = CallingConvention.Winapi, ExactSpelling = true)]
        private static extern uint SerialMonitorGetAbiVersion();

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

        private readonly CallbackDelegate nativeCallbackDelegate;
        private readonly object monitorLifecycleGate = new object();
        private readonly object callbackQueueGate = new object();
        private readonly Queue<MonitorRecord> callbackQueue = new Queue<MonitorRecord>();

        private bool firstLoad = true;
        private bool connected;
        private bool nativePluginAvailable = true;
        private bool nativeSessionStarted;
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
            nativeCallbackDelegate = NativeCallback;
        }

        private int NativeCallback(IntPtr parameter)
        {
            try
            {
                if (parameter == IntPtr.Zero || Volatile.Read(ref callbacksEnabled) == 0)
                    return 0;

                var packet = Marshal.PtrToStructure<MonitorPacket>(parameter);
                if (!IsPacketMetadataValid(packet, Volatile.Read(ref selectedComPort)))
                    return 0;

                int generation = Volatile.Read(ref callbackGeneration);
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
                if (record.CallbackGeneration != generation)
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

        private void ResetCallbackQueueForStart(uint comPort)
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
            if (!subscribedProgramClosed)
            {
                Tools.Global.ProgramClosedEvent += Global_ProgramClosedEvent;
                subscribedProgramClosed = true;
            }

            if (firstLoad)
            {
                firstLoad = false;
                try
                {
                    uint nativeAbi = SerialMonitorGetAbiVersion();
                    if (nativeAbi != AbiVersion)
                    {
                        nativePluginAvailable = false;
                        lifecycleStatus =
                            $"串口监听 native ABI 不匹配（需要 {AbiVersion}，实际 {nativeAbi}）；已禁用注入。";
                        MonitorStatusTextBlock.Text = lifecycleStatus;
                        MonitorButton.IsEnabled = false;
                        return;
                    }
                    // Only a matching ABI may enter any lifecycle API.
                    UnMonitorComm();
                }
                catch (Exception ex) when (
                    ex is DllNotFoundException ||
                    ex is BadImageFormatException ||
                    ex is EntryPointNotFoundException)
                {
                    nativePluginAvailable = false;
                    lifecycleStatus =
                        "串口监听 native 插件缺失、位数错误或为旧 ABI；已安全禁用，请先从 Rust 源码重建。" +
                        " / native monitor unavailable or stale: " + ex.Message;
                    MonitorButton.IsEnabled = false;
                    MonitorStatusTextBlock.Text = lifecycleStatus;
                    return;
                }
                Refresh();
            }

            UpdateMonitorControls();
            UpdateDropStatus();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            StopMonitoring();
            if (subscribedProgramClosed)
            {
                Tools.Global.ProgramClosedEvent -= Global_ProgramClosedEvent;
                subscribedProgramClosed = false;
            }
        }

        private void Global_ProgramClosedEvent(object sender, EventArgs e)
        {
            StopMonitoring();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void PidComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMonitorControls();
        }

        private void SerialPortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMonitorControls();
        }

        private void MonitorButton_Click(object sender, RoutedEventArgs e)
        {
            bool isConnected;
            lock (monitorLifecycleGate)
                isConnected = connected;

            if (isConnected)
            {
                StopMonitoring();
                UpdateMonitorControls();
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
                StartMonitoring(process.Pid, comPort);
            }
            catch (Exception ex)
            {
                StopMonitoring();
                lifecycleStatus = "加载失败 / start failed: " + ex.Message;
            }
            UpdateMonitorControls();
        }

        private bool StartMonitoring(uint pid, uint comPort)
        {
            lock (monitorLifecycleGate)
            {
                ResetCallbackQueueForStart(comPort);
                int status;
                try
                {
                    status = MonitorComm(pid, comPort, nativeCallbackDelegate);
                }
                catch
                {
                    StopAndClearCallbackQueue();
                    throw;
                }

                if (status != NativeStarted)
                {
                    connected = false;
                    nativeSessionStarted = false;
                    StopAndClearCallbackQueue();
                    lifecycleStatus = DescribeStartFailure(status);
                    return false;
                }

                connected = true;
                nativeSessionStarted = true;
                lifecycleStatus =
                    $"正在监听 COM{comPort}；x64 同位数注入已完成并通过初始化握手。";
                return true;
            }
        }

        private void StopMonitoring()
        {
            lock (monitorLifecycleGate)
            {
                StopAndClearCallbackQueue();
                connected = false;
                if (!nativeSessionStarted)
                    return;

                nativeSessionStarted = false;
                try
                {
                    lifecycleStatus = DescribeStopStatus(UnMonitorComm());
                }
                catch (Exception ex)
                {
                    lifecycleStatus =
                        "托管回调已停止；native 停用状态未知，模块不会主动卸载并保留至目标退出。" +
                        " / managed callbacks stopped; native state unknown: " + ex.Message;
                }
            }
        }

        private static string DescribeStartFailure(int status)
        {
            switch (status)
            {
                case -1:
                    return "参数无效 / invalid monitor arguments.";
                case -2:
                    return "安全策略已禁用 x86 注入监听：不存在经过验证的 x86 指令重定位方案。";
                case -3:
                    return "无法可靠识别目标进程位数，已拒绝注入。";
                case -4:
                    return "目标与 llcom plus 位数不同，已拒绝跨位数注入。";
                case -5:
                    return "无法创建版本化 IPC/共享配置；可能已有监控会话正在清理。";
                case -6:
                    return "无法验证或提取内嵌 hook DLL。";
                case -7:
                    return "远端线程或远端 LoadLibrary 地址解析失败。";
                case -8:
                    return "LoadLibrary 等待超时；后台将等待远端线程并释放参数内存，晚加载模块不会初始化。";
                case -9:
                    return "目标未成功加载 hook DLL。";
                case -10:
                case -11:
                    return "hook 显式初始化握手失败或超时；后台会安全停用晚成功的初始化。";
                case -12:
                    return "native 回调工作线程启动失败，hook 已请求停用。";
                case -13:
                    return "目标中存在不同版本的串口 hook；为避免叠加 detour，需先退出目标进程。";
                default:
                    if (status < -100)
                        return $"hook 初始化返回错误码 {status}；未建立监听。";
                    return $"native 监听启动失败（状态 {status}）。";
            }
        }

        private static string DescribeStopStatus(int status)
        {
            switch (status)
            {
                case NativeStoppedModuleRetained:
                    return "监听已停用；为避免执行中 trampoline 被卸载，hook 模块将保留至目标进程退出。";
                case NativeStopPendingModuleRetained:
                    return "托管回调已停止；native 正在后台有界等待活动调用排空，hook 模块保留至目标退出。";
                case NativeStoppedModuleStateUnknown:
                    return "托管回调已停止；native 停用状态暂无法确认，后台会重试，模块保留至目标退出。";
                case 0:
                    return "没有活动监听会话。";
                default:
                    return $"监听停止返回状态 {status}；hook 模块不会从活动目标中强制卸载。";
            }
        }

        private void UpdateMonitorControls()
        {
            bool isConnected;
            lock (monitorLifecycleGate)
                isConnected = connected;

            bool compatible = false;
            string compatibility = lifecycleStatus;
            if (!isConnected)
            {
                var target = PidComboBox.SelectedItem as ProcessChoice;
                compatible = target != null && IsTargetCompatible(target, out compatibility);
                compatible = compatible && TryParseComPort(
                    SerialPortComboBox.SelectedItem as string,
                    out _);
            }

            RefreshButton.IsEnabled = !isConnected;
            PidComboBox.IsEnabled = !isConnected;
            SerialPortComboBox.IsEnabled = !isConnected;
            MonitorButton.IsEnabled = nativePluginAvailable && (isConnected || compatible);
            MonitorButton.Content = isConnected
                ? TryFindResource("SerialMonitorStop") as string ?? "Stop"
                : TryFindResource("SerialMonitorStart") as string ?? "Start";
            MonitorStatusTextBlock.Text = isConnected
                ? lifecycleStatus
                : (string.IsNullOrWhiteSpace(lifecycleStatus) ? compatibility : lifecycleStatus);
        }

        private static bool IsTargetCompatible(ProcessChoice target, out string reason)
        {
            if (!Environment.Is64BitProcess)
            {
                reason =
                    "安全策略：x86 注入监听已禁用（没有经过验证的 x86 指令边界/重定位/原子切换实现）。";
                return false;
            }
            if (target.Architecture == ProcessArchitecture.Unknown)
            {
                reason = "无法可靠检测目标位数，已禁用注入。";
                return false;
            }
            if (target.Architecture != ProcessArchitecture.X64)
            {
                reason =
                    $"目标为 {target.ArchitectureDetail}，当前为 x64；跨位数注入已禁用。";
                return false;
            }
            reason = "目标与当前进程均为 x64，可启动监听。停止时 hook 模块会安全保留至目标退出。";
            return true;
        }

        private static ProcessArchitecture GetProcessArchitecture(Process process)
        {
            try
            {
                try
                {
                    if (IsWow64Process2(process.Handle, out ushort processMachine, out ushort nativeMachine))
                    {
                        ushort effectiveMachine = processMachine == ImageFileMachineUnknown
                            ? nativeMachine
                            : processMachine;
                        return MachineToArchitecture(effectiveMachine);
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    // Windows versions before IsWow64Process2 use the fallback.
                }

                if (!IsWow64Process(process.Handle, out bool wow64))
                    return ProcessArchitecture.Unknown;
                if (!Environment.Is64BitOperatingSystem)
                    return ProcessArchitecture.X86;
                return wow64 ? ProcessArchitecture.X86 : ProcessArchitecture.X64;
            }
            catch
            {
                return ProcessArchitecture.Unknown;
            }
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
                    return "x86 (disabled)";
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
            uint previousPid = (PidComboBox.SelectedItem as ProcessChoice)?.Pid ?? 0;
            string previousPort = SerialPortComboBox.SelectedItem as string;

            var processChoices = new List<ProcessChoice>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        var architecture = GetProcessArchitecture(process);
                        processChoices.Add(new ProcessChoice
                        {
                            Pid = checked((uint)process.Id),
                            Name = process.ProcessName,
                            Architecture = architecture,
                            ArchitectureDetail = ArchitectureText(architecture),
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
