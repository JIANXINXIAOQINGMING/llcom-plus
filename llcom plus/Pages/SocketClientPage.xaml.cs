using llcom_plus.ScriptEnv;
using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        public SocketClientPage()
        {
            InitializeComponent();
        }
        private bool initial = false;
        private int lastProtocolType = -1;

        //收到消息的事件
        public event EventHandler<byte[]> DataRecived;
        public bool IsConnected { get; set; } = false;
        public bool NeedDisconnected { get; set; } = false;

        //是否可更改服务器信息
        public bool Changeable { get; set; } = true;
        public bool HexMode { get; set; } = false;

        //暂存一个对象
        SocketObj socketNow = null;

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (initial)
                return;
            initial = true;

            this.DataContext = this;

            ServerTextBox.DataContext = Tools.Global.setting;
            PortTextBox.DataContext = Tools.Global.setting;
            ProtocolTypeComboBox.DataContext = Tools.Global.setting;
            ReconnectInterval.DataContext = Tools.Global.setting;
            NeedReconnect.DataContext = Tools.Global.setting;
            SslAuthModeComboBox.DataContext = Tools.Global.setting;
            SslProtocolVersionComboBox.DataContext = Tools.Global.setting;
            SslTargetHostTextBox.DataContext = Tools.Global.setting;
            SslCheckRevocationCheckBox.DataContext = Tools.Global.setting;
            SslCaCertPathTextBox.DataContext = Tools.Global.setting;
            SslClientCertPathTextBox.DataContext = Tools.Global.setting;
            SslClientKeyPathTextBox.DataContext = Tools.Global.setting;
            SslClientCertPasswordTextBox.DataContext = Tools.Global.setting;
            SslPrintDetailsCheckBox.DataContext = Tools.Global.setting;
            CipherSuitePicker.Attach(FindName("SslCipherSuitesPicker") as ComboBox, key => TryFindResource(key) as string);
            OpenSslPathTextBox.DataContext = Tools.Global.setting;
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

        private void ShowData(string title, byte[] data = null, bool send = false)
        {
            Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
            {
                title = $"🔗 socket client: {title}",
                data = data ?? new byte[0],
                color = send ? Brushes.DarkRed : Brushes.DarkGreen,
            });
        }

        private void ShowTextData(string title, string text, bool send = false)
        {
            ShowData(title, Encoding.UTF8.GetBytes(text ?? string.Empty), send);
        }

        private System.Timers.Timer reconnectTimer = null;
        private void Reconnect()
        {
            if (!Changeable || IsConnected)
                return;

            IPEndPoint ipe = null;
            Socket s = null;
            var protocol = GetSelectedProtocol();
            if (!IsConnectionProtocol(protocol))
                return;

            if (protocol == ProtocolTcpSsl || protocol == ProtocolDtls)
            {
                ConnectOpenSsl(protocol);
                return;
            }

            try
            {
                Changeable = false;
                IPAddress ip = null;
                try
                {
                    ip = IPAddress.Parse(ServerTextBox.Text);
                }
                catch
                {
                    var hostEntry = Dns.GetHostEntry(ServerTextBox.Text);
                    ip = hostEntry.AddressList.FirstOrDefault(a =>
                        a.AddressFamily == AddressFamily.InterNetwork ||
                        a.AddressFamily == AddressFamily.InterNetworkV6);
                    if (ip == null)
                        throw new Exception("server host has no available IP address");
                }
                ipe = new IPEndPoint(ip, int.Parse(PortTextBox.Text));
                s = new Socket(ipe.AddressFamily,
                    protocol == ProtocolUdp ? SocketType.Dgram : SocketType.Stream,
                    protocol == ProtocolUdp ? ProtocolType.Udp : ProtocolType.Tcp);
            }
            catch (Exception ex)
            {
                ShowData($"❗ Server information error {ex.Message}");
                Changeable = true;
                return;
            }
            ShowData("📢 Connecting......");
            try
            {
                StateObject so = new StateObject();
                s.BeginConnect(ipe, new AsyncCallback((r) =>
                {
                    var s = (Socket)r.AsyncState;
                    if (s.Connected)
                    {
                        socketNow = new SocketObj(s);
                        IsConnected = true;
                        NeedDisconnected = true;
                        ShowData("✔ Server connected");
                    }
                    else
                    {
                        Changeable = true;
                        ShowData("❗ Server connect failed");
                        return;
                    }

                    so.workSocket = s;
                    try
                    {
                        s.BeginReceive(so.buffer, 0, StateObject.BUFFER_SIZE, 0, new AsyncCallback(Read_Callback), so);
                    }
                    catch(Exception ex)
                    {
                        ShowData($"❗ Server connect error {ex.Message}");
                        socketNow = null;
                        IsConnected = false;
                        Changeable = true;
                        s.Close();
                        s.Dispose();
                        ShowData("❌ Server disconnected");
                        return;
                    }
                }), s);
            }
            catch (Exception ex)
            {
                ShowData($"❗ Server connect error {ex.Message}");
                Changeable = true;
                return;
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

            if (Tools.Global.setting.tcpReconnect)
            {
                reconnectTimer = new System.Timers.Timer(Tools.Global.setting.tcpReconnectInterval * 1000);
                reconnectTimer.Elapsed += (_, _) =>
                {
                    if (!IsConnected)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            Reconnect();
                        });
                    }
                };
                reconnectTimer.AutoReset = true;
                reconnectTimer.Enabled = true;                
                NeedDisconnected = true;
            }
            else
            {
                if (reconnectTimer != null)
                {
                    reconnectTimer.Stop();
                    reconnectTimer.Dispose();
                    reconnectTimer = null;
                }
            }
            Reconnect();
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
                ShowTextData(protocol == ProtocolDtls ? "🔐 OpenSSL DTLS connecting" : "🔐 OpenSSL TLS connecting",
                    OpenSslCli.BuildDiagnosticSummary(options));

                var connection = OpenSslCli.StartInteractive(
                    options,
                    data => DataRecived?.Invoke(null, data),
                    text =>
                    {
                        if (Tools.Global.setting.tcpClientSslPrintDetails && !string.IsNullOrWhiteSpace(text))
                            ShowTextData("🔐 OpenSSL detail", text);
                    },
                    exitCode =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            socketNow = null;
                            IsConnected = false;
                            if (!Tools.Global.setting.tcpReconnect)
                                NeedDisconnected = false;
                            Changeable = true;
                            ShowTextData("❌ OpenSSL disconnected", $"Exit code: {(exitCode.HasValue ? exitCode.Value.ToString() : "unknown")}");
                        });
                    });

                socketNow = new SocketObj(connection);
                IsConnected = true;
                NeedDisconnected = true;
                ShowData("✔ OpenSSL connected");
            }
            catch (Exception ex)
            {
                socketNow = null;
                IsConnected = false;
                if (!Tools.Global.setting.tcpReconnect)
                    NeedDisconnected = false;
                Changeable = true;
                ShowData($"❗ OpenSSL connect error {ex.Message}");
                ShowData("❌ Server disconnected");
            }
        }

        private int GetSelectedProtocol()
        {
            return ProtocolTypeComboBox.SelectedIndex < 0 ? ProtocolTcp : ProtocolTypeComboBox.SelectedIndex;
        }

        private bool IsConnectionProtocol(int protocol)
        {
            return protocol == ProtocolTcp || protocol == ProtocolUdp || protocol == ProtocolTcpSsl || protocol == ProtocolDtls;
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
            if (ProtocolTypeComboBox == null || PortTextBox == null)
                return;

            var protocol = GetSelectedProtocol();
            var isConnection = IsConnectionProtocol(protocol);
            var isPing = protocol == ProtocolPing;

            PortTextBox.IsEnabled = !isPing;
            NeedReconnect.IsEnabled = isConnection;
            ReconnectSettingsPanel.IsEnabled = isConnection;
            ToSendTextBox.IsEnabled = isConnection;
            SendButton.IsEnabled = isConnection;
            ConnectButton.Content = isConnection
                ? (TryFindResource("ConnectButton") as string ?? "连接")
                : (TryFindResource("TcpRunToolButton") as string ?? "执行");
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
            var host = GetServerHost();
            var watch = Stopwatch.StartNew();
            var entry = await Task.Run(() => Dns.GetHostEntry(host));
            watch.Stop();

            var sb = new StringBuilder();
            sb.AppendLine($"Host: {host}");
            sb.AppendLine($"Canonical name: {entry.HostName}");
            sb.AppendLine($"Elapsed: {watch.ElapsedMilliseconds} ms");
            if (entry.Aliases != null && entry.Aliases.Length > 0)
                sb.AppendLine($"Aliases: {string.Join(", ", entry.Aliases)}");
            sb.AppendLine("Addresses:");
            foreach (var address in entry.AddressList)
                sb.AppendLine($"  {address} ({address.AddressFamily})");

            ShowTextData("🌐 DNS result", sb.ToString());
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
            var request = new byte[48];
            request[0] = 0x23;

            using (var udp = new UdpClient(address.AddressFamily))
            {
                udp.Client.ReceiveTimeout = 5000;
                udp.Client.SendTimeout = 5000;
                var remote = new IPEndPoint(address, port);
                udp.Connect(remote);

                var startUtc = DateTime.UtcNow;
                udp.Send(request, request.Length);
                var any = address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                var endpoint = new IPEndPoint(any, 0);
                var response = udp.Receive(ref endpoint);
                var endUtc = DateTime.UtcNow;

                if (response.Length < 48)
                    throw new Exception($"invalid NTP response length: {response.Length}");

                var seconds = ReadUInt32BigEndian(response, 40);
                var fraction = ReadUInt32BigEndian(response, 44);
                var milliseconds = seconds * 1000d + fraction * 1000d / 0x100000000L;
                var serverUtc = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);
                var offset = serverUtc - endUtc;
                var roundtrip = endUtc - startUtc;

                var sb = new StringBuilder();
                sb.AppendLine($"Server: {host}:{port}");
                sb.AppendLine($"Address: {address}");
                sb.AppendLine($"Endpoint: {endpoint}");
                sb.AppendLine($"Mode: {response[0] & 0x7}");
                sb.AppendLine($"Version: {(response[0] >> 3) & 0x7}");
                sb.AppendLine($"Stratum: {response[1]}");
                sb.AppendLine($"Request UTC: {startUtc:O}");
                sb.AppendLine($"Response UTC: {endUtc:O}");
                sb.AppendLine($"NTP UTC: {serverUtc:O}");
                sb.AppendLine($"NTP Local: {serverUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}");
                sb.AppendLine($"Roundtrip: {roundtrip.TotalMilliseconds:F0} ms");
                sb.AppendLine($"Local offset: {offset.TotalMilliseconds:F0} ms");
                return sb.ToString();
            }
        }

        private static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   data[offset + 3];
        }

        public void Read_Callback(IAsyncResult ar)
        {
            StateObject so = (StateObject)ar.AsyncState;

            Socket s = so.workSocket;
            try
            {

                int read = s.EndReceive(ar);

                if (read > 0)
                {
                    var buff = new byte[read];
                    for (int i = 0; i < buff.Length; i++)
                        buff[i] = so.buffer[i];
                    DataRecived?.Invoke(null, buff);
                    s.BeginReceive(so.buffer, 0, StateObject.BUFFER_SIZE, 0,
                                             new AsyncCallback(Read_Callback), so);
                }
                else//断了？
                {
                    try
                    {
                        s.Close();
                        s.Dispose();
                    }
                    catch { }
                    socketNow = null;
                    IsConnected = false;
                    if (!Tools.Global.setting.tcpReconnect)
                        NeedDisconnected = false;
                    Changeable = true;
                    ShowData("❌ Server disconnected");
                }
            }
            catch { }
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
            if(socketNow != null)
            {
                try
                {
                    socketNow.Close();
                }
                catch { }
                socketNow = null;
                IsConnected = false;
                Changeable = true;
                ShowData("❌ Server disconnected");
            }

            NeedDisconnected = false;
            if (reconnectTimer != null)
            {
                reconnectTimer.Stop();
                reconnectTimer.Dispose();
                reconnectTimer = null;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (socketNow != null)
            {
                byte[] buff = HexMode ? Tools.Global.Hex2Byte(ToSendTextBox.Text) :
                    Tools.Global.GetEncoding().GetBytes(ToSendTextBox.Text);
                Send(buff);
            }
        }

        private bool Send(byte[] buff)
        {
            try
            {
                socketNow.Send(buff);
                ShowData($" ← send", buff, true);
                return true;
            }
            catch(Exception ex)
            {
                ShowData($"❗ Send data error {ex.Message}");
                return false;
            }
        }

        public class StateObject
        {
            public Socket workSocket = null;
            public const int BUFFER_SIZE = 204800;
            public byte[] buffer = new byte[BUFFER_SIZE];
        }

        public class SocketObj
        {
            Socket socket;
            OpenSslInteractiveConnection openSslConnection;
            public SocketObj(Socket s)
            {
                socket = s;
            }
            public SocketObj(OpenSslInteractiveConnection openSsl)
            {
                openSslConnection = openSsl;
            }
            public void Send(byte[] buff)
            {
                if (socket != null)
                    socket.Send(buff);
                else if (openSslConnection != null)
                    openSslConnection.Send(buff);
                    
            }

            public void Close()
            {
                if (socket != null)
                {
                    socket.Close();
                    socket.Dispose();
                }
                else if (openSslConnection != null)
                {
                    openSslConnection.Dispose();
                }
            }
        }
    }
}
