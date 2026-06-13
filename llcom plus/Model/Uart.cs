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
            ApplyControlLines();
            new Thread(ReadData).Start();

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
        private void refreshSerialDevice()
        {
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]start");
            try
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]lastPortBaseStream.Dispose");
                Task.Run(() =>//这行代码会卡住，我扔task里还卡吗？
                {
                    try
                    {
                        lastPortBaseStream?.Dispose();
                    }
                    catch { }
                });
            }
            catch (Exception e)
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]lastPortBaseStream.Dispose error:{e.Message}");
                Console.WriteLine($"portBaseStream?.Dispose error:{e.Message}");
            }
            try
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose");
                Task.Run(() =>//这行代码会卡住，我扔task里还卡吗？
                {
                    try
                    {
                        serial.BaseStream.Dispose();
                    }
                    catch { }
                });
            }
            catch (Exception e)
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose error:{e.Message}");
                Console.WriteLine($"serial.BaseStream.Dispose error:{e.Message}");
            }
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]Dispose");
            Task.Run(() =>//我服了
            {
                try
                {
                    serial.Dispose();
                }
                catch { }
            });
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]new");
            lock(useless)//存起来
                useless.Add(serial);
            serial = new SerialPort();
            //声明接收到事件
            serial.DataReceived += Serial_DataReceived;
            serial.BaudRate = Tools.Global.setting.baudRate;
            serial.Parity = (Parity)Tools.Global.setting.parity;
            serial.DataBits = Tools.Global.setting.dataBits;
            serial.StopBits = (StopBits)Tools.Global.setting.stopBit;
            ApplyControlLines();
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]done");
        }

        public void ApplyFlowControl()
        {
            ApplyControlLines();
        }

        private void ApplyControlLines()
        {
            var handshake = GetHandshake();
            if (handshake == Handshake.RequestToSend)
            {
                try
                {
                    serial.RtsEnable = false;
                }
                catch
                {
                }
            }
            serial.Handshake = handshake;
            if (handshake != Handshake.RequestToSend)
                serial.RtsEnable = Rts;
            serial.DtrEnable = Dtr;
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
        public void Close()
        {
            Tools.Logger.AddUartLogDebug($"[UartClose]refreshSerialDevice");
            refreshSerialDevice();
            Tools.Logger.AddUartLogDebug($"[UartClose]Close");
            serial.Close();
            Tools.Logger.AddUartLogDebug($"[UartClose]done");
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        /// <param name="data">数据内容</param>
        public void SendData(byte[] data, byte[] dataRaw = null)
        {
            if (data.Length == 0)
                return;

            lock (sendLock)
            {
                WriteData(data);
                Tools.Global.setting.SentCount += data.Length;

                //判断data与dataRaw是否相同，如果相同且实际发送也显示，就只显示一个
                if (dataRaw != null && Tools.Global.setting.showSend && ByteArrayEquals(dataRaw, data))
                    dataRaw = null;
                if (dataRaw != null && Tools.Global.setting.showSendRaw) UartDataRawSent?.Invoke(dataRaw, EventArgs.Empty);
                if (Tools.Global.setting.showSend) UartDataSent?.Invoke(data, EventArgs.Empty);//回调
            }
        }

        private void WriteData(byte[] data)
        {
            var packetSize = Math.Max(0, Tools.Global.setting.sendThrottlePacketSize);
            var delayMs = Math.Max(0, Tools.Global.setting.sendThrottleDelayMs);
            if (packetSize == 0 || delayMs == 0 || data.Length <= packetSize)
            {
                serial.Write(data, 0, data.Length);
                return;
            }

            for (int offset = 0; offset < data.Length; offset += packetSize)
            {
                var count = Math.Min(packetSize, data.Length - offset);
                serial.Write(data, offset, count);
                if (delayMs > 0 && offset + count < data.Length)
                    Thread.Sleep(delayMs);
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
