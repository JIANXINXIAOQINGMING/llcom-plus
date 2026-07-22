using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    public sealed class SshClientOptions
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string PrivateKeyPath { get; set; } = string.Empty;
        public string ExtraArguments { get; set; } = string.Empty;
        public string SshPath { get; set; } = string.Empty;
    }

    public sealed class SshInteractiveConnection : IDisposable
    {
        private readonly Process process;
        private readonly Stream input;
        private bool disposed;

        internal SshInteractiveConnection(
            Process process,
            Action<byte[]> onData,
            Action onConnected,
            Action<int?> onClosed)
        {
            this.process = process;
            input = process.StandardInput.BaseStream;
            process.EnableRaisingEvents = true;
            process.Exited += (_, __) => onClosed?.Invoke(SafeExitCode());
            StartReadLoop(process.StandardOutput.BaseStream, onData);
            StartDiagnosticReadLoop(process.StandardError.BaseStream, onData, onConnected);
        }

        public void Send(byte[] data)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(SshInteractiveConnection));

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

        private static void StartDiagnosticReadLoop(Stream stream, Action<byte[]> onData, Action onConnected)
        {
            Task.Run(() =>
            {
                var buffer = new byte[8192];
                var pendingText = new StringBuilder();
                var connectedRaised = 0;
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

                    pendingText.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (pendingText.Length > 32768)
                        pendingText.Remove(0, pendingText.Length - 16384);
                    var text = pendingText.ToString();
                    if ((text.IndexOf("Authenticated to ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         text.IndexOf("Authentication succeeded", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        Interlocked.Exchange(ref connectedRaised, 1) == 0)
                    {
                        onConnected?.Invoke();
                    }
                }
            });
        }
    }

    public static class SshCli
    {
        public static SshClientOptions FromGlobalSettings(string host, int port)
        {
            var setting = Global.setting;
            return new SshClientOptions
            {
                Host = host,
                Port = port,
                UserName = setting.tcpClientSshUserName,
                PrivateKeyPath = setting.tcpClientSshPrivateKeyPath,
                ExtraArguments = setting.tcpClientSshExtraArguments,
                SshPath = setting.tcpClientSshPath
            };
        }

        public static SshInteractiveConnection StartInteractive(
            SshClientOptions options,
            Action<byte[]> onData,
            Action onConnected,
            Action<int?> onClosed)
        {
            var process = StartSsh(options);
            return new SshInteractiveConnection(process, onData, onConnected, onClosed);
        }

        public static string BuildDiagnosticSummary(SshClientOptions options)
        {
            var builder = new StringBuilder();
            builder.AppendLine("OpenSSH backend enabled.");
            builder.AppendLine($"Endpoint: {options.Host}:{options.Port}");
            if (!string.IsNullOrWhiteSpace(options.UserName))
                builder.AppendLine($"User: {options.UserName}");
            if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
                builder.AppendLine($"Private key: {options.PrivateKeyPath}");
            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
                builder.AppendLine($"Extra arguments: {options.ExtraArguments}");
            return builder.ToString();
        }

        private static Process StartSsh(SshClientOptions options)
        {
            ValidateOptions(options);
            var executable = FindSshExecutable(options.SshPath);
            var arguments = BuildSshArguments(options);
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
                throw new InvalidOperationException("SSH 启动失败。");

            return process;
        }

        private static string BuildSshArguments(SshClientOptions options)
        {
            var args = new List<string>
            {
                "-v",
                "-tt",
                "-p",
                Math.Max(1, Math.Min(65535, options.Port)).ToString(),
                "-o",
                "ServerAliveInterval=30",
                "-o",
                "ServerAliveCountMax=3"
            };

            if (!string.IsNullOrWhiteSpace(options.UserName))
            {
                args.Add("-l");
                args.Add(options.UserName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            {
                args.Add("-i");
                args.Add(options.PrivateKeyPath.Trim());
            }

            var argumentText = string.Join(" ", args.Select(QuoteArgument));
            if (!string.IsNullOrWhiteSpace(options.ExtraArguments))
                argumentText += " " + options.ExtraArguments.Trim();

            argumentText += " " + QuoteArgument(NormalizeHost(options.Host));
            return argumentText;
        }

        private static void ValidateOptions(SshClientOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Host))
                throw new ArgumentException("SSH 服务器地址不能为空。", nameof(options));
            if (options.Port < 1 || options.Port > 65535)
                throw new ArgumentOutOfRangeException(nameof(options), "SSH 服务器端口必须在 1 到 65535 之间。");
            if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath) && !File.Exists(options.PrivateKeyPath.Trim()))
                throw new FileNotFoundException("SSH 私钥文件不存在：" + options.PrivateKeyPath, options.PrivateKeyPath);
        }

        private static string NormalizeHost(string host)
        {
            var value = (host ?? string.Empty).Trim();
            if (value.StartsWith("[") && value.EndsWith("]"))
                value = value.Substring(1, value.Length - 2);
            return value;
        }

        private static string FindSshExecutable(string configuredPath)
        {
            var configured = (configuredPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var localCandidates = new[]
            {
                Path.Combine(Global.AppPath, "OpenSSH", "ssh.exe"),
                Path.Combine(Global.AppPath, "openssh", "ssh.exe"),
                Path.Combine(Global.AppPath, "ssh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe")
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
                    var candidate = Path.Combine(dir.Trim(), "ssh.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            throw new FileNotFoundException("找不到 ssh.exe。请安装 Windows OpenSSH 客户端，或在 SSH 配置中指定 ssh.exe 路径。");
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
    }
}
