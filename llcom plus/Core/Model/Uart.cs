using llcom_plus.ScriptEnv;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Model
{
    class Uart
    {
        private const int SerialWriteTimeoutMilliseconds = 5000;
        private const int SerialDisposeTimeoutMilliseconds = 5000;
        private const int MaxRetiredSerialPorts = 8;

        //废弃的串口对象，存放处，尝试fix[System.ObjectDisposedException: 已关闭 Safe handle]
        //https://drdump.com/Problem.aspx?ProblemID=524533
        private List<SerialPort> useless = new List<SerialPort>();

        public SerialPort serial = new SerialPort();
        public event EventHandler UartDataRecived;
        public event EventHandler UartDataSent;
        public event EventHandler UartDataRawSent;
        private Stream lastPortBaseStream = null;
        private readonly object sendLock = new object();
        private readonly object receiveLock = new object();
        private readonly object lifecycleLock = new object();
        private volatile bool directReceiveMode;
        private int directReceiveScheduled;
        private long connectionGeneration;
        private bool isShuttingDown;
        private bool _rts = false;
        // DTR 的上升沿会让不少 USB 串口设备复位。默认保持关闭，确实需要
        // DTR 的设备再由用户按端口开启，避免“写入成功但设备正在重启”。
        private bool _dtr = false;
        private Tools.SerialPinMonitor pinMonitor;
        private UartPortProfile runtimeProfileOverride;

        internal void SetRuntimeProfileOverride(UartPortProfile profile)
        {
            Volatile.Write(
                ref runtimeProfileOverride,
                profile == null ? null : Settings.CreateNormalizedUartProfileSnapshot(profile));
            ApplyFlowControl();
        }

        internal void ClearRuntimeProfileOverride()
        {
            Volatile.Write(ref runtimeProfileOverride, null);
            ApplyFlowControl();
        }

        private UartPortProfile GetRuntimeProfile()
        {
            return Volatile.Read(ref runtimeProfileOverride) ??
                Tools.Global.setting?.GetCurrentUartProfileSnapshot() ??
                new UartPortProfile();
        }

        internal int GetRuntimeEncodingCodePage()
        {
            return GetRuntimeProfile().encoding;
        }

        internal sealed class ConnectionLease
        {
            private readonly Uart owner;

            internal ConnectionLease(Uart owner, SerialPort port, long generation, string portName)
            {
                this.owner = owner;
                Port = port;
                Generation = generation;
                PortName = portName ?? string.Empty;
            }

            internal SerialPort Port { get; }
            internal long Generation { get; }
            internal string PortName { get; }
            internal string Identity => $"uart:{PortName}:{Generation}";
            internal string DisplayName => string.IsNullOrWhiteSpace(PortName) ? "Serial" : PortName;
            internal bool IsOpen => owner.IsConnectionOpen(this);

            internal bool Send(
                byte[] data,
                CancellationToken token,
                Action<int> committedBytes,
                bool raiseEvents)
            {
                if (!IsOpen)
                    return false;

                owner.SendDataForLease(this, data, token, null, raiseEvents, committedBytes);
                return true;
            }
        }

        internal ConnectionLease CaptureConnectionLease()
        {
            lock (lifecycleLock)
            {
                var port = serial;
                var portName = string.Empty;
                try { portName = port?.PortName ?? string.Empty; }
                catch (Exception ex) when (IsClosedSerialException(ex)) { }
                return new ConnectionLease(this, port, connectionGeneration, portName);
            }
        }

        private bool IsConnectionOpen(ConnectionLease lease)
        {
            lock (lifecycleLock)
            {
                if (!IsConnectionCurrentUnsafe(lease))
                    return false;

                try { return lease.Port?.IsOpen == true; }
                catch (Exception ex) when (IsClosedSerialException(ex)) { return false; }
            }
        }

        private bool IsConnectionCurrentUnsafe(ConnectionLease lease)
        {
            return lease != null &&
                !isShuttingDown &&
                lease.Generation == connectionGeneration &&
                ReferenceEquals(lease.Port, serial);
        }

        private bool IsConnectionCurrent(ConnectionLease lease)
        {
            lock (lifecycleLock)
                return IsConnectionCurrentUnsafe(lease);
        }

        public bool Rts
        {
            get
            {
                return _rts;
            }
            set
            {
                _rts = value;
                if (!IsHardwareFlowControl())
                    TryApplyControlLine(port => port.RtsEnable = value);
            }
        }
        public bool Dtr
        {
            get
            {
                return _dtr;
            }
            set
            {
                _dtr = value;
                TryApplyControlLine(port => port.DtrEnable = value);
            }
        }

        private static readonly object objLock = new object();
        
        /// <summary>
        /// 初始化串口各个触发函数
        /// </summary>
        public Uart()
        {
            //声明接收到事件
            serial.DataReceived += Serial_DataReceived;
            ConfigureSerialDevice(serial);
            AttachPinMonitor(serial);
            var readThread = new Thread(ReadData)
            {
                IsBackground = true
            };
            readThread.Start();

            // Script sends resolve the active serial target at call time. The returned
            // lease then pins the chosen page/slot/connection for this one send.
            ScriptApis.SendChannelsRegister("uart", (data, _) =>
            {
                if (data == null)
                    return false;

                var target = Tools.Global.CaptureActiveSerialTarget();
                return target?.IsOpen == true &&
                    target.Send(data, CancellationToken.None, null);
            });
        }

        /// <summary>
        /// The first split pane keeps using the already-open main UART. While the split
        /// page is visible, read it directly from DataReceived just like the independently
        /// owned split ports. This avoids mixing the main UART's delayed wait-handle reader
        /// with direct readers from the other panes without closing or reopening the port.
        /// </summary>
        public void SetDirectReceiveMode(bool enabled)
        {
            if (directReceiveMode == enabled)
                return;

            directReceiveMode = enabled;
            lock (receiveLock)
                pendingReceivePort = null;

            // Wake a pending delayed read so it can observe the new mode immediately.
            WaitUartReceive.Set();
            if (enabled)
            {
                var currentPort = serial;
                if (currentPort != null &&
                    Interlocked.CompareExchange(ref directReceiveScheduled, 1, 0) == 0)
                {
                    ThreadPool.QueueUserWorkItem(_ => ReadDataDirect(currentPort));
                }
            }
            Tools.Logger.AddUartLogDebug($"[UartReceive]direct mode={enabled}");
        }

        /// <summary>
        /// 刷新串口对象
        /// </summary>
        private void refreshSerialDevice(bool waitForDispose = false)
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown)
                    return;

                RefreshSerialDeviceCore(waitForDispose);
            }
        }

        private void RefreshSerialDeviceCore(bool waitForDispose)
        {
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]start");
            connectionGeneration = unchecked(connectionGeneration + 1);
            if (connectionGeneration == 0)
                connectionGeneration = 1;
            var oldSerial = serial;
            var oldBaseStream = lastPortBaseStream;
            lastPortBaseStream = null;
            pinMonitor?.Dispose();
            pinMonitor = null;
            try
            {
                if (oldSerial != null)
                    oldSerial.DataReceived -= Serial_DataReceived;
            }
            catch (Exception e)
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]sync DataReceived unsubscribe error:{e.Message}");
            }
            lock (receiveLock)
            {
                if (ReferenceEquals(pendingReceivePort, oldSerial))
                    pendingReceivePort = null;
            }
            DisposeSerialResources(oldSerial, oldBaseStream, waitForDispose);
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]new");
            lock(useless)//短暂保留旧对象，避免系统回调还在引用；同时限制数量避免长期增长。
            {
                useless.Add(oldSerial);
                while (useless.Count > MaxRetiredSerialPorts)
                    useless.RemoveAt(0);
            }
            serial = new SerialPort();
            //声明接收到事件
            serial.DataReceived += Serial_DataReceived;
            ConfigureSerialDevice(serial);
            AttachPinMonitor(serial);
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]done");
        }

        private void AttachPinMonitor(SerialPort port)
        {
            pinMonitor?.Dispose();
            pinMonitor = port == null
                ? null
                : new Tools.SerialPinMonitor(port, Tools.Global.NotifySerialPinStatusChanged);
        }

        private void DisposeSerialResources(SerialPort port, Stream baseStream, bool waitForDispose)
        {
            Action dispose = () =>
            {
                try
                {
                    if (port != null)
                        port.DataReceived -= Serial_DataReceived;
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.DataReceived unsubscribe error:{e.Message}");
                }

                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Close");
                    if (port?.IsOpen == true)
                        port.Close();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Close error:{e.Message}");
                }

                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Dispose");
                    port?.Dispose();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Dispose error:{e.Message}");
                }

                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose");
                    baseStream?.Dispose();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose error:{e.Message}");
                }
            };

            var task = Task.Run(dispose);
            if (waitForDispose && !task.Wait(SerialDisposeTimeoutMilliseconds))
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]dispose timeout {SerialDisposeTimeoutMilliseconds}ms");
                throw new TimeoutException($"串口释放超过 {SerialDisposeTimeoutMilliseconds} 毫秒，后台仍在尝试释放。");
            }
        }

        private void ConfigureSerialDevice(SerialPort port)
        {
            if (port == null)
                return;

            var profile = GetRuntimeProfile();
            port.BaudRate = profile.baudRate;
            port.Parity = (Parity)profile.parity;
            port.DataBits = profile.dataBits;
            port.StopBits = (StopBits)profile.stopBit;

            port.WriteTimeout = SerialWriteTimeoutMilliseconds;
            ApplyControlLines(port);
        }

        public void ApplyFlowControl()
        {
            ApplyControlLines(serial);
        }

        private void ApplyControlLines(SerialPort port)
        {
            if (port == null)
                return;

            try
            {
                var handshake = GetHandshake();
                if (handshake == Handshake.RequestToSend)
                {
                    try
                    {
                        port.RtsEnable = false;
                    }
                    catch
                    {
                    }
                }
                port.Handshake = handshake;
                if (handshake != Handshake.RequestToSend)
                    port.RtsEnable = Rts;
                port.DtrEnable = Dtr;
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
            }
        }

        private void TryApplyControlLine(Action<SerialPort> apply)
        {
            try
            {
                var port = serial;
                if (port != null)
                    apply(port);
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
            }
        }

        private void SetSerialOption(Action<SerialPort> apply)
        {
            try
            {
                apply(serial);
            }
            catch (ObjectDisposedException)
            {
                refreshSerialDevice(waitForDispose: false);
                apply(serial);
            }
        }

        public void SetBaudRate(int baudRate)
        {
            SetSerialOption(port => port.BaudRate = baudRate);
        }

        public void SetParity(Parity parity)
        {
            SetSerialOption(port => port.Parity = parity);
        }

        public void SetDataBits(int dataBits)
        {
            SetSerialOption(port => port.DataBits = dataBits);
        }

        public void SetStopBits(StopBits stopBits)
        {
            SetSerialOption(port => port.StopBits = stopBits);
        }

        private bool IsHardwareFlowControl()
        {
            return GetRuntimeProfile().flowControl == 1;
        }

        private Handshake GetHandshake()
        {
            switch (GetRuntimeProfile().flowControl)
            {
                case 1:
                    return Handshake.RequestToSend;
                case 2:
                    return Handshake.XOnXOff;
                default:
                    return Handshake.None;
            }
        }

        /// <summary>
        /// 获取串口设备COM名
        /// </summary>
        /// <returns></returns>
        public string GetName()
        {
            try
            {
                return serial?.PortName ?? "";
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
                return "";
            }
        }

        /// <summary>
        /// 设置串口设备COM名
        /// </summary>
        /// <returns></returns>
        public void SetName(string s)
        {
            try
            {
                serial.PortName = s;
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
                refreshSerialDevice(waitForDispose: false);
                serial.PortName = s;
            }
        }

        /// <summary>
        /// 查看串口打开状态
        /// </summary>
        /// <returns></returns>
        public bool IsOpen()
        {
            try
            {
                return serial?.IsOpen == true;
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
                return false;
            }
        }

        /// <summary>
        /// 开启串口
        /// </summary>
        public void Open()
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown || Tools.Global.isMainWindowsClosed)
                    throw new ObjectDisposedException(nameof(Uart), "程序正在退出，不能再打开串口。");

                string temp = GetName();
                Tools.Logger.AddUartLogDebug($"[UartOpen]refreshSerialDevice");
                refreshSerialDevice();
                serial.PortName = temp;
                Tools.Logger.AddUartLogDebug($"[UartOpen]open");
                serial.Open();
                lastPortBaseStream = serial.BaseStream;
                pinMonitor?.Arm();
                Tools.Logger.AddUartLogDebug($"[UartOpen]done");
            }
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close(bool waitForDispose = false)
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown)
                    return;

                Tools.Logger.AddUartLogDebug($"[UartClose]refreshSerialDevice");
                refreshSerialDevice(waitForDispose);
                WaitUartReceive.Set();
                Tools.Logger.AddUartLogDebug($"[UartClose]done");
            }
        }

        public void Shutdown()
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown)
                    return;

                isShuttingDown = true;
                connectionGeneration = unchecked(connectionGeneration + 1);
                if (connectionGeneration == 0)
                    connectionGeneration = 1;
                var oldSerial = serial;
                var oldBaseStream = lastPortBaseStream;
                pinMonitor?.Dispose();
                pinMonitor = null;
                serial = null;
                lastPortBaseStream = null;
                lock (receiveLock)
                    pendingReceivePort = null;

                DisposeSerialResources(oldSerial, oldBaseStream, waitForDispose: true);
                WaitUartReceive.Set();
                Tools.Logger.AddUartLogDebug("[UartShutdown]all serial resources released");
            }
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        /// <param name="data">数据内容</param>
        public void SendData(byte[] data, byte[] dataRaw = null, bool raiseEvents = true)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            SendDataForLease(
                CaptureConnectionLease(),
                data,
                CancellationToken.None,
                dataRaw,
                raiseEvents,
                null);
        }

        public void SendDataCancelable(byte[] data, CancellationToken cancellationToken, byte[] dataRaw = null, bool raiseEvents = true)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            SendDataForLease(
                CaptureConnectionLease(),
                data,
                cancellationToken,
                dataRaw,
                raiseEvents,
                null);
        }

        private void SendDataForLease(
            ConnectionLease lease,
            byte[] data,
            CancellationToken cancellationToken,
            byte[] dataRaw,
            bool raiseEvents,
            Action<int> committedBytes)
        {
            lock (sendLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteData(lease, data, cancellationToken, committedBytes);
                Tools.Global.setting.SentCount += data.Length;
            }

            if (!raiseEvents)
                return;

            // External subscribers are invoked after the send serialization lock is released.
            var profile = GetRuntimeProfile();
            if (dataRaw != null && profile.showSend && ByteArrayEquals(dataRaw, data))
                dataRaw = null;
            if (dataRaw != null && profile.showSendRaw)
                UartDataRawSent?.Invoke(dataRaw, EventArgs.Empty);
            if (profile.showSend)
                UartDataSent?.Invoke(data, EventArgs.Empty);
        }

        private void WriteData(
            ConnectionLease lease,
            byte[] data,
            CancellationToken cancellationToken,
            Action<int> committedBytes)
        {
            var profile = GetRuntimeProfile();
            var packetSize = Math.Max(0, profile.sendThrottlePacketSize);
            var delayMs = Math.Max(0, profile.sendThrottleDelayMs);
            if (packetSize == 0 || delayMs == 0 || data.Length <= packetSize)
            {
                WritePort(lease, data, 0, data.Length, cancellationToken, committedBytes);
                return;
            }

            for (int offset = 0; offset < data.Length; offset += packetSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(packetSize, data.Length - offset);
                WritePort(lease, data, offset, count, cancellationToken, committedBytes);
                if (delayMs > 0 && offset + count < data.Length &&
                    cancellationToken.WaitHandle.WaitOne(delayMs))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private void WritePort(
            ConnectionLease lease,
            byte[] data,
            int offset,
            int count,
            CancellationToken cancellationToken,
            Action<int> committedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = lease?.Port;
            var portName = lease?.PortName ?? string.Empty;
            var invokingCommittedCallback = false;

            try
            {
                if (port == null)
                    throw new IOException("Serial port is not open.");

                string diagnostic;
                try
                {
                    diagnostic =
                        $"[UartWrite]port={portName},baud={port.BaudRate},parity={port.Parity}," +
                        $"dataBits={port.DataBits},stopBits={port.StopBits},handshake={port.Handshake}," +
                        $"dtr={SafeGetDtr(port)},rts={SafeGetRts(port)},bytes={count},generation={lease.Generation}";
                }
                catch
                {
                    diagnostic = $"[UartWrite]port={portName},bytes={count},generation={lease.Generation}";
                }
                Tools.Logger.AddUartLogDebug(diagnostic);

                // lifecycleLock protects only the actual Write and its identity check.
                // Close/reopen may proceed while the driver buffer is draining.
                lock (lifecycleLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsConnectionCurrentUnsafe(lease) || !port.IsOpen)
                        throw new IOException("The captured serial connection is no longer open.");
                    port.Write(data, offset, count);
                }

                invokingCommittedCallback = true;
                committedBytes?.Invoke(count);
                invokingCommittedCallback = false;

                WaitForWriteDrain(lease, count, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception) when (invokingCommittedCallback)
            {
                throw;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Serial write canceled.", ex, cancellationToken);
            }
            catch (Exception ex) when (IsClosedSerialException(UnwrapSerialException(ex)))
            {
                var cause = UnwrapSerialException(ex);
                var notifyClosed = false;
                lock (lifecycleLock)
                {
                    // A delayed failure from a retired SerialPort must never replace or
                    // close the newer connection that now occupies Global.uart.serial.
                    if (IsConnectionCurrentUnsafe(lease))
                    {
                        RefreshSerialDeviceCore(waitForDispose: false);
                        notifyClosed = true;
                    }
                }

                if (notifyClosed)
                    Tools.Global.NotifyUartPortClosed(portName);
                throw new IOException("Serial port was closed while writing.", cause);
            }
        }

        private void WaitForWriteDrain(
            ConnectionLease lease,
            int byteCount,
            CancellationToken cancellationToken)
        {
            var port = lease.Port;
            var baudRate = Math.Max(1, port.BaudRate);
            var estimatedMilliseconds = (long)Math.Ceiling(byteCount * 11000d / baudRate);
            var timeoutMilliseconds = Math.Max(SerialWriteTimeoutMilliseconds, estimatedMilliseconds + 2000L);
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsConnectionCurrent(lease))
                    throw new IOException("The captured serial connection changed while draining.");
                if (port.BytesToWrite <= 0)
                    return;
                if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
                    throw new TimeoutException("等待串口发送缓冲区清空超时。");
                Thread.Sleep(2);
            }
        }

        private static Exception UnwrapSerialException(Exception ex)
        {
            return ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
        }

        private static bool SafeGetDtr(SerialPort port)
        {
            try { return port?.DtrEnable == true; }
            catch { return false; }
        }

        private static bool SafeGetRts(SerialPort port)
        {
            try { return port?.RtsEnable == true; }
            catch { return false; }
        }

        private static bool IsClosedSerialException(Exception ex)
        {
            return ex is ObjectDisposedException ||
                ex is IOException ||
                ex is InvalidOperationException;
        }

        private static bool ByteArrayEquals(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }

        //收到串口事件的信号量
        public EventWaitHandle WaitUartReceive = new AutoResetEvent(false);
        private SerialPort pendingReceivePort = null;
        //接收到事件
        private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var eventPort = sender as SerialPort;
            if (directReceiveMode)
            {
                if (eventPort == null || !ReferenceEquals(eventPort, serial))
                {
                    Tools.Logger.AddUartLogDebug("[UartReceive]ignored stale direct DataReceived callback");
                    return;
                }

                if (Interlocked.CompareExchange(ref directReceiveScheduled, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(_ => ReadDataDirect(eventPort));
                return;
            }

            lock (receiveLock)
            {
                // SerialPort.Close/Dispose 之后，驱动仍可能补发旧对象的回调。
                // 旧回调绝不能覆盖当前串口，否则当前串口已经触发的接收信号会被读错对象。
                if (eventPort == null || !ReferenceEquals(eventPort, serial))
                {
                    Tools.Logger.AddUartLogDebug("[UartReceive]ignored stale DataReceived callback");
                    return;
                }
                pendingReceivePort = eventPort;
            }
            WaitUartReceive.Set();
        }

        private void ReadDataDirect(SerialPort eventPort)
        {
            try
            {
                var profile = GetRuntimeProfile();
                var timeout = profile.timeout;
                Thread.Sleep(timeout > 0 ? timeout : 10);
                if (!directReceiveMode || Tools.Global.isMainWindowsClosed)
                    return;

                var result = new List<byte>();
                while (directReceiveMode)
                {
                    try
                    {
                        lock (receiveLock)
                        {
                            if (!directReceiveMode ||
                                eventPort == null ||
                                !ReferenceEquals(eventPort, serial) ||
                                !eventPort.IsOpen)
                            {
                                break;
                            }

                            var length = eventPort.BytesToRead;
                            if (length <= 0)
                                break;

                            var block = new byte[length];
                            var read = eventPort.Read(block, 0, block.Length);
                            if (read <= 0)
                                break;
                            if (read == block.Length)
                                result.AddRange(block);
                            else
                                result.AddRange(block.Take(read));
                        }
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.AddUartLogDebug($"[UartReceive]direct read error:{ex.Message}");
                        break;
                    }

                    if (result.Count > profile.maxLength)
                        break;

                    if (profile.bitDelay && timeout > 0)
                        Thread.Sleep(timeout);
                    else if (timeout < 0)
                        Thread.Sleep(10);
                }

                PublishReceivedData(result);
            }
            finally
            {
                Interlocked.Exchange(ref directReceiveScheduled, 0);

                // Data may have arrived after the final BytesToRead check while this
                // callback was still marked as scheduled. Drain it without waiting for
                // another driver notification.
                try
                {
                    var currentPort = serial;
                    if (directReceiveMode &&
                        currentPort != null &&
                        currentPort.IsOpen &&
                        currentPort.BytesToRead > 0 &&
                        Interlocked.CompareExchange(ref directReceiveScheduled, 1, 0) == 0)
                    {
                        ThreadPool.QueueUserWorkItem(_ => ReadDataDirect(currentPort));
                    }
                }
                catch
                {
                }
            }
        }

        private void PublishReceivedData(ICollection<byte> result)
        {
            if (result == null || result.Count == 0 || Tools.Global.isMainWindowsClosed)
                return;

            Tools.Global.setting.ReceivedCount += result.Count;
            var data = result.ToArray();
            var handlers = UartDataRecived;
            if (handlers != null)
            {
                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(data, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        // One UI/log subscriber must not prevent the split-pane subscriber
                        // from receiving the same bytes.
                        Tools.Logger.AddUartLogDebug(
                            $"[UartReceive]subscriber {handler.Method.Name} error:{ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// 单独开个线程接收数据
        /// </summary>
        private void ReadData()
        {
            WaitUartReceive.Reset();
            while (true)
            {
                WaitUartReceive.WaitOne();
                if (Tools.Global.isMainWindowsClosed)
                    return;
                var profile = GetRuntimeProfile();
                if (profile.timeout > 0)
                    System.Threading.Thread.Sleep(profile.timeout);//等待时间
                else
                    System.Threading.Thread.Sleep(10);//等待时间默认给个10ms吧，防止中文被分割
                if (directReceiveMode)
                    continue;
                if (Tools.Global.isMainWindowsClosed)
                    return;
                List<byte> result = new List<byte>();
                while (true)//循环读
                {
                    try
                    {
                        lock (receiveLock)
                        {
                            if (directReceiveMode)
                                break;

                            var currentPort = serial;
                            var readPort = ReferenceEquals(pendingReceivePort, currentPort)
                                ? pendingReceivePort
                                : currentPort;
                            if (readPort == null || !readPort.IsOpen)//串口被关了，不读了
                                break;

                            int length = readPort.BytesToRead;
                            if (length == 0)//没数据，退出去
                                break;
                            byte[] rev = new byte[length];
                            var read = readPort.Read(rev, 0, length);//读数据
                            if (read <= 0)
                                break;
                            if (read == rev.Length)
                                result.AddRange(rev);//加到list末尾
                            else
                                result.AddRange(rev.Take(read));
                        }
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.AddUartLogDebug($"[UartReceive]read error:{ex.Message}");
                        break;
                    }

                    if (result.Count > profile.maxLength)//长度超了
                        break;
                    if (profile.bitDelay && profile.timeout > 0)//如果是设置了等待间隔时间
                    {
                        System.Threading.Thread.Sleep(profile.timeout);//等待时间
                    }
                    else if (profile.timeout < 0)//如果是设置了等待间隔时间
                    {
                        System.Threading.Thread.Sleep(10);//等待时间默认给个10ms吧，防止中文被分割
                    }
                }
                if (Tools.Global.isMainWindowsClosed)
                    return;
                PublishReceivedData(result);
            }
        }
    }
}
