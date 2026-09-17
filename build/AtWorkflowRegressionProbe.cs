using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Reflection isolates the pure test engine from the application's Global initializer.
public static class AtWorkflowRegressionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type optionsType, runnerType, hubType;
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object Invoke(MethodInfo method, object instance, params object[] args)
    {
        try { return method.Invoke(instance, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Options(int mode, string expected, int retries = 0)
    {
        try { return Activator.CreateInstance(optionsType, All, null, new object[] { mode, expected, 100, retries, false }, null); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static Task Execute(object options, Func<CancellationToken, Task> send, Func<bool> open = null,
        CancellationToken token = default(CancellationToken), string identity = "uart:COM_TEST:1")
    {
        return (Task)Invoke(runnerType.GetMethod("ExecuteAsync", All), null,
            options, identity, send, open ?? new Func<bool>(() => true), token);
    }
    private static object Finish(Task task)
    {
        Assert(task.Wait(5000), "Synthetic AT task did not complete.");
        return task.GetType().GetProperty("Result").GetValue(task, null);
    }
    private static string Outcome(object result) { return (string)result.GetType().GetProperty("Outcome", All).GetValue(result, null); }
    private static int Attempts(object result) { return (int)result.GetType().GetProperty("Attempts", All).GetValue(result, null); }
    private static void Rx(string text, string identity = "uart:COM_TEST:1") { RxBytes(Encoding.UTF8.GetBytes(text), identity); }
    private static void RxBytes(byte[] bytes, string identity = "uart:COM_TEST:1", int encoding = 65001)
    {
        Invoke(hubType.GetMethod("RecordData", All), null, "COM_TEST", identity, false, bytes, encoding, null);
    }
    private static int Subscribers()
    {
        var value = (Delegate)hubType.GetField("Recorded", All).GetValue(null);
        return value == null ? 0 : value.GetInvocationList().Length;
    }
    private static void ExpectFailure(Task task, Type type)
    {
        try { task.GetAwaiter().GetResult(); throw new InvalidOperationException("Expected failure was not raised."); }
        catch (Exception ex) { Assert(type.IsInstanceOfType(ex), "Unexpected error type: " + ex.GetType()); }
    }

    public static string[] Run(Assembly assembly)
    {
        var passed = new List<string>();
        optionsType = assembly.GetType("llcom_plus.Tools.SerialTestOptions", true);
        runnerType = assembly.GetType("llcom_plus.Tools.SerialTestRunner", true);
        hubType = assembly.GetType("llcom_plus.Tools.SerialTraceHub", true);
        int originalSubscribers = Subscribers();

        var itemType = assembly.GetType("llcom_plus.Pages.CircularSendItem", true);
        var item = Activator.CreateInstance(itemType);
        Assert((int)itemType.GetProperty("ExpectationMode").GetValue(item, null) == 0 &&
            (string)itemType.GetProperty("RetryCount").GetValue(item, null) == "0",
            "Legacy rows must default to no assertion and no retries.");
        passed.Add("Legacy rows default to send-only and zero automatic retries.");

        int count = 0;
        var result = Finish(Execute(Options(0, ""), token => { count++; return Task.CompletedTask; }));
        Assert(count == 1 && Outcome(result) == "Sent", "Send-only mode changed its behavior.");
        passed.Add("Send-only mode waits for one actual send and reports unvalidated delivery.");

        var sendCompleted = new TaskCompletionSource<bool>();
        var pending = Execute(Options(1, "OK"), token => { Rx("O"); Rx("K"); return sendCompleted.Task; });
        Assert(!pending.IsCompleted, "A reply must not complete the step before actual sending completes.");
        sendCompleted.SetResult(true);
        result = Finish(pending);
        Assert(Outcome(result) == "Passed", "Fast cross-packet response was lost.");
        passed.Add("Response subscription precedes send; fast cross-packet replies survive pending writes.");

        Rx("OK");
        result = Finish(Execute(Options(1, "OK"), token => { Rx("OK", "uart:COM_OTHER:1"); Rx("OK", "uart:COM_TEST:2"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Timeout", "Historical/other-port/reopened-generation reply was accepted.");
        passed.Add("Historical replies, other ports, and reopened connection generations cannot satisfy a step.");

        result = Finish(Execute(Options(1, "OK"), token => { Rx("OK"); return Task.CompletedTask; },
            identity: "split:page:slot:1:uart:COM_TEST:1"));
        Assert(Outcome(result) == "Passed", "Main UART split alias did not match the captured physical connection.");
        passed.Add("Main-port split aliases match the same captured physical connection.");

        result = Finish(Execute(Options(2, "OK"), token => { Rx("OK"); Rx("AY\r\n"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Timeout", "Exact-line assertion accepted a partial prefix.");
        result = Finish(Execute(Options(2, "OK"), token => { Rx("OK"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Timeout", "A deadline incorrectly completed an unterminated response line.");
        result = Finish(Execute(Options(2, "OK"), token => { Rx("URC\r\nO"); Rx("K\r\n"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Passed", "Exact response line failed across packet boundaries.");
        result = Finish(Execute(Options(2, "OK"), token => { Rx("OK"); Rx("\r"); Rx("\n"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Passed", "A line terminator in a later receive block did not complete the response.");
        passed.Add("Exact-line matching waits for line boundaries and rejects longer response prefixes.");

        var utf8 = Encoding.UTF8.GetBytes("中文");
        result = Finish(Execute(Options(1, "中文"), token => {
            RxBytes(new byte[] { utf8[0] });
            RxBytes(new byte[] { utf8[1], utf8[2], utf8[3], utf8[4], utf8[5] });
            return Task.CompletedTask;
        }));
        Assert(Outcome(result) == "Passed", "UTF-8 characters split across RX packets were corrupted.");
        passed.Add("Captured encoding uses an incremental decoder across multi-byte packet boundaries.");

        result = Finish(Execute(Options(3, @"\+CSQ:\s*\d+,\d+"), token => { Rx("\r\n+CSQ: 18,99\r\n"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Passed", "Valid regex response failed.");
        bool invalid = false;
        try { Options(3, "["); } catch (ArgumentException) { invalid = true; }
        Assert(invalid, "Invalid regex was not rejected before sending.");
        result = Finish(Execute(Options(3, "^(a+)+$", 3), token => { Rx(new string('a', 20000) + "!"); return Task.CompletedTask; }));
        Assert(Outcome(result) == "RegexTimeout" && Attempts(result) == 1, "Expensive regex must fail with a deadline and no replay.");
        passed.Add("Regex syntax is validated before send; expensive matches time out without replay.");

        count = 0;
        result = Finish(Execute(Options(1, "OK"), token => { count++; return Task.CompletedTask; }));
        Assert(count == 1 && Outcome(result) == "Timeout", "Default timeout unexpectedly replayed the command.");
        count = 0;
        result = Finish(Execute(Options(1, "OK", 2), token => { if (++count == 2) Rx("OK"); return Task.CompletedTask; }));
        Assert(count == 2 && Attempts(result) == 2 && Outcome(result) == "Passed", "Explicit timeout retry did not stop on success.");
        passed.Add("Timeout retries are opt-in and stop immediately after successful validation.");

        count = 0;
        pending = Execute(Options(1, "OK", 4), token => { count++; throw new IOException("Partial synthetic write failure"); });
        ExpectFailure(pending, typeof(IOException));
        Assert(count == 1, "A potentially partial write was automatically replayed.");
        passed.Add("Write failures never replay commands even when response retries were configured.");

        using (var cancel = new CancellationTokenSource())
        {
            pending = Execute(Options(1, "OK"), token => Task.CompletedTask, token: cancel.Token);
            cancel.Cancel();
            ExpectFailure(pending, typeof(OperationCanceledException));
        }
        Assert(Subscribers() == originalSubscribers, "Cancellation leaked the RX subscriber.");
        bool open = true;
        count = 0;
        pending = Execute(Options(1, "OK", 3), token => { count++; open = false; return Task.CompletedTask; }, () => open);
        ExpectFailure(pending, typeof(InvalidOperationException));
        Assert(count == 1, "Disconnected connection triggered replay.");
        passed.Add("Stop and disconnect cancel waiting, detach observers, and never replay on another connection.");

        result = Finish(Execute(Options(1, "OK", 2), token => { Rx(new string('x', 65537)); return Task.CompletedTask; }));
        Assert(Outcome(result) == "Overflow" && Attempts(result) == 1, "Oversized response must fail closed without retry.");
        passed.Add("Response memory is bounded and overflow is explicit instead of a false pass or replay.");

        count = 0;
        pending = Execute(Options(1, "OK"), token => { count++; return Task.CompletedTask; }, identity: "serial-all:a|b");
        ExpectFailure(pending, typeof(InvalidOperationException));
        Assert(count == 0, "Response validation transmitted to a broadcast target.");
        passed.Add("Broadcast response automation is rejected before any send.");

        var rowType = assembly.GetType("llcom_plus.Tools.SerialTestReportRow", true);
        var row = Activator.CreateInstance(rowType, true);
        rowType.GetField("Timestamp", All).SetValue(row, DateTime.Now);
        rowType.GetField("Command", All).SetValue(row, " =HYPERLINK(\"evil\")\r\nAT");
        rowType.GetField("Result", All).SetValue(row, result);
        var rows = Array.CreateInstance(rowType, 1); rows.SetValue(row, 0);
        var reportType = assembly.GetType("llcom_plus.Tools.SerialTestReport", true);
        var csv = (string)Invoke(reportType.GetMethod("ToCsv", All), null, rows);
        Assert(csv.Contains("\"' =HYPERLINK(\"\"evil\"\")\r\nAT\""), "CSV device-controlled cells were not safely escaped.");
        passed.Add("CSV reports quote embedded newlines/quotes and neutralize spreadsheet formulas.");
        Assert(Subscribers() == originalSubscribers, "Completed test runs leaked RX observers.");
        return passed.ToArray();
    }
}
