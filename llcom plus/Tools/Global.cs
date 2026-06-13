using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using llcom_plus.Model;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Shapes;
using Microsoft.Win32;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace llcom_plus.Tools
{
    class Global
    {
        public static event EventHandler ProgramClosedEvent;
        //api接口文档
        public static string apiDocumentUrl = "JavaScriptApi.md";
        //主窗口是否被关闭？
        private static bool _isMainWindowsClosed = false;
        public static bool isMainWindowsClosed
        {
            get
            {
                return _isMainWindowsClosed;
            }
            set
            {
                _isMainWindowsClosed = value;
                if (value)
                {
                    uart.WaitUartReceive.Set();
                    Logger.StopSessionLog();
                    Logger.CloseUartLog();
                    Logger.CloseScriptLog();
                    if (File.Exists(ProfilePath + "lock"))
                        File.Delete(ProfilePath + "lock");
                    ProgramClosedEvent?.Invoke(null,EventArgs.Empty);
                }
            }
        }
        //给全局使用的设置参数项
        public static Model.Settings setting;
        public static Model.Uart uart = new Model.Uart();

        //软件文件名
        private static string _fileName = "";
        public static string FileName
        {
            get
            {
                if (String.IsNullOrWhiteSpace(_fileName))
                {
                    using (var processModule = Process.GetCurrentProcess().MainModule)
                    {
                        _fileName = System.IO.Path.GetFileName(processModule?.FileName);
                    }
                }
                return _fileName;
            }
        }

        //软件根目录
        private static string _appPath = null;
        /// <summary>
        /// 软件根目录（末尾带\）
        /// </summary>
        public static string AppPath
        {
            get
            {
                if (_appPath == null)
                {
                    using (var processModule = Process.GetCurrentProcess().MainModule)
                    {
                        _appPath = System.IO.Path.GetDirectoryName(processModule?.FileName);
                    }
                    if (!_appPath.EndsWith("\\"))
                        _appPath = _appPath + "\\";
                }
                return _appPath;
            }
        }

        private const string ProductName = "llcom plus";
        private const string ExpectedExeFileName = ProductName + ".exe";

        //配置文件路径（普通exe时，会被替换为AppPath）
        public static string ProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\llcom plus\";

        /// <summary>
        /// 获取实际的ProfilePath路径（目前没啥用了）
        /// </summary>
        /// <returns></returns>
        public static string GetTrueProfilePath()
        {
            return ProfilePath;
        }

        /// <summary>
        /// 是否为应用商店版本？
        /// </summary>
        /// <returns></returns>
        public static bool IsMSIX()
        {
            return AppPath.ToUpper().Contains(@"\PROGRAM FILES\WINDOWSAPPS\");
        }

        /// <summary>
        /// 是否上报bug？低版本.net框架的上报行为将被限制
        /// </summary>
        public static bool ReportBug { get; set; } = true;

        /// <summary>
        /// 是否有新版本？
        /// </summary>
        public static bool HasNewVersion { get; set; } = false;


        /// <summary>
        /// 更换软件标题栏文字
        /// </summary>
        public static event EventHandler<string> ChangeTitleEvent;
        public static void ChangeTitle(string s) => ChangeTitleEvent?.Invoke(null, s);

        /// <summary>
        /// 让工具页面请求主串口发送原始字节
        /// </summary>
        public static event Action<byte[]> SendRawDataRequest;
        public static bool RequestSendRawData(byte[] data)
        {
            if (data == null || data.Length == 0 || SendRawDataRequest == null)
                return false;
            SendRawDataRequest(data);
            return true;
        }

        /// <summary>
        /// 刷新脚本列表
        /// </summary>
        public static event EventHandler RefreshScriptListEvent;
        public static void RefreshScriptList() => RefreshScriptListEvent?.Invoke(null, null);

        /// <summary>
        /// 加载配置文件
        /// </summary>
        public static void LoadSetting()
        {
            StartupProfiler.Mark("Global.LoadSetting enter");
            StartupProfiler.Measure("Global.LoadSetting profile path", () =>
            {
                if (IsMSIX())
                {
                    if (Directory.Exists(ProfilePath))
                    {
                        //已经开过一次了，那就继续用之前的路径
                    }
                    else
                    {
                        //appdata路径不可靠，用文档路径替代
                        ProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\llcom plus\\";
                        if (!Directory.Exists(ProfilePath))
                            Directory.CreateDirectory(ProfilePath);
                    }
                }
                else
                {
                    ProfilePath = AppPath;//普通exe时，直接用软件路径
                }
            });
            //配置文件
            if (File.Exists(ProfilePath + "settings.json"))
            {
                StartupProfiler.Measure("Global.LoadSetting read settings.json", () =>
                {
                    try
                    {
                        //cost 309ms
                        setting = JsonConvert.DeserializeObject<Model.Settings>(File.ReadAllText(ProfilePath + "settings.json"));
                        if (setting == null)
                            throw new Exception("settings.json is empty");
                        setting.SentCount = 0;
                        setting.ReceivedCount = 0;
                        setting.DisableLog = false;
                    }
                    catch
                    {
                        Tools.MessageBox.Show($"配置文件加载失败！\r\n" +
                            $"如果是配置文件损坏，可前往{ProfilePath}settings.json.bakup查找备份文件\r\n" +
                            $"并使用该文件替换{ProfilePath}settings.json文件恢复配置");
                        Environment.Exit(1);
                    }
                });
            }
            else
            {
                StartupProfiler.Measure("Global.LoadSetting create default settings", () =>
                {
                    if (Directory.GetFiles(ProfilePath).Length > 10)
                    {
                        var r = Tools.InputDialog.OpenDialog("检测到当前文件夹有其他文件\r\n" +
                            "建议新建一个文件夹给llcom plus，并将llcom plus.exe放入其中\r\n" +
                            "不然当前文件夹会显得很乱哦~\r\n" +
                            "是否想要继续运行呢？", null, "温馨提示");
                        if (!r.Item1)
                            Environment.Exit(1);
                    }
                    setting = new Model.Settings();
                });
            }
            StartupProfiler.Measure("Global.LoadSetting language", () => LoadLanguageFile(setting.language));
            StartupProfiler.Measure("Global.LoadSetting theme", () =>
            {
                try
                {
                    ApplyTheme(setting.darkMode);
                }
                catch
                {
                    // 主题失败不能影响主程序启动。
                }
            });
            StartupProfiler.Mark("Global.LoadSetting exit");
        }

        /// <summary>
        /// 软件打开后，所有东西的初始化流程
        /// </summary>
        public static void Initial()
        {
            StartupProfiler.Mark("Global.Initial enter");
            StartupProfiler.Measure("Global.Initial .NET version check", () =>
            {
                //检查.net版本
                var currentVersion = GetDotNetFrameworkVersionName();
                try
                {
                    if (!IsDotNetFramework48OrLater())
                        throw new Exception();
                }
                catch
                {
                    Tools.MessageBox.Show($"本软件仅支持.net framework 4.8以上版本，该计算机上的最高版本为{currentVersion}\r\n" +
                        $"你可以选择继续使用，但若运行途中遇到bug，将不会上报给开发者。\r\n" +
                        $"建议升级到最新.net framework版本");
                    ReportBug = false;
                }
            });

            StartupProfiler.Measure("Global.Initial app path checks", () =>
            {
                //文件名不能改！
                if (!string.Equals(FileName, ExpectedExeFileName, StringComparison.OrdinalIgnoreCase))
                {
                    Tools.MessageBox.Show("啊呀呀，软件文件名被改了。。。\r\n" +
                        "为了保证软件功能的正常运行，请将exe名改回llcom plus.exe");
                    Environment.Exit(1);
                }
                //C:\Users\chenx\AppData\Local\Temp\7zO05433053\user_script_run
                if (AppPath.ToUpper().Contains(@"\APPDATA\LOCAL\TEMP\") ||
                    AppPath.ToUpper().Contains(@"\WINDOWS\TEMP\"))
                {
                    Tools.MessageBox.Show("请勿在压缩包内直接打开本软件。");
                    Environment.Exit(1);
                }

                if (IsMSIX())//商店软件的文件路径需要手动新建文件夹
                {
                    if (!Directory.Exists(ProfilePath))
                    {
                        Directory.CreateDirectory(ProfilePath);
                    }
                }
            });

            StartupProfiler.Measure("Global.Initial process lock", () =>
            {
                //检测多开
                string processName = Process.GetCurrentProcess().ProcessName;
                Process[] processes = Process.GetProcessesByName(processName);
                //如果该数组长度大于1，说明多次运行
                if (processes.Length > 1 && File.Exists(ProfilePath + "lock"))
                {
                    Tools.MessageBox.Show("不支持同文件夹多开！\r\n如需多开，请在多个文件夹分别存放llcom plus.exe后，分别运行。");
                    Environment.Exit(1);
                }
                File.Create(ProfilePath + "lock").Close();
            });

            StartupProfiler.Measure("Global.Initial uart config", () =>
            {
                uart.serial.BaudRate = setting.baudRate;
                uart.serial.Parity = (Parity)setting.parity;
                uart.serial.DataBits = setting.dataBits;
                uart.serial.StopBits = (StopBits)setting.stopBit;
                uart.ApplyFlowControl();
                uart.UartDataRecived += Uart_UartDataRecived;
                uart.UartDataSent += Uart_UartDataSent;
                uart.UartDataRawSent += Uart_UartDataRawSent;
            });
            StartupProfiler.Mark("Global.Initial exit");
        }

        public static void PrepareRuntimeFiles()
        {
            StartupProfiler.Mark("Global.PrepareRuntimeFiles enter");
            try
            {
                StartupProfiler.Measure("PrepareRuntimeFiles core scripts", () =>
                {
                    if (IsMSIX() && Directory.Exists(ProfilePath + "core_script"))
                        Directory.Delete(ProfilePath + "core_script", true);

                    if (!Directory.Exists(ProfilePath + "core_script"))
                    {
                        Directory.CreateDirectory(ProfilePath + "core_script");
                    }
                });

                StartupProfiler.Measure("PrepareRuntimeFiles logs folder", () =>
                {
                    if (!Directory.Exists(ProfilePath + "logs"))
                        Directory.CreateDirectory(ProfilePath + "logs");
                });

                StartupProfiler.Measure("PrepareRuntimeFiles user_script_run", () =>
                {
                    if (!Directory.Exists(ProfilePath + "user_script_run"))
                    {
                        Directory.CreateDirectory(ProfilePath + "user_script_run");
                    }
                    CreateFileIfMissing("DefaultFiles/user_script_run/AT控制TCP连接-快发模式.js", ProfilePath + "user_script_run/AT控制TCP连接-快发模式.js");
                    CreateFileIfMissing("DefaultFiles/user_script_run/AT控制TCP连接-慢发模式.js", ProfilePath + "user_script_run/AT控制TCP连接-慢发模式.js");
                    CreateFileIfMissing("DefaultFiles/user_script_run/example.js", ProfilePath + "user_script_run/example.js");
                    CreateFileIfMissing("DefaultFiles/user_script_run/循环发送快捷发送区数据.js", ProfilePath + "user_script_run/循环发送快捷发送区数据.js");
                    //通用消息通道的demo
                    CreateFileIfMissing("DefaultFiles/user_script_run/channel-demo.js", ProfilePath + "user_script_run/channel-demo.js");

                    if (!Directory.Exists(ProfilePath + "user_script_run/requires"))
                        Directory.CreateDirectory(ProfilePath + "user_script_run/requires");
                    if (!Directory.Exists(ProfilePath + "user_script_run/logs"))
                        Directory.CreateDirectory(ProfilePath + "user_script_run/logs");
                });

                StartupProfiler.Measure("PrepareRuntimeFiles user_script_send_convert", () =>
                {
                    if (!Directory.Exists(ProfilePath + "user_script_send_convert"))
                    {
                        Directory.CreateDirectory(ProfilePath + "user_script_send_convert");
                    }
                    CreateFileIfMissing("DefaultFiles/user_script_send_convert/checksum.js", ProfilePath + "user_script_send_convert/checksum.js");
                    CreateFileIfMissing("DefaultFiles/user_script_send_convert/16进制数据.js", ProfilePath + "user_script_send_convert/16进制数据.js");
                    CreateFileIfMissing("DefaultFiles/user_script_send_convert/GPS NMEA.js", ProfilePath + "user_script_send_convert/GPS NMEA.js");
                    CreateFileIfMissing("DefaultFiles/user_script_send_convert/加上换行回车.js", ProfilePath + "user_script_send_convert/加上换行回车.js");
                    CreateFileIfMissing("DefaultFiles/user_script_send_convert/解析换行回车的转义字符.js", ProfilePath + "user_script_send_convert/解析换行回车的转义字符.js");
                    CreateFileIfMissing("DefaultFiles/user_script_send_convert/default.js", ProfilePath + "user_script_send_convert/default.js");
                });

                StartupProfiler.Measure("PrepareRuntimeFiles user_script_recv_convert", () =>
                {
                    if (!Directory.Exists(ProfilePath + "user_script_recv_convert"))
                    {
                        Directory.CreateDirectory(ProfilePath + "user_script_recv_convert");
                    }
                    CreateFileIfMissing("DefaultFiles/user_script_recv_convert/default.js", ProfilePath + "user_script_recv_convert/default.js");
                    CreateFileIfMissing("DefaultFiles/user_script_recv_convert/绘制曲线.js", ProfilePath + "user_script_recv_convert/绘制曲线.js");
                    CreateFileIfMissing("DefaultFiles/user_script_recv_convert/绘制曲线-多条.js", ProfilePath + "user_script_recv_convert/绘制曲线-多条.js");
                    CreateFileIfMissing("DefaultFiles/user_script_recv_convert/绘制曲线-解析结构体.js", ProfilePath + "user_script_recv_convert/绘制曲线-解析结构体.js");
                });

                StartupProfiler.Measure("PrepareRuntimeFiles license and libusb", () =>
                {
                    CreateFile("DefaultFiles/LICENSE", ProfilePath + "LICENSE", false);

                    if (IntPtr.Size == 8)
                        CreateFile("DefaultFiles/libusb-1.0-x64.dll", ProfilePath + "libusb-1.0", false);
                    else
                        CreateFile("DefaultFiles/libusb-1.0-x86.dll", ProfilePath + "libusb-1.0", false);
                });
            }
            catch (Exception e)
            {
                Tools.MessageBox.Show("生成文件结构失败，请确保本软件处于有读写权限的目录下再打开。\r\n错误信息：" + e.Message);
                Environment.Exit(1);
            }

            StartupProfiler.Measure("PrepareRuntimeFiles settings backup", () =>
            {
                //备份一下文件好了（心理安慰）
                if (File.Exists(ProfilePath + "settings.json"))
                {
                    if (File.Exists(ProfilePath + "settings.json.bakup"))
                        File.Delete(ProfilePath + "settings.json.bakup");
                    File.Copy(ProfilePath + "settings.json", ProfilePath + "settings.json.bakup");
                }
            });
            StartupProfiler.Mark("Global.PrepareRuntimeFiles exit");
        }

        /// <summary>
        /// 已发送记录到日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void Uart_UartDataSent(object sender, EventArgs e)
        {
            Logger.AddUartLogInfo($"<-{Byte2Readable((byte[])sender)}");
            Logger.AddUartLogDebug($"[HEX]{Byte2Hex((byte[])sender, " ")}");
        }
        private static void Uart_UartDataRawSent(object sender, EventArgs e)
        {
            Logger.AddUartLogInfo($"Raw<-{Byte2Readable((byte[])sender)}");
            Logger.AddUartLogDebug($"[Raw HEX]{Byte2Hex((byte[])sender, " ")}");
        }

        /// <summary>
        /// 收到的数据记录到日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void Uart_UartDataRecived(object sender, EventArgs e)
        {
            Logger.AddUartLogInfo($"->{Byte2Readable((byte[])sender)}");
            Logger.AddUartLogDebug($"[HEX]{Byte2Hex((byte[])sender, " ")}");
        }

        public static Encoding GetEncoding() => Encoding.GetEncoding(setting.encoding);

        /// <summary>
        /// 字符串转hex值
        /// </summary>
        /// <param name="str">字符串</param>
        /// <param name="space">间隔符号</param>
        /// <returns>结果</returns>
        public static string String2Hex(string str, string space)
        {
            return BitConverter.ToString(GetEncoding().GetBytes(str)).Replace("-", space);
        }


        /// <summary>
        /// hex值转字符串
        /// </summary>
        /// <param name="mHex">hex值</param>
        /// <returns>原始字符串</returns>
        public static string Hex2String(string mHex)
        {
            mHex = Regex.Replace(mHex, "[^0-9A-Fa-f]", "");
            if (mHex.Length % 2 != 0)
                mHex = mHex.Remove(mHex.Length - 1, 1);
            if (mHex.Length <= 0) return "";
            byte[] vBytes = new byte[mHex.Length / 2];
            for (int i = 0; i < mHex.Length; i += 2)
                if (!byte.TryParse(mHex.Substring(i, 2), NumberStyles.HexNumber, null, out vBytes[i / 2]))
                    vBytes[i / 2] = 0;
            return GetEncoding().GetString(vBytes);
        }


        /// <summary>
        /// byte转string
        /// </summary>
        /// <param name="mHex"></param>
        /// <returns></returns>
        public static string Byte2String(byte[] vBytes, int len = -1)
        {
            var br = from e in vBytes
                     where e != 0
                     select e;
            if (len == -1 || len > br.Count())
                len = br.Count();
            return GetEncoding().GetString(br.Take(len).ToArray());
        }

        private static string Byte2VisibleSymbol(byte data)
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

        private static bool IsDotNetFramework48OrLater()
        {
            return GetDotNetFrameworkReleaseKey() >= 528040;
        }

        private static string GetDotNetFrameworkVersionName()
        {
            var releaseKey = GetDotNetFrameworkReleaseKey();
            if (releaseKey <= 0)
                return Environment.Version.ToString();
            return GetDotNetFrameworkVersionName(releaseKey);
        }

        private static int GetDotNetFrameworkReleaseKey()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                    .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    var release = key?.GetValue("Release");
                    return release is int ? (int)release : 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        private static string GetDotNetFrameworkVersionName(int releaseKey)
        {
            if (releaseKey >= 533320)
                return "4.8.1";
            if (releaseKey >= 528040)
                return "4.8";
            if (releaseKey >= 461808)
                return "4.7.2";
            if (releaseKey >= 461308)
                return "4.7.1";
            if (releaseKey >= 460798)
                return "4.7";
            if (releaseKey >= 394802)
                return "4.6.2";
            if (releaseKey >= 394254)
                return "4.6.1";
            if (releaseKey >= 393295)
                return "4.6";
            if (releaseKey >= 379893)
                return "4.5.2";
            if (releaseKey >= 378675)
                return "4.5.1";
            if (releaseKey >= 378389)
                return "4.5";
            return $"unknown release {releaseKey}";
        }

        /// <summary>
        /// byte转string（可读）
        /// </summary>
        /// <param name="vBytes"></param>
        /// <returns></returns>
        public static string Byte2Readable(byte[] vBytes, int len = -1)
        {
            if (vBytes == null)//fix
                return "";
            if (len == -1 || len > vBytes.Length)
                len = vBytes.Length;
            //没开这个功能/非utf8就别搞了
            if (!setting.EnableSymbol || setting.encoding != 65001)
                return Byte2String(vBytes, len);

            var text = new StringBuilder();
            var plainBytes = new List<byte>();
            for (int i = 0; i < len; i++)
            {
                // Show whitespace controls, while preserving their visual effect.
                if (vBytes[i] == 0x0d && i < len - 1 && vBytes[i + 1] == 0x0a)
                {
                    if (plainBytes.Count > 0)
                    {
                        text.Append(GetEncoding().GetString(plainBytes.ToArray()));
                        plainBytes.Clear();
                    }
                    text.Append("\\r\\n");
                    text.Append("\r\n");
                    i++;
                    continue;
                }
                if (vBytes[i] == 0x0d || vBytes[i] == 0x0a || vBytes[i] == 0x09)
                {
                    if (plainBytes.Count > 0)
                    {
                        text.Append(GetEncoding().GetString(plainBytes.ToArray()));
                        plainBytes.Clear();
                    }
                    text.Append(Byte2VisibleSymbol(vBytes[i]));
                    text.Append((char)vBytes[i]);
                    continue;
                }

                if (vBytes[i] <= 0x1f || vBytes[i] == 0x7f)
                {
                    if (plainBytes.Count > 0)
                    {
                        text.Append(GetEncoding().GetString(plainBytes.ToArray()));
                        plainBytes.Clear();
                    }
                    text.Append(Byte2VisibleSymbol(vBytes[i]));
                }
                else
                {
                    plainBytes.Add(vBytes[i]);
                }
            }
            if (plainBytes.Count > 0)
                text.Append(GetEncoding().GetString(plainBytes.ToArray()));
            return text.ToString();
        }

        /// <summary>
        /// hex转byte
        /// </summary>
        /// <param name="mHex">hex值</param>
        /// <returns>原始字符串</returns>
        public static byte[] Hex2Byte(string mHex)
        {
            mHex = Regex.Replace(mHex, "[^0-9A-Fa-f]", "");
            if (mHex.Length % 2 != 0)
                mHex = mHex.Remove(mHex.Length - 1, 1);
            if (mHex.Length <= 0) return new byte[0];
            byte[] vBytes = new byte[mHex.Length / 2];
            for (int i = 0; i < mHex.Length; i += 2)
                if (!byte.TryParse(mHex.Substring(i, 2), NumberStyles.HexNumber, null, out vBytes[i / 2]))
                    vBytes[i / 2] = 0;
            return vBytes;
        }


        public static string Byte2Hex(byte[] d, string s = "", int len = -1)
        {
            if (len == -1)
                len = d.Length;
            return BitConverter.ToString(d,0,len).Replace("-", s);
        }


        /// <summary>
        /// 导入SSCOM配置文件数据
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static List<Model.ToSendData> ImportFromSSCOM(string path)
        {
            var lines = File.ReadAllLines(path, Encoding.GetEncoding("GB2312"));
            var r = new List<Model.ToSendData>();
            Regex title = new Regex(@"N1\d\d=\d*,");
            for (int i = 0; i < lines.Length; i++)
            {
                try
                {
                    var temp = new Model.ToSendData();
                    //Console.WriteLine(lines[i]);
                    if (title.IsMatch(lines[i]))//匹配上了
                    {
                        var strs = lines[i].Split(",".ToCharArray()[0]);
                        temp.commit = strs[1].Replace(((char)2).ToString(), ",");
                        if (string.IsNullOrWhiteSpace(temp.commit))
                            temp.commit = "发送";
                        //Console.WriteLine(temp.commit);

                        int dot = lines[i + 1].IndexOf(",");
                        temp.hex = lines[i + 1].Substring(dot - 1, 1) == "H";
                        //Console.WriteLine(strs[0].Substring(strs[0].Length - 1));

                        string text = lines[i + 1].Substring(dot + 1);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            temp.text = text.Replace(((char)2).ToString(), ",");
                            r.Add(temp);
                        }
                    }
                }
                catch
                {
                    //先不处理
                }
            }
            return r;
        }

        /// <summary>
        /// 读取软件资源文件内容
        /// </summary>
        /// <param name="path">路径</param>
        /// <returns>内容字节数组</returns>
        public static byte[] GetAssetsFileContent(string path)
        {
            Uri uri = new Uri(path, UriKind.Relative);
            var source = System.Windows.Application.GetResourceStream(uri).Stream;
            byte[] f = new byte[source.Length];
            source.Read(f, 0, (int)source.Length);
            return f;
        }

        /// <summary>
        /// 取出文件
        /// </summary>
        /// <param name="insidePath">软件内部的路径</param>
        /// <param name="outPath">需要释放到的路径</param>
        /// <param name="d">是否覆盖</param>
        public static void CreateFile(string insidePath, string outPath, bool d = true)
        {
            if(!File.Exists(outPath) || d)
                File.WriteAllBytes(outPath, GetAssetsFileContent(insidePath));
        }

        public static void CreateFileIfMissing(string insidePath, string outPath)
        {
            CreateFile(insidePath, outPath, false);
        }

        /// <summary>
        /// 更换语言文件
        /// </summary>
        /// <param name="languagefileName"></param>
        public static void LoadLanguageFile(string languagefileName)
        {
            try
            {
                System.Windows.Application.Current.Resources.MergedDictionaries[0] = new System.Windows.ResourceDictionary()
                {
                    Source = new Uri($"pack://application:,,,/languages/{languagefileName}.xaml", UriKind.RelativeOrAbsolute)
                };
            }
            catch
            {
                System.Windows.Application.Current.Resources.MergedDictionaries[0] = new System.Windows.ResourceDictionary()
                {
                    Source = new Uri("pack://application:,,,/languages/en-US.xaml", UriKind.RelativeOrAbsolute)
                };
            }

        }

        public static void ApplyTheme(bool darkMode)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
                return;

            try
            {
                app.Resources.MergedDictionaries[1] = new System.Windows.ResourceDictionary()
                {
                    Source = new Uri($"pack://application:,,,/AdonisUI;component/ColorSchemes/{(darkMode ? "Dark" : "Light")}.xaml", UriKind.RelativeOrAbsolute)
                };
            }
            catch
            {
                // 自定义资源仍会生效，Adonis 主题包不可用时不影响主功能。
            }

            if (darkMode)
            {
                SetBrush("AppGlassPanelBackground", "#D91A222C");
                SetBrush("AppGlassPanelBorderBrush", "#5FFFFFFF");
                SetBrush("AppGlassInputBackground", "#E1121820");
                SetBrush("AppGlassControlBackground", "#E3212B36");
                SetBrush("AppGlassControlHoverBackground", "#F02B3744");
                SetBrush("AppGlassControlPressedBackground", "#F017202A");
                SetBrush("AppGlassBorderBrush", "#6D9BAABD");
                SetBrush("AppGlassFocusBorderBrush", "#8EC8E6FF");
                SetBrush("AppGlassTextBrush", "#F4F7FAFC");
                SetBrush("AppGlassMutedBrush", "#B8C3CE");
                SetBrush("AppAccentBrush", "#38D6E6");
                SetBrush("AppAccentSoftBrush", "#3A38D6E6");
                SetBrush("AppWindowOverlayBrush", "#660A1018");
                SetBrush("AppTabStripBackground", "#4A101820");
                SetBrush("AppTabStripBorderBrush", "#46FFFFFF");
                SetBrush("AppToolContentBackground", "#D1121820");
                SetBrush("AppPopupBackground", "#F21A222C");
                SetBrush("AppReadOnlyBackground", "#E31D2731");
                SetBrush("AppSelectedItemBackground", "#5538D6E6");
                SetBrush("AppDataSentBrush", "#FF8F96");
                SetBrush("AppDataReceivedBrush", "#8EF3B4");
                SetBrush("AppDataSentSoftBrush", "#F5A3A9");
                SetBrush("AppDataReceivedSoftBrush", "#8EE6A6");
            }
            else
            {
                SetBrush("AppGlassPanelBackground", "#E8FFFFFF");
                SetBrush("AppGlassPanelBorderBrush", "#A6FFFFFF");
                SetBrush("AppGlassInputBackground", "#F4FFFFFF");
                SetBrush("AppGlassControlBackground", "#EAF6FBFF");
                SetBrush("AppGlassControlHoverBackground", "#FAFFFFFF");
                SetBrush("AppGlassControlPressedBackground", "#DDEEF7FF");
                SetBrush("AppGlassBorderBrush", "#8FB7CAD9");
                SetBrush("AppGlassFocusBorderBrush", "#6A9FC4E8");
                SetBrush("AppGlassTextBrush", "#1B1D20");
                SetBrush("AppGlassMutedBrush", "#66737D");
                SetBrush("AppAccentBrush", "#0E9AA7");
                SetBrush("AppAccentSoftBrush", "#DDF8FBFF");
                SetBrush("AppWindowOverlayBrush", "#24FFF8EC");
                SetBrush("AppTabStripBackground", "#36FFFFFF");
                SetBrush("AppTabStripBorderBrush", "#55FFFFFF");
                SetBrush("AppToolContentBackground", "#A8FFFFFF");
                SetBrush("AppPopupBackground", "#FAFFFFFF");
                SetBrush("AppReadOnlyBackground", "#E7F7F8FA");
                SetBrush("AppSelectedItemBackground", "#C9EFF7FA");
                SetBrush("AppDataSentBrush", "#8B0000");
                SetBrush("AppDataReceivedBrush", "#006400");
                SetBrush("AppDataSentSoftBrush", "#CD5C5C");
                SetBrush("AppDataReceivedSoftBrush", "#228B22");
            }
        }

        private static void SetBrush(string key, string colorText)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
                return;

            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText);
            if (app.Resources[key] is System.Windows.Media.SolidColorBrush brush)
            {
                try
                {
                    if (!brush.IsFrozen)
                    {
                        brush.Color = color;
                        return;
                    }
                }
                catch
                {
                    // 已被 WPF 冻结的画刷不能原地修改，下面用新画刷替换资源。
                }
            }

            app.Resources[key] = new System.Windows.Media.SolidColorBrush(color);
        }

    }
}
