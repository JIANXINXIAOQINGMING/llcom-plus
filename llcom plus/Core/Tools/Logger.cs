using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace llcom_plus.Tools
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
        public static void ShowData(byte[] data, bool send, string sessionStringText = null, ReceiveScriptContext receiveScriptContext = null)
        {
            //不刷新日志
            if (Tools.Global.setting.DisableLog)
                return;
            var showData = new DataShowPara
            {
                data = data,
                send = send,
                receiveScriptContext = receiveScriptContext
            };
            WriteSessionLog(showData.time, send ? "send" : "recv", data, sessionStringText);
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

        internal static SolidColorBrush GetThemeBrush(string key, SolidColorBrush fallback)
        {
            try
            {
                return Application.Current?.TryFindResource(key) as SolidColorBrush ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }


        private static Serilog.Core.Logger uartLogFile = null;
        private static Serilog.Core.Logger scriptLogFile = null;

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
            AddUartLogInfo("[START]Logs by llcom plus.");
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
        private static string sessionLogPortName = string.Empty;
        private const int MaxPortNotificationLogItems = 5000;
        private static readonly object portNotificationLogLock = new object();
        private static readonly List<PortNotificationLogItem> portNotificationLogItems =
            new List<PortNotificationLogItem>();
        public static string SessionStringLogFilePath { get; private set; } = "";
        public static string SessionHexLogFilePath { get; private set; } = "";

        private sealed class PortNotificationLogItem
        {
            public DateTime Timestamp { get; set; }
            public string PortName { get; set; }
            public string Title { get; set; }
            public string Message { get; set; }
        }

        internal static void RecordPortNotification(
            DateTime timestamp,
            string portName,
            string title,
            string message)
        {
            var normalizedPortName = NormalizePortName(portName);
            if (string.IsNullOrWhiteSpace(normalizedPortName))
                return;

            var item = new PortNotificationLogItem
            {
                Timestamp = timestamp == default(DateTime) ? DateTime.Now : timestamp,
                PortName = normalizedPortName,
                Title = title ?? string.Empty,
                Message = message ?? string.Empty
            };
            lock (portNotificationLogLock)
            {
                portNotificationLogItems.Add(item);
                while (portNotificationLogItems.Count > MaxPortNotificationLogItems)
                    portNotificationLogItems.RemoveAt(0);
            }

            lock (sessionLogLock)
            {
                if (!string.Equals(sessionLogPortName, normalizedPortName, StringComparison.OrdinalIgnoreCase))
                    return;

                var line = FormatPortNotificationLogLine(item);
                sessionStringLogWriter?.WriteLine(line);
                sessionHexLogWriter?.WriteLine(line);
            }
        }

        internal static string GetPortNotificationLogText(string portName)
        {
            var normalizedPortName = NormalizePortName(portName);
            if (string.IsNullOrWhiteSpace(normalizedPortName))
                return string.Empty;

            lock (portNotificationLogLock)
            {
                var text = new StringBuilder();
                foreach (var item in portNotificationLogItems.Where(item =>
                    string.Equals(item.PortName, normalizedPortName, StringComparison.OrdinalIgnoreCase)))
                {
                    text.AppendLine(FormatPortNotificationLogLine(item));
                }
                return text.ToString();
            }
        }

        internal static void ClearPortNotificationLogs()
        {
            lock (portNotificationLogLock)
                portNotificationLogItems.Clear();
        }

        private static string NormalizePortName(string portName)
        {
            return string.IsNullOrWhiteSpace(portName)
                ? string.Empty
                : portName.Trim().ToUpperInvariant();
        }

        private static string FormatPortNotificationLogLine(PortNotificationLogItem item)
        {
            var line = $"[{item.Timestamp:yyyy/MM/dd HH:mm:ss.fff}] [notice] {item.Title}";
            return string.IsNullOrWhiteSpace(item.Message) ? line : line + " | " + item.Message;
        }

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
                    sessionLogPortName = NormalizePortName(portName);
                    var startLine = $"[START] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  PORT={portName}";
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
                    var endLine = $"[END] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}";
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
                    sessionLogPortName = string.Empty;
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

        internal static string Byte2SessionString(byte[] data)
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
                    return "\\n" + Environment.NewLine;
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

        internal static string EscapeSessionString(string text)
        {
            if (text == null)
                return null;
            return text
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n" + Environment.NewLine)
                .Replace("\t", "\\t");
        }

        private static readonly Regex CommonSerialErrorRegex = new Regex(
            @"^(?:ERROR|\+CME\s+ERROR\b.*|\+CMS\s+ERROR\b.*|NO\s+CARRIER|NO\s+DIALTONE|BUSY|NO\s+ANSWER|FAIL(?:ED)?|ABORT(?:ED)?|COMMAND\s+NOT\s+SUPPORT(?:ED)?)(?:\s*:.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static SolidColorBrush GetLogDataBrush(bool sent)
        {
            var configured = sent
                ? Tools.Global.setting?.logSentColor
                : Tools.Global.setting?.logReceivedColor;
            var fallback = sent ? Brushes.IndianRed : Brushes.SeaGreen;
            var resourceKey = sent ? "AppDataSentBrush" : "AppDataReceivedBrush";
            return ParseConfiguredBrush(configured) ?? GetThemeBrush(resourceKey, fallback);
        }

        internal static SolidColorBrush GetLogErrorBrush()
        {
            return ParseConfiguredBrush(Tools.Global.setting?.logErrorColor) ??
                GetThemeBrush("AppDangerBrush", Brushes.OrangeRed);
        }

        internal static bool IsCommonSerialErrorLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var normalized = Regex.Replace(
                line.Trim(),
                @"(?:\\[rntvfab0])+$",
                "",
                RegexOptions.IgnoreCase).Trim();
            return CommonSerialErrorRegex.IsMatch(normalized);
        }

        private static SolidColorBrush ParseConfiguredBrush(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(value.Trim());
                return new SolidColorBrush(color);
            }
            catch
            {
                return null;
            }
        }

        internal static void WriteSessionStringLine(StreamWriter writer, string prefix, string readable)
        {
            if (writer == null)
                return;

            var lines = (readable ?? string.Empty).Split(
                new[] { Environment.NewLine },
                StringSplitOptions.None);
            var lineCount = lines.Length;
            if (lineCount > 1 && lines[lineCount - 1].Length == 0)
                lineCount--;
            if (lineCount == 0)
            {
                writer.WriteLine($"{prefix} │");
                return;
            }

            var continuationPrefix = new string(' ', prefix.Length);
            for (var i = 0; i < lineCount; i++)
                writer.WriteLine($"{(i == 0 ? prefix : continuationPrefix)} │ {lines[i]}");
        }

        internal static string BuildSessionLogPrefix(DateTime time, string direction)
        {
            string label;
            switch (direction)
            {
                case "send":
                    label = "TX";
                    break;
                case "recv":
                    label = "RX";
                    break;
                default:
                    label = (direction ?? "--").ToUpperInvariant();
                    break;
            }
            return $"{time:yyyy-MM-dd HH:mm:ss.fff}  {label}";
        }

        private static void WriteSessionLog(DateTime time, string direction, byte[] data, string stringText = null)
        {
            if (data == null || data.Length == 0)
                return;

            lock (sessionLogLock)
            {
                if (sessionStringLogWriter == null && sessionHexLogWriter == null)
                    return;
                try
                {
                    var prefix = BuildSessionLogPrefix(time, direction);
                    var readable = stringText == null ? Byte2SessionString(data) : EscapeSessionString(stringText);
                    var hex = Tools.Global.Byte2Hex(data, " ", data.Length);
                    WriteSessionStringLine(sessionStringLogWriter, prefix, readable);
                    sessionHexLogWriter?.WriteLine($"{prefix} │ {hex}");
                }
                catch (Exception ex)
                {
                    AddUartLogDebug($"[SessionLog]write failed:{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 初始化脚本日志文件
        /// </summary>
        public static void InitScriptLog()
        {
            scriptLogFile = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File(Tools.Global.ProfilePath + "user_script_run/logs/log.txt",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    encoding: Encoding.UTF8,
                    rollOnFileSizeLimit: true)
                .CreateLogger();
        }

        public static void CloseScriptLog()
        {
            if (scriptLogFile == null)
                return;
            scriptLogFile.Dispose();
            scriptLogFile = null;
        }

        /// <summary>
        /// 写入一条脚本日志
        /// </summary>
        /// <param name="l"></param>
        public static void AddScriptLog(string l)
        {
            if (scriptLogFile == null)
                InitScriptLog();
            scriptLogFile.Information(l);
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
        public ReceiveScriptContext receiveScriptContext;
    }

    class ReceiveScriptContext
    {
        public string ScriptName { get; set; } = "";
        public object Parameter { get; set; } = "";
        public byte[] SendRaw { get; set; } = new byte[0];
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
