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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Shapes;
using Microsoft.Win32;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace llcom_plus.Tools
{
    class UartSendRequest
    {
        public byte[] Data { get; set; }
        public bool? IsHex { get; set; }
        public bool ApplySendProcessing { get; set; } = true;
        public string SessionStringLogOverride { get; set; }
    }

    class Global
    {
        public static event EventHandler ProgramClosedEvent;
        public static event EventHandler ThemeChanged;
        public static bool IsDarkTheme { get; private set; }
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
                if (_isMainWindowsClosed == value)
                    return;

                _isMainWindowsClosed = value;
                if (value)
                {
                    try
                    {
                        uart?.Shutdown();
                    }
                    catch (Exception e)
                    {
                        Logger.AddUartLogDebug($"[ProgramClosed]uart close error:{e.Message}");
                    }
                    uart?.WaitUartReceive.Set();
                    NotifyProgramClosed();
                    Logger.StopSessionLog();
                    Logger.CloseUartLog();
                    Logger.CloseScriptLog();
                    ReleaseSingleInstanceMutex();
                }
            }
        }

        private static void NotifyProgramClosed()
        {
            var handlers = ProgramClosedEvent;
            if (handlers == null)
                return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(null, EventArgs.Empty);
                }
                catch (Exception e)
                {
                    Logger.AddUartLogDebug($"[ProgramClosed]handler error:{e.Message}");
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
        internal const string ExpectedExeFileName = ProductName + ".exe";
        private static Mutex singleInstanceMutex;
        private static bool singleInstanceMutexOwned;

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
        /// 后台发送/驱动异常导致串口被强制断开时，通知主界面同步状态。
        /// </summary>
        public static event EventHandler<string> UartPortClosedEvent;
        public static void NotifyUartPortClosed(string portName) => UartPortClosedEvent?.Invoke(null, portName ?? "");

        /// <summary>
        /// 当前串口配置档切换后，通知界面刷新串口相关控件。
        /// </summary>
        public static event EventHandler UartProfileChangedEvent;
        public static void NotifyUartProfileChanged() => UartProfileChangedEvent?.Invoke(null, EventArgs.Empty);

        /// <summary>
        /// 主界面串口分屏数量改变后，通知主窗口切换布局。
        /// </summary>
        public static event EventHandler SerialSplitScreenChangedEvent;
        public static void NotifySerialSplitScreenChanged() => SerialSplitScreenChangedEvent?.Invoke(null, EventArgs.Empty);

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

        public sealed class MainSendTarget
        {
            public string Key { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public Func<byte[], bool> Send { get; set; }
        }

        public static event EventHandler MainSendTargetChangedEvent;
        private static MainSendTarget mainSendTarget;

        public static bool HasMainSendTarget => mainSendTarget != null;
        public static string MainSendTargetDisplayName => mainSendTarget?.DisplayName ?? "";

        public static void SetMainSendTarget(MainSendTarget target)
        {
            mainSendTarget = target;
            MainSendTargetChangedEvent?.Invoke(null, EventArgs.Empty);
        }

        public static void ClearMainSendTarget(string key = null)
        {
            if (mainSendTarget == null)
                return;

            if (!string.IsNullOrWhiteSpace(key) &&
                !string.Equals(mainSendTarget.Key, key, StringComparison.Ordinal))
            {
                return;
            }

            mainSendTarget = null;
            MainSendTargetChangedEvent?.Invoke(null, EventArgs.Empty);
        }

        public static bool SendToMainSendTarget(byte[] data)
        {
            var target = mainSendTarget;
            if (target == null || target.Send == null || data == null)
                return false;

            return target.Send(data);
        }

        /// <summary>
        /// 让工具页面请求主串口按普通发送链路发送数据
        /// </summary>
        public static event Action<UartSendRequest> SendDataRequest;
        public static bool RequestSendData(UartSendRequest request)
        {
            if (request?.Data == null || request.Data.Length == 0 || SendDataRequest == null)
                return false;
            SendDataRequest(request);
            return true;
        }

        public static Func<bool> IsActiveSerialTargetOpenRequest;
        public static Func<bool> EnsureActiveSerialTargetOpenRequest;
        public static Func<byte[], CancellationToken, bool> SendRawDataToActiveTargetRequest;
        public static event EventHandler<byte[]> ActiveSerialTargetReceivedEvent;
        public static void NotifyActiveSerialTargetReceived(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            ActiveSerialTargetReceivedEvent?.Invoke(null, data);
        }

        public static bool IsActiveSerialTargetOpen()
        {
            if (IsActiveSerialTargetOpenRequest != null)
                return IsActiveSerialTargetOpenRequest();

            return uart?.IsOpen() == true;
        }

        public static bool EnsureActiveSerialTargetOpen()
        {
            if (EnsureActiveSerialTargetOpenRequest != null)
                return EnsureActiveSerialTargetOpenRequest();

            return uart?.IsOpen() == true;
        }

        public static bool SendRawDataToActiveTarget(byte[] data, CancellationToken token)
        {
            if (data == null || data.Length == 0)
                return false;

            if (SendRawDataToActiveTargetRequest != null)
                return SendRawDataToActiveTargetRequest(data, token);

            if (uart?.IsOpen() != true)
                return false;

            uart.SendDataCancelable(data, token, null, raiseEvents: false);
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
                        setting.EnsureRuntimeState();
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
                    setting = new Model.Settings();
                    setting.EnsureRuntimeState();
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

            StartupProfiler.Measure("Global.Initial single instance lock", EnsureSingleInstance);

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

        internal static void EnsureSingleInstance()
        {
            if (singleInstanceMutexOwned && singleInstanceMutex != null)
                return;

            var normalizedPath = Regex.Replace(
                (AppPath ?? "").TrimEnd('\\').ToUpperInvariant(),
                @"[^A-Z0-9]+",
                "_");
            if (normalizedPath.Length > 180)
                normalizedPath = normalizedPath.Substring(normalizedPath.Length - 180);

            var mutexName = @"Local\llcom_plus_single_instance_" + normalizedPath;
            bool createdNew;
            singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            singleInstanceMutexOwned = createdNew;
            if (createdNew)
                return;

            Tools.MessageBox.Show("当前目录下已运行 llcom plus。\r\n请使用小工具里的“四串口分屏”同时操作多个串口。");
            Environment.Exit(1);
        }

        private static void ReleaseSingleInstanceMutex()
        {
            try
            {
                if (singleInstanceMutexOwned)
                    singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
            }
            finally
            {
                singleInstanceMutexOwned = false;
                singleInstanceMutex?.Dispose();
                singleInstanceMutex = null;
            }
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
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_run/AT控制TCP连接-快发模式.js", ProfilePath + "user_script_run/AT控制TCP连接-快发模式.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_run/AT控制TCP连接-慢发模式.js", ProfilePath + "user_script_run/AT控制TCP连接-慢发模式.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_run/example.js", ProfilePath + "user_script_run/example.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_run/循环发送快捷发送区数据.js", ProfilePath + "user_script_run/循环发送快捷发送区数据.js");
                    //通用消息通道的demo
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_run/channel-demo.js", ProfilePath + "user_script_run/channel-demo.js");

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
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_send_convert/checksum.js", ProfilePath + "user_script_send_convert/checksum.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_send_convert/16进制数据.js", ProfilePath + "user_script_send_convert/16进制数据.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_send_convert/GPS NMEA.js", ProfilePath + "user_script_send_convert/GPS NMEA.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_send_convert/加上换行回车.js", ProfilePath + "user_script_send_convert/加上换行回车.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_send_convert/解析换行回车的转义字符.js", ProfilePath + "user_script_send_convert/解析换行回车的转义字符.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_send_convert/default.js", ProfilePath + "user_script_send_convert/default.js");
                });

                StartupProfiler.Measure("PrepareRuntimeFiles user_script_recv_convert", () =>
                {
                    if (!Directory.Exists(ProfilePath + "user_script_recv_convert"))
                    {
                        Directory.CreateDirectory(ProfilePath + "user_script_recv_convert");
                    }
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_recv_convert/default.js", ProfilePath + "user_script_recv_convert/default.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_recv_convert/绘制曲线.js", ProfilePath + "user_script_recv_convert/绘制曲线.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_recv_convert/绘制曲线-多条.js", ProfilePath + "user_script_recv_convert/绘制曲线-多条.js");
                    CreateFileIfMissing("Resources/DefaultFiles/user_script_recv_convert/绘制曲线-解析结构体.js", ProfilePath + "user_script_recv_convert/绘制曲线-解析结构体.js");
                });

                StartupProfiler.Measure("PrepareRuntimeFiles license and libusb", () =>
                {
                    CreateFile("Resources/DefaultFiles/LICENSE", ProfilePath + "LICENSE", false);

                    if (IntPtr.Size == 8)
                        CreateFile("Resources/DefaultFiles/libusb-1.0-x64.dll", ProfilePath + "libusb-1.0", false);
                    else
                        CreateFile("Resources/DefaultFiles/libusb-1.0-x86.dll", ProfilePath + "libusb-1.0", false);
                });
            }
            catch (Exception e)
            {
                Tools.MessageBox.Show("生成文件结构失败，请确保本软件处于有读写权限的目录下再打开。\r\n错误信息：" + e.Message);
                Environment.Exit(1);
            }

            StartupProfiler.Measure("PrepareRuntimeFiles settings backup", () =>
            {
                try
                {
                    //备份一下文件好了（心理安慰），多开抢占时不能影响启动。
                    if (File.Exists(ProfilePath + "settings.json"))
                        File.Copy(ProfilePath + "settings.json", ProfilePath + "settings.json.bakup", true);
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"[PrepareRuntimeFiles] settings backup skipped: {e.Message}");
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
            if (File.Exists(outPath) && !d)
                return;

            try
            {
                File.WriteAllBytes(outPath, GetAssetsFileContent(insidePath));
            }
            catch (IOException)
            {
                if (d || !File.Exists(outPath))
                    throw;
            }
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
            var app = System.Windows.Application.Current;
            if (app == null)
                return;

            System.Windows.ResourceDictionary dictionary;
            try
            {
                dictionary = new System.Windows.ResourceDictionary()
                {
                    Source = new Uri($"pack://application:,,,/Resources/Languages/{languagefileName}.xaml", UriKind.RelativeOrAbsolute)
                };
            }
            catch
            {
                dictionary = new System.Windows.ResourceDictionary()
                {
                    Source = new Uri("pack://application:,,,/Resources/Languages/en-US.xaml", UriKind.RelativeOrAbsolute)
                };
            }

            ReplaceMergedDictionary(app.Resources, "Resources/Languages/", dictionary, 0);
        }

        public static void ApplyTheme(bool darkMode)
        {
            var app = System.Windows.Application.Current;
            if (app == null)
                return;

            try
            {
                AdonisUI.ResourceLocator.SetColorScheme(
                    app.Resources,
                    darkMode ? AdonisUI.ResourceLocator.DarkColorScheme : AdonisUI.ResourceLocator.LightColorScheme);
            }
            catch
            {
                // 自定义资源仍会生效，Adonis 主题包不可用时不影响主功能。
            }

            var palette = new System.Windows.ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/Resources/Themes/Palette.{(darkMode ? "Dark" : "Light")}.xaml",
                    UriKind.RelativeOrAbsolute)
            };
            ReplaceMergedDictionary(app.Resources, "Resources/Themes/Palette.", palette);

            IsDarkTheme = darkMode;
            foreach (System.Windows.Window window in app.Windows)
            {
                Win32.ApplyWindowTheme(window, darkMode, !(window is llcom_plus.MainWindow));
            }

            RaiseThemeChanged();
        }

        private static void ReplaceMergedDictionary(
            System.Windows.ResourceDictionary resources,
            string sourceMarker,
            System.Windows.ResourceDictionary replacement,
            int fallbackIndex = -1)
        {
            var dictionaries = resources.MergedDictionaries;
            for (var i = 0; i < dictionaries.Count; i++)
            {
                var source = dictionaries[i].Source?.OriginalString;
                if (!string.IsNullOrEmpty(source) &&
                    source.IndexOf(sourceMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dictionaries[i] = replacement;
                    return;
                }
            }

            if (fallbackIndex >= 0 && fallbackIndex <= dictionaries.Count)
                dictionaries.Insert(fallbackIndex, replacement);
            else
                dictionaries.Add(replacement);
        }

        private static void RaiseThemeChanged()
        {
            var handlers = ThemeChanged;
            if (handlers == null)
                return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(null, EventArgs.Empty);
                }
                catch
                {
                    // 单个工具页的主题适配失败不应阻断全局主题切换。
                }
            }
        }

    }
}
