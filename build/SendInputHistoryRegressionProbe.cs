using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

public static class SendInputHistoryRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type historyType, stateType;

    private static object NewHistory(IEnumerable<string> entries = null)
    {
        return historyType.GetConstructor(Instance, null, new[] { typeof(IEnumerable<string>) }, null)
            .Invoke(new object[] { entries });
    }

    private static object State(string text, int caret = 0, int start = 0, int length = 0)
    {
        return stateType.GetConstructor(Instance, null,
            new[] { typeof(string), typeof(int), typeof(int), typeof(int) }, null)
            .Invoke(new object[] { text, caret, start, length });
    }

    private static object Call(object target, string method, params object[] arguments)
    {
        try { return target.GetType().GetMethod(method, Instance).Invoke(target, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static object Value(object target, string property)
    {
        return target.GetType().GetProperty(property, Instance).GetValue(target, null);
    }

    private static string[] Entries(object target) { return ((IEnumerable<string>)Value(target, "Entries")).ToArray(); }
    private static int Count(object target) { return (int)Value(target, "Count"); }
    private static bool Record(object target, string text) { return (bool)Call(target, "Record", new object[] { text }); }
    private static string Text(object state) { return (string)Value(state, "Text"); }

    private static bool Move(object target, bool older, object current, out object recalled)
    {
        var arguments = new object[] { older, current, null };
        var moved = (bool)Call(target, "TryMove", arguments);
        recalled = arguments[2];
        return moved;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static string[] Run(Assembly assembly)
    {
        historyType = assembly.GetType("llcom_plus.Tools.SendInputHistory", true);
        stateType = historyType.GetNestedType("State", BindingFlags.NonPublic);
        var passed = new List<string>();
        object recalled;
        var empty = NewHistory();
        Assert(!Move(empty, true, State("draft"), out recalled) && recalled == null &&
            !Move(empty, false, State("draft"), out recalled), "Empty history must leave the input alone.");
        passed.Add("Empty history leaves editing untouched.");

        var history = NewHistory(new[] { "AT", "AT+VER?", "AT+CSQ" });
        var draft = State("unsent draft", 4, 2, 3);
        Assert(Move(history, true, draft, out recalled) && Text(recalled) == "AT+CSQ", "Up must first recall the latest input.");
        Assert(Move(history, true, recalled, out recalled) && Text(recalled) == "AT+VER?", "Up must recall the preceding input.");
        Assert(Move(history, true, recalled, out recalled) && Text(recalled) == "AT", "Up must reach the oldest retained input.");
        Assert(!Move(history, true, State("AT"), out recalled) && recalled == null, "Up at the oldest input must not wrap.");
        Assert(Move(history, false, State("AT"), out recalled) && Text(recalled) == "AT+VER?", "Down must move forward in history.");
        Assert(Move(history, false, recalled, out recalled) && Text(recalled) == "AT+CSQ", "Down must reach the latest input.");
        Assert(Move(history, false, recalled, out recalled) && Text(recalled) == "unsent draft" &&
            (int)Value(recalled, "CaretIndex") == 4 && (int)Value(recalled, "SelectionStart") == 2 &&
            (int)Value(recalled, "SelectionLength") == 3, "Leaving history must restore the complete original draft.");
        Assert(!Move(history, false, recalled, out recalled), "Down from the draft must not wrap.");
        passed.Add("Up/Down preserve order, stop at bounds and restore the draft caret and selection.");

        history = NewHistory(new[] { "first", "last" });
        draft = State("last", 1, 0, 2);
        Assert(Move(history, true, draft, out recalled) && Text(recalled) == "first", "Up after sending must skip the already visible latest input.");
        Assert(Move(history, false, recalled, out recalled) && Text(recalled) == "last" &&
            (int)Value(recalled, "CaretIndex") == 4, "Newer history entries must put the caret at the end.");
        Assert(Move(history, false, recalled, out recalled) && (int)Value(recalled, "CaretIndex") == 1 &&
            (int)Value(recalled, "SelectionLength") == 2, "The original latest-input draft must still retain its selection.");
        passed.Add("Sending without clearing the composer still lets the first Up reach an earlier command.");

        var raw = "  AT+ONE\r\nAT+TWO\n  ";
        history = NewHistory(new[] { raw, raw, null, "\t \r\n", "at", "AT", "at" });
        Assert(Count(history) == 4 && Entries(history)[0] == raw &&
            Entries(history).SequenceEqual(new[] { raw, "at", "AT", "at" }),
            "Normalization must preserve exact commands, skip blanks and collapse only adjacent duplicates.");
        var snapshot = (IEnumerable<string>)Value(history, "Entries");
        Assert(Record(history, "new command") && snapshot.SequenceEqual(new[] { raw, "at", "AT", "at" }),
            "A captured history snapshot must not change after later submissions.");
        passed.Add("Multiline text, spaces and case are preserved; blank and adjacent duplicate entries are ignored.");

        history = NewHistory(new[] { "first", "second" });
        Assert(Move(history, true, State("draft"), out recalled), "History should begin browsing.");
        Assert(!Record(history, "second"), "A repeated submission must not add another consecutive entry.");
        Assert(!Move(history, false, State("second"), out recalled), "Even a repeated submission must reset navigation.");
        Assert(Move(history, true, State("draft"), out recalled), "History should restart browsing.");
        Assert(!Record(history, " "), "A blank submission must not enter history.");
        Assert(!Move(history, false, State("changed draft"), out recalled), "A blank submission must discard a stale browsing draft.");
        Assert(Move(history, true, State("fresh draft"), out recalled), "History should browse after reset.");
        Call(history, "ResetNavigation");
        Assert(!Move(history, false, State("edited command"), out recalled), "Manual edits must reset navigation without restoring stale text.");
        Assert(Entries(history).SequenceEqual(new[] { "first", "second" }), "Browsing and edits must not change stored commands.");
        passed.Add("Submission and explicit reset end browsing, including blank and duplicate submissions.");

        history = NewHistory(Enumerable.Range(0, 205).Select(i => "command " + i));
        Assert(Count(history) == 200 && Entries(history)[0] == "command 5" &&
            Entries(history)[199] == "command 204", "Count limits must evict oldest whole entries.");
        var limitText = new string('x', 16384);
        history = NewHistory(Enumerable.Range(0, 18).Select(i => i.ToString("D2") + limitText.Substring(2)));
        Assert(Count(history) == 16 && Entries(history)[0].StartsWith("02", StringComparison.Ordinal) &&
            Entries(history).Sum(s => s.Length) == 262144, "Total character limits must evict oldest whole entries.");
        Assert(!Record(history, new string('y', 16385)) && Count(history) == 16,
            "Oversized commands must be skipped without truncation.");
        Assert(Record(history, "short") && Count(history) == 16 && Entries(history)[0].StartsWith("03", StringComparison.Ordinal) &&
            Entries(history).Last() == "short", "A new command must fit by evicting a whole old entry.");
        passed.Add("Count, individual input and total character limits retain complete recent commands.");

        history = NewHistory(new[] { "ok", new string('x', 16385), "ok", "last" });
        Assert(Entries(history).SequenceEqual(new[] { "ok", "last" }), "Loaded history must use the same normalization as new submissions.");
        var clamped = State("abc", int.MaxValue, -4, int.MaxValue);
        Assert((int)Value(clamped, "CaretIndex") == 3 && (int)Value(clamped, "SelectionStart") == 0 &&
            (int)Value(clamped, "SelectionLength") == 3, "State must clamp caret and selection without integer overflow.");
        clamped = State(null, -5, int.MaxValue, -1);
        Assert(Text(clamped) == "" && (int)Value(clamped, "CaretIndex") == 0 &&
            (int)Value(clamped, "SelectionStart") == 0 && (int)Value(clamped, "SelectionLength") == 0,
            "A null or malformed draft must produce a safe empty state.");
        passed.Add("Loaded history and malformed caret/selection positions normalize safely.");
        return passed.ToArray();
    }
}
