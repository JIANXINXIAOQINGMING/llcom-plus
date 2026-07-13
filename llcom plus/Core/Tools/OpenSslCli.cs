using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    public sealed class OpenSslClientOptions
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string TargetHost { get; set; } = string.Empty;
        public bool UseDtls { get; set; }
        public int ProtocolType { get; set; }
        public int AuthMode { get; set; }
        public string CaCertPath { get; set; } = string.Empty;
        public string ClientCertPath { get; set; } = string.Empty;
        public string ClientKeyPath { get; set; } = string.Empty;
        public string ClientCertPassword { get; set; } = string.Empty;
        public string CipherSuites { get; set; } = string.Empty;
        public bool CheckRevocation { get; set; }
        public bool PrintDetails { get; set; }
    }

    public sealed class OpenSslCommandResult
    {
        public byte[] Output { get; set; } = new byte[0];
        public string Diagnostics { get; set; } = string.Empty;
        public int? ExitCode { get; set; }
        public bool TimedOut { get; set; }
    }

    public sealed class OpenSslInteractiveConnection : IDisposable
    {
        private readonly Process process;
        private readonly Stream input;
        private bool disposed;

        internal OpenSslInteractiveConnection(
            Process process,
            Action<byte[]> onData,
            Action<string> onDiagnostics,
            Action<int?> onClosed)
        {
            this.process = process;
            input = process.StandardInput.BaseStream;
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) => onClosed?.Invoke(SafeExitCode());
            StartReadLoop(process.StandardOutput.BaseStream, onData);
            StartTextReadLoop(process.StandardError.BaseStream, onDiagnostics);
        }

        public void Send(byte[] data)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(OpenSslInteractiveConnection));

            input.Write(data, 0, data.Length);
            input.Flush();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;

            try { input.Close(); } catch { }
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }
            try { process.Dispose(); } catch { }
        }

        private int? SafeExitCode()
        {
            try { return process.ExitCode; }
            catch { return null; }
        }

        private static void StartReadLoop(Stream stream, Action<byte[]> onData)
        {
            Task.Run(() =>
            {
                var buffer = new byte[8192];
                while (true)
                {
                    int read;
                    try { read = stream.Read(buffer, 0, buffer.Length); }
                    catch { return; }
                    if (read <= 0)
                        return;

                    var data = new byte[read];
                    Buffer.BlockCopy(buffer, 0, data, 0, read);
                    onData?.Invoke(data);
                }
            });
        }

        private static void StartTextReadLoop(Stream stream, Action<string> onText)
        {
            Task.Run(() =>
            {
                var buffer = new byte[4096];
                var textBuffer = new StringBuilder();
                var blockBuffer = new StringBuilder();
                var blockExpectedLength = -1;
                var blockActualLength = 0;

                void FlushBlock()
                {
                    if (blockBuffer.Length <= 0)
                        return;

                    onText?.Invoke(blockBuffer.ToString());
                    blockBuffer.Clear();
                    blockExpectedLength = -1;
                    blockActualLength = 0;
                }

                void FlushTextLine(string line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        onText?.Invoke(line + Environment.NewLine);
                }

                void ProcessLine(string line)
                {
                    if (IsOpenSslMessageHeader(line, out var expectedLength))
                    {
                        FlushBlock();
                        blockBuffer.AppendLine(line);
                        blockExpectedLength = expectedLength;
                        blockActualLength = 0;
                        if (blockExpectedLength == 0)
                            FlushBlock();
                        return;
                    }

                    if (blockBuffer.Length > 0 && IsOpenSslHexLine(line, out var hexByteCount))
                    {
                        blockBuffer.AppendLine(line);
                        blockActualLength += hexByteCount;
                        if (blockExpectedLength >= 0 && blockActualLength >= blockExpectedLength)
                            FlushBlock();
                        return;
                    }

                    FlushBlock();
                    FlushTextLine(line);
                }

                void ProcessText(string text)
                {
                    textBuffer.Append(text);
                    while (true)
                    {
                        var current = textBuffer.ToString();
                        var lineEnd = current.IndexOf('\n');
                        if (lineEnd < 0)
                            return;

                        var line = current.Substring(0, lineEnd).TrimEnd('\r');
                        textBuffer.Remove(0, lineEnd + 1);
                        ProcessLine(line);
                    }
                }

                while (true)
                {
                    int read;
                    try { read = stream.Read(buffer, 0, buffer.Length); }
                    catch
                    {
                        FlushBlock();
                        return;
                    }
                    if (read <= 0)
                    {
                        if (textBuffer.Length > 0)
                        {
                            ProcessLine(textBuffer.ToString());
                            textBuffer.Clear();
                        }
                        FlushBlock();
                        return;
                    }

                    ProcessText(Encoding.UTF8.GetString(buffer, 0, read));
                }
            });
        }

        private static bool IsOpenSslMessageHeader(string line, out int expectedLength)
        {
            expectedLength = -1;
            var text = (line ?? string.Empty).Trim();
            if (!(text.StartsWith("<<<", StringComparison.Ordinal) || text.StartsWith(">>>", StringComparison.Ordinal)))
                return false;

            var marker = "[length ";
            var start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return false;

            start += marker.Length;
            var end = text.IndexOf(']', start);
            if (end <= start)
                return true;

            if (int.TryParse(
                text.Substring(start, end - start),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length))
            {
                expectedLength = length;
            }
            return true;
        }

        private static bool IsOpenSslHexLine(string line, out int byteCount)
        {
            byteCount = 0;
            var parts = (line ?? string.Empty)
                .Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            foreach (var part in parts)
            {
                if (part.Length != 2 ||
                    !byte.TryParse(
                        part,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out _))
                {
                    return false;
                }
            }

            byteCount = parts.Length;
            return true;
        }
    }

    public static class OpenSslCli
    {
        public static OpenSslClientOptions FromGlobalSettings(string host, int port, bool useDtls)
        {
            var setting = Global.setting;
            var targetHost = string.IsNullOrWhiteSpace(setting.tcpClientSslTargetHost)
                ? host
                : setting.tcpClientSslTargetHost.Trim();

            return new OpenSslClientOptions
            {
                Host = host,
                Port = port,
                TargetHost = targetHost,
                UseDtls = useDtls,
                ProtocolType = setting.tcpClientSslProtocolType,
                AuthMode = setting.tcpClientSslAuthMode,
                CaCertPath = setting.tcpClientSslCaCertPath,
                ClientCertPath = setting.tcpClientSslClientCertPath,
                ClientKeyPath = setting.tcpClientSslClientKeyPath,
                ClientCertPassword = setting.tcpClientSslClientCertPassword,
                CipherSuites = setting.tcpClientSslCipherSuites,
                CheckRevocation = setting.tcpClientSslCheckRevocation,
                PrintDetails = setting.tcpClientSslPrintDetails
            };
        }

        public static OpenSslInteractiveConnection StartInteractive(
            OpenSslClientOptions options,
            Action<byte[]> onData,
            Action<string> onDiagnostics,
            Action<int?> onClosed)
        {
            var process = StartSClient(options, interactive: true);
            return new OpenSslInteractiveConnection(process, onData, onDiagnostics, onClosed);
        }

        public static async Task<OpenSslCommandResult> SendAsync(
            OpenSslClientOptions options,
            byte[] request,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            using (var process = StartSClient(options, interactive: false))
            {
                if (timeoutMilliseconds <= 0)
                    timeoutMilliseconds = 30000;

                var outputTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
                var errorTask = ReadAllTextAsync(process.StandardError.BaseStream, cancellationToken);

                await process.StandardInput.BaseStream.WriteAsync(request, 0, request.Length, cancellationToken);
                process.StandardInput.Close();

                var waitTask = Task.Run(() =>
                {
                    process.WaitForExit();
                    return process.ExitCode;
                });

                var completed = await Task.WhenAny(waitTask, Task.Delay(timeoutMilliseconds, cancellationToken));
                var timedOut = completed != waitTask;
                if (timedOut)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    try { process.WaitForExit(2000); } catch { }
                }

                byte[] output;
                string diagnostics;
                try { output = await outputTask; } catch { output = new byte[0]; }
                try { diagnostics = await errorTask; } catch { diagnostics = string.Empty; }

                int? exitCode = null;
                if (!timedOut)
                    exitCode = await waitTask;

                return new OpenSslCommandResult
                {
                    Output = output,
                    Diagnostics = diagnostics,
                    ExitCode = exitCode,
                    TimedOut = timedOut
                };
            }
        }

        public static string BuildDiagnosticSummary(OpenSslClientOptions options)
        {
            var builder = new StringBuilder();
            builder.AppendLine("OpenSSL backend enabled.");
            builder.AppendLine($"Endpoint: {options.Host}:{options.Port}");
            builder.AppendLine($"SNI/target host: {options.TargetHost}");
            builder.AppendLine($"Transport: {(options.UseDtls ? "DTLS/UDP" : "TLS/TCP")}");
            builder.AppendLine($"Protocol option: {GetProtocolDisplayName(options)}");
            builder.AppendLine($"Auth mode: {GetAuthModeName(options.AuthMode)}");
            if (!string.IsNullOrWhiteSpace(options.CaCertPath))
                builder.AppendLine($"CA file: {options.CaCertPath}");
            if (!string.IsNullOrWhiteSpace(options.ClientCertPath))
                builder.AppendLine($"Client cert: {options.ClientCertPath}");
            if (!string.IsNullOrWhiteSpace(options.ClientKeyPath))
                builder.AppendLine($"Client key: {options.ClientKeyPath}");
            if (!string.IsNullOrWhiteSpace(options.CipherSuites))
                builder.AppendLine($"Cipher config: {options.CipherSuites}");
            return builder.ToString();
        }

        private static Process StartSClient(OpenSslClientOptions options, bool interactive)
        {
            var executable = FindOpenSslExecutable();
            var arguments = BuildSClientArguments(options, interactive);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("OpenSSL 启动失败。");

            return process;
        }

        private static string BuildSClientArguments(OpenSslClientOptions options, bool interactive)
        {
            var args = new List<string>
            {
                "s_client",
                "-connect",
                $"{options.Host}:{options.Port}"
            };

            if (!string.IsNullOrWhiteSpace(options.TargetHost) && !IPAddress.TryParse(options.TargetHost, out _))
            {
                args.Add("-servername");
                args.Add(options.TargetHost);
            }

            AddProtocolArguments(args, options);
            AddCertificateArguments(args, options);
            AddCipherArguments(args, options);

            if (options.PrintDetails)
            {
                args.Add("-state");
                args.Add("-msg");
                args.Add("-showcerts");
            }

            if (interactive)
            {
                args.Add("-quiet");
                args.Add("-ign_eof");
            }
            else
            {
                args.Add("-quiet");
            }

            return string.Join(" ", args.Select(QuoteArgument));
        }

        private static void AddProtocolArguments(List<string> args, OpenSslClientOptions options)
        {
            if (options.UseDtls)
            {
                switch (options.ProtocolType)
                {
                    case 1:
                    case 2:
                        args.Add("-dtls1");
                        break;
                    case 3:
                    case 4:
                        args.Add("-dtls1_2");
                        break;
                    default:
                        args.Add("-dtls");
                        break;
                }
                args.Add("-timeout");
                return;
            }

            switch (options.ProtocolType)
            {
                case 1:
                    args.Add("-tls1");
                    break;
                case 2:
                    args.Add("-tls1_1");
                    break;
                case 3:
                    args.Add("-tls1_2");
                    break;
                case 4:
                    args.Add("-tls1_3");
                    break;
            }
        }

        private static void AddCertificateArguments(List<string> args, OpenSslClientOptions options)
        {
            if (options.AuthMode > 0)
            {
                args.Add("-verify");
                args.Add("10");
                args.Add("-verify_return_error");
                if (!string.IsNullOrWhiteSpace(options.TargetHost) && !IPAddress.TryParse(options.TargetHost, out _))
                {
                    args.Add("-verify_hostname");
                    args.Add(options.TargetHost);
                }
            }

            if (!string.IsNullOrWhiteSpace(options.CaCertPath))
            {
                args.Add("-CAfile");
                args.Add(options.CaCertPath);
            }

            if (options.CheckRevocation)
                args.Add("-crl_check");

            if (options.AuthMode < 2)
                return;

            if (!string.IsNullOrWhiteSpace(options.ClientCertPath))
            {
                args.Add("-cert");
                args.Add(options.ClientCertPath);
            }

            if (!string.IsNullOrWhiteSpace(options.ClientKeyPath))
            {
                args.Add("-key");
                args.Add(options.ClientKeyPath);
            }

            if (!string.IsNullOrEmpty(options.ClientCertPassword))
            {
                args.Add("-pass");
                args.Add("pass:" + options.ClientCertPassword);
            }
        }

        private static void AddCipherArguments(List<string> args, OpenSslClientOptions options)
        {
            var cipherSuites = SplitCipherSuites(options.CipherSuites).ToList();
            if (cipherSuites.Count == 0)
                return;

            var tls13Suites = cipherSuites
                .Where(IsTls13CipherSuite)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var legacySuites = cipherSuites
                .Where(suite => !IsTls13CipherSuite(suite))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (legacySuites.Count > 0)
            {
                args.Add("-cipher");
                args.Add(string.Join(":", legacySuites));
            }

            if (tls13Suites.Count > 0)
            {
                args.Add("-ciphersuites");
                args.Add(string.Join(":", tls13Suites));
            }
        }

        private static bool IsTls13CipherSuite(string cipherSuite)
        {
            return cipherSuite != null &&
                   cipherSuite.Trim().StartsWith("TLS_", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitCipherSuites(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ':', ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0);
        }

        public static IReadOnlyList<string> GetAvailableCipherSuites()
        {
            var cipherSuites = TryGetAvailableCipherSuites("ciphers -v ALL:@SECLEVEL=0");
            if (cipherSuites.Count == 0)
                cipherSuites = TryGetAvailableCipherSuites("ciphers -v ALL");

            return cipherSuites;
        }

        private static IReadOnlyList<string> TryGetAvailableCipherSuites(string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = FindOpenSslExecutable(),
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                        return Array.Empty<string>();

                    var output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(3000))
                    {
                        try { process.Kill(); } catch { }
                        return Array.Empty<string>();
                    }

                    return ParseCipherListOutput(output);
                }
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> ParseCipherListOutput(string output)
        {
            var cipherSuites = new List<string>();
            foreach (var rawLine in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var token = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                foreach (var cipherSuite in token.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var value = cipherSuite.Trim();
                    if (value.Length == 0)
                        continue;
                    if (cipherSuites.Contains(value, StringComparer.OrdinalIgnoreCase))
                        continue;

                    cipherSuites.Add(value);
                }
            }

            return cipherSuites;
        }

        private static string FindOpenSslExecutable()
        {
            var configured = (Global.setting.openSslPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var localCandidates = new[]
            {
                Path.Combine(Global.AppPath, "OpenSSL", "openssl.exe"),
                Path.Combine(Global.AppPath, "openssl", "openssl.exe"),
                Path.Combine(Global.AppPath, "openssl.exe")
            };
            foreach (var local in localCandidates)
            {
                if (File.Exists(local))
                    return local;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "openssl.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            throw new FileNotFoundException("找不到 openssl.exe。请将 OpenSSL 放到程序目录的 OpenSSL 文件夹、PATH，或在设置中配置 openSslPath。");
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read <= 0)
                        break;
                    memory.Write(buffer, 0, read);
                }
                return memory.ToArray();
            }
        }

        private static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = await ReadAllBytesAsync(stream, cancellationToken);
            return Encoding.UTF8.GetString(bytes);
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>' }) < 0)
                return value;

            var builder = new StringBuilder();
            builder.Append('"');
            var backslashCount = 0;

            foreach (var ch in value)
            {
                if (ch == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (ch == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    backslashCount = 0;
                    continue;
                }

                if (backslashCount > 0)
                {
                    builder.Append('\\', backslashCount);
                    backslashCount = 0;
                }

                builder.Append(ch);
            }

            if (backslashCount > 0)
                builder.Append('\\', backslashCount * 2);

            builder.Append('"');
            return builder.ToString();
        }

        private static string GetProtocolDisplayName(OpenSslClientOptions options)
        {
            if (options.UseDtls)
            {
                switch (options.ProtocolType)
                {
                    case 1:
                    case 2:
                        return "DTLS 1.0";
                    case 3:
                    case 4:
                        return "DTLS 1.2";
                    default:
                        return "DTLS auto";
                }
            }

            switch (options.ProtocolType)
            {
                case 1:
                    return "TLS 1.0";
                case 2:
                    return "TLS 1.1";
                case 3:
                    return "TLS 1.2";
                case 4:
                    return "TLS 1.3";
                default:
                    return "TLS auto";
            }
        }

        private static string GetAuthModeName(int authMode)
        {
            switch (authMode)
            {
                case 1:
                    return "单向认证";
                case 2:
                    return "双向认证";
                default:
                    return "无认证";
            }
        }
    }
}
