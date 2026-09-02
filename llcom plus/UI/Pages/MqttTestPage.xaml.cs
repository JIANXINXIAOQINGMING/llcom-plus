using llcom_plus.ScriptEnv;
using llcom_plus.Model;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using MQTTnet.Client.Subscribing;
using Newtonsoft.Json;
using ScottPlot.Drawing.Colormaps;
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
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

namespace llcom_plus.Pages
{
    /// <summary>
    /// MqttTestPage.xaml 的交互逻辑
    /// </summary>
    [PropertyChanged.AddINotifyPropertyChangedInterface]
    public partial class MqttTestPage : Page
    {
        public MqttTestPage()
        {
            InitializeComponent();
        }

        private bool initial = false;
        private static readonly MqttFactory factory = new MqttFactory();
        private MQTTnet.Client.IMqttClient mqttClient;
        public bool MqttIsConnected { get; set; } = false;

        private enum MqttConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Disconnecting
        }

        private readonly object mqttStateLock = new object();
        private MqttConnectionState mqttConnectionState = MqttConnectionState.Disconnected;
        private long mqttAttemptSequence;
        private long activeMqttAttempt;
        private CancellationTokenSource mqttAttemptCts;
        private bool mqttConnectionEstablished;
        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (initial)
                return;
            initial = true;

            BrokerTextBox.DataContext = Tools.Global.setting;
            PortTextBox.DataContext = Tools.Global.setting;
            ClientTextBox.DataContext = Tools.Global.setting;
            TLSCheckBox.DataContext = Tools.Global.setting;
            UserTextBox.DataContext = Tools.Global.setting;
            PasswordTextBox.DataContext = Tools.Global.setting;
            PasswordTextBox.Password = Tools.Global.setting.mqttPassword ?? string.Empty;
            KeepAliveTextBox.DataContext = Tools.Global.setting;
            CleanTextBox.DataContext = Tools.Global.setting;
            HexCheckBox.DataContext = Tools.Global.setting;
            WsCheckBox.DataContext = Tools.Global.setting;
            WsPathTextBox.DataContext = Tools.Global.setting;
            publishTopicTextBox.DataContext = Tools.Global.setting;
            subcribeTextBox.DataContext = Tools.Global.setting;
            TLSCertBox.DataContext = Tools.Global.setting;
            MQTTTLSCa.DataContext = Tools.Global.setting;
            MQTTTLSClient.DataContext = Tools.Global.setting;
            MQTTTLSPassword.DataContext = Tools.Global.setting;
            MQTTTLSPassword.Password = Tools.Global.setting.mqttTLSCertClientPassword ?? string.Empty;
            MQTTTLSCheckRevocation.DataContext = Tools.Global.setting;
            ConnectButton.DataContext = this;
            SettingStackPanel.DataContext = this;
            Unloaded += MqttTestPage_Unloaded;

            // Event handlers are attached to a fresh client for each attempt so an
            // old client's callbacks can never mutate the active attempt.

