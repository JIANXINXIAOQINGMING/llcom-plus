using llcom_plus.Tools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace llcom_plus.Pages
{
    public partial class Ch347BusPage : Page, IDisposable
    {
        private const int MaximumLogLines = 1000;
        private const int MaximumLogBytes = 1024 * 1024;
        private readonly Ch347BusService service;
        private readonly Ch347PresetStore presetStore;
        private readonly bool refreshOnLoaded;
        private readonly Application application;
        private readonly List<Ch347BusPreset> presets = new List<Ch347BusPreset>();
        private readonly object logGate = new object();
        private readonly LinkedList<string> logLines = new LinkedList<string>();
        private CancellationTokenSource operationCancellation;
        private int logBytes;
        private int disposeState;
        private bool logDispatchPending;
        private bool initialized;
        private bool loaded;
        private bool busy;
        private bool periodicRunning;
        private bool updatingUi;
        private bool presetsAvailable = true;
        private bool loadingPreset;

        public Ch347BusPage() : this(new Ch347BusService(), new Ch347PresetStore(), true) { }

        // Tests inject a fake-backed service and a temporary preset store. Loading
        // that page never enumerates or opens a physical device by default.
        internal Ch347BusPage(Ch347BusService service, Ch347PresetStore presetStore = null, bool refreshOnLoaded = false)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.presetStore = presetStore ?? new Ch347PresetStore();
            this.refreshOnLoaded = refreshOnLoaded;
            InitializeComponent();
            Localize();
            initialized = true;
            service.Logged += Service_Logged;
            service.StateChanged += Service_StateChanged;
            application = Application.Current;
            if (application != null) application.Exit += Application_Exit;
            Global.ProgramClosedEvent += ProgramClosed;
            LoadPresets();
            SetStatus(Text("Ready", "就绪", "Ready"));
            UpdateUi();
        }

        private bool IsDisposed => Volatile.Read(ref disposeState) != 0;

        private string Text(string key, string chinese, string english)
        {
            var language = Global.setting?.language ?? CultureInfo.CurrentUICulture.Name;
            var value = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
            Resources["Ch347" + key] = value;
            return value;
        }

        private void Localize()
        {
            Text("Title", "CH347 总线调试", "CH347 bus debugger");
            Text("Intro", "I²C / SPI / JTAG 调试 · 请先确认接线、电平和目标芯片手册。", "I²C / SPI / JTAG · Check wiring, voltage and the target datasheet first.");
            Text("Refresh", "刷新", "Refresh");
            Text("Connect", "连接", "Connect");
            Text("Disconnect", "断开", "Disconnect");
            Text("Presets", "常用操作 · 选择只加载参数", "Presets · select to load parameters");
            Text("Dummy", "补发字节 HEX", "Dummy byte HEX");
            Text("Delete", "删除", "Delete");
            Text("PresetName", "常用操作名称（最多 80 字符）", "Preset name (up to 80 characters)");
            Text("SavePreset", "保存操作", "Save preset");
            Text("I2cAddress", "地址 HEX（7 位）", "7-bit address HEX");
            Text("Speed", "速率", "Speed");
            Text("ReadLength", "读取字节数", "Read bytes");
            Text("WriteHex", "写入 HEX（空格或换行分隔字节）", "Write HEX (bytes separated by spaces or new lines)");
            Text("Scan", "手动扫描地址…", "Scan addresses…");
            Text("I2cHint", "写入留空 = 只读；读取填 0 = 只写。", "Empty write = read only; read length 0 = write only.");
            Text("SpiMode", "SPI 模式", "SPI mode");
            Text("Clock", "时钟", "Clock");
            Text("BitOrder", "位序", "Bit order");
            Text("ChipSelect", "片选", "Chip select");
            Text("Operation", "操作", "Operation");
            Text("WriteOnly", "只写", "Write only");
            Text("ReadOnly", "只读", "Read only");
            Text("WriteRead", "先写后读（同一片选）", "Write then read (same CS)");
            Text("Exchange", "全双工交换（等长）", "Full-duplex exchange (equal length)");
            Text("SpiHint", "只读时仍输出补发字节；先写后读保持同一片选；交换读写等长。", "Reads still send dummy bytes. Write then read keeps CS asserted. Exchange reads as many bytes as it writes.");
            Text("JtagModeHint", "微雪 CH347T 请断电切换到 M3，并连接 TCK / TMS / TDI / TDO / GND。先执行 TAP 复位，再按目标手册交换 IR / DR；不驱动 TRST。", "For Waveshare CH347T, power off and select M3. Wire TCK / TMS / TDI / TDO / GND. Execute a TAP reset first, then shift IR / DR per the target datasheet. TRST is not driven.");
            Text("JtagReset", "TAP 复位 → Idle", "TAP reset → Idle");
            Text("JtagIr", "IR 指令寄存器交换", "Shift IR (instruction)");
            Text("JtagDr", "DR 数据寄存器交换", "Shift DR (data)");
            Text("JtagBits", "位数（bits）", "Bit count");
            Text("JtagTdi", "TDI 数据 HEX（首字节低位先发）", "TDI HEX (first byte, LSB first)");
            Text("JtagShiftHint", "数据字节数 = 位数向上除以 8，末字节仅低位有效。IR / DR 完成后回到 Idle 并更新寄存器。更换时钟、操作其它总线或出错后，需重新 TAP 复位及设置 IR；这不是芯片烧录或断点调试器。", "Supply ceil(bits / 8) bytes; only low bits of the last byte are valid. Each shift updates the register and returns to Idle. Reset TAP and reload IR after changing clock, using another bus or an error. This is not a flash programmer or breakpoint debugger.");
            Text("Timeout", "超时（ms）", "Timeout (ms)");
            Text("Interval", "周期间隔（ms）", "Interval (ms)");
            Text("Periodic", "周期读取（勾选后手动执行）", "Periodic reads (check, then execute)");
            Text("PeriodicHint", "仅允许只读。上次读取完成后等待间隔再读取；取消、失败或离开本页即停止。", "Read-only operations only. Waits after each completed read; stops on cancel, failure or leaving this page.");
            Text("Execute", "执行", "Execute");
            Text("Cancel", "取消 / 停止", "Cancel / stop");
            Text("Readback", "读取结果 HEX", "Read result HEX");
            Text("Log", "总线日志", "Bus log");
            Text("SaveLog", "保存日志", "Save log");
            Text("ClearLog", "清空", "Clear");
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (IsDisposed) return;
            Localize();
            UpdateUi();
            if (loaded) return;
            loaded = true;
            if (refreshOnLoaded) await RefreshDevicesAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            // Toolbox pages are cached: navigation keeps an explicit connection.
            // Repeated reads, however, must not continue from a hidden page.
            if (periodicRunning) operationCancellation?.Cancel();
            PeriodicCheckBox.IsChecked = false;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

        private async Task RefreshDevicesAsync()
        {
            await RunOperationAsync(async token =>
            {
                var selected = (DeviceComboBox.SelectedItem as DeviceChoice)?.Device.Identity;
                SetStatus(Text("Discovering", "正在查找 CH347…", "Finding CH347 devices…"));
                var devices = await service.EnumerateAsync(token);
                token.ThrowIfCancellationRequested();
                if (IsDisposed) return;
                var choices = devices.Select(device => new DeviceChoice(device)).ToArray();
                DeviceComboBox.ItemsSource = choices;
                DeviceComboBox.SelectedItem = choices.FirstOrDefault(item => item.Device.Identity == selected) ?? choices.FirstOrDefault();
                SetStatus(choices.Length == 0
                    ? Text("NoDevices", "未发现支持的 CH347 设备。", "No supported CH347 devices found.")
                    : Text("DevicesFound", "发现设备：", "Devices found: ") + choices.Length);
            });
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = DeviceComboBox.SelectedItem as DeviceChoice;
            if (selected == null) return;
            await RunOperationAsync(async token =>
            {
                SetStatus(Text("Connecting", "正在连接…", "Connecting…"));
                await service.ConnectAsync(selected.Device, token);
                SetStatus(Text("Connected", "已连接", "Connected"));
            });
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (busy || IsDisposed) return;
            PeriodicCheckBox.IsChecked = false;
            await RunOperationAsync(async token =>
            {
                await service.DisconnectAsync();
                SetStatus(Text("Disconnected", "已断开", "Disconnected"));
            });
        }

        private async Task RunOperationAsync(Func<CancellationToken, Task> operation, bool repeat = false)
        {
            // There is a single owner of all page work. Repeated clicks never
            // append requests to the service or create another periodic loop.
            if (busy || IsDisposed) return;
            busy = true;
            periodicRunning = repeat;
            var cancellation = new CancellationTokenSource();
            operationCancellation = cancellation;
            UpdateUi();
            try
            {
                await operation(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus(Text("Cancelled", "已取消；不会重发。", "Cancelled; no retry will be sent."));
            }
            catch (Exception ex)
            {
                var error = ex.GetBaseException();
                ShowError(error is Ch347JtagResetRequiredException
                    ? Text("JtagResetRequired", "请先选择并执行“ TAP 复位 → Idle ”。连接、更换时钟、操作其它总线或出错后，需要重新复位，再按目标手册设置 IR。", "Execute TAP reset → Idle first. Reset and reload IR after connecting, changing clock, using another bus or an error.")
                    : error is Ch347JtagSdkUnavailableException
                        ? Text("JtagSdkMissing", "当前 CH347 开发库缺少带设备索引的 JTAG 接口，请使用随程序附带的匹配位数开发库。I²C / SPI 不受影响。", "The SDK lacks indexed JTAG APIs. Use the matching-architecture SDK shipped with the application. I²C / SPI are unaffected.")
                        : error.Message);
            }
            finally
            {
                if (ReferenceEquals(operationCancellation, cancellation)) operationCancellation = null;
                cancellation.Dispose();
                periodicRunning = false;
                busy = false;
                if (!IsDisposed)
                {
                    if (repeat) PeriodicCheckBox.IsChecked = false;
                    UpdateUi();
                }
            }
        }

        private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (busy || IsDisposed || !service.IsConnected) return;
            Ch347TransferRequest request;
            int interval = 0;
            var repeat = PeriodicCheckBox.IsChecked == true;
            try
            {
                request = BuildRequest();
                if (repeat)
                {
                    if (!IsReadOnly(request)) throw new ArgumentException(Text("ReadOnlyRequired", "周期读取仅允许只读操作。", "Periodic reads require a read-only operation."));
                    interval = ParseInteger(IntervalTextBox.Text, 200, 3600000, Text("IntervalName", "周期间隔", "Interval"));
                }
            }
            catch (Exception ex) { ShowError(ex.Message); return; }

            if (request.Protocol == Ch347BusProtocol.Jtag &&
                !Confirm(Text("JtagConfirm", "JTAG 操作会产生时钟并改变目标 TAP / 寄存器，可能影响正在运行的设备。确认电平、接线和目标指令后执行？", "JTAG clocks and updates the target TAP/registers and may affect a running device. Have you verified the voltage, wiring and target instruction?") +
                    "\n" + request.JtagOperation + (request.JtagOperation == Ch347JtagOperation.Reset ? "" : " · " + request.JtagBitLength + " bits"))) return;

            await RunOperationAsync(async token =>
            {
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (!service.IsConnected) throw new InvalidOperationException(Text("ConnectionRequired", "请先连接设备。", "Connect a device first."));
                    SetStatus(Text("Running", "正在执行…", "Running…"));
                    ReadbackTextBox.Clear();
                    var result = await service.ExecuteAsync(request.Clone(), token);
                    token.ThrowIfCancellationRequested();
                    if (IsDisposed) return;
                    ReadbackTextBox.Text = Ch347TransferRequest.ToHex(result.ReadData);
                    var summary = (result.Success ? Text("Succeeded", "完成", "Completed") : Text("Failed", "失败", "Failed")) +
                        $" · {result.Duration.TotalMilliseconds:0.0} ms";
                    if (request.Protocol == Ch347BusProtocol.Jtag)
                        summary += " · " + (result.Success
                            ? request.JtagOperation == Ch347JtagOperation.Reset
                                ? Text("JtagResetDone", "TAP 已复位并进入 Idle；未控制 TRST。", "TAP reset to Idle; TRST was not driven.")
                                : Text("JtagShiftDone", "已交换并更新寄存器，回到 Idle。", "Register shifted and updated; returned to Idle.")
                            : Text("JtagFailed", "TAP 状态未知，请检查接线并重新复位。不会自动重发。", "TAP state unknown. Check wiring and reset explicitly; no automatic retry."));
                    else if (!string.IsNullOrWhiteSpace(result.Message)) summary += " · " + result.Message;
                    SetStatus(summary);
                    // A failed read stops the loop. A write is never retried.
                    if (!repeat || !result.Success || !service.IsConnected) break;
                    await Task.Delay(interval, token);
                } while (!token.IsCancellationRequested);
            }, repeat);
        }

        private async void ScanI2cButton_Click(object sender, RoutedEventArgs e)
        {
            if (busy || IsDisposed || !service.IsConnected) return;
            int timeout;
            try { timeout = ParseTimeout(); }
            catch (Exception ex) { ShowError(ex.Message); return; }
            if (!Confirm(Text("ScanConfirm", "扫描会向 08–77 地址发出探测，可能改变已连接设备的状态。确认接线和目标设备允许此操作后继续？", "Scanning probes addresses 08–77 and may change connected devices' state. Continue only if the wiring and target devices permit this operation?"))) return;
            var speed = I2cSpeedComboBox.SelectedIndex;
            await RunOperationAsync(async token =>
            {
                SetStatus(Text("Scanning", "正在扫描，可随时取消…", "Scanning; cancel to stop…"));
                I2cScanResultText.Text = "";
                var addresses = await service.ScanI2cAsync(speed, timeout, token);
                token.ThrowIfCancellationRequested();
                if (IsDisposed) return;
                var result = addresses.Count == 0 ? Text("NoAck", "未发现应答地址。", "No addresses acknowledged.") :
                    Text("AckAddresses", "应答地址（7 位）：", "Acknowledged addresses (7-bit): ") + string.Join(" ", addresses.Select(address => address.ToString("X2")));
                I2cScanResultText.Text = result;
                SetStatus(Text("ScanFinished", "扫描完成", "Scan completed"));
            });
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

        private void PeriodicCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized || IsDisposed) return;
            if (PeriodicCheckBox.IsChecked != true && periodicRunning) operationCancellation?.Cancel();
        }

        private Ch347TransferRequest BuildRequest()
        {
            var request = new Ch347TransferRequest { TimeoutMs = ParseTimeout() };
            if (ProtocolTabs.SelectedIndex == 0)
            {
                var addressText = (I2cAddressTextBox.Text ?? "").Trim();
                if (addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) addressText = addressText.Substring(2);
                if (!int.TryParse(addressText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address) || address < 0x08 || address > 0x77)
                    throw new ArgumentException(Text("BadAddress", "I²C 必须使用 08–77 的 7 位 HEX 地址。", "I²C requires a 7-bit HEX address from 08 to 77."));
                request.Protocol = Ch347BusProtocol.I2c;
                request.I2cAddress = address;
                request.I2cSpeedCode = I2cSpeedComboBox.SelectedIndex;
                request.WriteData = Ch347TransferRequest.ParseHex(I2cWriteTextBox.Text);
                request.ReadLength = ParseInteger(I2cReadLengthTextBox.Text, 0, 4096, Text("ReadLengthName", "读取字节数", "Read length"));
            }
            else if (ProtocolTabs.SelectedIndex == 1)
            {
                request.Protocol = Ch347BusProtocol.Spi;
                request.SpiMode = SpiModeComboBox.SelectedIndex;
                request.SpiClockCode = 7 - SpiClockComboBox.SelectedIndex;
                request.SpiLsbFirst = SpiBitOrderComboBox.SelectedIndex == 1;
                request.SpiChipSelect = SpiCsComboBox.SelectedIndex;
                var dummy = Ch347TransferRequest.ParseHex(SpiDummyTextBox.Text);
                if (dummy.Length != 1) throw new ArgumentException(Text("BadDummy", "补发字节必须是一字节 HEX，例如 FF。", "Dummy must be one HEX byte, for example FF."));
                request.SpiDummyByte = dummy[0];
                request.SpiOperation = (Ch347SpiOperation)SpiOperationComboBox.SelectedIndex;
                request.WriteData = request.SpiOperation == Ch347SpiOperation.ReadOnly ? Array.Empty<byte>() : Ch347TransferRequest.ParseHex(SpiWriteTextBox.Text);
                request.ReadLength = request.SpiOperation == Ch347SpiOperation.WriteOnly ? 0 :
                    request.SpiOperation == Ch347SpiOperation.Exchange ? request.WriteData.Length :
                    ParseInteger(SpiReadLengthTextBox.Text, 0, 4096, Text("ReadLengthName", "读取字节数", "Read length"));
            }
            else if (ProtocolTabs.SelectedIndex == 2)
            {
                request.Protocol = Ch347BusProtocol.Jtag;
                request.JtagOperation = (Ch347JtagOperation)JtagOperationComboBox.SelectedIndex;
                request.JtagClockCode = JtagClockComboBox.SelectedIndex;
                var reset = request.JtagOperation == Ch347JtagOperation.Reset;
                request.JtagBitLength = reset ? 0 : ParseInteger(JtagBitLengthTextBox.Text, 1, 32768, Text("JtagBits", "位数（bits）", "Bit count"));
                request.WriteData = reset ? Array.Empty<byte>() : Ch347TransferRequest.ParseHex(JtagWriteTextBox.Text);
                request.ReadLength = reset ? 0 : (request.JtagBitLength + 7) / 8;
                if (!reset && request.WriteData.Length != request.ReadLength)
                    throw new ArgumentException(Text("JtagLengthMismatch", "JTAG 数据字节数必须为位数除以 8 后向上取整。本次需要：", "JTAG requires exactly ceil(bits / 8) HEX bytes. Required: ") + request.ReadLength);
                var finalBits = request.JtagBitLength & 7;
                if (!reset && finalBits != 0 && (request.WriteData[request.WriteData.Length - 1] >> finalBits) != 0)
                    throw new ArgumentException(Text("JtagHighBits", "末字节未使用的高位必须为 0。例如 10 bit 可填 A5 03，不能填 A5 FF。", "Unused high bits in the final byte must be zero. For 10 bits, A5 03 is valid; A5 FF is not."));
            }
            else throw new ArgumentException("Select I2C, SPI or JTAG.");
            request.Validate();
            return request;
        }

        private int ParseTimeout() => ParseInteger(TimeoutTextBox.Text, 100, 10000, Text("TimeoutName", "超时", "Timeout"));

        private static int ParseInteger(string value, int minimum, int maximum, string field)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum)
                throw new ArgumentException($"{field}: {minimum}–{maximum}.");
            return number;
        }

        private static bool IsReadOnly(Ch347TransferRequest request) => request.ReadLength > 0 &&
            request.WriteData.Length == 0 && (request.Protocol == Ch347BusProtocol.I2c ||
                (request.Protocol == Ch347BusProtocol.Spi && request.SpiOperation == Ch347SpiOperation.ReadOnly));

        private void Inputs_Changed(object sender, RoutedEventArgs e)
        {
            if (initialized && !loadingPreset && !updatingUi) UpdateUi();
        }

        private void UpdateUi()
        {
            if (!initialized || IsDisposed || updatingUi) return;
            updatingUi = true;
            try
            {
                var connected = service.IsConnected;
                var device = service.ConnectedDevice;
                DeviceComboBox.IsEnabled = !busy && !connected;
                RefreshButton.IsEnabled = !busy && !connected;
                ConnectButton.IsEnabled = !busy && !connected && DeviceComboBox.SelectedItem != null;
                DisconnectButton.IsEnabled = !busy && connected;
                CancelButton.IsEnabled = busy;
                ConnectionStatusText.Text = connected ? Text("Connected", "已连接", "Connected") : Text("Disconnected", "已断开", "Disconnected");
                DeviceIdentityText.Text = connected ? device?.DevicePath ?? device?.DeviceId ?? "" : "";
                DeviceIdentityText.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
                // Keep the selected protocol even for an incompatible device or
                // preset; silently falling back could send on the wrong bus.
                var protocolSupported = !connected || device?.SupportsProtocol((Ch347BusProtocol)ProtocolTabs.SelectedIndex) == true;
                I2cInputs.IsEnabled = !busy;
                SpiInputs.IsEnabled = !busy;
                JtagInputs.IsEnabled = !busy;
                ProtocolTabs.IsEnabled = !busy;
                CommonInputs.IsEnabled = !busy;
                ExecuteButton.IsEnabled = !busy && connected && protocolSupported;
                ProtocolAvailabilityText.Visibility = protocolSupported ? Visibility.Collapsed : Visibility.Visible;
                ProtocolAvailabilityText.Text = protocolSupported ? "" : Text("ModeUnsupported", "当前设备模式不支持此协议。JTAG 请断电切换到 M3 后重新连接；SPI 请用 M1 / M2。", "This mode does not support the selected protocol. For JTAG, power off and select M3; for SPI use M1/M2, then reconnect.");
                ScanI2cButton.IsEnabled = !busy && connected && device?.SupportsI2c == true;
                PresetComboBox.IsEnabled = !busy && presetsAvailable;
                PresetNameTextBox.IsEnabled = !busy && presetsAvailable;
                SavePresetButton.IsEnabled = !busy && presetsAvailable;
                DeletePresetButton.IsEnabled = !busy && presetsAvailable && PresetComboBox.SelectedItem != null;
                var operation = (Ch347SpiOperation)SpiOperationComboBox.SelectedIndex;
                SpiWriteTextBox.IsEnabled = !busy && operation != Ch347SpiOperation.ReadOnly;
                SpiReadLengthTextBox.IsEnabled = !busy && (operation == Ch347SpiOperation.ReadOnly || operation == Ch347SpiOperation.WriteThenRead);
                var jtagReset = JtagOperationComboBox.SelectedIndex == 0;
                JtagBitLengthTextBox.IsEnabled = !busy && !jtagReset;
                JtagWriteTextBox.IsEnabled = !busy && !jtagReset;
                var periodicVisibility = ProtocolTabs.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
                IntervalField.Visibility = periodicVisibility;
                PeriodicCheckBox.Visibility = periodicVisibility;
                PeriodicHintText.Visibility = periodicVisibility;
                var repeatEligible = false;
                try { repeatEligible = IsReadOnly(BuildRequest()); } catch (ArgumentException) { }
                PeriodicCheckBox.IsEnabled = periodicRunning || (!busy && repeatEligible);
                if (!busy && !repeatEligible) PeriodicCheckBox.IsChecked = false;
            }
            finally { updatingUi = false; }
        }

        private void Service_StateChanged()
        {
            PostToUi(() =>
            {
                if (periodicRunning && !service.IsConnected) operationCancellation?.Cancel();
                UpdateUi();
            });
        }

        private void LoadPresets()
        {
            try
            {
                presets.AddRange(presetStore.Load());
                RefreshPresets(null);
            }
            catch (Exception ex)
            {
                // Preserve an unreadable file: do not replace it with an empty set.
                presetsAvailable = false;
                ShowError(Text("PresetLoadError", "常用操作加载失败：", "Unable to load presets: ") + ex.Message);
            }
        }

        private void RefreshPresets(string selectedName)
        {
            loadingPreset = true;
            try
            {
                PresetComboBox.ItemsSource = null;
                PresetComboBox.ItemsSource = presets;
                PresetComboBox.SelectedItem = presets.FirstOrDefault(item => string.Equals(item.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            }
            finally { loadingPreset = false; }
            UpdateUi();
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!initialized || loadingPreset || busy || IsDisposed) return;
            if (!(PresetComboBox.SelectedItem is Ch347BusPreset preset)) { UpdateUi(); return; }
            loadingPreset = true;
            try
            {
                var request = preset.Request.Clone();
                request.Validate();
                PeriodicCheckBox.IsChecked = false;
                PresetNameTextBox.Text = preset.Name;
                TimeoutTextBox.Text = request.TimeoutMs.ToString(CultureInfo.InvariantCulture);
                ProtocolTabs.SelectedIndex = (int)request.Protocol;
                if (request.Protocol == Ch347BusProtocol.I2c)
                {
                    I2cAddressTextBox.Text = request.I2cAddress.ToString("X2");
                    I2cSpeedComboBox.SelectedIndex = request.I2cSpeedCode;
                    I2cWriteTextBox.Text = Ch347TransferRequest.ToHex(request.WriteData);
                    I2cReadLengthTextBox.Text = request.ReadLength.ToString(CultureInfo.InvariantCulture);
                }
                else if (request.Protocol == Ch347BusProtocol.Spi)
                {
                    SpiModeComboBox.SelectedIndex = request.SpiMode;
                    SpiClockComboBox.SelectedIndex = 7 - request.SpiClockCode;
                    SpiBitOrderComboBox.SelectedIndex = request.SpiLsbFirst ? 1 : 0;
                    SpiCsComboBox.SelectedIndex = request.SpiChipSelect;
                    SpiDummyTextBox.Text = request.SpiDummyByte.ToString("X2");
                    SpiOperationComboBox.SelectedIndex = (int)request.SpiOperation;
                    SpiWriteTextBox.Text = Ch347TransferRequest.ToHex(request.WriteData);
                    SpiReadLengthTextBox.Text = request.ReadLength.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    JtagOperationComboBox.SelectedIndex = (int)request.JtagOperation;
                    JtagClockComboBox.SelectedIndex = request.JtagClockCode;
                    if (request.JtagOperation != Ch347JtagOperation.Reset)
                    {
                        JtagBitLengthTextBox.Text = request.JtagBitLength.ToString(CultureInfo.InvariantCulture);
                        JtagWriteTextBox.Text = Ch347TransferRequest.ToHex(request.WriteData);
                    }
                }
                SetStatus(Text("PresetLoaded", "已加载参数；点击执行才会发送。", "Parameters loaded; execute to send."));
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { loadingPreset = false; UpdateUi(); }
        }

        private void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (busy || IsDisposed || !presetsAvailable) return;
            try
            {
                var name = (PresetNameTextBox.Text ?? "").Trim();
                if (name.Length == 0 || name.Length > 80) throw new ArgumentException(Text("NameRequired", "请输入 1–80 字符的操作名称。", "Enter a preset name of 1–80 characters."));
                var request = BuildRequest();
                var candidate = presets.Select(item => item.Clone()).ToList();
                var index = candidate.FindIndex(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && !Confirm(Text("OverwriteConfirm", "覆盖同名常用操作？", "Replace the preset with this name?") + "\n" + name)) return;
                var preset = new Ch347BusPreset { Name = name, Request = request.Clone() };
                if (index >= 0) candidate[index] = preset;
                else candidate.Add(preset);
                presetStore.Save(candidate);
                presets.Clear();
                presets.AddRange(candidate);
                RefreshPresets(name);
                SetStatus(Text("PresetSaved", "常用操作已保存。", "Preset saved."));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
        {
            if (busy || IsDisposed || !presetsAvailable || !(PresetComboBox.SelectedItem is Ch347BusPreset selected)) return;
            if (!Confirm(Text("DeleteConfirm", "删除此总线常用操作？", "Delete this bus preset?") + "\n" + selected.Name)) return;
            try
            {
                var candidate = presets.Where(item => !ReferenceEquals(item, selected)).Select(item => item.Clone()).ToList();
                presetStore.Save(candidate);
                presets.Clear();
                presets.AddRange(candidate);
                RefreshPresets(null);
                PresetNameTextBox.Clear();
                SetStatus(Text("PresetDeleted", "常用操作已删除。", "Preset deleted."));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private bool Confirm(string message) => System.Windows.MessageBox.Show(Window.GetWindow(this), message, "CH347",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        private void Service_Logged(Ch347BusLogEntry entry)
        {
            if (entry == null || IsDisposed) return;
            var identity = entry.DeviceIdentity ?? "";
            AppendLog($"{entry.Timestamp:HH:mm:ss.fff} [{identity}] {entry.Text}");
        }

        private void ShowError(string message)
        {
            SetStatus(message);
            AppendLog($"{DateTime.Now:HH:mm:ss.fff} · {message}");
        }

        private void SetStatus(string message)
        {
            if (!IsDisposed) OperationStatusText.Text = message;
        }

        private void AppendLog(string text)
        {
            if (IsDisposed) return;
            var shouldDispatch = false;
            lock (logGate)
            {
                if (IsDisposed) return;
                foreach (var value in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    // A valid maximum-size transaction can contain both a full
                    // TX and RX HEX dump (~25 KiB); preserve that whole record.
                    var line = value.Length > 32768 ? value.Substring(0, 32768) + "…" : value;
                    logLines.AddLast(line);
                    logBytes += Encoding.UTF8.GetByteCount(line) + 2;
                    while (logLines.Count > MaximumLogLines || logBytes > MaximumLogBytes)
                    {
                        logBytes -= Encoding.UTF8.GetByteCount(logLines.First.Value) + 2;
                        logLines.RemoveFirst();
                    }
                }
                if (!logDispatchPending) { logDispatchPending = true; shouldDispatch = true; }
            }
            // Coalesce background events into one dispatcher callback. Both the
            // stored log and pending display work remain bounded when UI is busy.
            if (shouldDispatch) PostToUi(RenderLog);
        }

        private void RenderLog()
        {
            string text;
            int count;
            int bytes;
            lock (logGate)
            {
                logDispatchPending = false;
                text = string.Join(Environment.NewLine, logLines);
                count = logLines.Count;
                bytes = logBytes;
            }
            LogTextBox.Text = text;
            LogTextBox.ScrollToEnd();
            LogCountText.Text = $"{count}/{MaximumLogLines} · {bytes / 1024.0:0.#}/1024 KiB";
            SaveLogButton.IsEnabled = count > 0;
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            lock (logGate) { logLines.Clear(); logBytes = 0; }
            RenderLog();
        }

        private void SaveLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsDisposed) return;
            var dialog = new SaveFileDialog
            {
                Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
                FileName = "CH347-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string snapshot;
                lock (logGate) snapshot = string.Join(Environment.NewLine, logLines);
                File.WriteAllText(dialog.FileName, snapshot, new UTF8Encoding(false));
                SetStatus(Text("LogSaved", "日志已保存。", "Log saved."));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void PostToUi(Action action)
        {
            if (IsDisposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!IsDisposed) action();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void Application_Exit(object sender, ExitEventArgs e) => Dispose();
        private void ProgramClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
            operationCancellation?.Cancel();
            service.Logged -= Service_Logged;
            service.StateChanged -= Service_StateChanged;
            Global.ProgramClosedEvent -= ProgramClosed;
            if (application != null) application.Exit -= Application_Exit;
            service.Dispose();
        }

        private sealed class DeviceChoice
        {
            public Ch347DeviceInfo Device { get; }
            public string Label { get; }
            public DeviceChoice(Ch347DeviceInfo device)
            {
                Device = device;
                var identity = string.IsNullOrWhiteSpace(device.DevicePath) ? device.DeviceId ?? "" : device.DevicePath;
                var segments = identity.Split('#');
                // USB paths usually end with a shared interface GUID. Put the
                // preceding, per-device instance segment at the visible end.
                var suffix = segments.Length >= 3 ? segments[segments.Length - 2] : identity;
                if (suffix.Length > 32) suffix = "…" + suffix.Substring(suffix.Length - 32);
                Label = $"{device} · {suffix}";
            }
        }
    }
}
