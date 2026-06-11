using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace llcom.Tools
{
    class Logger
    {
        //显示日志数据的回调函数
        public static event EventHandler<DataShow> DataShowTask;
        //清空显示的回调函数
        public static event EventHandler DataClearEvent;
        //清空日志显示
        public static void ClearData()
        {
            DataClearEvent?.Invoke(null,null);
        }
        //显示日志数据
        public static void ShowData(byte[] data, bool send, string sessionStringText = null)
        {
            //不刷新日志
            if (Tools.Global.setting.DisableLog)
                return;
            var showData = new DataShowPara
            {
                data = data,
                send = send
            };
            WriteSessionLog(showData.time, send ? "send" : "recv", null, data, sessionStringText);
            DataShowTask?.Invoke(null, showData);
        }

        public static void ShowRawData(string title, byte[] data, bool send)
        {
            //不刷新日志
            if (Tools.Global.setting.DisableLog)
                return;
            var showData = new DataShowRaw
            {
                title = title,
                data = data,
                color = send ? Brushes.DarkRed : Brushes.DarkGreen
            };
            DataShowTask?.Invoke(null, showData);
        }
        //显示日志数据
        public static void ShowDataRaw(DataShowRaw s)
        {
            //不刷新日志
            if (Tools.Global.setting.DisableLog)
                return;
            DataShowTask?.Invoke(null, s);
        }


        private static Serilog.Core.Logger uartLogFile = null;
        private static Serilog.Core.Logger luaLogFile = null;

        /// <summary>
        /// 初始化串口日志文件
        /// </summary>
        public static void InitUartLog()
        {
            uartLogFile = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(Tools.Global.ProfilePath + "logs/log.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    encoding: Encoding.UTF8,
                    rollOnFileSizeLimit: true)
                .CreateLogger();
            AddUartLogInfo("[START]Logs by LLCOM. https://github.com/chenxuuu/llcom");
        }

        public static void CloseUartLog()
        {
            if (uartLogFile == null)
                return;
            uartLogFile.Dispose();
            uartLogFile = null;
        }

        /// <summary>
        /// 写入一条串口日志
        /// </summary>
        /// <param name="l"></param>
        public static void AddUartLogInfo(string l)
        {
            if (uartLogFile == null)
                InitUartLog();
            uartLogFile.Information(l);
        }
        /// <summary>
        /// 写入一条串口日志
        /// </summary>
        /// <param name="l"></param>
        public static void AddUartLogDebug(string l)
        {
            if (uartLogFile == null)
                InitUartLog();
            uartLogFile.Debug(l);
        }

        private static readonly object sessionLogLock = new object();
        private static StreamWriter sessionStringLogWriter = null;
        private static StreamWriter sessionHexLogWriter = null;
        public static string SessionStringLogFilePath { get; private set; } = "";
        public static string SessionHexLogFilePath { get; private set; } = "";

        public static void StartSessionLog(string portName)
        {
            StopSessionLog();
            if (Tools.Global.setting == null || !Tools.Global.setting.sessionLogEnabled)
                return;

            try
            {
                var folder = Tools.Global.setting.sessionLogFolder;
                if (string.IsNullOrWhiteSpace(folder))
                {
                    folder = Path.Combine(Tools.Global.ProfilePath, "session_logs");
                    Tools.Global.setting.sessionLogFolder = folder;
                }

                var safePortName = MakeSafeFileName(string.IsNullOrWhiteSpace(portName) ? "COM" : portName);
                var portFolder = Path.Combine(folder, safePortName);
                var stringFolder = Path.Combine(portFolder, "STRING");
                var hexFolder = Path.Combine(portFolder, "HEX");
                Directory.CreateDirectory(stringFolder);
                Directory.CreateDirectory(hexFolder);

                var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}.log";
                SessionStringLogFilePath = Path.Combine(stringFolder, fileName);
                SessionHexLogFilePath = Path.Combine(hexFolder, fileName);
                lock (sessionLogLock)
                {
                    sessionStringLogWriter = CreateSessionLogWriter(SessionStringLogFilePath);
                    sessionHexLogWriter = CreateSessionLogWriter(SessionHexLogFilePath);
                    var startLine = $"[START] {DateTime.Now:yyyy/MM/dd HH:mm:ss.fff} {portName}";
                    sessionStringLogWriter.WriteLine(startLine);
                    sessionHexLogWriter.WriteLine(startLine);
                }
            }
            catch (Exception ex)
            {
                StopSessionLog();
                AddUartLogDebug($"[SessionLog]start failed:{ex.Message}");
                SessionStringLogFilePath = "";
                SessionHexLogFilePath = "";
            }
        }

        public static void StopSessionLog()
        {
            lock (sessionLogLock)
            {
                if (sessionStringLogWriter == null && sessionHexLogWriter == null)
                    return;
                try
                {
                    var endLine = $"[END] {DateTime.Now:yyyy/MM/dd HH:mm:ss.fff}";
                    sessionStringLogWriter?.WriteLine(endLine);
                    sessionHexLogWriter?.WriteLine(endLine);
                }
                catch { }
                finally
                {
                    sessionStringLogWriter?.Dispose();
                    sessionHexLogWriter?.Dispose();
                    sessionStringLogWriter = null;
                    sessionHexLogWriter = null;
                    SessionStringLogFilePath = "";
                    SessionHexLogFilePath = "";
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
            var text = new StringBuilder();
            var plainBytes = new List<byte>();
            foreach (var b in data)
            {
                if (b <= 0x1f || b == 0x7f)
                {
                    if (plainBytes.Count > 0)
                    {
                        text.Append(Tools.Global.GetEncoding().GetString(plainBytes.ToArray()));
                        plainBytes.Clear();
                    }
                    text.Append(Byte2SessionVisibleSymbol(b));
                }
                else
                {
                    plainBytes.Add(b);
                }
            }
            if (plainBytes.Count > 0)
                text.Append(Tools.Global.GetEncoding().GetString(plainBytes.ToArray()));
            return text.ToString();
        }

        private static string Byte2SessionVisibleSymbol(byte data)
        {
            switch (data)
            {
                case 0x00:
                    return "\\0";
                case 0x07:
                    return "\\a";
                case 0x08:
                    return "\\b";
                case 0x09:
                    return "\\t";
                case 0x0a:
                    return "\\n";
                case 0x0b:
                    return "\\v";
                case 0x0c:
                    return "\\f";
                case 0x0d:
                    return "\\r";
                case 0x1b:
                    return "\\e";
                default:
                    return $"\\x{data:X2}";
            }
        }

        private static string EscapeSessionString(string text)
        {
            if (text == null)
                return null;
            return text
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static void WriteSessionLog(DateTime time, string direction, string title, byte[] data, string stringText = null)
        {
            if (data == null || data.Length == 0)
                return;

            lock (sessionLogLock)
            {
                if (sessionStringLogWriter == null && sessionHexLogWriter == null)
                    return;
                try
                {
                    var prefix = $"[{time:yyyy/MM/dd HH:mm:ss.fff}] [{direction}]";
                    if (!string.IsNullOrWhiteSpace(title))
                        prefix += $" [{title}]";

                    var readable = stringText == null ? Byte2SessionString(data) : EscapeSessionString(stringText);
                    var hex = Tools.Global.Byte2Hex(data, " ", data.Length);
                    sessionStringLogWriter?.WriteLine($"{prefix} {readable}");
                    sessionHexLogWriter?.WriteLine($"{prefix} {hex}");
                }
                catch (Exception ex)
                {
                    AddUartLogDebug($"[SessionLog]write failed:{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 初始化lua日志文件
        /// </summary>
        public static void InitLuaLog()
        {
            luaLogFile = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(Tools.Global.ProfilePath + "user_script_run/logs/log.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    encoding: Encoding.UTF8,
                    rollOnFileSizeLimit: true)
                .CreateLogger();
        }

        public static void CloseLuaLog()
        {
            if (luaLogFile == null)
                return;
            luaLogFile.Dispose();
            luaLogFile = null;
        }

        /// <summary>
        /// 写入一条lua日志
        /// </summary>
        /// <param name="l"></param>
        public static void AddLuaLog(string l)
        {
            if (luaLogFile == null)
                InitLuaLog();
            luaLogFile.Information(l);
        }
    }

    //整个父类统一下
    class DataShow
    {
        public DateTime time { get; set; } = DateTime.Now;
        public byte[] data;
    }

    /// <summary>
    /// 显示到日志显示页面的类
    /// </summary>
    class DataShowPara : DataShow
    {
        public bool send;
    }

    /// <summary>
    /// 更通用的日志数据
    /// </summary>
    class DataShowRaw : DataShow
    {
        public string title;
        public SolidColorBrush color;
    }
    class DataShowSendRaw : DataShow { }
}
