using llcom_plus.ScriptEnv;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
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
        private bool isShuttingDown;
        private bool _rts = false;
        // DTR 的上升沿会让不少 USB 串口设备复位。默认保持关闭，确实需要
        // DTR 的设备再由用户按端口开启，避免“写入成功但设备正在重启”。
        private bool _dtr = false;
        private Tools.SerialPinMonitor pinMonitor;

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

            //适配一下通用通道
            ScriptApis.SendChannelsRegister("uart", (data, _) => 
            {
                if (IsOpen() && data != null)
                {
                    SendData(data);
                    return true;
                }
                else
                    return false;
            });
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

            if (Tools.Global.setting != null)
            {
                port.BaudRate = Tools.Global.setting.baudRate;
                port.Parity = (Parity)Tools.Global.setting.parity;
                port.DataBits = Tools.Global.setting.dataBits;
                port.StopBits = (StopBits)Tools.Global.setting.stopBit;
            }

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
            return Tools.Global.setting != null && Tools.Global.setting.flowControl == 1;
        }

        private Handshake GetHandshake()
        {
            if (Tools.Global.setting == null)
                return Handshake.None;
            switch (Tools.Global.setting.flowControl)
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
            if (data.Length == 0)
                return;

            lock (sendLock)
            {
                WriteData(data);
                Tools.Global.setting.SentCount += data.Length;

                if (!raiseEvents)
                    return;

                //判断data与dataRaw是否相同，如果相同且实际发送也显示，就只显示一个
                if (dataRaw != null && Tools.Global.setting.showSend && ByteArrayEquals(dataRaw, data))
                    dataRaw = null;
                if (dataRaw != null && Tools.Global.setting.showSendRaw) UartDataRawSent?.Invoke(dataRaw, EventArgs.Empty);
                if (Tools.Global.setting.showSend) UartDataSent?.Invoke(data, EventArgs.Empty);//回调
            }
        }

        public void SendDataCancelable(byte[] data, CancellationToken cancellationToken, byte[] dataRaw = null, bool raiseEvents = true)
        {
            if (data.Length == 0)
                return;

            lock (sendLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteData(data, cancellationToken);
                Tools.Global.setting.SentCount += data.Length;

                if (!raiseEvents)
                    return;

                if (dataRaw != null && Tools.Global.setting.showSend && ByteArrayEquals(dataRaw, data))
                    dataRaw = null;
                if (dataRaw != null && Tools.Global.setting.showSendRaw) UartDataRawSent?.Invoke(dataRaw, EventArgs.Empty);
                if (Tools.Global.setting.showSend) UartDataSent?.Invoke(data, EventArgs.Empty);
            }
        }

        private void WriteData(byte[] data)
        {
            WriteData(data, CancellationToken.None);
        }

        private void WriteData(byte[] data, CancellationToken cancellationToken)
        {
            var packetSize = Math.Max(0, Tools.Global.setting.sendThrottlePacketSize);
            var delayMs = Math.Max(0, Tools.Global.setting.sendThrottleDelayMs);
            if (packetSize == 0 || delayMs == 0 || data.Length <= packetSize)
            {
                WritePort(data, 0, data.Length, cancellationToken);
                return;
            }

            for (int offset = 0; offset < data.Length; offset += packetSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(packetSize, data.Length - offset);
                WritePort(data, offset, count, cancellationToken);
                if (delayMs > 0 && offset + count < data.Length)
                {
                    if (cancellationToken.WaitHandle.WaitOne(delayMs))
                        cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private void WritePort(byte[] data, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var portName = "";

            try
            {
                var port = serial;
                portName = port?.PortName ?? "";
                if (port == null || !port.IsOpen)
                    throw new IOException("Serial port is not open.");

                Tools.Logger.AddUartLogDebug(
                    $"[UartWrite]port={portName},baud={port.BaudRate},parity={port.Parity},dataBits={port.DataBits},stopBits={port.StopBits},handshake={port.Handshake},dtr={SafeGetDtr(port)},rts={SafeGetRts(port)},bytes={count}");
                port.Write(data, offset, count);
                WaitForWriteDrain(port, count, cancellationToken);
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
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Serial write canceled.", ex, cancellationToken);
            }
            catch (Exception ex) when (IsClosedSerialException(UnwrapSerialException(ex)))
            {
                var cause = UnwrapSerialException(ex);
                refreshSerialDevice(waitForDispose: false);
                Tools.Global.NotifyUartPortClosed(portName);
                throw new IOException("Serial port was closed while writing.", cause);
            }
        }

        private static void WaitForWriteDrain(SerialPort port, int byteCount, CancellationToken cancellationToken)
        {
            var baudRate = Math.Max(1, port.BaudRate);
            var estimatedMilliseconds = (long)Math.Ceiling(byteCount * 11000d / baudRate);
            var timeoutMilliseconds = Math.Max(SerialWriteTimeoutMilliseconds, estimatedMilliseconds + 2000L);
            var stopwatch = Stopwatch.StartNew();
            while (port.BytesToWrite > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                if (Tools.Global.setting.timeout > 0)
                    System.Threading.Thread.Sleep(Tools.Global.setting.timeout);//等待时间
                else
                    System.Threading.Thread.Sleep(10);//等待时间默认给个10ms吧，防止中文被分割
                if (Tools.Global.isMainWindowsClosed)
                    return;
                List<byte> result = new List<byte>();
                while (true)//循环读
                {
                    SerialPort readPort;
                    lock (receiveLock)
                    {
                        var currentPort = serial;
                        readPort = ReferenceEquals(pendingReceivePort, currentPort)
                            ? pendingReceivePort
                            : currentPort;
                    }

                    try
                    {
                        if (readPort == null || !readPort.IsOpen)//串口被关了，不读了
                            break;

                        int length = readPort.BytesToRead;
                        if (length == 0)//没数据，退出去
                            break;
                        byte[] rev = new byte[length];
                        readPort.Read(rev, 0, length);//读数据
                        if (rev.Length == 0)
                            break;
                        result.AddRange(rev);//加到list末尾
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.AddUartLogDebug($"[UartReceive]read error:{ex.Message}");
                        break;
                    }

                    if (result.Count > Tools.Global.setting.maxLength)//长度超了
                        break;
                    if (Tools.Global.setting.bitDelay && Tools.Global.setting.timeout > 0)//如果是设置了等待间隔时间
                    {
                        System.Threading.Thread.Sleep(Tools.Global.setting.timeout);//等待时间
                    }
                    else if (Tools.Global.setting.timeout < 0)//如果是设置了等待间隔时间
                    {
                        System.Threading.Thread.Sleep(10);//等待时间默认给个10ms吧，防止中文被分割
                    }
                }
                if (Tools.Global.isMainWindowsClosed)
                    return;
                Tools.Global.setting.ReceivedCount += result.Count;
                if (result.Count > 0)
                    try
                    {
                        var r = result.ToArray();
                        UartDataRecived(r, EventArgs.Empty);//回调事件
                        ScriptApis.SendChannelsReceived("uart", r);
                    }
                    catch { }
            }
        }
    }
}
