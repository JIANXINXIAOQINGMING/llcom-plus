using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace llcom_plus.Tools
{
    internal enum PlotInputFormat { Single, Csv, KeyValue, Json, Regex, Script }

    internal sealed class PlotSeriesOptions
    {
        public string Key { get; set; } = "1";
        public string Name { get; set; } = "1";
        public string Unit { get; set; } = "";
        public string Color { get; set; } = "#007AFF";
        public bool Visible { get; set; } = true;
        internal PlotSeriesOptions Copy() => (PlotSeriesOptions)MemberwiseClone();
    }

    internal sealed class PlotConfiguration
    {
        public int Version { get; set; } = 1;
        public string SourcePort { get; set; } = "";
        public PlotInputFormat Format { get; set; } = PlotInputFormat.Csv;
        public string Separator { get; set; } = ",";
        public string Pattern { get; set; } = @"(?<value>-?\d+(?:\.\d+)?)";
        public List<PlotSeriesOptions> Series { get; set; } = new List<PlotSeriesOptions>();
        internal PlotConfiguration Copy() => new PlotConfiguration
        {
            Version = Version, SourcePort = SourcePort, Format = Format, Separator = Separator, Pattern = Pattern,
            Series = (Series ?? new List<PlotSeriesOptions>()).Select(item => item.Copy()).ToList()
        };

        internal void Validate()
        {
            if (Version != 1 || !Enum.IsDefined(typeof(PlotInputFormat), Format))
                throw new FormatException("Unsupported plot configuration version or format.");
            if ((SourcePort ?? "").Length > 128 || (Pattern ?? "").Length > 512)
                throw new FormatException("Port name or regular expression is too long.");
            if (Separator != "," && Separator != ";" && Separator != "\t")
                throw new FormatException("Choose comma, semicolon or tab as the separator.");
            if (Series == null || Series.Count > PlotWorkflow.MaximumSeries || Series.Any(item => item == null))
                throw new FormatException("At most 10 series are supported.");
            if (Series.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 128 ||
                (item.Name ?? "").Length > 80 || (item.Unit ?? "").Length > 30 ||
                !Regex.IsMatch(item.Color ?? "", "^#[0-9a-fA-F]{6}$", RegexOptions.None, TimeSpan.FromMilliseconds(50))) ||
                Series.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != Series.Count)
                throw new FormatException("Series keys must be unique; colors must use #RRGGBB.");
            if (Format == PlotInputFormat.Regex) PlotWorkflow.CreateRegex(Pattern);
        }
    }

    internal sealed class PlotSample
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
    }

    internal sealed class PlotSeriesSnapshot
    {
        public PlotSeriesOptions Options { get; set; }
        public PlotSample[] Samples { get; set; }
    }

    internal sealed class PlotSnapshot
    {
        public long Revision { get; set; }
        public long AcceptedLines { get; set; }
        public long RejectedLines { get; set; }
        public int PendingCharacters { get; set; }
        public string Preview { get; set; }
        public string Error { get; set; }
        public string ConnectionId { get; set; }
        public PlotSeriesSnapshot[] Series { get; set; }
    }

    // Parsing and retained chart samples have no dependency on Global or WPF.
    // Every record is newline framed and uses the encoding of its originating COM.
    internal sealed class PlotWorkflow
    {
        internal const int MaximumSeries = 10;
        internal const int MaximumPoints = 5000;
        internal const int MaximumLineLength = 8192;
        private static readonly string[] Palette = { "#007AFF", "#E88422", "#20A46A", "#D25091", "#8565D0", "#14A7AC", "#D55E50", "#9A9D26", "#657BAC", "#9F785A" };
        private readonly object gate = new object();
        private readonly PlotConfiguration configuration;
        private readonly Regex regex;
        private readonly Dictionary<string, Queue<PlotSample>> samples = new Dictionary<string, Queue<PlotSample>>(StringComparer.Ordinal);
        private readonly List<PlotSeriesOptions> series;
        private readonly StringBuilder pendingLine = new StringBuilder();
        private Decoder decoder;
        private int codePage;
        private string connection = "", preview = "", error = "";
        private bool discardingLongLine, discovered;
        private long revision, accepted, rejected;

        internal PlotWorkflow(PlotConfiguration options)
        {
            options.Validate();
            configuration = options.Copy();
            series = configuration.Series.Select(item => item.Copy()).ToList();
            discovered = series.Count > 0;
            if (configuration.Format == PlotInputFormat.Regex) regex = CreateRegex(configuration.Pattern);
            foreach (var item in series) samples.Add(item.Key, new Queue<PlotSample>());
        }

        internal static Regex CreateRegex(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512)
                throw new FormatException("Provide a regular expression of at most 512 characters.");
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }

        internal void Process(SerialTraceEntry entry)
        {
            if (entry == null || entry.Kind != SerialTraceKind.Rx || configuration.Format == PlotInputFormat.Script ||
                !string.Equals(entry.PortName, configuration.SourcePort, StringComparison.OrdinalIgnoreCase)) return;
            lock (gate)
            {
                if (connection != entry.ConnectionId || decoder == null || codePage != entry.EncodingCodePage)
                {
                    // Never join an unfinished line or chart segment across a reopen.
                    if (decoder != null && connection != entry.ConnectionId)
                        foreach (var values in samples.Values) values.Clear();
                    pendingLine.Clear(); discardingLongLine = false;
                    connection = entry.ConnectionId; codePage = entry.EncodingCodePage;
                    try { decoder = Encoding.GetEncoding(codePage).GetDecoder(); }
                    catch (ArgumentException) { decoder = Encoding.UTF8.GetDecoder(); }
                    revision++;
                }
                var data = entry.GetData();
                var characters = new char[1024];
                int offset = 0;
                while (offset < data.Length)
                {
                    decoder.Convert(data, offset, data.Length - offset, characters, 0, characters.Length,
                        false, out int usedBytes, out int usedChars, out bool completed);
                    offset += usedBytes;
                    for (int i = 0; i < usedChars; i++)
                    {
                        char value = characters[i];
                        if (value == '\n' || value == '\r')
                        {
                            if (discardingLongLine) { discardingLongLine = false; pendingLine.Clear(); }
                            else if (pendingLine.Length > 0)
                            {
                                ParseLine(pendingLine.ToString(), entry.Timestamp);
                                pendingLine.Clear();
                            }
                        }
                        else if (!discardingLongLine)
                        {
                            if (pendingLine.Length >= MaximumLineLength)
                            {
                                pendingLine.Clear(); discardingLongLine = true;
                                Reject("Record exceeds 8192 characters; skipped until the next newline.");
                            }
                            else pendingLine.Append(value);
                        }
                    }
                    if (usedBytes == 0 && usedChars == 0) break;
                }
            }
        }

        private static bool TryNumber(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            !double.IsNaN(number) && !double.IsInfinity(number);

        private Dictionary<string, double> ParseValues(string line)
        {
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            Action<string, string> add = (key, text) =>
            {
                if (values.Count >= MaximumSeries && !values.ContainsKey(key)) return;
                if (key.Length > 128 || !TryNumber(text, out double value))
                    throw new FormatException("A selected value is not a finite number.");
                values[key] = value;
            };
            // If the user selected fields, ignore unrelated JSON/key-value fields.
            Func<string, bool> selected = key => !discovered || series.Any(item => item.Key == key);
            switch (configuration.Format)
            {
                case PlotInputFormat.Single:
                    add("1", line.Trim()); break;
                case PlotInputFormat.Csv:
                    var parts = line.Split(configuration.Separator[0]);
                    for (int i = 0; i < parts.Length && (discovered || i < MaximumSeries); i++)
                    {
                        var key = (i + 1).ToString(CultureInfo.InvariantCulture);
                        if (selected(key)) add(key, parts[i].Trim());
                    }
                    break;
                case PlotInputFormat.KeyValue:
                    foreach (var part in line.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int at = part.IndexOf('=');
                        if (at <= 0) continue;
                        var key = part.Substring(0, at).Trim();
                        if (selected(key)) add(key, part.Substring(at + 1).Trim());
                    }
                    break;
                case PlotInputFormat.Json:
                    using (var reader = new JsonTextReader(new StringReader(line)) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                    {
                        var token = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        if (reader.Read()) throw new FormatException("One JSON object or array is required per line.");
                        if (token is JObject obj)
                        {
                            foreach (var property in obj.Properties())
                                if (selected(property.Name) && (property.Value.Type == JTokenType.Integer || property.Value.Type == JTokenType.Float))
                                    add(property.Name, property.Value.ToString(Formatting.None));
                        }
                        else if (token is JArray array)
                        {
                            for (int i = 0; i < array.Count && (discovered || i < MaximumSeries); i++)
                            {
                                var key = (i + 1).ToString(CultureInfo.InvariantCulture);
                                if (selected(key)) add(key, array[i].ToString(Formatting.None));
                            }
                        }
                        else throw new FormatException("Use a JSON object or array.");
                    }
                    break;
                case PlotInputFormat.Regex:
                    var match = regex.Match(line);
                    if (!match.Success) throw new FormatException("The regular expression did not match.");
                    var names = regex.GetGroupNames().Where(name => name != "0").ToArray();
                    if (names.Length == 0) names = new[] { "0" };
                    foreach (var name in names)
                        if (selected(name) && match.Groups[name].Success) add(name, match.Groups[name].Value);
                    break;
            }
            if (values.Count == 0) throw new FormatException("No selected numeric field was found.");
            return values;
        }

        private void ParseLine(string line, DateTime timestamp)
        {
            preview = line.Length > 256 ? line.Substring(0, 256) + "…" : line;
            try
            {
                var values = ParseValues(line);
                if (!discovered)
                {
                    foreach (var key in values.Keys)
                    {
                        series.Add(new PlotSeriesOptions { Key = key, Name = key, Color = Palette[series.Count] });
                        samples.Add(key, new Queue<PlotSample>());
                    }
                    discovered = true;
                }
                foreach (var item in series)
                    if (values.TryGetValue(item.Key, out double value)) Append(item.Key, timestamp, value);
                accepted++; error = ""; revision++;
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is RegexMatchTimeoutException || ex is ArgumentException)
            {
                Reject(ex is RegexMatchTimeoutException ? "Regular expression timed out (50 ms); record skipped." : ex.Message);
            }
        }

        private void Reject(string message) { error = message; rejected++; revision++; }
        private void Append(string key, DateTime time, double value)
        {
            var values = samples[key];
            values.Enqueue(new PlotSample { Timestamp = time, Value = value });
            while (values.Count > MaximumPoints) values.Dequeue();
        }

        internal void AddScriptPoint(double value, int line, DateTime timestamp)
        {
            if (configuration.Format != PlotInputFormat.Script || line < 0 || line >= MaximumSeries ||
                double.IsNaN(value) || double.IsInfinity(value)) return;
            lock (gate)
            {
                string key = (line + 1).ToString(CultureInfo.InvariantCulture);
                if (!samples.ContainsKey(key))
                {
                    if (series.Count >= MaximumSeries) return;
                    series.Add(new PlotSeriesOptions { Key = key, Name = key, Color = Palette[line] });
                    samples.Add(key, new Queue<PlotSample>());
                }
                Append(key, timestamp, value); accepted++; revision++;
                preview = "apiAddPoint(" + value.ToString(CultureInfo.InvariantCulture) + ", " + line + ")";
            }
        }

        internal void UpdateSeriesOptions(IEnumerable<PlotSeriesOptions> options)
        {
            var next = configuration.Copy();
            next.Series = options.Select(item => item.Copy()).ToList();
            next.Validate();
            lock (gate)
            {
                if (!series.Select(item => item.Key).SequenceEqual(next.Series.Select(item => item.Key)))
                    throw new ArgumentException("Series fields changed; restart capture to rediscover fields.");
                series.Clear();
                series.AddRange(next.Series.Select(item => item.Copy()));
                configuration.Series = next.Series;
                revision++;
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                foreach (var values in samples.Values) values.Clear();
                pendingLine.Clear(); decoder?.Reset(); discardingLongLine = false;
                accepted = rejected = 0; error = preview = ""; revision++;
            }
        }

        internal PlotSnapshot Snapshot()
        {
            lock (gate) return new PlotSnapshot
            {
                Revision = revision, AcceptedLines = accepted, RejectedLines = rejected, Preview = preview,
                Error = error, ConnectionId = connection, PendingCharacters = pendingLine.Length,
                Series = series.Select(item => new PlotSeriesSnapshot { Options = item.Copy(), Samples = samples[item.Key].ToArray() }).ToArray()
            };
        }

        internal static string ToCsv(PlotSnapshot snapshot)
        {
            var result = new StringBuilder("Timestamp,Field,Name,Unit,Value\r\n");
            foreach (var row in snapshot.Series.SelectMany(item => item.Samples.Select(sample => new { item.Options, Sample = sample }))
                .OrderBy(row => row.Sample.Timestamp))
                result.Append(CsvCell(row.Sample.Timestamp.ToString("o", CultureInfo.InvariantCulture))).Append(',')
                    .Append(CsvCell(row.Options.Key)).Append(',').Append(CsvCell(row.Options.Name)).Append(',')
                    .Append(CsvCell(row.Options.Unit)).Append(',').Append(row.Sample.Value.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            return result.ToString();
        }

        private static string CsvCell(string value)
        {
            value = value ?? "";
            // Prevent a user-controlled series label from becoming an Excel formula.
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    // The IO callback never performs decoding or regular-expression work.
    internal sealed class PlotCaptureSession : IDisposable
    {
        private const int MaximumQueuedEntries = 256, MaximumQueuedBytes = 2 * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Queue<SerialTraceEntry> pending = new Queue<SerialTraceEntry>();
        private readonly PlotWorkflow workflow;
        private readonly string port;
        private int queuedBytes;
        private bool draining, disposed;
        private long dropped;
        internal PlotCaptureSession(PlotConfiguration configuration)
        {
            workflow = new PlotWorkflow(configuration); port = configuration.SourcePort;
            if (configuration.Format != PlotInputFormat.Script) SerialTraceHub.Recorded += OnRecorded;
        }
        internal PlotWorkflow Workflow => workflow;
        internal long DroppedCount { get { lock (gate) return dropped; } }
        private void OnRecorded(object sender, SerialTraceEntry entry)
        {
            if (entry.Kind != SerialTraceKind.Rx || !string.Equals(entry.PortName, port, StringComparison.OrdinalIgnoreCase)) return;
            lock (gate)
            {
                if (disposed) return;
                if (pending.Count >= MaximumQueuedEntries || queuedBytes + entry.ByteCount > MaximumQueuedBytes)
                {
                    // Discard all partial input after overload, never concatenate across a gap.
                    dropped += pending.Count + 1; pending.Clear(); queuedBytes = 0;
                    pending.Enqueue(null);
                }
                else { pending.Enqueue(entry); queuedBytes += entry.ByteCount; }
                if (draining) return;
                draining = true;
                ThreadPool.QueueUserWorkItem(_ => Drain());
            }
        }
        private void Drain()
        {
            while (true)
            {
                SerialTraceEntry entry;
                lock (gate)
                {
                    if (disposed || pending.Count == 0) { draining = false; return; }
                    entry = pending.Dequeue(); queuedBytes -= entry?.ByteCount ?? 0;
                }
                if (entry == null) workflow.Clear();
                else workflow.Process(entry);
            }
        }
        internal void Clear()
        {
            lock (gate)
            {
                pending.Clear(); queuedBytes = 0;
                pending.Enqueue(null);
                if (!draining) { draining = true; ThreadPool.QueueUserWorkItem(_ => Drain()); }
            }
        }
        public void Dispose()
        {
            SerialTraceHub.Recorded -= OnRecorded;
            lock (gate) { disposed = true; pending.Clear(); queuedBytes = 0; }
        }
    }

    internal static class PlotSettingsStore
    {
        internal static PlotConfiguration Load(string profileRoot, out string warning)
        {
            warning = "";
            string path = Path.Combine(profileRoot, "plot-settings.json");
            if (!File.Exists(path)) return new PlotConfiguration();
            try
            {
                if (new FileInfo(path).Length > 64 * 1024) throw new FormatException("Plot settings exceed 64 KB.");
                var value = JsonConvert.DeserializeObject<PlotConfiguration>(File.ReadAllText(path),
                    new JsonSerializerSettings { MaxDepth = 16, TypeNameHandling = TypeNameHandling.None });
                if (value == null) throw new FormatException("Empty plot settings.");
                value.Validate(); return value;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is FormatException || ex is ArgumentException)
            {
                warning = ex.Message;
                // Keep the original file untouched for manual recovery. Save() refuses
                // to overwrite an invalid existing file until it has made a safe copy.
                return new PlotConfiguration();
            }
        }

        internal static void Save(string profileRoot, PlotConfiguration value)
        {
            value.Validate();
            Directory.CreateDirectory(profileRoot);
            string path = Path.Combine(profileRoot, "plot-settings.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string warning;
                Load(profileRoot, out warning);
                if (warning.Length > 0)
                {
                    string recovery = path + ".invalid";
                    // Never overwrite a previous recovery. Abort instead of losing evidence.
                    if (File.Exists(recovery)) throw new IOException("An invalid settings recovery file already exists; move it aside before saving.");
                    File.Copy(path, recovery, false);
                }
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value, Formatting.Indented));
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
