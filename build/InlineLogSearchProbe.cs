using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

public static class InlineLogSearchProbe
{
    private static readonly BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type searchType, snapshotType;
    private static object Get(object instance, string property)
    { return instance.GetType().GetProperty(property, AnyInstance).GetValue(instance, null); }
    private static object Invoke(MethodInfo method, object instance, params object[] args)
    {
        try { return method.Invoke(instance, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Find(string text, string query, bool regex, bool matchCase, CancellationToken token)
    { return Invoke(searchType.GetMethod("Find", AnyStatic), null, text, query, regex, matchCase, token); }
    private static object[] Hits(object result) { return ((IEnumerable)Get(result, "Hits")).Cast<object>().ToArray(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception(message);
    }
    private static object Capture(FlowDocument document)
    { return Invoke(snapshotType.GetMethod("Capture", AnyStatic), null, document); }
    private static TextPointer Pointer(object snapshot, int index, bool end)
    { return (TextPointer)Invoke(snapshotType.GetMethod("PointerAt", AnyInstance), snapshot, index, end); }

    public static string[] Run(Assembly assembly)
    {
        searchType = assembly.GetType("llcom_plus.Tools.LogTextSearch", true);
        snapshotType = assembly.GetType("llcom_plus.Views.LogFindBar+DocumentSnapshot", true);
        var passed = new List<string>();
        var result = Find("AT\nOK\nat\nokay", "at", false, false, CancellationToken.None);
        Assert(Hits(result).Length == 2, "Case-insensitive text search lost a match.");
        Assert(Hits(Find("AT\nat", "at", false, true, CancellationToken.None)).Length == 1, "Case-sensitive text search ignored case.");
        Assert(Hits(Find("AT\nOK\nOKAY\n", "^OK$", true, true, CancellationToken.None)).Length == 1, "Regex line anchors must match displayed lines.");
        passed.Add("Text, case sensitivity and multiline regex match displayed log text.");

        Throws<ArgumentException>(() => Find("data", "[", true, false, CancellationToken.None), "Invalid regex was accepted.");
        Throws<ArgumentException>(() => Find("data", new string('a', 1025), false, false, CancellationToken.None), "Oversized query was accepted.");
        Throws<ArgumentException>(() => Find(new string('a', 1024 * 1024 + 1), "a", false, false, CancellationToken.None), "Oversized snapshot was accepted.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Throws<OperationCanceledException>(() => Find("data", "a", false, false, cancel.Token), "Canceled search still ran.");
        }
        passed.Add("Malformed queries, oversized inputs and cancellation fail safely.");

        Assert(Hits(Find("AT\nOK", "^", true, true, CancellationToken.None)).Length == 0, "Zero-width regex must not make invisible selections.");
        var capped = Find(new string('a', 3000), "a", false, true, CancellationToken.None);
        Assert(Hits(capped).Length == 2000 && (bool)Get(capped, "LimitReached"), "Search results must remain bounded.");
        passed.Add("Zero-width results are ignored and result retention is bounded.");

        Throws<TimeoutException>(() => Find(new string('a', 20000) + "!", "(a+)+$", true, true, CancellationToken.None),
            "Pathological regex must time out instead of blocking the UI indefinitely.");
        passed.Add("Pathological regular expressions time out on the background search path.");

        var document = new FlowDocument();
        var paragraph = new Paragraph();
        var sent = new Run("AT+") { Foreground = Brushes.Red };
        var command = new Run("VER?") { Foreground = Brushes.Red };
        var received = new Run("版本 你好😀") { Foreground = Brushes.Green };
        paragraph.Inlines.Add(sent);
        paragraph.Inlines.Add(command);
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(received);
        document.Blocks.Add(paragraph);
        document.Blocks.Add(new Paragraph(new Run("OK")));
        var before = new TextRange(document.ContentStart, document.ContentEnd).Text;
        var snapshot = Capture(document);
        var text = (string)Get(snapshot, "Text");
        Assert(text.Contains("AT+VER?\n版本 你好😀\nOK"), "Run/LineBreak/paragraph mapping lost display text: " + text);
        var matchIndex = text.IndexOf("VER?\n版本", StringComparison.Ordinal);
        var range = new TextRange(Pointer(snapshot, matchIndex, false), Pointer(snapshot, matchIndex + "VER?\n版本".Length, true));
        Assert(range.Text.Replace("\r\n", "\n") == "VER?\n版本", "Pointers do not align across styled runs and line breaks.");
        var box = new RichTextBox { Document = document, IsReadOnly = true };
        box.Selection.Select(range.Start, range.End);
        Assert(ReferenceEquals(sent.Foreground, Brushes.Red) && ReferenceEquals(received.Foreground, Brushes.Green), "Find changed TX/RX colors.");
        Assert(before == new TextRange(document.ContentStart, document.ContentEnd).Text, "Find mutated original log text.");
        passed.Add("Pointer mapping spans colored runs, Unicode and line breaks without altering the log.");

        var appended = new Paragraph(new Run("NEW DATA"));
        document.Blocks.Add(appended);
        Assert(range.Text.Replace("\r\n", "\n") == "VER?\n版本", "New data invalidated retained match anchors.");
        Assert(!text.Contains("NEW DATA"), "A snapshot must not silently change while being searched.");
        passed.Add("Concurrent appends preserve existing hit anchors and do not alter the search snapshot.");

        document.Blocks.Clear();
        document.Blocks.Add(new Paragraph(new Run("unrelated replacement")));
        Assert(range.Text.Replace("\r\n", "\n") != "VER?\n版本", "Trimmed anchors must be distinguishable from retained hits.");
        passed.Add("Discarded document segments can be detected before selecting a stale hit.");

        var largeDocument = new FlowDocument(new Paragraph(new Run("OLDEST" + new string('a', 1024 * 1024 + 500) + "LATEST")));
        var largeSnapshot = Capture(largeDocument);
        var boundedText = (string)Get(largeSnapshot, "Text");
        Assert(boundedText.Length <= 1024 * 1024 && (bool)Get(largeSnapshot, "Truncated"), "Document capture exceeded the memory bound.");
        Assert(boundedText.Contains("LATEST") && !boundedText.Contains("OLDEST"), "Bounded capture must prefer the latest retained log.");
        passed.Add("Large document snapshots keep the newest text within the 1 MiB budget.");
        return passed.ToArray();
    }
}
