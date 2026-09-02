using llcom_plus.ScriptEnv;
using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using static llcom_plus.Pages.SocketClientPage;

namespace llcom_plus.Pages
{
    /// <summary>
    /// SocketClientPage.xaml 的交互逻辑
    /// </summary>
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public partial class SocketClientPage : Page
    {
        private const int ProtocolTcp = 0;
        private const int ProtocolUdp = 1;
        private const int ProtocolTcpSsl = 2;
        private const int ProtocolDns = 3;
        private const int ProtocolPing = 4;
        private const int ProtocolNtp = 5;
        private const int ProtocolDtls = 6;
        private const int ProtocolSsh = 7;
        private const int DnsAddressAll = 0;
        private const int DnsAddressIpv4 = 1;
        private const int DnsAddressIpv6 = 2;
        private const string MainSendTargetKey = "socket-client";
        private const ushort DnsRecordA = 1;
        private const ushort DnsRecordCname = 5;
        private const ushort DnsRecordAaaa = 28;
        private const ushort DnsClassInternet = 1;
        private const int DnsQueryTimeoutMs = 5000;
        private static readonly Random DnsQueryRandom = new Random();
        private const long NtpEraSeconds = 1L << 32;
        private static readonly DateTime NtpEpochUtc = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public SocketClientPage()
        {
            InitializeComponent();
            Unloaded += SocketClientPage_Unloaded;
        }
        private bool initial = false;
        private int lastProtocolType = -1;

        //收到消息的事件
        public event EventHandler<byte[]> DataRecived;
        public bool IsConnected { get; set; } = false;
        public bool NeedDisconnected { get; set; } = false;

        //是否可更改服务器信息
        public bool Changeable { get; set; } = true;
        public bool IsDnsToolSelected { get; set; } = false;
        public bool IsSshToolSelected { get; set; } = false;

        //暂存一个对象
        SocketObj socketNow = null;
        private long nativeSocketEpoch = 0;

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (initial)
                return;
            initial = true;

            this.DataContext = this;

            ServerTextBox.DataContext = Tools.Global.setting;
            PortTextBox.DataContext = Tools.Global.setting;
            ProtocolTypeComboBox.DataContext = Tools.Global.setting;
            DnsDomainTextBox.DataContext = Tools.Global.setting;
            DnsAddressTypeComboBox.DataContext = Tools.Global.setting;
            ReconnectInterval.DataContext = Tools.Global.setting;
            NeedReconnect.DataContext = Tools.Global.setting;
            SslAuthModeComboBox.DataContext = Tools.Global.setting;
            SslProtocolVersionComboBox.DataContext = Tools.Global.setting;
            SslTargetHostTextBox.DataContext = Tools.Global.setting;
            SslCheckRevocationCheckBox.DataContext = Tools.Global.setting;
            SslCaCertPathTextBox.DataContext = Tools.Global.setting;
            SslClientCertPathTextBox.DataContext = Tools.Global.setting;
            SslClientKeyPathTextBox.DataContext = Tools.Global.setting;
            SslClientCertPasswordBox.Password = Tools.Global.setting.tcpClientSslClientCertPassword ?? string.Empty;
            SslPrintDetailsCheckBox.DataContext = Tools.Global.setting;
            CipherSuitePicker.Attach(FindName("SslCipherSuitesPicker") as ComboBox, key => TryFindResource(key) as string);
            SshUserNameTextBox.DataContext = Tools.Global.setting;
            SshPrivateKeyPathTextBox.DataContext = Tools.Global.setting;
            SshExtraArgumentsTextBox.DataContext = Tools.Global.setting;
            SshPathTextBox.DataContext = Tools.Global.setting;
            MigrateLegacyDnsSettings();
            lastProtocolType = ProtocolTypeComboBox.SelectedIndex;
            UpdateProtocolUi();

            //收到消息显示
            DataRecived += (_, buff) =>
            {
                ShowData($" → receive", buff);
            };

            //适配一下通用通道
            ScriptApis.SendChannelsRegister("socket-client", (data, _) =>
            {
                if (socketNow != null && data != null)
                {
                    return Send(data);
                }
                else
                    return false;
            });
            //通用通道收到消息
            DataRecived += (_, data) =>
            {
                ScriptApis.SendChannelsReceived("socket-client", data);
            };
        }

        private void SelectSslCaCertPathButton_Click(object sender, RoutedEventArgs e)
        {
            SelectFilePath(SslCaCertPathTextBox);
        }

        private void SelectSslClientCertPathButton_Click(object sender, RoutedEventArgs e)
        {
            SelectFilePath(SslClientCertPathTextBox);
        }

        private void SelectSslClientKeyPathButton_Click(object sender, RoutedEventArgs e)
        {
            SelectFilePath(SslClientKeyPathTextBox);
        }

        private void ClearSslCaCertPathButton_Click(object sender, RoutedEventArgs e)
        {
            SslCaCertPathTextBox.Clear();
        }

        private void ClearSslClientCertPathButton_Click(object sender, RoutedEventArgs e)
        {
            SslClientCertPathTextBox.Clear();
        }

        private void ClearSslClientKeyPathButton_Click(object sender, RoutedEventArgs e)
        {
            SslClientKeyPathTextBox.Clear();
        }

