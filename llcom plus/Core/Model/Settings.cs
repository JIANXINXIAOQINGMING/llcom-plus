using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Model
{
    class UartPortProfile
    {
        public int baudRate { get; set; } = 115200;
        public int showHexFormat { get; set; } = 0;
        public bool hexSend { get; set; } = false;
        public bool showSend { get; set; } = true;
        public bool showSendRaw { get; set; } = true;
        public int parity { get; set; } = 0;
        public int timeout { get; set; } = 50;
        public int dataBits { get; set; } = 8;
        public int stopBit { get; set; } = 1;
        public int flowControl { get; set; } = 0;
        public int sendThrottlePacketSize { get; set; } = 0;
        public int sendThrottleDelayMs { get; set; } = 0;
        public bool bitDelay { get; set; } = true;
        public uint maxLength { get; set; } = 10240;
        public string sendScript { get; set; } = "default";
        public string recvScript { get; set; } = "default";
        public bool terminal { get; set; } = true;
        public int encoding { get; set; } = 65001;
        public bool extraEnter { get; set; } = false;
        public bool enterSend { get; set; } = false;
        public bool enableSymbol { get; set; } = true;
        public bool rts { get; set; } = false;
        public bool dtr { get; set; } = false;
    }

    [PropertyChanged.AddINotifyPropertyChangedInterface]
    class Settings
    {
        private const int DefaultQuickSendRows = 10;
        private const int CurrentUartProfileSchemaVersion = 2;
        public event EventHandler MainWindowTop;
        private string _dataToSend = "uart data";
        private int _baudRate = 115200;
        private bool _autoReconnect = true;
        private bool _autoSaveLog = true;
        private int _showHexFormat = 0;
        private bool _hexSend = false;
        private bool _showSend = true;
        private bool _showSendRaw = true;
        private int _parity = 0;
        private int _timeout = 50;
        private int _dataBits = 8;
        private int _stopBit = 1;
        private int _flowControl = 0;
        private int _sendThrottlePacketSize = 0;
        private int _sendThrottleDelayMs = 0;
        private string _sendScript = "default";
        private string _recvScript = "default";
        private string _runScript = "example";
        private bool _topmost = false;
        public List<List<ToSendData>> quickSendList = new List<List<ToSendData>>();
        public List<string> quickListNames = new List<string>();
        private int _quickSendSelect = -1;
        private bool _bitDelay = true;
        private uint _maxLength = 10240;
        private string _language = System.Threading.Thread.CurrentThread.CurrentCulture.Name;
        private int _encoding = 65001;
        private bool _terminal = true;
        private bool _extraEnter = false;
        private bool _enterSend = false;
        private bool _enableSymbol = true;
        private bool _sessionLogEnabled = false;
        private string _sessionLogFolder = "";
        private bool _darkMode = false;
        public Dictionary<string, UartPortProfile> uartProfiles = new Dictionary<string, UartPortProfile>(StringComparer.OrdinalIgnoreCase);
        public int uartProfileSchemaVersion { get; set; } = 0;
        [JsonIgnore] private string _activeUartProfileName = "";
        [JsonIgnore] private bool _suspendSave = false;
        [JsonIgnore] private readonly HashSet<string> _uartProfilesPendingWrite = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [OnDeserializing]
        internal void OnDeserializing(StreamingContext context)
        {
            _suspendSave = true;
        }

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context)
        {
            _suspendSave = false;
            EnsureUartProfiles();
        }

        //窗口大小与位置
        private double _windowTop = 0;
        public double windowTop { get { return _windowTop; } set { _windowTop = value; Save(); } }
        private double _windowLeft = 0;
        public double windowLeft { get { return _windowLeft; } set { _windowLeft = value; Save(); } }
        private double _windowWidth = 0;
        public double windowWidth { get { return _windowWidth; } set { _windowWidth = value; Save(); } }
        private double _windowHeight = 0;
        public double windowHeight { get { return _windowHeight; } set { _windowHeight = value; Save(); } }

        public int SentCount { get; set; } = 0;
        public int ReceivedCount { get; set; } = 0;

        /// <summary>
        /// 保存配置
        /// </summary>
        private void Save(bool copyCurrentToActiveProfile = true)
        {
            if (_suspendSave)
                return;

            if (copyCurrentToActiveProfile)
                CopyCurrentToActiveUartProfile(true);

            var settingsPath = Path.Combine(Tools.Global.ProfilePath, "settings.json");
            var tempPath = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var mutexName = @"Local\llcom_plus_settings_" + GetProfileMutexKey(Tools.Global.ProfilePath);
            var hasLock = false;

            using (var mutex = new Mutex(false, mutexName))
            {
                try
                {
                    try
                    {
                        hasLock = mutex.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    catch (AbandonedMutexException)
                    {
                        hasLock = true;
                    }

                    if (!hasLock)
                        throw new IOException("等待配置文件写入锁超时");

                    var data = JObject.FromObject(this);
                    MergeUartProfilesForSave(settingsPath, data);
                    File.WriteAllText(tempPath, data.ToString(Formatting.None));
                    File.Copy(tempPath, settingsPath, true);
                    _uartProfilesPendingWrite.Clear();
                }
                finally
                {
                    if (hasLock)
                        mutex.ReleaseMutex();
                    try
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void MergeUartProfilesForSave(string settingsPath, JObject data)
        {
            var currentProfiles = data["uartProfiles"] as JObject ?? new JObject();
            var mergedProfiles = new JObject();

            try
            {
                if (File.Exists(settingsPath))
                {
                    var diskData = JObject.Parse(File.ReadAllText(settingsPath));
                    if (diskData["uartProfiles"] is JObject diskProfiles)
                    {
                        foreach (var profile in diskProfiles.Properties())
                            mergedProfiles[profile.Name] = profile.Value.DeepClone();
                    }
                }
            }
            catch
            {
            }

            foreach (var profile in currentProfiles.Properties())
            {
                if (mergedProfiles.Property(profile.Name) == null)
                    mergedProfiles[profile.Name] = profile.Value.DeepClone();
            }

            foreach (var profileName in _uartProfilesPendingWrite.ToList())
            {
                var profile = currentProfiles.Properties()
                    .FirstOrDefault(i => string.Equals(i.Name, profileName, StringComparison.OrdinalIgnoreCase));
                if (profile != null)
                    mergedProfiles[profileName] = profile.Value.DeepClone();
            }

            data["uartProfiles"] = mergedProfiles;
        }

        private static string GetProfileMutexKey(string profilePath)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(profilePath ?? ""))).Replace("-", "");
            }
        }

        [JsonIgnore]
        public string ActiveUartProfileName => _activeUartProfileName;

        public void EnsureRuntimeState()
        {
            EnsureUartProfiles();
            EnsureQuickSendListState();
            MigrateUartProfiles();
        }

        private void MigrateUartProfiles()
        {
            if (uartProfileSchemaVersion >= CurrentUartProfileSchemaVersion)
                return;

            // v1：旧版把 DTR=true 作为所有串口的默认值；部分设备会因此在
            // 打开时复位。只对尚未完成 v1 迁移的配置执行一次。
            foreach (var entry in uartProfiles.Where(i => !string.IsNullOrWhiteSpace(i.Key) && i.Value != null))
            {
                if (uartProfileSchemaVersion < 1 && entry.Value.dtr && !entry.Value.rts)
                    entry.Value.dtr = false;

                // v2：发送框内容改为全局只保存当前一份。标记所有端口配置重写，
                // 让旧 JSON 中每个 COM 下残留的 dataToSend 字段被清除。
                _uartProfilesPendingWrite.Add(NormalizePortName(entry.Key));
            }

            uartProfileSchemaVersion = CurrentUartProfileSchemaVersion;
            Save(false);
        }

        public void SetActiveUartProfile(string portName)
        {
            var normalizedPortName = NormalizePortName(portName);
            if (string.IsNullOrWhiteSpace(normalizedPortName))
                return;

            EnsureUartProfiles();
            if (string.Equals(_activeUartProfileName, normalizedPortName, StringComparison.OrdinalIgnoreCase))
            {
                CopyCurrentToActiveUartProfile(true);
                Save();
                return;
            }

            CopyCurrentToActiveUartProfile(true);
            _activeUartProfileName = normalizedPortName;
            if (!uartProfiles.ContainsKey(normalizedPortName) || uartProfiles[normalizedPortName] == null)
                uartProfiles[normalizedPortName] = CreateUartProfileFromCurrent();

            ApplyUartProfile(uartProfiles[normalizedPortName]);
            _uartProfilesPendingWrite.Add(normalizedPortName);
            Save();
            Tools.Global.NotifyUartProfileChanged();
        }

        public void SaveActiveUartProfile()
        {
            CopyCurrentToActiveUartProfile(true);
            Save();
        }

        public UartPortProfile GetUartProfileForPort(string portName)
        {
            var normalizedPortName = NormalizePortName(portName);
            if (string.IsNullOrWhiteSpace(normalizedPortName))
                return null;

            EnsureUartProfiles();
            if (!uartProfiles.ContainsKey(normalizedPortName) || uartProfiles[normalizedPortName] == null)
                uartProfiles[normalizedPortName] = CreateUartProfileFromCurrent();

            return uartProfiles[normalizedPortName];
        }

        public void SaveUartProfileForPort(string portName, int baudRate, bool hexSend, bool rts, bool dtr)
        {
            var normalizedPortName = NormalizePortName(portName);
            if (string.IsNullOrWhiteSpace(normalizedPortName))
                return;

            EnsureUartProfiles();
            if (!uartProfiles.ContainsKey(normalizedPortName) || uartProfiles[normalizedPortName] == null)
                uartProfiles[normalizedPortName] = CreateUartProfileFromCurrent();

            var profile = uartProfiles[normalizedPortName];
            profile.baudRate = baudRate > 0 ? baudRate : 115200;
            profile.hexSend = hexSend;
            profile.rts = rts;
            profile.dtr = dtr;
            _uartProfilesPendingWrite.Add(normalizedPortName);
            Save(false);
        }

        private static string NormalizePortName(string portName)
        {
            return string.IsNullOrWhiteSpace(portName) ? "" : portName.Trim().ToUpperInvariant();
        }

        private void EnsureUartProfiles()
        {
            if (uartProfiles == null)
            {
                uartProfiles = new Dictionary<string, UartPortProfile>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            if (uartProfiles.Comparer == StringComparer.OrdinalIgnoreCase)
                return;

            uartProfiles = uartProfiles
                .Where(i => !string.IsNullOrWhiteSpace(i.Key) && i.Value != null)
                .GroupBy(i => NormalizePortName(i.Key))
                .ToDictionary(i => i.Key, i => i.Last().Value, StringComparer.OrdinalIgnoreCase);
        }

        private void CopyCurrentToActiveUartProfile(bool markPendingWrite)
        {
            var portName = NormalizePortName(_activeUartProfileName);
            if (string.IsNullOrWhiteSpace(portName))
                return;

            EnsureUartProfiles();
            uartProfiles[portName] = CreateUartProfileFromCurrent();
            if (markPendingWrite)
                _uartProfilesPendingWrite.Add(portName);
        }

        private UartPortProfile CreateUartProfileFromCurrent()
        {
            return new UartPortProfile
            {
                baudRate = _baudRate,
                showHexFormat = _showHexFormat,
                hexSend = _hexSend,
                showSend = _showSend,
                showSendRaw = _showSendRaw,
                parity = _parity,
                timeout = _timeout,
                dataBits = _dataBits,
                stopBit = _stopBit,
                flowControl = _flowControl,
                sendThrottlePacketSize = _sendThrottlePacketSize,
                sendThrottleDelayMs = _sendThrottleDelayMs,
                bitDelay = _bitDelay,
                maxLength = _maxLength,
                sendScript = _sendScript,
                recvScript = _recvScript,
                terminal = _terminal,
                encoding = _encoding,
                extraEnter = _extraEnter,
                enterSend = _enterSend,
                enableSymbol = _enableSymbol,
                rts = Tools.Global.uart?.Rts ?? false,
                dtr = Tools.Global.uart?.Dtr ?? false
            };
        }

        private void ApplyUartProfile(UartPortProfile profile)
        {
            if (profile == null)
                return;

            _suspendSave = true;
            try
            {
                baudRate = profile.baudRate > 0 ? profile.baudRate : 115200;
                showHexFormat = profile.showHexFormat < 0 || profile.showHexFormat > 2 ? 0 : profile.showHexFormat;
                hexSend = profile.hexSend;
                showSend = profile.showSend;
                showSendRaw = profile.showSendRaw;
                parity = profile.parity < 0 || profile.parity > 4 ? 0 : profile.parity;
                timeout = profile.timeout;
                dataBits = profile.dataBits < 5 || profile.dataBits > 8 ? 8 : profile.dataBits;
                stopBit = profile.stopBit < 1 || profile.stopBit > 3 ? 1 : profile.stopBit;
                flowControl = profile.flowControl < 0 || profile.flowControl > 2 ? 0 : profile.flowControl;
                sendThrottlePacketSize = Math.Max(0, profile.sendThrottlePacketSize);
                sendThrottleDelayMs = Math.Max(0, profile.sendThrottleDelayMs);
                bitDelay = profile.bitDelay;
                maxLength = profile.maxLength == 0 ? 10240 : profile.maxLength;
                sendScript = string.IsNullOrWhiteSpace(profile.sendScript) ? "default" : profile.sendScript;
                recvScript = string.IsNullOrWhiteSpace(profile.recvScript) ? "default" : profile.recvScript;
                terminal = profile.terminal;
                encoding = profile.encoding > 0 ? profile.encoding : 65001;
                extraEnter = profile.extraEnter;
                enterSend = profile.enterSend;
                EnableSymbol = profile.enableSymbol;
                if (Tools.Global.uart != null)
                {
                    Tools.Global.uart.Rts = profile.rts;
                    Tools.Global.uart.Dtr = profile.dtr;
                    Tools.Global.uart.ApplyFlowControl();
                }
            }
            finally
            {
                _suspendSave = false;
            }
        }

        /// <summary>
        /// 串口接收每包最大长度
        /// </summary>
        public uint maxLength
        {
            get
            {
                return _maxLength;
            }
            set
            {
                _maxLength = value;
                Save();
            }
        }

        /// <summary>
        /// 当前选中的快捷发送列表数据
        /// </summary>
        [JsonIgnore]
        public List<ToSendData> quickSend
        {
            get
            {
                EnsureQuickSendListState();
                return quickSendList[_quickSendSelect];
            }
            set
            {
                EnsureQuickSendListState();
                quickSendList[_quickSendSelect] = value ?? CreateDefaultQuickSendRows();
                NormalizeQuickSendListRows();
                Save();
            }
        }

        private void EnsureQuickSendListState()
        {
            if (quickSendList == null)
                quickSendList = new List<List<ToSendData>>();
            if (quickSendList.Count == 0)
                quickSendList.Add(CreateDefaultQuickSendRows());

            EnsureQuickListNames();

            if (_quickSendSelect < 0 || _quickSendSelect >= quickSendList.Count)
                _quickSendSelect = 0;

            NormalizeQuickSendListRows();
        }

        private void NormalizeQuickSendListRows()
        {
            for (int i = 0; i < quickSendList.Count; i++)
            {
                var list = quickSendList[i];
                if (list == null || list.Count == 0)
                {
                    quickSendList[i] = CreateDefaultQuickSendRows();
                    continue;
                }

                var blankRows = 0;
                var normalized = new List<ToSendData>();
                foreach (var item in list.Where(item => item != null))
                {
                    if (IsBlankQuickSendRow(item))
                    {
                        if (blankRows >= DefaultQuickSendRows)
                            continue;

                        blankRows++;
                    }

                    normalized.Add(item);
                }

                quickSendList[i] = normalized.Count == 0 ? CreateDefaultQuickSendRows() : normalized;
            }
        }

        private bool IsBlankQuickSendRow(ToSendData item)
        {
            return item == null ||
                   (string.IsNullOrWhiteSpace(item.text) &&
                    !item.hex &&
                    !item.appendCrlf &&
                    string.IsNullOrWhiteSpace(item.recvScriptPath) &&
                    string.IsNullOrWhiteSpace(item.recvScriptPara));
        }

        private void EnsureQuickListNames()
        {
            if (quickListNames == null)
                quickListNames = new List<string>();

            if (quickListNames.Count == 0)
                quickListNames.AddRange(GetLegacyQuickListNames());

            while (quickListNames.Count < quickSendList.Count)
                quickListNames.Add(GetDefaultQuickListName(quickListNames.Count));

            if (quickListNames.Count > quickSendList.Count)
                quickListNames.RemoveRange(quickSendList.Count, quickListNames.Count - quickSendList.Count);

            for (int i = 0; i < quickListNames.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(quickListNames[i]))
                    quickListNames[i] = GetDefaultQuickListName(i);
            }
        }

        private string GetDefaultQuickListName(int index)
        {
            return $"未命名{index}";
        }

        private List<string> GetLegacyQuickListNames()
        {
            return new List<string>
            {
                quickListName0,
                quickListName1,
                quickListName2,
                quickListName3,
                quickListName4,
                quickListName5,
                quickListName6,
                quickListName7,
                quickListName8,
                quickListName9,
            };
        }

        public List<List<ToSendData>> GetAllQuickSendLists()
        {
            EnsureQuickSendListState();
            return quickSendList;
        }

        public void SetAllQuickSendLists(List<List<ToSendData>> data)
        {
            quickSendList = data ?? new List<List<ToSendData>>();
            EnsureQuickSendListState();
            Save();
        }

        public List<string> GetAllQuickListNames()
        {
            EnsureQuickSendListState();
            return quickListNames.Take(quickSendList.Count).ToList();
        }

        public void SetAllQuickListNames(IList<string> names)
        {
            if (names != null)
                quickListNames = names.ToList();
            EnsureQuickSendListState();
            Save();
        }

        public int GetQuickSendListCount()
        {
            EnsureQuickSendListState();
            return quickSendList.Count;
        }

        public int AddQuickSendPage()
        {
            EnsureQuickSendListState();
            quickSendList.Add(CreateDefaultQuickSendRows());
            quickListNames.Add(GetDefaultQuickListName(quickSendList.Count - 1));
            _quickSendSelect = quickSendList.Count - 1;
            Save();
            return _quickSendSelect;
        }

        private List<ToSendData> CreateDefaultQuickSendRows()
        {
            var rows = new List<ToSendData>();
            for (int i = 0; i < DefaultQuickSendRows; i++)
            {
                rows.Add(new ToSendData
                {
                    id = i + 1,
                    text = "",
                    hex = false,
                    appendCrlf = false,
                    commit = ""
                });
            }
            return rows;
        }

        public bool RemoveQuickSendPage(int index)
        {
            EnsureQuickSendListState();
            if (quickSendList.Count <= 1 || index < 0 || index >= quickSendList.Count)
                return false;

            quickSendList.RemoveAt(index);
            if (index < quickListNames.Count)
                quickListNames.RemoveAt(index);
            if (_quickSendSelect >= quickSendList.Count)
                _quickSendSelect = quickSendList.Count - 1;
            if (_quickSendSelect < 0)
                _quickSendSelect = 0;
            Save();
            return true;
        }

        /// <summary>
        /// 当前选中的快速发送列表编号
        /// </summary>
        public int quickSendSelect
        {
            get
            {
                return _quickSendSelect;
            }
            set
            {
                EnsureQuickSendListState();
                _quickSendSelect = value < 0 || value >= quickSendList.Count ? 0 : value;
                Save();
            }
        }

        public bool bitDelay
        {
            get
            {
                return _bitDelay;
            }
            set
            {
                _bitDelay = value;
                Save();
            }
        }

        /// <summary>
        /// 0 无流控，1 RTS/CTS硬件流控，2 XON/XOFF软件流控
        /// </summary>
        public int flowControl
        {
            get
            {
                return _flowControl;
            }
            set
            {
                try
                {
                    _flowControl = value < 0 || value > 2 ? 0 : value;
                    Tools.Global.uart.ApplyFlowControl();
                    Save();
                }
                catch (Exception e)
                {
                    Tools.MessageBox.Show(e.Message);
                }
            }
        }

        public int sendThrottlePacketSize
        {
            get
            {
                return _sendThrottlePacketSize;
            }
            set
            {
                _sendThrottlePacketSize = value < 0 ? 0 : value;
                if (_sendThrottlePacketSize == 0 && _sendThrottleDelayMs != 0)
                    sendThrottleDelayMs = 0;
                Save();
            }
        }

        public int sendThrottleDelayMs
        {
            get
            {
                return _sendThrottleDelayMs;
            }
            set
            {
                if (value < 0)
                    value = 0;
                if (_sendThrottlePacketSize == 0)
                    value = 0;
                if (value > 10000)
                    value = 10000;
                _sendThrottleDelayMs = value;
                Save();
            }
        }

        public string dataToSend
        {
            get
            {
                return _dataToSend;
            }
            set
            {
                _dataToSend = value;
                Save();
            }
        }
        public int baudRate
        {
            get
            {
                return _baudRate;
            }
            set
            {
                try
                {
                    Tools.Global.uart.SetBaudRate(value);
                    _baudRate = value;
                    Save();
                }
                catch(Exception e)
                {
                    Tools.MessageBox.Show(e.Message);
                }
            }
        }

        public bool autoReconnect
        {
            get
            {
                return _autoReconnect;
            }
            set
            {
                _autoReconnect = value;
                Save();
            }
        }

        public bool autoSaveLog
        {
            get
            {
                return _autoSaveLog;
            }
            set
            {
                _autoSaveLog = value;
                Save();
            }
        }

        /// <summary>
        /// 串口数据显示格式
        /// 0 都显示
        /// 1 只显示字符串
        /// 2 只显示Hex
        /// </summary>
        public int showHexFormat
        {
            get
            {
                return _showHexFormat;
            }
            set
            {
                _showHexFormat = value;
                Save();
            }
        }

        /// <summary>
        /// 主数据发送框是否发hex
        /// </summary>
        public bool hexSend
        {
            get
            {
                return _hexSend;
            }
            set
            {
                _hexSend = value;
                Save();
            }
        }

        public bool showSend
        {
            get
            {
                return _showSend;
            }
            set
            {
                _showSend = value;
                Save();
            }
        }

        public bool showSendRaw
        {
            get
            {
                return _showSendRaw;
            }
            set
            {
                _showSendRaw = value;
                Save();
            }
        }

        public int parity
        {
            get
            {
                return _parity;
            }
            set
            {
                try
                {
                    _parity = value;
                    Tools.Global.uart.SetParity((Parity)value);
                    Save();
                }
                catch (Exception e)
                {
                    Tools.MessageBox.Show(e.Message);
                }
            }
        }

        public int timeout
        {
            get
            {
                return _timeout;
            }
            set
            {
                _timeout = value;
                Save();
            }
        }

        public int dataBits
        {
            get
            {
                return _dataBits;
            }
            set
            {
                try
                {
                    _dataBits = value;
                    Tools.Global.uart.SetDataBits(value);
                    Save();
                }
                catch (Exception e)
                {
                    Tools.MessageBox.Show(e.Message);
                }
            }
        }

        public int stopBit
        {
            get
            {
                return _stopBit;
            }
            set
            {
                try
                {
                    _stopBit = value;
                    Tools.Global.uart.SetStopBits((StopBits)value);
                    Save();
                }
                catch (Exception e)
                {
                    Tools.MessageBox.Show(e.Message);
                }
            }
        }

        public string sendScript
        {
            get
            {
                return _sendScript;
            }
            set
            {
                _sendScript = value;
                Save();
            }
        }

        public string recvScript
        {
            get
            {
                return _recvScript;
            }
            set
            {
                _recvScript = value;
                Save();
            }
        }

        public string runScript
        {
            get
            {
                return _runScript;
            }
            set
            {
                _runScript = value;
                Save();
            }
        }

        public bool topmost
        {
            get
            {
                return _topmost;
            }
            set
            {
                _topmost = value;
                try
                {
                    MainWindowTop(value, EventArgs.Empty);
                }
                catch { }
                Save();
            }
        }

        public bool terminal
        {
            get
            {
                return _terminal;
            }
            set
            {
                _terminal = value;
                Save();
            }
        }

        public string language
        {
            get
            {
                return _language;
            }
            set
            {
                _language = value;
                Tools.Global.LoadLanguageFile(value);
                Save();
            }
        }

        public int encoding
        {
            get
            {
                return _encoding;
            }
            set
            {
                try
                {
                    Encoding.GetEncoding(value);
                    _encoding = value;
                    Save();
                }
                catch { }//获取出错说明编码不对
            }
        }

        public bool extraEnter
        {
            get
            {
                return _extraEnter;
            }
            set
            {
                _extraEnter = value;
                Save();
            }
        }

        public bool enterSend
        {
            get
            {
                return _enterSend;
            }
            set
            {
                _enterSend = value;
                Save();
            }
        }

        public bool DisableLog { get; set; } = false;

        public bool EnableSymbol
        {
            get => _enableSymbol;
            set
            {
                _enableSymbol = value;
                Save();
            }
        }

        public bool sessionLogEnabled
        {
            get => _sessionLogEnabled;
            set
            {
                _sessionLogEnabled = value;
                Save();
            }
        }

        public string sessionLogFolder
        {
            get => _sessionLogFolder;
            set
            {
                _sessionLogFolder = value ?? "";
                Save();
            }
        }

        public bool darkMode
        {
            get => _darkMode;
            set
            {
                _darkMode = value;
                Save();
            }
        }

        private string _mqttServer = "broker.emqx.io";
        private int _mqttPort = 1883;
        private string _mqttClientID = Guid.NewGuid().ToString();
        private bool _mqttTLS = false;
        private bool _mqttTLSCert = false;
        private string _mqttTLSCertCaPath = "";
        private string _mqttTLSCertClientPath = "";
        private string _mqttTLSCertClientPassword = "";
        private bool _mqttWs = false;
        private string _mqttWsPath = "/mqtt";
        private string _mqttUser = "user";
        private string _mqttPassword = "password";
        private int _mqttKeepAlive = 120;
        private bool _mqttCleanSession = false;
        private string _mqttPublishTopic = "your/publish/topic";
        private string _mqttSubscribeTopic = "your/subcribe/topic";
        public string mqttServer { get { return _mqttServer; } set { _mqttServer = value; Save(); } }
        public int mqttPort { get { return _mqttPort; } set { _mqttPort = value; Save(); } }
        public string mqttClientID { get { return _mqttClientID; } set { _mqttClientID = value; Save(); } }
        public bool mqttTLS { get { return _mqttTLS; } set { _mqttTLS = value; Save(); } }
        public bool mqttTLSCert { get { return _mqttTLSCert; } set { _mqttTLSCert = value; Save(); } }
        public string mqttTLSCertCaPath { get { return _mqttTLSCertCaPath; } set { _mqttTLSCertCaPath = value; Save(); } }
        public string mqttTLSCertClientPath { get { return _mqttTLSCertClientPath; } set { _mqttTLSCertClientPath = value; Save(); } }
        public string mqttTLSCertClientPassword { get { return _mqttTLSCertClientPassword; } set { _mqttTLSCertClientPassword = value; Save(); } }
        public bool mqttWs { get { return _mqttWs; } set { _mqttWs = value; Save(); } }
        public string mqttWsPath { get { return _mqttWsPath; } set { _mqttWsPath = value; Save(); } }
        public string mqttUser { get { return _mqttUser; } set { _mqttUser = value; Save(); } }
        public string mqttPassword { get { return _mqttPassword; } set { _mqttPassword = value; Save(); } }
        public int mqttKeepAlive { get { return _mqttKeepAlive; } set { _mqttKeepAlive = value; Save(); } }
        public bool mqttCleanSession { get { return _mqttCleanSession; } set { _mqttCleanSession = value; Save(); } }
        public string mqttPublishTopic { get { return _mqttPublishTopic; } set { _mqttPublishTopic = value; Save(); } }
        public string mqttSubscribeTopic { get { return _mqttSubscribeTopic; } set { _mqttSubscribeTopic = value; Save(); } }


        private string _quickListName0 = "未命名0";
        public string quickListName0 { get { return _quickListName0; } set { _quickListName0 = value; Save(); } }

        private string _quickListName1 = "未命名1";
        public string quickListName1 { get { return _quickListName1; } set { _quickListName1 = value; Save(); } }

        private string _quickListName2 = "未命名2";
        public string quickListName2 { get { return _quickListName2; } set { _quickListName2 = value; Save(); } }

        private string _quickListName3 = "未命名3";
        public string quickListName3 { get { return _quickListName3; } set { _quickListName3 = value; Save(); } }

        private string _quickListName4 = "未命名4";
        public string quickListName4 { get { return _quickListName4; } set { _quickListName4 = value; Save(); } }

        private string _quickListName5 = "未命名5";
        public string quickListName5 { get { return _quickListName5; } set { _quickListName5 = value; Save(); } }

        private string _quickListName6 = "未命名6";
        public string quickListName6 { get { return _quickListName6; } set { _quickListName6 = value; Save(); } }

        private string _quickListName7 = "未命名7";
        public string quickListName7 { get { return _quickListName7; } set { _quickListName7 = value; Save(); } }

        private string _quickListName8 = "未命名8";
        public string quickListName8 { get { return _quickListName8; } set { _quickListName8 = value; Save(); } }

        private string _quickListName9 = "未命名9";
        public string quickListName9 { get { return _quickListName9; } set { _quickListName9 = value; Save(); } }

        public string GetQuickListNameNow()
        {
            EnsureQuickSendListState();
            return quickListNames[_quickSendSelect];
        }

        public void SetQuickListNameNow(string name)
        {
            EnsureQuickSendListState();
            quickListNames[_quickSendSelect] = string.IsNullOrWhiteSpace(name) ? GetDefaultQuickListName(_quickSendSelect) : name;
            Save();
        }




        private string _tcpClientServer = "qq.com";
        private int _tcpClientPort = 80;
        private int _tcpClientProtocolType = 0;
        private string _tcpClientDnsServer = "223.5.5.5";
        private string _tcpClientDnsDomain = "qq.com";
        private int _tcpClientDnsAddressType = 0;
        private string _tcpClientSshUserName = "";
        private string _tcpClientSshPrivateKeyPath = "";
        private string _tcpClientSshExtraArguments = "";
        private string _tcpClientSshPath = "";
        private int _serialSplitScreenCount = 1;
        public string tcpClientServer { get { return _tcpClientServer; } set { _tcpClientServer = value; Save(); } }
        public int tcpClientPort { get { return _tcpClientPort; } set { _tcpClientPort = value; Save(); } }
        public int tcpClientProtocolType { get { return _tcpClientProtocolType; } set { _tcpClientProtocolType = value; Save(); } }
        public string tcpClientDnsServer { get { return _tcpClientDnsServer; } set { _tcpClientDnsServer = value; Save(); } }
        public string tcpClientDnsDomain { get { return _tcpClientDnsDomain; } set { _tcpClientDnsDomain = value; Save(); } }
        public int tcpClientDnsAddressType { get { return _tcpClientDnsAddressType; } set { _tcpClientDnsAddressType = Math.Max(0, Math.Min(2, value)); Save(); } }
        public string tcpClientSshUserName { get { return _tcpClientSshUserName; } set { _tcpClientSshUserName = value; Save(); } }
        public string tcpClientSshPrivateKeyPath { get { return _tcpClientSshPrivateKeyPath; } set { _tcpClientSshPrivateKeyPath = value; Save(); } }
        public string tcpClientSshExtraArguments { get { return _tcpClientSshExtraArguments; } set { _tcpClientSshExtraArguments = value; Save(); } }
        public string tcpClientSshPath { get { return _tcpClientSshPath; } set { _tcpClientSshPath = value; Save(); } }
        public int serialSplitScreenCount
        {
            get { return Math.Max(1, Math.Min(4, _serialSplitScreenCount)); }
            set
            {
                var normalized = Math.Max(1, Math.Min(4, value));
                if (_serialSplitScreenCount == normalized)
                    return;

                _serialSplitScreenCount = normalized;
                Save();
                Tools.Global.NotifySerialSplitScreenChanged();
            }
        }

        private int _tcpClientSslAuthMode = 0;
        private int _tcpClientSslProtocolType = 0;
        private string _tcpClientSslTargetHost = "";
        private string _tcpClientSslCaCertPath = "";
        private string _tcpClientSslClientCertPath = "";
        private string _tcpClientSslClientCertPassword = "";
        private string _tcpClientSslCipherSuites = "";
        private bool _tcpClientSslCheckRevocation = false;
        private bool _tcpClientSslPrintDetails = true;
        public int tcpClientSslAuthMode { get { return _tcpClientSslAuthMode; } set { _tcpClientSslAuthMode = value; Save(); } }
        public int tcpClientSslProtocolType { get { return _tcpClientSslProtocolType; } set { _tcpClientSslProtocolType = value; Save(); } }
        public string tcpClientSslTargetHost { get { return _tcpClientSslTargetHost; } set { _tcpClientSslTargetHost = value; Save(); } }
        public string tcpClientSslCaCertPath { get { return _tcpClientSslCaCertPath; } set { _tcpClientSslCaCertPath = value; Save(); } }
        public string tcpClientSslClientCertPath { get { return _tcpClientSslClientCertPath; } set { _tcpClientSslClientCertPath = value; Save(); } }
        public string tcpClientSslClientCertPassword { get { return _tcpClientSslClientCertPassword; } set { _tcpClientSslClientCertPassword = value; Save(); } }
        public string tcpClientSslCipherSuites { get { return _tcpClientSslCipherSuites; } set { _tcpClientSslCipherSuites = value; Save(); } }
        public bool tcpClientSslCheckRevocation { get { return _tcpClientSslCheckRevocation; } set { _tcpClientSslCheckRevocation = value; Save(); } }
        public bool tcpClientSslPrintDetails { get { return _tcpClientSslPrintDetails; } set { _tcpClientSslPrintDetails = value; Save(); } }
        private string _tcpClientSslClientKeyPath = "";
        private string _openSslPath = "";
        public string tcpClientSslClientKeyPath { get { return _tcpClientSslClientKeyPath; } set { _tcpClientSslClientKeyPath = value; Save(); } }
        public string openSslPath { get { return _openSslPath; } set { _openSslPath = value; Save(); } }

        private bool _tcpReconnect = false;
        public bool tcpReconnect { get { return _tcpReconnect; } set { _tcpReconnect = value; Save(); } }
        private int _tcpReconnectInterval = 5;
        public int tcpReconnectInterval { get { return _tcpReconnectInterval; } set { _tcpReconnectInterval = value; Save(); } }

        private bool _scriptTestHex = false;
        private bool _scriptTestHexRev = false;
        public bool scriptTestHex { get { return _scriptTestHex; } set { _scriptTestHex = value; Save(); } }
        public bool scriptTestHexRev { get { return _scriptTestHexRev; } set { _scriptTestHexRev = value; Save(); } }
    }
}
