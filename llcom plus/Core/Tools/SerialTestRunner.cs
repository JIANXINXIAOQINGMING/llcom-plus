using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    internal enum SerialTestMatchMode { None, Contains, ExactLine, Regex }

    internal sealed class SerialTestOptions
    {
        internal SerialTestOptions(int mode, string expected, int timeoutMs, int retries, bool continueOnFailure)
        {
            if (mode < 0 || mode > 3) throw new ArgumentException("响应验证方式无效。");
            if (timeoutMs < 100 || timeoutMs > 600000)
                throw new ArgumentException("响应超时请输入 100～600000 毫秒。");
            if (retries < 0 || retries > 10) throw new ArgumentException("重试次数请输入 0～10；0 表示不重发。");
            Mode = (SerialTestMatchMode)mode;
            Expected = expected ?? "";
            if (Mode != SerialTestMatchMode.None && (Expected.Length == 0 || Expected.Length > 1024))
                throw new ArgumentException("预期响应不能为空，且不能超过 1024 个字符。");
            if (Mode == SerialTestMatchMode.Regex)
                Pattern = new Regex(Expected, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            TimeoutMs = timeoutMs; Retries = retries; ContinueOnFailure = continueOnFailure;
        }
        internal SerialTestMatchMode Mode { get; }
        internal string Expected { get; }
        internal int TimeoutMs { get; }
        internal int Retries { get; }
        internal bool ContinueOnFailure { get; }
        internal Regex Pattern { get; }
    }

    internal sealed class SerialTestResult
    {
        internal bool Success { get; set; }
        internal bool Validated { get; set; }
        internal int Attempts { get; set; }
        internal double? ResponseMs { get; set; }
        internal string Outcome { get; set; }
        internal string Details { get; set; }
    }

    internal static class SerialTestRunner
    {
        // Only a response timeout can be retried, and only when explicitly requested.
        // A write exception may occur after partial delivery; never automatically replay it.
        internal static async Task<SerialTestResult> ExecuteAsync(SerialTestOptions options,
            string identity, Func<CancellationToken, Task> send, Func<bool> connectionOpen,
            CancellationToken token)
        {
            if (options == null || send == null) throw new ArgumentNullException();
            if (string.IsNullOrEmpty(identity) || identity.StartsWith("serial-all:", StringComparison.Ordinal))
                throw new InvalidOperationException("测试需要选择一个明确的串口，不支持“全部”广播目标。");
            for (int attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (connectionOpen?.Invoke() != true)
                    throw new InvalidOperationException("测试串口已断开；请重新连接后手动开始。");
                if (options.Mode == SerialTestMatchMode.None)
                {
                    await send(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new SerialTestResult { Success = true, Attempts = 1, Outcome = "Sent",
                        Details = "发送完成（未验证响应）" };
                }
                // Subscribe before requesting the send: a device may reply before Write returns.
                using (var response = new SerialTestResponseWaiter(identity, options))
                {
                    response.Arm();
                    await send(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    // Matching/decoding never runs on the hardware IO publisher or the UI thread.
                    var result = await Task.Run(() => response.WaitAsync(connectionOpen, token), token)
                        .ConfigureAwait(false);
                    result.Attempts = attempt;
                    if (result.Success || result.Outcome != "Timeout" || attempt > options.Retries)
                        return result;
                }
            }
        }
    }

    internal sealed class SerialTestResponseWaiter : IDisposable
    {
        internal const int MaximumResponseBytes = 64 * 1024;
        private readonly object gate = new object();
        private readonly Queue<Packet> pending = new Queue<Packet>();
        private readonly string identity;
        private readonly SerialTestOptions options;
        private readonly Stopwatch clock = new Stopwatch();
        private TaskCompletionSource<bool> signal = NewSignal();
        private long afterId;
        private bool armed, disposed, overflow;
        private int receivedBytes;
        private struct Packet { internal SerialTraceEntry Entry; internal double ElapsedMs; }

        internal SerialTestResponseWaiter(string identity, SerialTestOptions options)
        {
            this.identity = identity; this.options = options;
            SerialTraceHub.Recorded += OnRecorded;
        }
        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Arm()
        {
            lock (gate)
            {
                afterId = SerialTraceHub.LastId;
                clock.Restart();
                armed = true;
            }
        }
        private void OnRecorded(object sender, SerialTraceEntry entry)
        {
            if (entry.Kind != SerialTraceKind.Rx || !SerialTraceHub.MatchesConnection(entry, identity)) return;
            lock (gate)
            {
                if (!armed || disposed || entry.Id <= afterId) return;
                if (overflow) return;
                if (entry.ByteCount > MaximumResponseBytes - receivedBytes || pending.Count >= 256)
                    overflow = true;
                else
                {
                    receivedBytes += entry.ByteCount;
                    pending.Enqueue(new Packet { Entry = entry, ElapsedMs = clock.Elapsed.TotalMilliseconds });
                }
                signal.TrySetResult(true);
            }
        }

        internal async Task<SerialTestResult> WaitAsync(Func<bool> connectionOpen, CancellationToken token)
        {
            var deadline = Stopwatch.StartNew();
            var text = new StringBuilder();
            Decoder decoder = null;
            int encoding = 0;
            double responseMs = 0;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (connectionOpen?.Invoke() != true)
                        throw new InvalidOperationException("等待响应时串口已断开；测试停止，不自动重发。");
                    Packet[] batch;
                    Task wait;
                    lock (gate)
                    {
                        if (overflow) return Failed("Overflow", "响应超过 64 KiB / 256 个待处理数据包，已停止验证，未自动重发。");
                        batch = pending.ToArray(); pending.Clear();
                        signal = NewSignal(); wait = signal.Task;
                    }
                    foreach (var packet in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        if (decoder == null)
                        {
                            encoding = packet.Entry.EncodingCodePage;
                            try { decoder = Encoding.GetEncoding(encoding).GetDecoder(); }
                            catch (ArgumentException) { decoder = Encoding.UTF8.GetDecoder(); }
                        }
                        else if (encoding != packet.Entry.EncodingCodePage)
                            return Failed("EncodingChanged", "步骤执行过程中编码已改变，请重新开始测试。");
                        var data = packet.Entry.GetData();
                        var chars = new char[Encoding.GetEncoding(DecoderFallbackCodePage(encoding)).GetMaxCharCount(data.Length)];
                        int count = decoder.GetChars(data, 0, data.Length, chars, 0, false);
                        text.Append(chars, 0, count); responseMs = packet.ElapsedMs;
                        if (Matches(text.ToString()))
                            return Passed(responseMs, text.ToString());
                    }
                    if (deadline.ElapsedMilliseconds >= options.TimeoutMs)
                    {
                        // A deadline is not a line terminator. An unfinished
                        // matching prefix must never release a dependent step.
                        return Failed("Timeout", text.Length == 0
                            ? "发送完成后未收到响应，等待超时。"
                            : "已收到数据，但未匹配预期响应。\r\n" + Preview(text.ToString()));
                    }
                    await Task.WhenAny(wait, Task.Delay((int)Math.Min(100,
                        Math.Max(1, options.TimeoutMs - deadline.ElapsedMilliseconds)), token)).ConfigureAwait(false);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return Failed("RegexTimeout", "正则匹配超过 100 毫秒，已停止验证，请简化表达式；未自动重发。");
            }
        }
        private static int DecoderFallbackCodePage(int encoding)
        {
            try { return Encoding.GetEncoding(encoding).CodePage; }
            catch (ArgumentException) { return Encoding.UTF8.CodePage; }
        }
        private bool Matches(string text)
        {
            if (options.Mode == SerialTestMatchMode.Contains)
                return text.IndexOf(options.Expected, StringComparison.Ordinal) >= 0;
            if (options.Mode == SerialTestMatchMode.Regex) return options.Pattern.IsMatch(text);
            if (options.Mode == SerialTestMatchMode.ExactLine)
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                int complete = lines.Length - 1;
                for (int i = 0; i < complete; i++)
                    if (string.Equals(lines[i], options.Expected, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        private static string Preview(string text) => text.Length <= 2048 ? text : text.Substring(0, 2048) + "…";
        private static SerialTestResult Passed(double elapsed, string text) => new SerialTestResult
        { Success = true, Validated = true, ResponseMs = elapsed, Outcome = "Passed", Details = Preview(text) };
        private static SerialTestResult Failed(string outcome, string details) => new SerialTestResult
        { Success = false, Validated = true, Outcome = outcome, Details = details };
        public void Dispose()
        {
            SerialTraceHub.Recorded -= OnRecorded;
            lock (gate) { disposed = true; pending.Clear(); signal.TrySetResult(true); }
        }
    }

    internal sealed class SerialTestReportRow
    {
        internal DateTime Timestamp;
        internal int Round, Step;
        internal string Port, Command;
        internal SerialTestResult Result;
    }
    internal static class SerialTestReport
    {
        internal static string ToCsv(IEnumerable<SerialTestReportRow> rows)
        {
            var csv = new StringBuilder("Time,Round,Step,Port,Command,Outcome,Attempts,ResponseMs,Details\r\n");
            foreach (var row in rows)
            {
                var result = row.Result;
                csv.AppendLine(string.Join(",", new[] { row.Timestamp.ToString("o"), row.Round.ToString(CultureInfo.InvariantCulture),
                    row.Step.ToString(CultureInfo.InvariantCulture), row.Port, row.Command, result.Outcome,
                    result.Attempts.ToString(CultureInfo.InvariantCulture),
                    result.ResponseMs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", result.Details }.Select(Escape)));
            }
            return csv.ToString();
        }
        private static string Escape(string value)
        {
            value = value ?? "";
            // Device-controlled content must remain text when opened in spreadsheet software.
            var probe = value.TrimStart();
            if (probe.Length > 0 && "=+-@".IndexOf(probe[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