        private void SslClientCertPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Tools.Global.setting != null)
                Tools.Global.setting.tcpClientSslClientCertPassword = SslClientCertPasswordBox.Password;
        }

        private void SelectFilePath(TextBox target)
        {
            var openFileDialog = new System.Windows.Forms.OpenFileDialog
            {
                Filter = TryFindResource("SendFileFilter") as string ?? "All files|*.*"
            };
            if (openFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                target.Text = openFileDialog.FileName;
        }

        private void ShowData(string title, byte[] data = null, bool send = false)
        {
            Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
            {
                title = $"🔗 socket client: {title}",
                data = data ?? new byte[0],
                color = send
                    ? Tools.Logger.GetThemeBrush("AppDataSentBrush", Brushes.IndianRed)
                    : Tools.Logger.GetThemeBrush("AppDataReceivedBrush", Brushes.SeaGreen),
            });
        }

        private void ShowTextData(string title, string text, bool send = false)
        {
            ShowData(title, Encoding.UTF8.GetBytes(text ?? string.Empty), send);
        }

        private readonly object reconnectTimerLock = new object();
        private System.Timers.Timer reconnectTimer;
        private long reconnectGeneration;

        private bool IsReconnectGenerationActive(long generation)
        {
            lock (reconnectTimerLock)
            {
                return generation == reconnectGeneration &&
                    NeedDisconnected &&
                    reconnectTimer != null;
            }
        }

        private long StartReconnectTimer()
        {
            System.Timers.Timer previous;
            System.Timers.Timer current;
            long generation;
            lock (reconnectTimerLock)
            {
                previous = reconnectTimer;
                reconnectTimer = null;
                generation = ++reconnectGeneration;
                NeedDisconnected = true;
                current = new System.Timers.Timer(
                    Math.Max(1, Tools.Global.setting.tcpReconnectInterval) * 1000.0)
                {
                    AutoReset = true,
                    Enabled = false
                };
                reconnectTimer = current;
            }

            DisposeReconnectTimer(previous);
            current.Elapsed += (_, __) => QueueReconnect(generation);
            var shouldDispose = false;
            lock (reconnectTimerLock)
            {
                if (generation == reconnectGeneration &&
                    NeedDisconnected &&
                    ReferenceEquals(reconnectTimer, current))
                {
                    current.Start();
                }
                else
                {
                    shouldDispose = true;
                }
            }
            if (shouldDispose)
                DisposeReconnectTimer(current);
            return generation;
        }

        private void QueueReconnect(long generation)
        {
            if (!IsReconnectGenerationActive(generation) || IsConnected)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsReconnectGenerationActive(generation) || IsConnected)
                    return;
                _ = ReconnectAsync(generation);
            }));
        }

        private void StopReconnectTimer(bool clearDisconnectIntent)
        {
            System.Timers.Timer timer;
            lock (reconnectTimerLock)
            {
                ++reconnectGeneration;
                timer = reconnectTimer;
                reconnectTimer = null;
                if (clearDisconnectIntent)
                    NeedDisconnected = false;
            }
            DisposeReconnectTimer(timer);
        }

        private static void DisposeReconnectTimer(System.Timers.Timer timer)
        {
            if (timer == null)
                return;
            try { timer.Stop(); } catch { }
            try { timer.Dispose(); } catch { }
        }

        private async Task ReconnectAsync(long reconnectAttemptGeneration = 0)
        {
            if (reconnectAttemptGeneration != 0 &&
                !IsReconnectGenerationActive(reconnectAttemptGeneration))
            {
                return;
            }
            if (!Changeable || IsConnected || !NeedDisconnected)
                return;

            var protocol = GetSelectedProtocol();
            if (!IsConnectionProtocol(protocol))
                return;

            if (protocol == ProtocolTcpSsl || protocol == ProtocolDtls)
            {
                ConnectOpenSsl(protocol);
                return;
            }
            if (protocol == ProtocolSsh)
            {
                ConnectSsh();
                return;
            }

            Changeable = false;
            var epoch = Interlocked.Increment(ref nativeSocketEpoch);
            SocketObj owner = null;
            string host;
            int port;
            string targetName;

            try
            {
                host = GetServerHost();
                port = int.Parse(PortTextBox.Text);
                targetName = BuildMainSendTargetName(protocol, host, port);
            }
            catch (Exception ex)
            {
                DisconnectNativeSocket(owner, epoch, "❗ Server information error", ex.Message, true);
                return;
            }

            IPAddress ip;
            try
            {
                ip = await ResolveSocketAddressAsync(host);
            }
            catch (Exception ex)
            {
                DisconnectNativeSocket(owner, epoch, "❗ Server information error", ex.Message, true);
                return;
            }

            if (!IsCurrentNativeAttempt(null, epoch))
                return;

            try
            {
                var endpoint = new IPEndPoint(ip, port);
                var socket = new Socket(
                    endpoint.AddressFamily,
                    protocol == ProtocolUdp ? SocketType.Dgram : SocketType.Stream,
                    protocol == ProtocolUdp ? ProtocolType.Udp : ProtocolType.Tcp);
                owner = new SocketObj(socket, epoch);

                if (!IsCurrentNativeAttempt(null, epoch))
                {
                    CloseSocketQuietly(owner);
                    return;
                }

                socketNow = owner;
                ShowData("📢 Connecting......");
                var state = new StateObject
                {
                    workSocket = socket,
                    owner = owner,
                    epoch = epoch,
                    isDatagram = protocol == ProtocolUdp,
                    targetName = targetName,
                };
                socket.BeginConnect(endpoint, Connect_Callback, state);
            }
            catch (Exception ex)
            {
                DisconnectNativeSocket(owner, epoch, "❗ Server connect error", ex.Message, true);
            }
        }

        private static async Task<IPAddress> ResolveSocketAddressAsync(string host)
        {
            IPAddress address;
            if (IPAddress.TryParse(host, out address))
                return address;

            return await Task.Run(() =>
            {
                var addresses = Dns.GetHostAddresses(host)
                    .Where(item => item.AddressFamily == AddressFamily.InterNetwork ||
                                   item.AddressFamily == AddressFamily.InterNetworkV6)
                    .ToArray();
                if (addresses.Length == 0)
                    throw new Exception("server host has no available IP address");
                return addresses[0];
            });
        }

        private void Connect_Callback(IAsyncResult ar)
        {
            var state = (StateObject)ar.AsyncState;
            try
            {
                state.workSocket.EndConnect(ar);
            }
            catch (Exception ex)
            {
                DisconnectNativeSocket(state.owner, state.epoch, "❗ Server connect error", ex.Message, true);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => CompleteNativeSocketConnect(state)));
        }

        private void CompleteNativeSocketConnect(StateObject state)
        {
            if (!IsCurrentNativeAttempt(state.owner, state.epoch))
            {
                CloseSocketQuietly(state.owner);
                return;
            }

            try
            {
                IsConnected = true;
                NeedDisconnected = true;
                RegisterMainSendTarget(state.targetName);
                ShowData("✔ Server connected");
                state.workSocket.BeginReceive(
                    state.buffer,
                    0,
                    StateObject.BUFFER_SIZE,
                    SocketFlags.None,
                    Read_Callback,
                    state);
            }
            catch (Exception ex)
            {
                DisconnectNativeSocket(state.owner, state.epoch, "❗ Server receive error", ex.Message, false);
            }
        }

        private bool IsCurrentNativeAttempt(SocketObj owner, long epoch)
        {
            if (Volatile.Read(ref nativeSocketEpoch) != epoch)
                return false;

            return owner == null
                ? socketNow == null
                : ReferenceEquals(socketNow, owner);
        }

        private void DisconnectNativeSocket(
            SocketObj owner,
            long epoch,
            string errorTitle = null,
            string detail = null,
            bool publishFailure = false)
        {
            CloseSocketQuietly(owner);

            Action updateState = () =>
            {
                if (!IsCurrentNativeAttempt(owner, epoch))
                    return;
                if (Interlocked.CompareExchange(ref nativeSocketEpoch, epoch + 1, epoch) != epoch)
                    return;

                socketNow = null;
                IsConnected = false;
                Tools.Global.ClearMainSendTarget(MainSendTargetKey);
                if (!Tools.Global.setting.tcpReconnect)
                    NeedDisconnected = false;
                Changeable = true;

                if (!string.IsNullOrWhiteSpace(errorTitle))
                {
                    ShowData(string.IsNullOrWhiteSpace(detail)
                        ? errorTitle
                        : $"{errorTitle} {detail}");
                }
                if (publishFailure)
                    PublishConnectionFailure(detail ?? errorTitle ?? "Server connection failed");
                ShowData("❌ Server disconnected");
            };

            if (Dispatcher.CheckAccess())
                updateState();
            else
                Dispatcher.BeginInvoke(updateState);
        }

        private static void CloseSocketQuietly(SocketObj owner)
        {
            if (owner == null)
                return;

            try
            {
                owner.Close();
            }
            catch
            {
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ConnectOrRunToolAsync();
        }

        private async Task ConnectOrRunToolAsync()
        {
            var protocol = GetSelectedProtocol();
            if (!IsConnectionProtocol(protocol))
            {
                await RunSingleShotProtocolAsync(protocol);
                return;
            }

            long reconnectAttemptGeneration = 0;
            if (Tools.Global.setting.tcpReconnect)
            {
                reconnectAttemptGeneration = StartReconnectTimer();
            }
            else
            {
                StopReconnectTimer(clearDisconnectIntent: false);
                NeedDisconnected = true;
            }
            await ReconnectAsync(reconnectAttemptGeneration);
        }

        private async Task RunSingleShotProtocolAsync(int protocol)
        {
            if (!Changeable)
                return;

            Changeable = false;
            try
            {
                switch (protocol)
                {
                    case ProtocolDns:
                        await RunDnsLookupAsync();
                        break;
                    case ProtocolPing:
                        await RunPingAsync();
                        break;
                    case ProtocolNtp:
                        await RunNtpQueryAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowData($"❗ Tool error {ex.Message}");
            }
            finally
            {
                Changeable = true;
            }
        }

        private void ConnectOpenSsl(int protocol)
        {
            try
            {
                Changeable = false;
                var host = GetServerHost();
                var options = OpenSslCli.FromGlobalSettings(host, GetPortOrDefault(protocol == ProtocolDtls ? 4433 : 443), protocol == ProtocolDtls);
                if (options.AuthMode == 0)
                {
                    var confirmed = Tools.InputDialog.OpenDialog(
                        "SECURITY WARNING: No authentication disables CA and hostname verification. Continue only for explicit debugging.",
                        null,
                        "Unverified TLS/DTLS connection").Item1;
                    if (!confirmed)
                    {
                        Changeable = true;
                        return;
                    }
                }
                ShowTextData(protocol == ProtocolDtls ? "🔐 OpenSSL DTLS connecting" : "🔐 OpenSSL TLS connecting",
                    OpenSslCli.BuildDiagnosticSummary(options));

                OpenSslInteractiveConnection connection = null;
                var connectionEstablished = false;
                connection = OpenSslCli.StartInteractive(
                    options,
                    data => DataRecived?.Invoke(null, data),
                    text =>
                    {
                        if (Tools.Global.setting.tcpClientSslPrintDetails && !string.IsNullOrWhiteSpace(text))
                        {
                            var detail = OpenSslMessageExplainer.Explain(text);
                            if (!string.IsNullOrWhiteSpace(detail))
                                ShowTextData("🔐 OpenSSL detail", detail);
                        }
                    },
                    () => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (socketNow == null || !socketNow.Owns(connection))
                            return;

                        connectionEstablished = true;
                        IsConnected = true;
                        NeedDisconnected = true;
                        RegisterMainSendTarget(BuildMainSendTargetName(protocol, host, options.Port));
                        ShowData("✔ OpenSSL connected");
                    })),
                    exitCode =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (socketNow == null || !socketNow.Owns(connection))
                                return;

                            socketNow = null;
                            IsConnected = false;
                            Tools.Global.ClearMainSendTarget(MainSendTargetKey);
                            if (!Tools.Global.setting.tcpReconnect)
                                NeedDisconnected = false;
                            Changeable = true;
                            ShowTextData(
                                connectionEstablished ? "❌ OpenSSL disconnected" : "❌ OpenSSL handshake failed",
                                $"Exit code: {(exitCode.HasValue ? exitCode.Value.ToString() : "unknown")}");
                            if (!connectionEstablished)
                            {
                                PublishConnectionFailure(
                                    $"OpenSSL exit code: {(exitCode.HasValue ? exitCode.Value.ToString() : "unknown")}");
                            }
                        }));
                    });

                socketNow = new SocketObj(connection);
                NeedDisconnected = true;
            }
            catch (Exception ex)
            {
                socketNow = null;
                IsConnected = false;
                Tools.Global.ClearMainSendTarget(MainSendTargetKey);
                if (!Tools.Global.setting.tcpReconnect)
                    NeedDisconnected = false;
                Changeable = true;
                ShowData($"❗ OpenSSL connect error {ex.Message}");
                PublishConnectionFailure(ex.Message);
                ShowData("❌ Server disconnected");
            }
        }

        private void ConnectSsh()
        {
            try
            {
                Changeable = false;
                var host = GetServerHost();
                var options = SshCli.FromGlobalSettings(host, GetPortOrDefault(22));
                ShowTextData("🔐 SSH connecting", SshCli.BuildDiagnosticSummary(options));

                SshInteractiveConnection connection = null;
                var connectionEstablished = false;
                connection = SshCli.StartInteractive(
                    options,
                    data => DataRecived?.Invoke(null, data),
                    () => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (socketNow == null || !socketNow.Owns(connection))
                            return;

                        connectionEstablished = true;
                        IsConnected = true;
                        NeedDisconnected = true;
                        RegisterMainSendTarget(BuildMainSendTargetName(ProtocolSsh, host, options.Port));
                        ShowData("✔ SSH authenticated");
                    })),
                    exitCode =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (socketNow == null || !socketNow.Owns(connection))
                                return;

                            socketNow = null;
                            IsConnected = false;
                            Tools.Global.ClearMainSendTarget(MainSendTargetKey);
                            if (!Tools.Global.setting.tcpReconnect)
                                NeedDisconnected = false;
                            Changeable = true;
                            ShowTextData(
                                connectionEstablished ? "❌ SSH disconnected" : "❌ SSH authentication failed",
                                $"Exit code: {(exitCode.HasValue ? exitCode.Value.ToString() : "unknown")}");
                            if (!connectionEstablished)
                            {
                                PublishConnectionFailure(
                                    $"SSH exit code: {(exitCode.HasValue ? exitCode.Value.ToString() : "unknown")}");
                            }
                        }));
                    });

                socketNow = new SocketObj(connection);
                NeedDisconnected = true;
            }
            catch (Exception ex)
            {
                socketNow = null;
                IsConnected = false;
                Tools.Global.ClearMainSendTarget(MainSendTargetKey);
                if (!Tools.Global.setting.tcpReconnect)
                    NeedDisconnected = false;
                Changeable = true;
                ShowData($"❗ SSH connect error {ex.Message}");
                PublishConnectionFailure(ex.Message);
                ShowData("❌ Server disconnected");
            }
        }

        private int GetSelectedProtocol()
        {
            return ProtocolTypeComboBox.SelectedIndex < 0 ? ProtocolTcp : ProtocolTypeComboBox.SelectedIndex;
        }

        private bool IsConnectionProtocol(int protocol)
        {
            return protocol == ProtocolTcp ||
                   protocol == ProtocolUdp ||
                   protocol == ProtocolTcpSsl ||
                   protocol == ProtocolDtls ||
                   protocol == ProtocolSsh;
        }

        private void ProtocolTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProtocolTypeComboBox == null)
                return;

            UpdateProtocolUi();
            if (PortTextBox == null)
                return;

            var protocol = GetSelectedProtocol();
            if (initial && lastProtocolType >= 0 && protocol != lastProtocolType)
                ApplyDefaultPort(protocol);

            lastProtocolType = protocol;
        }

        private void UpdateProtocolUi()
        {
            if (ProtocolTypeComboBox == null || PortTextBox == null || ServerTextBox == null)
                return;

            var protocol = GetSelectedProtocol();
            var isConnection = IsConnectionProtocol(protocol);
            var isPing = protocol == ProtocolPing;
            var isDns = protocol == ProtocolDns;
            var isSsh = protocol == ProtocolSsh;

            IsDnsToolSelected = isDns;
            IsSshToolSelected = isSsh;
            UpdateServerTextBoxBinding(isDns);
            PortTextBox.IsEnabled = !isPing;
            NeedReconnect.IsEnabled = isConnection;
            ReconnectSettingsPanel.IsEnabled = isConnection;
            ConnectButton.Content = isConnection
                ? (TryFindResource("ConnectButton") as string ?? "连接")
                : (TryFindResource("TcpRunToolButton") as string ?? "执行");
        }

        private void RegisterMainSendTarget(string displayName)
        {
            Tools.Global.SetMainSendTarget(new Tools.Global.MainSendTarget
            {
                Key = MainSendTargetKey,
                DisplayName = displayName,
                Send = Send
            });
        }

        private void PublishConnectionFailure(string detail)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => PublishConnectionFailure(detail)));
                return;
            }

            var protocol = GetSelectedProtocol();
            string displayName;
            try
            {
                displayName = BuildMainSendTargetName(
                    protocol,
                    GetServerHost(),
                    GetPortOrDefault(GetDefaultPort(protocol)));
            }
            catch
            {
                displayName = GetProtocolText(protocol);
            }

            Tools.Global.PublishNotification(
                string.Format(
                    TryFindResource("NotificationOperationFailedTitleFormat") as string ?? "{0} 失败",
                    displayName),
                detail ?? string.Empty,
                Tools.AppNotificationLevel.Error,
                category: Tools.AppNotificationCategory.Connection);
        }

        private string BuildMainSendTargetName(int protocol, string host, int port)
        {
            return $"{GetProtocolText(protocol)} {host?.Trim()}:{port}";
        }

        private string GetProtocolText(int protocol)
        {
            switch (protocol)
            {
                case ProtocolTcp:
                    return TryFindResource("TcpProtocolTcp") as string ?? "TCP";
                case ProtocolUdp:
                    return TryFindResource("TcpProtocolUdp") as string ?? "UDP";
                case ProtocolTcpSsl:
                    return TryFindResource("TcpProtocolTcpSsl") as string ?? "TCP SSL";
                case ProtocolDtls:
                    return TryFindResource("TcpProtocolDtls") as string ?? "DTLS";
                case ProtocolSsh:
                    return TryFindResource("TcpProtocolSsh") as string ?? "SSH";
                default:
                    return TryFindResource("TcpClientTitle") as string ?? "socket";
            }
        }

        private void UpdateServerTextBoxBinding(bool isDns)
        {
            var propertyName = isDns ? "tcpClientDnsServer" : "tcpClientServer";
            var binding = BindingOperations.GetBinding(ServerTextBox, TextBox.TextProperty);
            if (binding != null && binding.Path != null && binding.Path.Path == propertyName)
                return;

            ServerTextBox.SetBinding(TextBox.TextProperty, new Binding(propertyName)
            {
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        }

        private void MigrateLegacyDnsSettings()
        {
            var setting = Tools.Global.setting;
            if (setting == null || setting.tcpClientProtocolType != ProtocolDns)
                return;

            var oldServerText = (setting.tcpClientServer ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(oldServerText))
                return;

            if (IsDefaultDnsDomain(setting.tcpClientDnsDomain) && !LooksLikeIpAddress(oldServerText))
                setting.tcpClientDnsDomain = oldServerText;
        }

        private static bool IsDefaultDnsDomain(string domain)
        {
            return string.IsNullOrWhiteSpace(domain) ||
                   string.Equals(domain.Trim(), "qq.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeIpAddress(string text)
        {
            IPAddress address;
            return IPAddress.TryParse((text ?? string.Empty).Trim().Trim('[', ']'), out address);
        }

        private void ApplyDefaultPort(int protocol)
        {
            var defaultPort = GetDefaultPort(protocol);
            if (defaultPort < 0)
                return;

            Tools.Global.setting.tcpClientPort = defaultPort;
        }

        private int GetDefaultPort(int protocol)
        {
            switch (protocol)
            {
                case ProtocolTcpSsl:
                    return 443;
                case ProtocolDns:
                    return 53;
                case ProtocolPing:
                    return 0;
                case ProtocolNtp:
                    return 123;
                case ProtocolDtls:
                    return 4433;
                case ProtocolSsh:
                    return 22;
                case ProtocolTcp:
                case ProtocolUdp:
                    return 80;
                default:
                    return -1;
            }
        }

        private string GetServerHost()
        {
            string host = ServerTextBox.Text ?? string.Empty;
            host = host.Trim();
            if (string.IsNullOrWhiteSpace(host))
                throw new Exception("server host is empty");
            return host;
        }

        private int GetPortOrDefault(int defaultPort)
        {
            int port;
            if (!int.TryParse(PortTextBox.Text, out port) || port <= 0 || port > 65535)
                return defaultPort;
            return port;
        }

        private async Task RunDnsLookupAsync()
        {
            var dnsServer = GetServerHost();
            var port = GetPortOrDefault(53);
            var domain = GetDnsDomain();
            var addressType = GetSelectedDnsAddressType();
            var watch = Stopwatch.StartNew();
            var text = await Task.Run(() => QueryDns(dnsServer, port, domain, addressType));
            watch.Stop();

            var sb = new StringBuilder();
            sb.AppendLine($"Elapsed: {watch.ElapsedMilliseconds} ms");
            sb.Append(text);

            ShowTextData("🌐 DNS result", sb.ToString());
        }

        private string GetDnsDomain()
        {
            string domain = DnsDomainTextBox.Text ?? string.Empty;
            domain = domain.Trim();
            if (string.IsNullOrWhiteSpace(domain))
                throw new Exception("domain is empty");
            return domain;
        }

        private string QueryDns(string dnsServer, int port, string domain, int addressType)
        {
            var serverAddress = ResolveDnsServerAddress(dnsServer);
            var queryTypes = GetDnsQueryTypes(addressType);
            var records = new List<DnsResourceRecord>();
            var responses = new List<string>();

            foreach (var queryType in queryTypes)
            {
                var response = QueryDnsOnce(serverAddress, port, domain, queryType);
                records.AddRange(response.Answers);
                responses.Add($"{GetDnsRecordTypeText(queryType)}={GetDnsResponseCodeText(response.ResponseCode)}{response.ResponseCodeTextSuffix}");
            }

            var cnameRecords = records
                .Where(record => record.Type == DnsRecordCname && !string.IsNullOrWhiteSpace(record.TextData))
                .Select(record => record.TextData)
                .Distinct()
                .ToArray();
            var addressRecords = records
                .Where(record => record.Address != null && IsDnsAddressFamilyMatch(record.Address, addressType))
                .GroupBy(record => $"{record.Type}:{record.Address}")
                .Select(group => group.First())
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"DNS server: {dnsServer}:{port} ({serverAddress})");
            sb.AppendLine($"Domain: {domain}");
            sb.AppendLine($"Query type: {GetDnsAddressTypeText(addressType)}");
            sb.AppendLine($"Response: {string.Join(", ", responses)}");
            if (cnameRecords.Length > 0)
                sb.AppendLine($"CNAME: {string.Join(", ", cnameRecords)}");
            sb.AppendLine("Addresses:");
            if (addressRecords.Length == 0)
                sb.AppendLine("  (none)");
            foreach (var record in addressRecords)
                sb.AppendLine($"  {record.Address} ({GetDnsRecordTypeText(record.Type)}, TTL {record.Ttl})");
            return sb.ToString();
        }

        private static IPAddress ResolveDnsServerAddress(string dnsServer)
        {
            var server = (dnsServer ?? string.Empty).Trim();
            if (server.StartsWith("[") && server.EndsWith("]"))
                server = server.Substring(1, server.Length - 2);

            IPAddress address;
            if (IPAddress.TryParse(server, out address))
                return address;

            var addresses = Dns.GetHostAddresses(server)
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork ||
                               item.AddressFamily == AddressFamily.InterNetworkV6)
                .ToArray();
            if (addresses.Length == 0)
                throw new Exception("DNS server has no available IP address");
            return addresses[0];
        }

        private static ushort[] GetDnsQueryTypes(int addressType)
        {
            switch (addressType)
            {
                case DnsAddressIpv4:
                    return new[] { DnsRecordA };
                case DnsAddressIpv6:
                    return new[] { DnsRecordAaaa };
                default:
                    return new[] { DnsRecordA, DnsRecordAaaa };
            }
        }

        private static DnsQueryResponse QueryDnsOnce(IPAddress serverAddress, int port, string domain, ushort queryType)
        {
            var queryId = CreateDnsQueryId();
            var request = BuildDnsQuery(domain, queryType, queryId);

            using (var udp = new UdpClient(serverAddress.AddressFamily))
            {
                udp.Client.ReceiveTimeout = DnsQueryTimeoutMs;
                udp.Client.SendTimeout = DnsQueryTimeoutMs;
                udp.Connect(new IPEndPoint(serverAddress, port));
                udp.Send(request, request.Length);

                var any = serverAddress.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                var endpoint = new IPEndPoint(any, 0);
                var response = udp.Receive(ref endpoint);
                return ParseDnsResponse(response, queryId);
            }
        }

        private static ushort CreateDnsQueryId()
        {
            lock (DnsQueryRandom)
            {
                return (ushort)DnsQueryRandom.Next(0, ushort.MaxValue + 1);
            }
        }

        private static byte[] BuildDnsQuery(string domain, ushort queryType, ushort queryId)
        {
            var data = new List<byte>();
            WriteUInt16(data, queryId);
            WriteUInt16(data, 0x0100);
            WriteUInt16(data, 1);
            WriteUInt16(data, 0);
            WriteUInt16(data, 0);
            WriteUInt16(data, 0);
            WriteDnsQuestionName(data, domain);
            WriteUInt16(data, queryType);
            WriteUInt16(data, DnsClassInternet);
            return data.ToArray();
        }

        private static void WriteDnsQuestionName(List<byte> data, string domain)
        {
            var normalizedDomain = (domain ?? string.Empty).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(normalizedDomain))
                throw new Exception("domain is empty");

            var asciiDomain = new IdnMapping().GetAscii(normalizedDomain);
            var labels = asciiDomain.Split('.');
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                    throw new Exception("domain contains empty label");
                if (label.Length > 63)
                    throw new Exception("domain label is too long");

                data.Add((byte)label.Length);
                data.AddRange(Encoding.ASCII.GetBytes(label));
            }
            data.Add(0);
        }

        private static DnsQueryResponse ParseDnsResponse(byte[] message, ushort expectedQueryId)
        {
            if (message == null || message.Length < 12)
                throw new Exception("invalid DNS response");

            var offset = 0;
            var responseId = ReadUInt16(message, ref offset);
            if (responseId != expectedQueryId)
                throw new Exception("DNS response id mismatch");

            var flags = ReadUInt16(message, ref offset);
            var questionCount = ReadUInt16(message, ref offset);
            var answerCount = ReadUInt16(message, ref offset);
            var authorityCount = ReadUInt16(message, ref offset);
            var additionalCount = ReadUInt16(message, ref offset);
            var response = new DnsQueryResponse
            {
                ResponseCode = flags & 0x000F,
                IsTruncated = (flags & 0x0200) != 0,
            };

            for (var i = 0; i < questionCount; i++)
            {
                ReadDnsName(message, ref offset);
                offset += 4;
                EnsureDnsOffset(message, offset, 0);
            }

            for (var i = 0; i < answerCount; i++)
                response.Answers.Add(ReadDnsResourceRecord(message, ref offset));

            for (var i = 0; i < authorityCount + additionalCount; i++)
                SkipDnsResourceRecord(message, ref offset);

            if (response.IsTruncated)
                response.ResponseCodeTextSuffix = " (truncated)";

            return response;
        }

        private static DnsResourceRecord ReadDnsResourceRecord(byte[] message, ref int offset)
        {
            var record = new DnsResourceRecord();
            record.Name = ReadDnsName(message, ref offset);
            record.Type = ReadUInt16(message, ref offset);
            record.Class = ReadUInt16(message, ref offset);
            record.Ttl = ReadUInt32(message, ref offset);
            var length = ReadUInt16(message, ref offset);
            var dataOffset = offset;
            EnsureDnsOffset(message, dataOffset, length);
            offset += length;

            if (record.Type == DnsRecordA && length == 4)
            {
                record.Address = new IPAddress(message.Skip(dataOffset).Take(length).ToArray());
            }
            else if (record.Type == DnsRecordAaaa && length == 16)
            {
                record.Address = new IPAddress(message.Skip(dataOffset).Take(length).ToArray());
            }
            else if (record.Type == DnsRecordCname)
            {
                var cnameOffset = dataOffset;
                record.TextData = ReadDnsName(message, ref cnameOffset);
            }

            return record;
        }

        private static void SkipDnsResourceRecord(byte[] message, ref int offset)
        {
            ReadDnsName(message, ref offset);
            offset += 8;
            var length = ReadUInt16(message, ref offset);
            EnsureDnsOffset(message, offset, length);
            offset += length;
        }

        private static string ReadDnsName(byte[] message, ref int offset)
        {
            var labels = new List<string>();
            var jumped = false;
            var jumpReturnOffset = offset;
            var jumpCount = 0;

            while (true)
            {
                EnsureDnsOffset(message, offset, 1);
                var length = message[offset++];
                if (length == 0)
                    break;

                if ((length & 0xC0) == 0xC0)
                {
                    EnsureDnsOffset(message, offset, 1);
                    var pointer = ((length & 0x3F) << 8) | message[offset++];
                    EnsureDnsOffset(message, pointer, 1);
                    if (!jumped)
                    {
                        jumped = true;
                        jumpReturnOffset = offset;
                    }
                    offset = pointer;
                    jumpCount++;
                    if (jumpCount > 32)
                        throw new Exception("DNS name compression loop");
                    continue;
                }

                if ((length & 0xC0) != 0)
                    throw new Exception("invalid DNS name label");

                EnsureDnsOffset(message, offset, length);
                labels.Add(Encoding.ASCII.GetString(message, offset, length));
                offset += length;
            }

            if (jumped)
                offset = jumpReturnOffset;

            return labels.Count == 0 ? "." : string.Join(".", labels);
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            EnsureDnsOffset(data, offset, 2);
            var value = (ushort)((data[offset] << 8) | data[offset + 1]);
            offset += 2;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            EnsureDnsOffset(data, offset, 4);
            var value = ((uint)data[offset] << 24) |
                        ((uint)data[offset + 1] << 16) |
                        ((uint)data[offset + 2] << 8) |
                        data[offset + 3];
            offset += 4;
            return value;
        }

        private static void WriteUInt16(List<byte> data, int value)
        {
            data.Add((byte)((value >> 8) & 0xFF));
            data.Add((byte)(value & 0xFF));
        }

        private static void EnsureDnsOffset(byte[] data, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new Exception("invalid DNS response format");
        }

        private int GetSelectedDnsAddressType()
        {
            var value = DnsAddressTypeComboBox?.SelectedIndex ?? Tools.Global.setting.tcpClientDnsAddressType;
            return value < DnsAddressAll || value > DnsAddressIpv6 ? DnsAddressAll : value;
        }

        private static bool IsDnsAddressFamilyMatch(IPAddress address, int addressType)
        {
            switch (addressType)
            {
                case DnsAddressIpv4:
                    return address.AddressFamily == AddressFamily.InterNetwork;
                case DnsAddressIpv6:
                    return address.AddressFamily == AddressFamily.InterNetworkV6;
                default:
                    return address.AddressFamily == AddressFamily.InterNetwork ||
                           address.AddressFamily == AddressFamily.InterNetworkV6;
            }
        }

        private static string GetDnsAddressTypeText(int addressType)
        {
            switch (addressType)
            {
                case DnsAddressIpv4:
                    return "IPv4 (A)";
                case DnsAddressIpv6:
                    return "IPv6 (AAAA)";
                default:
                    return "All";
            }
        }

        private static string GetDnsRecordTypeText(ushort recordType)
        {
            switch (recordType)
            {
                case DnsRecordA:
                    return "A";
                case DnsRecordAaaa:
                    return "AAAA";
                case DnsRecordCname:
                    return "CNAME";
                default:
                    return $"TYPE{recordType}";
            }
        }

        private static string GetDnsResponseCodeText(int responseCode)
        {
            switch (responseCode)
            {
                case 0:
                    return "NoError";
                case 1:
                    return "FormErr";
                case 2:
                    return "ServFail";
                case 3:
                    return "NXDomain";
                case 4:
                    return "NotImp";
                case 5:
                    return "Refused";
                default:
                    return $"RCODE {responseCode}";
            }
        }

        private class DnsQueryResponse
        {
            public int ResponseCode { get; set; }
            public bool IsTruncated { get; set; }
            public string ResponseCodeTextSuffix { get; set; }
            public List<DnsResourceRecord> Answers { get; } = new List<DnsResourceRecord>();
        }

        private class DnsResourceRecord
        {
            public string Name { get; set; }
            public ushort Type { get; set; }
            public ushort Class { get; set; }
            public uint Ttl { get; set; }
            public IPAddress Address { get; set; }
            public string TextData { get; set; }
        }

        private async Task RunPingAsync()
        {
            var host = GetServerHost();
            using (var ping = new Ping())
            {
                var payload = Encoding.ASCII.GetBytes("llcom plus ping");
                var reply = await ping.SendPingAsync(host, 4000, payload);
                var sb = new StringBuilder();
                sb.AppendLine($"Host: {host}");
                sb.AppendLine($"Status: {reply.Status}");
                if (reply.Address != null)
                    sb.AppendLine($"Address: {reply.Address}");
                sb.AppendLine($"Roundtrip: {reply.RoundtripTime} ms");
                if (reply.Options != null)
                {
                    sb.AppendLine($"TTL: {reply.Options.Ttl}");
                    sb.AppendLine($"Don't fragment: {reply.Options.DontFragment}");
                }
                sb.AppendLine($"Buffer: {reply.Buffer.Length} bytes");

                ShowTextData("📡 Ping result", sb.ToString());
            }
        }

        private async Task RunNtpQueryAsync()
        {
            var host = GetServerHost();
            var port = GetPortOrDefault(123);
            var text = await Task.Run(() => QueryNtp(host, port));
            ShowTextData("🕘 NTP result", text);
        }

        private string QueryNtp(string host, int port)
        {
            var addresses = Dns.GetHostAddresses(host)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6)
                .ToArray();
            if (addresses.Length == 0)
                throw new Exception("server host has no available IP address");

            var errors = new List<string>();
            foreach (var address in addresses)
            {
                try
                {
                    return QueryNtpAddress(host, port, address);
                }
                catch (Exception ex)
                {
                    errors.Add($"{address}: {ex.Message}");
                }
            }

            throw new Exception("NTP query failed: " + string.Join("; ", errors));
        }

        private string QueryNtpAddress(string host, int port, IPAddress address)
        {
            using (var udp = new UdpClient(address.AddressFamily))
            {
                udp.Client.ReceiveTimeout = 5000;
                udp.Client.SendTimeout = 5000;
                var remote = new IPEndPoint(address, port);
                udp.Connect(remote);

                var request = BuildNtpRequest(DateTime.UtcNow);
                udp.Send(request, request.Length);
                var any = address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                var endpoint = new IPEndPoint(any, 0);
                var response = udp.Receive(ref endpoint);
                var destinationUtc = DateTime.UtcNow;
                var measurement = ParseNtpResponse(request, response, destinationUtc);

                var sb = new StringBuilder();
                sb.AppendLine($"Server: {host}:{port}");
                sb.AppendLine($"Address: {address}");
                sb.AppendLine($"Endpoint: {endpoint}");
                sb.AppendLine($"Mode: {measurement.Mode}");
                sb.AppendLine($"Version: {measurement.Version}");
                sb.AppendLine($"Stratum: {measurement.Stratum}");
                sb.AppendLine($"Request UTC (T1): {measurement.OriginateUtc:O}");
                sb.AppendLine($"Server receive UTC (T2): {measurement.ReceiveUtc:O}");
                sb.AppendLine($"Server transmit UTC (T3): {measurement.TransmitUtc:O}");
                sb.AppendLine($"Response UTC (T4): {measurement.DestinationUtc:O}");
                sb.AppendLine($"NTP Local: {measurement.TransmitUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}");
                sb.AppendLine($"Roundtrip: {(measurement.DestinationUtc - measurement.OriginateUtc).TotalMilliseconds:F3} ms");
                sb.AppendLine($"Network delay: {measurement.Delay.TotalMilliseconds:F3} ms");
                sb.AppendLine($"Local offset: {measurement.Offset.TotalMilliseconds:F3} ms");
                return sb.ToString();
            }
        }

        internal static byte[] BuildNtpRequest(DateTime transmitUtc)
        {
            var request = new byte[48];
            request[0] = 0x23;
            WriteNtpTimestamp(request, 40, transmitUtc);
            return request;
        }

        internal static NtpMeasurement ParseNtpResponse(
            byte[] request,
            byte[] response,
            DateTime destinationUtc)
        {
            if (request == null || request.Length < 48)
                throw new Exception("invalid NTP request");
            if (response == null || response.Length < 48)
                throw new Exception($"invalid NTP response length: {response?.Length ?? 0}");

            var mode = response[0] & 0x7;
            var version = (response[0] >> 3) & 0x7;
            var leapIndicator = (response[0] >> 6) & 0x3;
            var stratum = response[1];
            if (mode != 4)
                throw new Exception($"invalid NTP response mode: {mode}");
            if (stratum < 1 || stratum > 15)
                throw new Exception($"invalid NTP response stratum: {stratum}");
            if (leapIndicator == 3)
                throw new Exception("NTP server clock is unsynchronized");
            if (!NtpTimestampEquals(response, 24, request, 40))
                throw new Exception("NTP originate timestamp mismatch");
            if (IsZeroNtpTimestamp(response, 32) || IsZeroNtpTimestamp(response, 40))
                throw new Exception("NTP response contains an empty server timestamp");

            var t4 = NormalizeUtc(destinationUtc);
            var t1 = ReadNtpTimestamp(request, 40, t4);
            var t2 = ReadNtpTimestamp(response, 32, t4);
            var t3 = ReadNtpTimestamp(response, 40, t4);
            var offsetTicks = ((t2 - t1).Ticks + (t3 - t4).Ticks) / 2;
            var delay = (t4 - t1) - (t3 - t2);

            return new NtpMeasurement
            {
                Mode = mode,
                Version = version,
                Stratum = stratum,
                OriginateUtc = t1,
                ReceiveUtc = t2,
                TransmitUtc = t3,
                DestinationUtc = t4,
                Offset = TimeSpan.FromTicks(offsetTicks),
                Delay = delay,
            };
        }

        private static void WriteNtpTimestamp(byte[] data, int offset, DateTime value)
        {
            var utc = NormalizeUtc(value);
            var ticks = (utc - NtpEpochUtc).Ticks;
            if (ticks < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "NTP timestamp cannot be before 1900-01-01 UTC");

            var wholeSeconds = ticks / TimeSpan.TicksPerSecond;
            var remainderTicks = ticks % TimeSpan.TicksPerSecond;
            var fraction = (uint)(((ulong)remainderTicks * (1UL << 32)) / TimeSpan.TicksPerSecond);
            WriteUInt32BigEndian(data, offset, unchecked((uint)wholeSeconds));
            WriteUInt32BigEndian(data, offset + 4, fraction);
        }

        private static DateTime ReadNtpTimestamp(byte[] data, int offset, DateTime referenceUtc)
        {
            var seconds = ReadUInt32BigEndian(data, offset);
            var fraction = ReadUInt32BigEndian(data, offset + 4);
            var referenceSeconds = (NormalizeUtc(referenceUtc) - NtpEpochUtc).Ticks / TimeSpan.TicksPerSecond;
            var era = (long)Math.Round(
                (referenceSeconds - seconds) / (double)NtpEraSeconds,
                MidpointRounding.AwayFromZero);
            var unfoldedSeconds = seconds + era * NtpEraSeconds;
            var fractionTicks = (long)(((ulong)fraction * TimeSpan.TicksPerSecond + (1UL << 31)) / (1UL << 32));
            if (fractionTicks == TimeSpan.TicksPerSecond)
            {
                unfoldedSeconds++;
                fractionTicks = 0;
            }

            return NtpEpochUtc.AddTicks(checked(unfoldedSeconds * TimeSpan.TicksPerSecond + fractionTicks));
        }

        private static bool NtpTimestampEquals(byte[] left, int leftOffset, byte[] right, int rightOffset)
        {
            for (var i = 0; i < 8; i++)
            {
                if (left[leftOffset + i] != right[rightOffset + i])
                    return false;
            }
            return true;
        }

        private static bool IsZeroNtpTimestamp(byte[] data, int offset)
        {
            for (var i = 0; i < 8; i++)
            {
                if (data[offset + i] != 0)
                    return false;
            }
            return true;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   data[offset + 3];
        }

        private static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        internal sealed class NtpMeasurement
        {
            public int Mode { get; set; }
            public int Version { get; set; }
            public int Stratum { get; set; }
            public DateTime OriginateUtc { get; set; }
            public DateTime ReceiveUtc { get; set; }
            public DateTime TransmitUtc { get; set; }
            public DateTime DestinationUtc { get; set; }
            public TimeSpan Offset { get; set; }
            public TimeSpan Delay { get; set; }
        }

        public void Read_Callback(IAsyncResult ar)
        {
            var state = (StateObject)ar.AsyncState;
            try
            {
                var read = state.workSocket.EndReceive(ar);
                if (!IsCurrentNativeAttempt(state.owner, state.epoch))
                {
                    CloseSocketQuietly(state.owner);
                    return;
                }

                if (read == 0 && !state.isDatagram)
                {
                    DisconnectNativeSocket(state.owner, state.epoch);
                    return;
                }

                var buffer = new byte[read];
                if (read > 0)
                    Buffer.BlockCopy(state.buffer, 0, buffer, 0, read);
                DataRecived?.Invoke(null, buffer);

                if (!IsCurrentNativeAttempt(state.owner, state.epoch))
                    return;
                state.workSocket.BeginReceive(
                    state.buffer,
                    0,
                    StateObject.BUFFER_SIZE,
                    SocketFlags.None,
                    Read_Callback,
                    state);
            }
            catch (Exception ex)
            {
                DisconnectNativeSocket(state.owner, state.epoch, "❗ Receive data error", ex.Message, false);
            }
        }

        private void Reconnect_TextInputCheck(object sender, TextCompositionEventArgs e)
        {
            if (!int.TryParse(e.Text, out int num) || num < 0 || num > 120)
            {
                e.Handled = true;
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            DisconnectCurrentSocket();
        }

        private void SocketClientPage_Unloaded(object sender, RoutedEventArgs e)
        {
            DisconnectCurrentSocket();
        }

        private void DisconnectCurrentSocket()
        {
            // Invalidate queued callbacks before closing the current transport.
            StopReconnectTimer(clearDisconnectIntent: true);
            var current = socketNow;
            if (current != null && current.IsNativeSocket)
            {
                DisconnectNativeSocket(current, current.NativeEpoch);
            }
            else
            {
                Interlocked.Increment(ref nativeSocketEpoch);
                if (current != null)
                {
                    CloseSocketQuietly(current);
                    ShowData("❌ Server disconnected");
                }
                socketNow = null;
                IsConnected = false;
                Tools.Global.ClearMainSendTarget(MainSendTargetKey);
                Changeable = true;
            }

            NeedDisconnected = false;
        }

        private bool Send(byte[] buff)
        {
            var current = socketNow;
            if (current == null || !IsConnected || buff == null)
                return false;

            try
            {
                current.Send(buff);
                ShowData(" ← send", buff, true);
                return true;
            }
            catch (Exception ex)
            {
                if (current.IsNativeSocket)
                {
                    DisconnectNativeSocket(current, current.NativeEpoch, "❗ Send data error", ex.Message, false);
                }
                else
                {
                    ShowData($"❗ Send data error {ex.Message}");
                }
                return false;
            }
        }

        private static void SendAll(Socket socket, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var sent = socket.Send(buffer, offset, buffer.Length - offset, SocketFlags.None);
                if (sent <= 0)
                    throw new InvalidOperationException("stream socket send returned 0 bytes");
                offset += sent;
            }
        }

        private static void SendDatagram(Socket socket, byte[] buffer)
        {
            var sent = socket.Send(buffer, 0, buffer.Length, SocketFlags.None);
            if (sent != buffer.Length)
                throw new InvalidOperationException($"datagram socket sent {sent} of {buffer.Length} bytes");
        }

        public class StateObject
        {
            public Socket workSocket = null;
            public SocketObj owner = null;
            public long epoch;
            public bool isDatagram;
            public string targetName;
            public const int BUFFER_SIZE = 204800;
            public byte[] buffer = new byte[BUFFER_SIZE];
        }

        public class SocketObj
        {
            private readonly Socket socket;
            private readonly OpenSslInteractiveConnection openSslConnection;
            private readonly SshInteractiveConnection sshConnection;
            private readonly bool isDatagram;
            private int closed;

            public SocketObj(Socket socket)
                : this(socket, 0)
            {
            }

            public SocketObj(Socket socket, long nativeEpoch)
            {
                this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
                isDatagram = socket.SocketType == SocketType.Dgram;
                NativeEpoch = nativeEpoch;
            }

            public SocketObj(OpenSslInteractiveConnection openSsl)
            {
                openSslConnection = openSsl;
            }

            public SocketObj(SshInteractiveConnection ssh)
            {
                sshConnection = ssh;
            }

            public bool IsNativeSocket => socket != null;
            public long NativeEpoch { get; }

            public bool Owns(OpenSslInteractiveConnection connection)
            {
                return ReferenceEquals(openSslConnection, connection);
            }

            public bool Owns(SshInteractiveConnection connection)
            {
                return ReferenceEquals(sshConnection, connection);
            }

            public void Send(byte[] buff)
            {
                if (socket != null)
                {
                    if (isDatagram)
                        SendDatagram(socket, buff);
                    else
                        SendAll(socket, buff);
                }
                else if (openSslConnection != null)
                {
                    openSslConnection.Send(buff);
                }
                else if (sshConnection != null)
                {
                    sshConnection.Send(buff);
                }
            }

            public void Close()
            {
                if (Interlocked.Exchange(ref closed, 1) != 0)
                    return;

                if (socket != null)
                {
                    try
                    {
                        socket.Close();
                    }
                    finally
                    {
                        socket.Dispose();
                    }
                }
                else if (openSslConnection != null)
                {
                    openSslConnection.Dispose();
                }
                else if (sshConnection != null)
                {
                    sshConnection.Dispose();
                }
            }
        }

    }
}
