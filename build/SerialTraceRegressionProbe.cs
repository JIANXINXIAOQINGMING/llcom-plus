using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class SerialTraceRegressionProbe
{
    private static Type hub, query, kind, mode;
    private static object Call(Type type, string method, params object[] args)
    {
        try { return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Value(object row, string property)
    {
        return row.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(row, null);
    }
    private static object[] Snapshot() { return ((IEnumerable)Call(hub, "Snapshot")).Cast<object>().ToArray(); }
    private static void Data(string port, string connection, bool sent, byte[] bytes)
    {
        Call(hub, "RecordData", port, connection, sent, bytes, 65001, null);
    }
    private static void Assert(bool success, string message) { if (!success) throw new Exception(message); }
    private static object Find(string text, string searchMode, string port, string filter, ISet<long> bookmarks, bool only, CancellationToken token)
    {
        return Call(query, "Find", Call(hub, "Snapshot"), text, Enum.Parse(mode, searchMode), port, filter, bookmarks, only, token);
    }
    public static string[] Run(Assembly assembly)
    {
        hub = assembly.GetType("llcom_plus.Tools.SerialTraceHub", true);
        query = assembly.GetType("llcom_plus.Tools.SerialTraceQuery", true);
        kind = assembly.GetType("llcom_plus.Tools.SerialTraceKind", true);
        mode = assembly.GetType("llcom_plus.Tools.TraceSearchMode", true);
        var passes = new List<string>();
        var bytes = Encoding.UTF8.GetBytes("AT\r\n");
        Data("COM41", "uart:COM41:1", true, bytes);
        bytes[0] = 0;
        var entry = Snapshot().Last();
        var copied = (byte[])entry.GetType().GetMethod("GetData").Invoke(entry, null);
        Assert(copied[0] == 65, "Publisher bytes must be copied.");
        copied[0] = 0;
        Assert(((byte[])entry.GetType().GetMethod("GetData").Invoke(entry, null))[0] == 65, "Reader cannot mutate stored bytes.");
        passes.Add("Trace bytes are immutable on input and output.");
        Assert((bool)Call(hub, "MatchesConnection", entry, "split:p:slot:1:uart:COM41:1"), "Main split identity must match physical UART.");
        Assert(!(bool)Call(hub, "MatchesConnection", entry, "uart:COM41:2") &&
            !(bool)Call(hub, "MatchesConnection", entry, "uart:COM42:1"), "Old generation/other COM must not match.");
        passes.Add("Captured connection matching isolates COM and generation.");
        Data("COM41", "uart:COM41:1", false, Encoding.UTF8.GetBytes("OK\r\n"));
        Data("COM42", "split:p:slot:2:COM42:1", false, Encoding.UTF8.GetBytes("OK\r\n"));
        var matches = ((IEnumerable)Find("ok", "Text", "COM41", "Rx", null, false, CancellationToken.None)).Cast<object>().ToArray();
        Assert(matches.Length == 1, "Text search must combine direction and COM filters.");
        Assert(((IEnumerable)Find("4F 4B", "Hex", "COM41", "Rx", null, false, CancellationToken.None)).Cast<object>().Count() == 1,
            "HEX must search byte values.");
        passes.Add("Text and HEX searches combine COM and direction filters.");
        var marker = new HashSet<long> { (long)Value(entry, "Id") };
        Assert(((IEnumerable)Find("", "Text", "", "", marker, true, CancellationToken.None)).Cast<object>().Count() == 1,
            "Bookmarks filter failed.");
        var invalid = false;
        try { Find("0 G", "Hex", "", "", null, false, CancellationToken.None); }
        catch (ArgumentException) { invalid = true; }
        Assert(invalid, "Invalid HEX must be rejected, not silently normalized.");
        passes.Add("Bookmarks isolate selected IDs; malformed HEX is rejected.");
        Assert(((IEnumerable)Find("^OK", "Regex", "COM42", "Rx", null, false, CancellationToken.None)).Cast<object>().Count() == 1,
            "Regex search failed.");
        var canceled = false;
        try { Find("", "Text", "", "", null, false, new CancellationToken(true)); }
        catch (OperationCanceledException) { canceled = true; }
        Assert(canceled, "Search cancellation was ignored.");
        passes.Add("Regex matching works and searches honor cancellation.");
        var text = (string)Call(query, "CsvCell", "=HYPERLINK(\"bad\")");
        Assert(text.StartsWith("\"'=") && text.Contains("\"\"bad\"\""), "CSV must escape formulas and quotes.");
        Assert(((byte[])entry.GetType().GetMethod("GetData").Invoke(entry, null))[0] == 65, "Search/export cannot alter raw log.");
        passes.Add("CSV neutralizes spreadsheet formulas and queries preserve raw data.");

        // Check repeated-prefix fallback against a small independent oracle.
        var random = new Random(1701);
        for (int trial = 0; trial < 80; trial++)
        {
            var sample = new byte[random.Next(1, 100)];
            var needle = new byte[random.Next(1, 20)];
            for (int i = 0; i < sample.Length; i++) sample[i] = (byte)random.Next(3);
            for (int i = 0; i < needle.Length; i++) needle[i] = (byte)random.Next(3);
            bool expected = false;
            for (int start = 0; start <= sample.Length - needle.Length; start++)
            {
                int matched = 0;
                while (matched < needle.Length && sample[start + matched] == needle[matched]) matched++;
                if (matched == needle.Length) { expected = true; break; }
            }
            string port = "HEX_ORACLE_" + trial;
            Data(port, port, false, sample);
            int count = ((IEnumerable)Find(BitConverter.ToString(needle).Replace("-", ""), "Hex", port, "Rx", null,
                false, CancellationToken.None)).Cast<object>().Count();
            Assert((count == 1) == expected, "HEX repeated-prefix search disagreed with the byte oracle.");
        }
        passes.Add("HEX searches handle overlapping and repeated prefixes without false matches.");

        bool emptyCanceled = false;
        try
        {
            Call(query, "Find", Array.CreateInstance(assembly.GetType("llcom_plus.Tools.SerialTraceEntry", true), 0),
                "", Enum.Parse(mode, "Text"), "", "", null, false, new CancellationToken(true));
        }
        catch (OperationCanceledException) { emptyCanceled = true; }
        Assert(emptyCanceled, "An empty search ignored an already cancelled request.");
        passes.Add("Already-cancelled searches are rejected even when the trace is empty.");

        var repeated = Enumerable.Repeat((byte)0xAA, 16 * 1024 * 1024).ToArray();
        Data("HEX_LARGE", "HEX_LARGE", false, repeated);
        string nearMatch = new string('A', 1022) + "FF";
        var largeSearch = Task.Run(() => ((IEnumerable)Find(nearMatch, "Hex", "HEX_LARGE", "Rx", null,
            false, CancellationToken.None)).Cast<object>().Count());
        Assert(largeSearch.Wait(4000) && largeSearch.GetAwaiter().GetResult() == 0,
            "A single repeated 16 MiB record exceeded the search deadline or produced a false match.");
        using (var cancellation = new CancellationTokenSource())
        {
            var search = Task.Run(() => Find(nearMatch, "Hex", "HEX_LARGE", "Rx", null, false, cancellation.Token));
            cancellation.CancelAfter(10);
            bool canceledLarge = false;
            try
            {
                Assert(search.Wait(2000), "Cancellation did not interrupt a large HEX record promptly.");
                search.GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                if (!(ex.InnerException is OperationCanceledException)) throw;
                canceledLarge = true;
            }
            catch (OperationCanceledException) { canceledLarge = true; }
            Assert(canceledLarge, "A large in-flight HEX search ignored cancellation.");
        }
        passes.Add("A large repeated HEX record stays bounded and can be cancelled during its scan.");

        var data = new byte[140000];
        Call(hub, "RecordBuffer", "COM43", "uart:COM43:1", false, data, 0, data.Length, 65001);
        var blocks = Snapshot().Where(e => (string)Value(e, "PortName") == "COM43").ToArray();
        Assert(blocks.Length == 3 && blocks.Sum(e => (int)Value(e, "ByteCount")) == data.Length,
            "Large physical buffers must be segmented without loss.");
        Assert(blocks.All(e => (int)Value(e, "ByteCount") <= 65536), "Trace record exceeded block limit.");
        passes.Add("Large buffers split into bounded records with exact byte totals.");
        for (int i = 0; i < 10010; i++) Call(hub, "RecordEvent", "COM41", "", Enum.Parse(kind, "Info"), "event", null);
        Assert(Snapshot().Length <= 10000, "Trace entry limit failed.");
        var dropped = (long)hub.GetProperty("DroppedCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null, null);
        Assert(dropped > 0, "Eviction must be visible to the user.");
        for (int i = 0; i < 270; i++) Data("COM44", "uart:COM44:1", false, new byte[65536]);
        Assert(Snapshot().Sum(e => (long)(int)Value(e, "ByteCount") + ((string)Value(e, "Text")).Length * 2) <= 16L * 1024 * 1024,
            "Trace byte limit failed.");
        passes.Add("Entry count and byte budget stay bounded with observable eviction.");
        return passes.ToArray();
    }
}