            //适配一下通用通道
            ScriptApis.SendChannelsRegister("mqtt", (data, options) =>
            {
                var currentClient = mqttClient;
                if (currentClient != null && currentClient.IsConnected && options != null)
                {
                    try
                    {
                        var topic = ScriptApis.GetOption<string>(options, "topic");
                        var payload = ScriptApis.GetOption<byte[]>(options, "payload", data);
                        var qos = ScriptApis.GetOption<int>(options, "qos", 0);
                        if (string.IsNullOrEmpty(topic))
                            return false;
                        return Publish(
                            topic,
                            payload,
                            qos);
                    }
                    catch
                    {
                        return false;
                    }
                }
                else
                    return false;
            });
        }



        internal static bool ValidateCertificateWithCustomCa(
            X509Certificate certificate,
            X509Chain suppliedChain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors,
            X509Certificate2 trustedCa,
            DateTime verificationTime,
            out string failureReason)
        {
            return ValidateCertificateWithCustomCa(
                certificate,
                suppliedChain,
                sslPolicyErrors,
                trustedCa,
                verificationTime,
                true,
                out failureReason);
        }

        internal static bool ValidateCertificateWithCustomCa(
            X509Certificate certificate,
            X509Chain suppliedChain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors,
            X509Certificate2 trustedCa,
            DateTime verificationTime,
            bool checkRevocation,
            out string failureReason)
        {
            failureReason = null;

            if (certificate == null ||
                (sslPolicyErrors & System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            {
                failureReason = "server certificate is missing";
                return false;
            }

            if ((sslPolicyErrors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                failureReason = "server certificate host name does not match";
                return false;
            }

            var unsupportedPolicyErrors = sslPolicyErrors &
                ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
            if (unsupportedPolicyErrors != System.Net.Security.SslPolicyErrors.None)
            {
                failureReason = $"unsupported SSL policy error: {unsupportedPolicyErrors}";
                return false;
            }

            if (trustedCa == null)
            {
                failureReason = "selected CA certificate is missing";
                return false;
            }

            X509Certificate2 serverCertificate = certificate as X509Certificate2;
            var disposeServerCertificate = serverCertificate == null;
            try
            {
                if (disposeServerCertificate)
                    serverCertificate = new X509Certificate2(certificate);

                if (!IsCertificateValidAt(serverCertificate, verificationTime))
                {
                    failureReason = "server certificate is not currently valid";
                    return false;
                }

                if (!IsCertificateValidAt(trustedCa, verificationTime))
                {
                    failureReason = "selected CA certificate is not currently valid";
                    return false;
                }

                if (!IsCertificateAuthority(trustedCa))
                {
                    failureReason = "selected certificate is not a CA certificate";
                    return false;
                }

                using (var customChain = new X509Chain())
                {
                    customChain.ChainPolicy.RevocationMode = checkRevocation
                        ? X509RevocationMode.Online
                        : X509RevocationMode.NoCheck;
                    customChain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                    customChain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
                    customChain.ChainPolicy.VerificationFlags =
                        X509VerificationFlags.AllowUnknownCertificateAuthority;
                    customChain.ChainPolicy.VerificationTime = verificationTime.Kind == DateTimeKind.Utc
                        ? verificationTime.ToLocalTime()
                        : verificationTime;
                    customChain.ChainPolicy.ApplicationPolicy.Add(
                        new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                    customChain.ChainPolicy.ExtraStore.Add(trustedCa);

                    if (suppliedChain != null)
                    {
                        foreach (X509ChainElement suppliedElement in suppliedChain.ChainElements)
                        {
                            var suppliedCertificate = suppliedElement.Certificate;
                            if (suppliedCertificate != null &&
                                !CertificateThumbprintsMatch(suppliedCertificate, serverCertificate) &&
                                !CertificateThumbprintsMatch(suppliedCertificate, trustedCa))
                            {
                                customChain.ChainPolicy.ExtraStore.Add(suppliedCertificate);
                            }
                        }
                    }

                    var chainBuilt = customChain.Build(serverCertificate);
                    if (!AreCustomCaChainStatusesAllowed(customChain.ChainStatus))
                    {
                        failureReason = "certificate chain contains a disallowed status: " +
                            FormatChainStatuses(customChain.ChainStatus);
                        return false;
                    }

                    if (!chainBuilt)
                    {
                        failureReason = "certificate chain could not be built";
                        return false;
                    }

                    if (customChain.ChainElements.Count < 2)
                    {
                        failureReason = "certificate chain is not anchored by a separate CA certificate";
                        return false;
                    }

                    var chainLeaf = customChain.ChainElements[0].Certificate;
                    if (!CertificateThumbprintsMatch(chainLeaf, serverCertificate))
                    {
                        failureReason = "certificate chain does not begin with the server certificate";
                        return false;
                    }

                    var chainTail = customChain.ChainElements[
                        customChain.ChainElements.Count - 1].Certificate;
                    if (!CertificateThumbprintsMatch(chainTail, trustedCa))
                    {
                        failureReason = "certificate chain is not anchored by the selected CA certificate";
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                failureReason = $"certificate validation failed: {ex.GetBaseException().Message}";
                return false;
            }
            finally
            {
                if (disposeServerCertificate)
                    serverCertificate?.Dispose();
            }
        }

        internal static bool AreCustomCaChainStatusesAllowed(X509ChainStatus[] chainStatuses)
        {
            if (chainStatuses == null)
                return false;

            foreach (var chainStatus in chainStatuses)
            {
                var disallowedStatus = chainStatus.Status & ~X509ChainStatusFlags.UntrustedRoot;
                if (disallowedStatus != X509ChainStatusFlags.NoError)
                    return false;
            }

            return true;
        }

        internal static bool CertificateThumbprintsMatch(
            X509Certificate2 firstCertificate,
            X509Certificate2 secondCertificate)
        {
            if (firstCertificate == null || secondCertificate == null)
                return false;

            var firstThumbprint = firstCertificate.Thumbprint?.Replace(" ", string.Empty);
            var secondThumbprint = secondCertificate.Thumbprint?.Replace(" ", string.Empty);
            return !string.IsNullOrEmpty(firstThumbprint) &&
                !string.IsNullOrEmpty(secondThumbprint) &&
                string.Equals(firstThumbprint, secondThumbprint, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsCertificateValidAt(
            X509Certificate2 certificate,
            DateTime verificationTime)
        {
            if (certificate == null)
                return false;

            var verificationTimeUtc = verificationTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(verificationTime, DateTimeKind.Local).ToUniversalTime()
                : verificationTime.ToUniversalTime();
            return verificationTimeUtc >= certificate.NotBefore.ToUniversalTime() &&
                verificationTimeUtc <= certificate.NotAfter.ToUniversalTime();
        }

        internal static bool IsCertificateAuthority(X509Certificate2 certificate)
        {
            if (certificate == null)
                return false;

            var basicConstraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault();
            return basicConstraints != null && basicConstraints.CertificateAuthority;
        }

        private static string FormatChainStatuses(X509ChainStatus[] chainStatuses)
        {
            if (chainStatuses == null || chainStatuses.Length == 0)
                return "none";

            return string.Join(", ", chainStatuses.Select(status => status.Status.ToString()).Distinct());
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            MqttConnectionState state;
            lock (mqttStateLock)
                state = mqttConnectionState;

            if (state == MqttConnectionState.Connected)
            {
                await DisconnectMqttAsync().ConfigureAwait(true);
                return;
            }

            // Rapid clicks while a transition is in flight are deliberately ignored.
            if (state != MqttConnectionState.Disconnected)
                return;

            await ConnectMqttAsync().ConfigureAwait(true);
        }

        private async Task ConnectMqttAsync()
        {
            long attemptId;
            CancellationTokenSource attemptCts;
            IMqttClient client;
            lock (mqttStateLock)
            {
                if (mqttConnectionState != MqttConnectionState.Disconnected)
                    return;

                mqttConnectionState = MqttConnectionState.Connecting;
                attemptId = ++mqttAttemptSequence;
                activeMqttAttempt = attemptId;
                attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                mqttAttemptCts = attemptCts;
                client = factory.CreateMqttClient();
                mqttClient = client;
                mqttConnectionEstablished = false;
                MqttIsConnected = true;
            }

            ConfigureMqttClientHandlers(client, attemptId);
            try
            {
                var options = BuildMqttOptions();
                await client.ConnectAsync(options, attemptCts.Token).ConfigureAwait(true);
                HandleMqttConnected(client, attemptId);
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                CompleteMqttAttempt(client, attemptId, null, false);
            }
            catch (Exception ex)
            {
                CompleteMqttAttempt(client, attemptId, ex, true);
            }
        }

        private async Task DisconnectMqttAsync()
        {
            IMqttClient client;
            long attemptId;
            CancellationTokenSource cts;
            lock (mqttStateLock)
            {
                if (mqttConnectionState != MqttConnectionState.Connected)
                    return;

                mqttConnectionState = MqttConnectionState.Disconnecting;
                client = mqttClient;
                attemptId = activeMqttAttempt;
                cts = mqttAttemptCts;
                MqttIsConnected = true;
            }

            try { cts?.Cancel(); } catch { }
            try
            {
                if (client != null && client.IsConnected)
                    await client.DisconnectAsync().ConfigureAwait(true);
            }
            catch
            {
                // The final state transition below is authoritative even if transport teardown fails.
            }
            finally
            {
                CompleteMqttAttempt(client, attemptId, null, false);
            }
        }

        private void ConfigureMqttClientHandlers(IMqttClient client, long attemptId)
        {
            client.UseConnectedHandler(_ =>
                Dispatcher.BeginInvoke(new Action(() => HandleMqttConnected(client, attemptId))));
            client.UseDisconnectedHandler(_ =>
                Dispatcher.BeginInvoke(new Action(() => CompleteMqttAttempt(client, attemptId, null, false))));
            client.UseApplicationMessageReceivedHandler(e =>
            {
                if (!IsCurrentMqttAttempt(client, attemptId, MqttConnectionState.Connected))
                    return;

                ScriptApis.SendChannelsReceived("mqtt",
                    new
                    {
                        topic = e.ApplicationMessage.Topic,
                        payload = e.ApplicationMessage.Payload,
                        qos = (int)e.ApplicationMessage.QualityOfServiceLevel
                    });
                Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
                {
                    title = $"MQTT → {e.ApplicationMessage.Topic}({(int)e.ApplicationMessage.QualityOfServiceLevel})",
                    data = e.ApplicationMessage.Payload,
                    color = Tools.Logger.GetThemeBrush("AppDataReceivedBrush", Brushes.SeaGreen)
                });
            });
        }

        private bool IsCurrentMqttAttempt(
            IMqttClient client,
            long attemptId,
            params MqttConnectionState[] allowedStates)
        {
            lock (mqttStateLock)
            {
                return ReferenceEquals(mqttClient, client) &&
                    activeMqttAttempt == attemptId &&
                    (allowedStates == null || allowedStates.Length == 0 ||
                     allowedStates.Contains(mqttConnectionState));
            }
        }

        private void HandleMqttConnected(IMqttClient client, long attemptId)
        {
            var notify = false;
            lock (mqttStateLock)
            {
                if (!ReferenceEquals(mqttClient, client) || activeMqttAttempt != attemptId ||
                    (mqttConnectionState != MqttConnectionState.Connecting &&
                     mqttConnectionState != MqttConnectionState.Connected))
                {
                    return;
                }

                notify = mqttConnectionState != MqttConnectionState.Connected;
                mqttConnectionState = MqttConnectionState.Connected;
                mqttConnectionEstablished = true;
                MqttIsConnected = true;
            }

            if (!notify)
                return;

            subListBox.Items.Clear();
            var source = TryFindResource("NotificationMqttSource") as string ?? "MQTT";
            Tools.Global.PublishNotification(
                string.Format(
                    TryFindResource("NotificationConnectedTitleFormat") as string ?? "{0} 已连接",
                    source),
                $"{Tools.Global.setting.mqttServer}:{Tools.Global.setting.mqttPort}",
                Tools.AppNotificationLevel.Success,
                category: Tools.AppNotificationCategory.Connection);
            Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
            {
                title = "MQTT event: ✔ connected",
                data = new byte[0],
                color = Tools.Logger.GetThemeBrush("AppDataReceivedBrush", Brushes.SeaGreen)
            });
        }

        private void CompleteMqttAttempt(
            IMqttClient client,
            long attemptId,
            Exception error,
            bool publishFailure)
        {
            bool wasConnected;
            CancellationTokenSource cts;
            lock (mqttStateLock)
            {
                if (!ReferenceEquals(mqttClient, client) || activeMqttAttempt != attemptId)
                    return;

                wasConnected = mqttConnectionEstablished;
                mqttConnectionEstablished = false;
                mqttConnectionState = MqttConnectionState.Disconnected;
                MqttIsConnected = false;
                mqttClient = null;
                cts = mqttAttemptCts;
                mqttAttemptCts = null;
            }

            try { cts?.Dispose(); } catch { }
            subListBox.Items.Clear();
            subListBox.Items.Add(TryFindResource("MQTTNotConnect") as string ?? "?!");

            var source = TryFindResource("NotificationMqttSource") as string ?? "MQTT";
            if (publishFailure && error != null)
            {
                Tools.Global.PublishNotification(
                    string.Format(
                        TryFindResource("NotificationOperationFailedTitleFormat") as string ?? "{0} 失败",
                        source),
                    error.GetBaseException().Message,
                    Tools.AppNotificationLevel.Error,
                    category: Tools.AppNotificationCategory.Connection);
            }
            else if (wasConnected)
            {
                Tools.Global.PublishNotification(
                    string.Format(
                        TryFindResource("NotificationDisconnectedTitleFormat") as string ?? "{0} 已断开",
                        source),
                    $"{Tools.Global.setting.mqttServer}:{Tools.Global.setting.mqttPort}",
                    Tools.AppNotificationLevel.Warning,
                    category: Tools.AppNotificationCategory.Connection);
            }

            Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
            {
                title = error == null
                    ? "MQTT event: ❌ disconnected"
                    : $"MQTT event: ❌ connection failed ({error.GetBaseException().Message})",
                data = new byte[0],
                color = Tools.Logger.GetThemeBrush("AppDangerBrush", Brushes.OrangeRed)
            });
        }

        private IMqttClientOptions BuildMqttOptions()
        {
            var setting = Tools.Global.setting;
            var builder = new MqttClientOptionsBuilder()
                .WithClientId(setting.mqttClientID)
                .WithCredentials(setting.mqttUser, setting.mqttPassword)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(setting.mqttKeepAlive));

            if (setting.mqttTLS)
            {
                if (!setting.mqttTLSCert || setting.mqttWs)
                {
                    // With no callback, TCP TLS and WSS retain platform CA, hostname and revocation checks.
                    builder.WithTls(new MqttClientOptionsBuilderTlsParameters
                    {
                        UseTls = true,
                        SslProtocol = System.Security.Authentication.SslProtocols.Tls12,
                        AllowUntrustedCertificates = false,
                        IgnoreCertificateChainErrors = false,
                        IgnoreCertificateRevocationErrors = false,
                    });
                }
                else
                {
                    var certCa = new X509Certificate2(setting.mqttTLSCertCaPath);
                    var certClient = new X509Certificate2(
                        setting.mqttTLSCertClientPath,
                        string.IsNullOrEmpty(setting.mqttTLSCertClientPassword)
                            ? null
                            : setting.mqttTLSCertClientPassword);
                    if (!certClient.HasPrivateKey)
                        throw new InvalidDataException("The client PFX does not contain a private key.");
                    if (!IsCertificateAuthority(certCa))
                        throw new InvalidDataException("The selected CA file is not a CA certificate.");

                    var checkRevocation = setting.mqttTLSCheckRevocation;
                    if (!checkRevocation)
                    {
                        Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
                        {
                            title = "MQTT SECURITY WARNING: certificate revocation mode is explicitly NoCheck",
                            data = new byte[0],
                            color = Tools.Logger.GetThemeBrush("AppDangerBrush", Brushes.OrangeRed)
                        });
                    }

                    builder.WithTls(new MqttClientOptionsBuilderTlsParameters
                    {
                        UseTls = true,
                        SslProtocol = System.Security.Authentication.SslProtocols.Tls12,
                        AllowUntrustedCertificates = false,
                        IgnoreCertificateChainErrors = false,
                        IgnoreCertificateRevocationErrors = !checkRevocation,
                        CertificateValidationHandler = eventArgs =>
                        {
                            try
                            {
                                if (eventArgs == null)
                                    return false;

                                string failureReason;
                                var certificateAccepted = ValidateCertificateWithCustomCa(
                                    eventArgs.Certificate,
                                    eventArgs.Chain,
                                    eventArgs.SslPolicyErrors,
                                    certCa,
                                    DateTime.UtcNow,
                                    checkRevocation,
                                    out failureReason);
                                if (!certificateAccepted)
                                {
                                    Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
                                    {
                                        title = $"MQTT event: ‼ TLS certificate rejected ({failureReason})",
                                        data = new byte[0],
                                        color = Tools.Logger.GetThemeBrush("AppDangerBrush", Brushes.OrangeRed)
                                    });
                                }
                                return certificateAccepted;
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
                                    {
                                        title = $"MQTT event: ‼ TLS certificate validation failed closed ({ex.GetBaseException().Message})",
                                        data = new byte[0],
                                        color = Tools.Logger.GetThemeBrush("AppDangerBrush", Brushes.OrangeRed)
                                    });
                                }
                                catch { }
                                return false;
                            }
                        },
                        // The CA is only a trust anchor; only the client identity is sent.
                        Certificates = new List<X509Certificate> { certClient },
                    });
                }
            }

            if (setting.mqttWs)
                builder.WithWebSocketServer($"{setting.mqttServer}:{setting.mqttPort}{setting.mqttWsPath}");
            else
                builder.WithTcpServer(setting.mqttServer, setting.mqttPort);
            if (setting.mqttCleanSession)
                builder.WithCleanSession();
            return builder.Build();
        }

        private async void MqttTestPage_Unloaded(object sender, RoutedEventArgs e)
        {
            IMqttClient client;
            CancellationTokenSource cts;
            lock (mqttStateLock)
            {
                client = mqttClient;
                cts = mqttAttemptCts;
                activeMqttAttempt = ++mqttAttemptSequence;
                mqttConnectionState = MqttConnectionState.Disconnected;
                mqttConnectionEstablished = false;
                mqttClient = null;
                mqttAttemptCts = null;
                MqttIsConnected = false;
            }

            try { cts?.Cancel(); } catch { }
            try
            {
                if (client != null && client.IsConnected)
                    await client.DisconnectAsync().ConfigureAwait(true);
            }
            catch { }
            finally
            {
                try { cts?.Dispose(); } catch { }
            }
        }

        private void MqttPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Tools.Global.setting != null)
                Tools.Global.setting.mqttPassword = PasswordTextBox.Password;
        }

        private void MqttTlsPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Tools.Global.setting != null)
                Tools.Global.setting.mqttTLSCertClientPassword = MQTTTLSPassword.Password;
        }

        private void subcribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(subcribeTextBox.Text))
            {
                Tools.MessageBox.Show("no subcribe topic!");
                return;
            }
            var currentClient = mqttClient;
            if (currentClient != null && currentClient.IsConnected)
            {
                var topic = subcribeTextBox.Text;
                var qos = int.Parse(subQOSComboBox.Text);
                Task.Run(async () =>
                {
                    try
                    {
                        var r = await currentClient.SubscribeAsync(
                            new MqttTopicFilterBuilder()
                            .WithTopic(topic)
                            .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)qos)
                            .Build());
                        this.Dispatcher.Invoke(new Action(delegate
                        {
                            foreach (string i in subListBox.Items)
                            {
                                if (i == topic)
                                    return;
                            }
                            subListBox.Items.Add(topic);
                        }));
                    }
                    catch { }
                });
            }
        }

        private void publishButton_Click(object sender, RoutedEventArgs e)
        {
            if(string.IsNullOrEmpty(publishTopicTextBox.Text))
            {
                Tools.MessageBox.Show("no publish topic!");
                return;
            }
            var currentClient = mqttClient;
            if (currentClient != null && currentClient.IsConnected)
            {
                var topic = publishTopicTextBox.Text;
                var payload = HexCheckBox.IsChecked ?? false ?
                    Tools.Global.Hex2Byte(PublishTextBox.Text) :
                    Tools.Global.GetEncoding().GetBytes(PublishTextBox.Text);
                var qos = int.Parse(publishQOSComboBox.Text);
                Task.Run(() =>
                {
                    Publish(topic, payload, qos);
                });
            }
        }

        private bool Publish(string topic, byte[] payload, int qos)
        {
            try
            {
                var client = mqttClient;
                if (client == null || !client.IsConnected)
                    return false;

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(qos)
                    .Build();
                client.PublishAsync(message, CancellationToken.None).Wait();
                Tools.Logger.ShowDataRaw(new Tools.DataShowRaw
                {
                    title = $"MQTT ← {message.Topic}({(int)message.QualityOfServiceLevel})",
                    data = message.Payload ?? new byte[0],
                    color = Tools.Logger.GetThemeBrush("AppDataSentBrush", Brushes.IndianRed)
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void LoadCertificate_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            System.Windows.Forms.OpenFileDialog OpenFileDialog = new System.Windows.Forms.OpenFileDialog();
            OpenFileDialog.Filter = (string)((TextBlock)sender).Tag switch
            {
                "CA" => "crt file|*.crt",
                "Client" => "pfx file|*.pfx",
                _ => "",
            };
            if (OpenFileDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    switch ((string)((TextBlock)sender).Tag)
                    {
                        case "CA":
                            Tools.Global.setting.mqttTLSCertCaPath = OpenFileDialog.FileName;
                            break;
                        case "Client":
                            Tools.Global.setting.mqttTLSCertClientPath = OpenFileDialog.FileName;
                            break;
                    }
                }
                catch { }
            }
        }
    }
}
