using llcom_plus.ScriptEnv;
using System;
using System.Collections.Generic;
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
        private const int SerialDisposeTimeoutMilliseconds = 1500;

        //废弃的串口对象，存放处，尝试fix[System.ObjectDisposedException: 已关闭 Safe handle]
        //https://drdump.com/Problem.aspx?ProblemID=524533
        private List<SerialPort> useless = new List<SerialPort>();

        public SerialPort serial = new SerialPort();
        public event EventHandler UartDataRecived;
        public event EventHandler UartDataSent;
        public event EventHandler UartDataRawSent;
        private Stream lastPortBaseStream = null;
        private readonly object sendLock = new object();
        private bool _rts = false;
        private bool _dtr = true;

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
                    Tools.Global.uart.serial.RtsEnable = value;
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
                Tools.Global.uart.serial.DtrEnable = _dtr = value;
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
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]start");
            var oldSerial = serial;
            var oldBaseStream = lastPortBaseStream;
            lastPortBaseStream = null;
            DisposeSerialResources(oldSerial, oldBaseStream, waitForDispose);
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]new");
            lock(useless)//存起来
                useless.Add(oldSerial);
            serial = new SerialPort();
            //声明接收到事件
            serial.DataReceived += Serial_DataReceived;
            ConfigureSerialDevice(serial);
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]done");
        }

        private void DisposeSerialResources(SerialPort port, Stream baseStream, bool waitForDispose)
        {
            Action dispose = () =>
            {
                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose");
                    baseStream?.Dispose();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose error:{e.Message}");
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
            };

            var task = Task.Run(dispose);
            if (waitForDispose && !task.Wait(SerialDisposeTimeoutMilliseconds))
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]dispose timeout {SerialDisposeTimeoutMilliseconds}ms");
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
            return serial.PortName;
        }

        /// <summary>
        /// 设置串口设备COM名
        /// </summary>
        /// <returns></returns>
        public void SetName(string s)
        {
            serial.PortName = s;
        }

        /// <summary>
        /// 查看串口打开状态
        /// </summary>
        /// <returns></returns>
        public bool IsOpen()
        {
            return serial.IsOpen;
        }

        /// <summary>
        /// 开启串口
        /// </summary>
        public void Open()
        {
            string temp = serial.PortName;
            Tools.Logger.AddUartLogDebug($"[UartOpen]refreshSerialDevice");
            refreshSerialDevice();
            serial.PortName = temp;
            Tools.Logger.AddUartLogDebug($"[UartOpen]open");
            serial.Open();
            lastPortBaseStream = serial.BaseStream;
            Tools.Logger.AddUartLogDebug($"[UartOpen]done");
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close(bool waitForDispose = false)
        {
            Tools.Logger.AddUartLogDebug($"[UartClose]refreshSerialDevice");
            refreshSerialDevice(waitForDispose);
            WaitUartReceive.Set();
            Tools.Logger.AddUartLogDebug($"[UartClose]done");
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

            var port = serial;
            if (port == null || !port.IsOpen)
                throw new IOException("Serial port is not open.");

            var portName = port.PortName;
            var stream = port.BaseStream;
            var timeout = port.WriteTimeout > 0 ? port.WriteTimeout : SerialWriteTimeoutMilliseconds;
            IAsyncResult asyncResult = null;

            try
            {
                asyncResult = stream.BeginWrite(data, offset, count, null, null);
                if (asyncResult.AsyncWaitHandle.WaitOne(timeout))
                {
                    stream.EndWrite(asyncResult);
                    return;
                }

                refreshSerialDevice(waitForDispose: false);
                Tools.Global.NotifyUartPortClosed(portName);
                throw new TimeoutException($"Serial write timed out after {timeout} ms.");
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
        public EventWaitHandle WaitUartReceive = new AutoResetEvent(true);
        //接收到事件
        private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
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
                List<byte> result = new List<byte>();
                while (true)//循环读
                {
                    if (serial == null || !serial.IsOpen)//串口被关了，不读了
                        break;
                    try
                    {
                        int length = serial.BytesToRead;
                        if (length == 0)//没数据，退出去
                            break;
                        byte[] rev = new byte[length];
                        serial.Read(rev, 0, length);//读数据
                        if (rev.Length == 0)
                            break;
                        result.AddRange(rev);//加到list末尾
                    }
                    catch { break; }//崩了？

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
