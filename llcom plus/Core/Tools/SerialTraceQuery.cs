using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace llcom_plus.Tools
{
    internal enum TraceSearchMode { Text, Hex, Regex }

    internal sealed class SerialTraceRow
    {
        internal SerialTraceRow(SerialTraceEntry entry, bool bookmarked)
        {
            Entry = entry; Bookmarked = bookmarked;
            Text = SerialTraceQuery.Decode(entry);
        }
        internal SerialTraceEntry Entry { get; }
        public long Id => Entry.Id;
        public string Time => Entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        public string Port => Entry.PortName;
        public string Kind => Entry.Kind.ToString().ToUpperInvariant();
        public bool Bookmarked { get; }
        public string Marker => Bookmarked ? "★" : "";
        public string Text { get; }
        public string Preview => Text.Length > 512 ? Text.Substring(0, 512) + "…" : Text;
        public string Hex => BitConverter.ToString(Entry.GetData()).Replace('-', ' ');
    }

    internal static class SerialTraceQuery
    {
        internal static string Decode(SerialTraceEntry entry)
        {
            if (entry.ByteCount == 0) return entry.Text;
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(entry.EncodingCodePage); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            return encoding.GetString(entry.GetData());
        }

        internal static IReadOnlyList<SerialTraceRow> Find(IReadOnlyList<SerialTraceEntry> source,
            string query, TraceSearchMode mode, string port, string kind,
            ISet<long> bookmarks, bool bookmarksOnly, CancellationToken token)
        {
            query = query ?? "";
            if (query.Length > 1024) throw new ArgumentException("搜索内容最多 1024 字符 / Search is limited to 1024 characters.");
            Regex regex = null;
            byte[] pattern = null;
            if (query.Length > 0 && mode == TraceSearchMode.Regex)
                regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            if (query.Length > 0 && mode == TraceSearchMode.Hex)
            {
                var hex = string.Concat(query.Where(ch => !char.IsWhiteSpace(ch)));
                if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Any(ch => !Uri.IsHexDigit(ch)))
                    throw new ArgumentException("HEX 请填写完整字节，例如 41 54 0D 0A / Enter complete hex bytes.");
                pattern = new byte[hex.Length / 2];
                for (int i = 0; i < pattern.Length; i++)
                    pattern[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            var clock = Stopwatch.StartNew();
            var rows = new List<SerialTraceRow>();
            foreach (var entry in source)
            {
                token.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds > 1500)
                    throw new TimeoutException("搜索超过时间限制，请缩小 COM/类型范围 / Narrow the search scope.");
                if (!string.IsNullOrWhiteSpace(port) && !entry.PortName.Equals(port, StringComparison.OrdinalIgnoreCase)) continue;
                if (kind == "Power")
                {
                    if (entry.Kind != SerialTraceKind.Wake && entry.Kind != SerialTraceKind.Tx &&
                        entry.Kind != SerialTraceKind.Rx && entry.Kind != SerialTraceKind.Pin &&
                        entry.Kind != SerialTraceKind.Info && entry.Kind != SerialTraceKind.Error) continue;
                }
                else if (!string.IsNullOrEmpty(kind) && !entry.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
                var marked = bookmarks?.Contains(entry.Id) == true;
                if (bookmarksOnly && !marked) continue;
                if (pattern != null && !Contains(entry.GetData(), pattern)) continue;
                var row = new SerialTraceRow(entry, marked);
                if (regex != null && !regex.IsMatch(row.Text)) continue;
                if (query.Length > 0 && mode == TraceSearchMode.Text &&
                    row.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(row);
            }
            return rows;
        }

        private static bool Contains(byte[] data, byte[] pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && data[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return true;
            }
            return false;
        }

        internal static string CsvCell(string text)
        {
            text = text ?? "";
            // Prevent spreadsheet formula execution in exported device-controlled text.
            if (text.Length > 0 && "=+-@\t\r\n".IndexOf(text[0]) >= 0) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        internal static void Export(TextWriter writer, IEnumerable<SerialTraceRow> rows)
        {
            writer.WriteLine("Id,HostTime,COM,Connection,Kind,Bookmark,Text,Hex");
            foreach (var row in rows)
                writer.WriteLine(string.Join(",", row.Id.ToString(CultureInfo.InvariantCulture),
                    CsvCell(row.Entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    CsvCell(row.Port), CsvCell(row.Entry.ConnectionId), CsvCell(row.Kind),
                    row.Bookmarked ? "1" : "0", CsvCell(row.Text), CsvCell(row.Hex)));
        }
    }
}
