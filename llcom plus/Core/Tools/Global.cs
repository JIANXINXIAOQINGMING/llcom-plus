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
using Path = System.IO.Path;

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
    internal enum AppNotificationLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    internal enum AppNotificationCategory
    {
        General,
        Connection,
        SerialPin,
        Task,
        Update
    }

    internal sealed class AppNotificationEventArgs : EventArgs
    {
        public DateTime Timestamp { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public AppNotificationLevel Level { get; set; }
        public AppNotificationCategory Category { get; set; }
        public string PortName { get; set; }
    }

    class UartSendRequest
    {
        public byte[] Data { get; set; }
        public bool? IsHex { get; set; }
        public bool ApplySendProcessing { get; set; } = true;
        public string SessionStringLogOverride { get; set; }
        public string SourceText { get; set; }
    }

    /// <summary>
    /// An immutable lease for the serial connection that was active when it was captured.
    /// The delegates close over a concrete page/slot/connection generation, so changing the
    /// UI selection or reopening a port cannot redirect an in-flight operation.
    /// </summary>
    internal sealed class ActiveSerialTarget
    {
        private readonly Func<bool> isOpen;
        private readonly Func<byte[], CancellationToken, Action<int>, bool> send;

        internal ActiveSerialTarget(
            string identity,
            string displayName,
            Func<bool> isOpen,
            Func<byte[], CancellationToken, Action<int>, bool> send,
            bool supportsResumableCommits = true)
        {
            Identity = identity ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            SupportsResumableCommits = supportsResumableCommits;
            this.isOpen = isOpen ?? throw new ArgumentNullException(nameof(isOpen));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public string Identity { get; }
        public string DisplayName { get; }
        public bool SupportsResumableCommits { get; }

        public bool IsOpen
        {
            get
            {
                try { return isOpen(); }
                catch (ObjectDisposedException) { return false; }
                catch (IOException) { return false; }
                catch (InvalidOperationException) { return false; }
            }
        }

        /// <summary>
        /// Sends through this captured connection. committedBytes is invoked immediately
        /// after each successful SerialPort.Write and before waiting for driver drain.
        /// </summary>
        public bool Send(byte[] data, CancellationToken token, Action<int> committedBytes = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return true;

            token.ThrowIfCancellationRequested();
            return send(data, token, committedBytes);
        }
    }

    class Global
    {
        public static event EventHandler ProgramClosedEvent;
        public static event EventHandler ThemeChanged;
        public static event EventHandler LogColorsChanged;
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
        private static bool quickSendLegacyRecoveryNotice;
        private static QuickSendBackupState quickSendLegacyRecoveryCandidate;
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

        internal static string NormalizeScriptFileName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 3).TrimEnd();
            return name;
        }

        internal static bool IsValidScriptFileName(string value)
        {
            var rawName = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(rawName) ||
                Path.IsPathRooted(rawName) ||
                rawName == "." ||
                rawName == ".." ||
                rawName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                rawName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return false;
            }

            var name = NormalizeScriptFileName(rawName);
            if (string.IsNullOrWhiteSpace(name) ||
                name == "." ||
                name == ".." ||
                name.EndsWith(".", StringComparison.Ordinal) ||
                name.EndsWith(" ", StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
            {
                return false;
            }

            return !Regex.IsMatch(
                name,
                @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsValidPathSegment(string value)
        {
            var segment = (value ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(segment) &&
                segment != "." &&
                segment != ".." &&
                !Path.IsPathRooted(segment) &&
                segment.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                segment.IndexOf(Path.AltDirectorySeparatorChar) < 0 &&
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                string.Equals(Path.GetFileName(segment), segment, StringComparison.Ordinal);
        }

        private static string GetCanonicalDirectoryPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath);
            if (string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath;

            return fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static bool IsPathWithinOrEqual(string canonicalRoot, string canonicalPath)
        {
            if (string.Equals(canonicalRoot, canonicalPath, StringComparison.OrdinalIgnoreCase))
                return true;

            var rootWithSeparator = canonicalRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return canonicalPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool TryGetCanonicalScriptPath(
            string allowedRoot,
            string scriptName,
            out string normalizedScriptName,
            out string scriptPath)
        {
            normalizedScriptName = NormalizeScriptFileName(scriptName);
            scriptPath = string.Empty;
            if (!IsValidScriptFileName(scriptName) || string.IsNullOrWhiteSpace(allowedRoot))
                return false;

            try
            {
                var profileRoot = GetCanonicalDirectoryPath(ProfilePath);
                var canonicalRoot = GetCanonicalDirectoryPath(allowedRoot);
                if (!IsPathWithinOrEqual(profileRoot, canonicalRoot))
                    return false;

                var candidate = Path.GetFullPath(Path.Combine(
                    canonicalRoot,
                    normalizedScriptName + ".js"));
                var candidateDirectory = GetCanonicalDirectoryPath(Path.GetDirectoryName(candidate));
                if (!string.Equals(
                        candidateDirectory,
                        canonicalRoot,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetExtension(candidate), ".js", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        Path.GetFileNameWithoutExtension(candidate),
                        normalizedScriptName,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                scriptPath = candidate;
                return true;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is IOException)
            {
                return false;
            }
        }

        internal static bool TryGetProfileScriptPath(
            string directoryName,
            string scriptName,
            out string normalizedScriptName,
            out string scriptPath)
        {
            normalizedScriptName = NormalizeScriptFileName(scriptName);
            scriptPath = string.Empty;
            if (!IsValidPathSegment(directoryName))
                return false;

            try
            {
                var profileRoot = GetCanonicalDirectoryPath(ProfilePath);
                var root = GetCanonicalDirectoryPath(Path.Combine(profileRoot, directoryName));
                if (!IsPathWithinOrEqual(profileRoot, root))
                    return false;

                return TryGetCanonicalScriptPath(
                    root,
                    scriptName,
                    out normalizedScriptName,
                    out scriptPath);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is IOException)
            {
                return false;
            }
        }

        internal static bool TryGetProfileScriptPathFromRelativePath(
            string expectedDirectoryName,
            string relativePath,
            out string normalizedScriptName,
            out string scriptPath)
        {
            normalizedScriptName = string.Empty;
            scriptPath = string.Empty;
            var value = (relativePath ?? string.Empty).Trim();
            if (!IsValidPathSegment(expectedDirectoryName) ||
                string.IsNullOrWhiteSpace(value) ||
                Path.IsPathRooted(value))
            {
                return false;
            }

            var separatorIndex = value.IndexOfAny(new[]
            {
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            });
            if (separatorIndex <= 0 ||
                separatorIndex != value.LastIndexOfAny(new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                }))
            {
                return false;
            }

            var directoryName = value.Substring(0, separatorIndex);
            var fileName = value.Substring(separatorIndex + 1);
            if (!string.Equals(
                    directoryName,
                    expectedDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TryGetProfileScriptPath(
                expectedDirectoryName,
                fileName,
                out normalizedScriptName,
                out scriptPath);
        }

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
        /// 主串口和多串口分屏共用的输入引脚状态变化通道。
        /// </summary>
        public static event EventHandler<SerialPinStatusSnapshot> SerialPinStatusChangedEvent;
        public static void NotifySerialPinStatusChanged(SerialPinStatusSnapshot snapshot)
        {
            if (snapshot == null || isMainWindowsClosed)
                return;

            SerialPinStatusChangedEvent?.Invoke(null, snapshot);
        }

        /// <summary>
        /// 各功能模块共用的消息中心入口。普通收发数据不要写入此通道。
        /// </summary>
        public static event EventHandler<AppNotificationEventArgs> AppNotificationEvent;
        public static void PublishNotification(
            string title,
            string message = "",
            AppNotificationLevel level = AppNotificationLevel.Info,
            DateTime? timestamp = null,
            AppNotificationCategory category = AppNotificationCategory.General,
            string portName = "")
        {
            if (isMainWindowsClosed || string.IsNullOrWhiteSpace(title))
                return;

            var notification = new AppNotificationEventArgs
            {
                Timestamp = timestamp ?? DateTime.Now,
                Title = title.Trim(),
                Message = message ?? string.Empty,
                Level = level,
                Category = category,
                PortName = portName ?? string.Empty
            };
            var handlers = AppNotificationEvent;
            if (handlers == null)
                return;

            foreach (EventHandler<AppNotificationEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(null, notification);
                }
                catch (Exception ex)
                {
                    Logger.AddUartLogDebug($"[AppNotification]handler error:{ex.Message}");
                }
            }
        }

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

        // Unlike the legacy notification above, this contract completes only after
        // the captured connection has actually finished sending. Never fall back to
        // the Action event: queuing a request is not evidence of successful delivery.
        public static Func<UartSendRequest, CancellationToken, Task<bool>> SendDataAsyncRequest;

        public static Task<bool> RequestSendDataAsync(
            UartSendRequest request,
            CancellationToken token = default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            var send = SendDataAsyncRequest;
            if (request?.Data == null || request.Data.Length == 0 || send == null)
                return Task.FromResult(false);

            return send(new UartSendRequest
            {
                Data = (byte[])request.Data.Clone(),
                IsHex = request.IsHex,
                ApplySendProcessing = request.ApplySendProcessing,
                SessionStringLogOverride = request.SessionStringLogOverride,
                SourceText = request.SourceText
            }, token);
        }

        public static Func<bool> IsActiveSerialTargetOpenRequest;
        public static Func<bool> EnsureActiveSerialTargetOpenRequest;
        public static Func<byte[], CancellationToken, bool> SendRawDataToActiveTargetRequest;
        public static Func<ActiveSerialTarget> CaptureActiveSerialTargetRequest;
        public static event EventHandler<byte[]> ActiveSerialTargetReceivedEvent;

        public static ActiveSerialTarget CaptureActiveSerialTarget()
        {
            var capture = CaptureActiveSerialTargetRequest;
            if (capture != null)
                return capture();

            var connection = uart?.CaptureConnectionLease();
            if (connection == null)
                return null;

            return new ActiveSerialTarget(
                connection.Identity,
                connection.DisplayName,
                () => connection.IsOpen,
                (data, token, committedBytes) =>
                    connection.Send(data, token, committedBytes, raiseEvents: false));
        }

        public static void NotifyActiveSerialTargetReceived(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            var handlers = ActiveSerialTargetReceivedEvent;
            if (handlers != null)
            {
                foreach (EventHandler<byte[]> handler in handlers.GetInvocationList())
                {
                    try { handler(null, data); }
                    catch (Exception ex)
                    {
                        Logger.AddUartLogDebug($"[ActiveSerialReceive]handler error:{ex.Message}");
                    }
                }
            }

            try
            {
                ScriptEnv.ScriptApis.SendChannelsReceived("uart", data);
            }
            catch (Exception ex)
            {
                Logger.AddUartLogDebug($"[ActiveSerialReceive]script channel error:{ex.Message}");
            }
        }

        public static bool IsActiveSerialTargetOpen()
        {
            var target = CaptureActiveSerialTarget();
            if (target != null)
                return target.IsOpen;

            return IsActiveSerialTargetOpenRequest?.Invoke() == true;
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

            var target = CaptureActiveSerialTarget();
            if (target != null)
                return target.IsOpen && target.Send(data, token, null);

            return SendRawDataToActiveTargetRequest?.Invoke(data, token) == true;
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
                        var settingsText = File.ReadAllText(ProfilePath + "settings.json");
                        var hadPersistedTlsPassword = false;
                        try
                        {
                            hadPersistedTlsPassword = Newtonsoft.Json.Linq.JObject.Parse(settingsText)
                                .Property("tcpClientSslClientCertPassword", StringComparison.OrdinalIgnoreCase) != null;
                        }
                        catch { }

                        setting = JsonConvert.DeserializeObject<Model.Settings>(settingsText);
                        if (setting == null)
                            throw new Exception("settings.json is empty");
                        QuickSendBackupService.Initialize(setting);
                        QuickSendBackupService.CreateNow(setting, "startup");
                        TryRecoverEmptyQuickSendFromLegacyBackup(setting);
                        setting.EnsureRuntimeState();
                        if (hadPersistedTlsPassword)
                            setting.RemovePersistedTlsPassword();
                        setting.SentCount = 0;
                        setting.ReceivedCount = 0;
                        setting.DisableLog = false;
                    }
                    catch
                    {
                        if (!TryRecoverSettingsAfterLoadFailure(out setting))
                        {
                            Tools.MessageBox.Show($"配置文件加载失败！\r\n" +
                                $"如果是配置文件损坏，可前往{ProfilePath}settings.json.bakup查找备份文件\r\n" +
                                $"并使用该文件替换{ProfilePath}settings.json文件恢复配置");
                            Environment.Exit(1);
                        }
                    }
                });
            }
            else
            {
                StartupProfiler.Measure("Global.LoadSetting create default settings", () =>
                {
                    setting = new Model.Settings();
                    setting.EnsureRuntimeState();
                    QuickSendBackupService.Initialize(setting);
                    QuickSendBackupService.CreateNow(setting, "startup");
                });
            }
            setting.SentCount = 0;
            setting.ReceivedCount = 0;
            setting.DisableLog = false;
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

        internal static bool ConsumeQuickSendLegacyRecoveryNotice()
        {
            if (!quickSendLegacyRecoveryNotice)
                return false;
            quickSendLegacyRecoveryNotice = false;
            return true;
        }

        internal static bool TryConsumeQuickSendLegacyRecoveryCandidate(
            out QuickSendBackupState candidate)
        {
            candidate = quickSendLegacyRecoveryCandidate;
            quickSendLegacyRecoveryCandidate = null;
            return candidate != null;
        }

        private static void TryRecoverEmptyQuickSendFromLegacyBackup(Model.Settings current)
        {
            if (current == null || GetQuickSendRecoveryScore(current) > 0)
                return;

            Model.Settings best = null;
            var bestScore = 0;
            foreach (var path in EnumerateLegacySettingsBackupPaths())
            {
                try
                {
                    var candidate = JsonConvert.DeserializeObject<Model.Settings>(File.ReadAllText(path));
                    var score = GetQuickSendRecoveryScore(candidate);
                    if (score <= bestScore)
                        continue;
                    best = candidate;
                    bestScore = score;
                }
                catch
                {
                }
            }

            if (best == null || bestScore <= 0)
                return;

            var preserved = QuickSendBackupService.CreateNow(best, "legacy-backup-candidate");
            if (preserved.Succeeded &&
                QuickSendBackupService.TryReadSnapshot(
                    preserved.FilePath,
                    out var recoveryState,
                    out _))
                quickSendLegacyRecoveryCandidate = recoveryState;
        }

        private static bool TryRecoverSettingsAfterLoadFailure(out Model.Settings recovered)
        {
            recovered = null;
            foreach (var path in EnumerateLegacySettingsBackupPaths())
            {
                try
                {
                    recovered = JsonConvert.DeserializeObject<Model.Settings>(File.ReadAllText(path));
                    if (recovered == null)
                        continue;
                    QuickSendBackupService.Initialize(recovered);
                    QuickSendBackupService.CreateNow(recovered, "legacy-settings-recovery");
                    recovered.EnsureRuntimeState();
                    quickSendLegacyRecoveryNotice = true;
                    return true;
                }
                catch
                {
                    recovered = null;
                }
            }

            QuickSendBackupService.Initialize();
            var latest = QuickSendBackupService.GetSnapshots().FirstOrDefault();
            if (latest == null ||
                !QuickSendBackupService.TryLoad(latest, out var state, out _))
                return false;

            recovered = new Model.Settings();
            recovered.EnsureRuntimeState();
            recovered.SetAllQuickSendState(
                state.CreateModelLists(),
                state.Names,
                state.SelectedIndex);
            QuickSendBackupService.CreateNow(recovered, "snapshot-recovery");
            quickSendLegacyRecoveryNotice = true;
            return true;
        }

        private static IEnumerable<string> EnumerateLegacySettingsBackupPaths()
        {
            var paths = new List<string>();
            var conventional = Path.Combine(ProfilePath, "settings.json.bakup");
            if (File.Exists(conventional))
                paths.Add(conventional);
            try
            {
                paths.AddRange(Directory.EnumerateFiles(
                    ProfilePath,
                    "settings.json.*.bak",
                    SearchOption.TopDirectoryOnly));
            }
            catch
            {
            }
            return paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(path =>
                {
                    try { return File.GetLastWriteTimeUtc(path); }
                    catch { return DateTime.MinValue; }
                });
        }

        private static int GetQuickSendRecoveryScore(Model.Settings value)
        {
            if (value == null)
                return 0;
            try
            {
                var pages = value.GetAllQuickSendLists();
                var names = value.GetAllQuickListNames();
                var score = Math.Max(0, pages.Count - 1) * 1000;
                for (var index = 0; index < names.Count; index++)
                {
                    var name = (names[index] ?? string.Empty).Trim();
                    if (name.Length > 0 &&
                        !name.Equals("未命名" + index, StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("Untitled " + index, StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("Untitled" + index, StringComparison.OrdinalIgnoreCase))
                        score += 100;
                }
                foreach (var item in pages.Where(page => page != null).SelectMany(page => page))
                {
                    if (item == null)
                        continue;
                    var button = (item.commit ?? string.Empty).Trim();
                    var customButton = button.Length > 0 &&
                        !button.Equals("发送", StringComparison.OrdinalIgnoreCase) &&
                        !button.Equals("Send", StringComparison.OrdinalIgnoreCase) &&
                        !button.Equals("?!", StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(item.text) || item.hex || customButton ||
                        !string.IsNullOrWhiteSpace(item.recvScriptPath) ||
                        !string.IsNullOrWhiteSpace(item.recvScriptPara) ||
                        item.disableSuggestion)
                        score += 10;
                }
                return score;
            }
            catch
            {
                return 0;
            }
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

        internal static bool TryAcquireSingleInstance()
        {
            if (singleInstanceMutexOwned && singleInstanceMutex != null)
                return true;

            return TryAcquireSingleInstance(BuildSingleInstanceMutexName(AppPath));
        }

        private static string BuildSingleInstanceMutexName(string appPath)
        {
            var normalizedPath = Regex.Replace(
                (appPath ?? "").TrimEnd('\\').ToUpperInvariant(),
                @"[^A-Z0-9]+",
                "_");
            if (normalizedPath.Length > 180)
                normalizedPath = normalizedPath.Substring(normalizedPath.Length - 180);

            return @"Local\llcom_plus_single_instance_" + normalizedPath;
        }

        private static bool TryAcquireSingleInstance(string mutexName)
        {
            bool createdNew;
            singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            singleInstanceMutexOwned = createdNew;
            if (createdNew)
                return true;

            singleInstanceMutex.Dispose();
            singleInstanceMutex = null;
            return false;
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

        public static Encoding GetEncoding() => GetEncoding(setting?.encoding ?? 65001);

        internal static Encoding GetEncoding(int encodingCodePage)
        {
            try { return Encoding.GetEncoding(encodingCodePage); }
            catch { return Encoding.UTF8; }
        }

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
            return Byte2Readable(
                vBytes,
                len,
                setting?.encoding ?? 65001,
                setting?.EnableSymbol == true);
        }

        internal static string Byte2Readable(
            byte[] vBytes,
            int len,
            int encodingCodePage,
            bool enableSymbol)
        {
            if (vBytes == null)//fix
                return "";
            if (len == -1 || len > vBytes.Length)
                len = vBytes.Length;

            var encoding = GetEncoding(encodingCodePage);
            //没开这个功能/非utf8就别搞了
            if (!enableSymbol || encodingCodePage != 65001)
                return encoding.GetString(vBytes, 0, len);

            var text = new StringBuilder();
            var plainBytes = new List<byte>();
            for (int i = 0; i < len; i++)
            {
                // Show whitespace controls, while preserving their visual effect.
                if (vBytes[i] == 0x0d && i < len - 1 && vBytes[i + 1] == 0x0a)
                {
                    if (plainBytes.Count > 0)
                    {
                        text.Append(encoding.GetString(plainBytes.ToArray()));
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
                        text.Append(encoding.GetString(plainBytes.ToArray()));
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
                        text.Append(encoding.GetString(plainBytes.ToArray()));
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
                text.Append(encoding.GetString(plainBytes.ToArray()));
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
            NotifyLogColorsChanged();
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

        public static void NotifyLogColorsChanged()
        {
            var handlers = LogColorsChanged;
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
                    // 单个页面刷新失败不应阻断设置保存和其它页面更新。
                }
            }
        }

    }
}
