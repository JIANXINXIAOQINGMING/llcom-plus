using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// The coordinator is exercised with synthetic names and injected cleanup work.
// No Global/App/SerialPort, USB enumeration, driver open or control-line I/O is used.
public static class SerialPortAccessRegressionProbe
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int WaitMs = 5000;
    private static MethodInfo acquire, releaseAsync, describe;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Invoke(MethodInfo method, object target, params object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    private static object Acquire(string port, string owner)
    {
        return Invoke(acquire, null, port, owner);
    }

    private static void Dispose(object reservation)
    {
        if (reservation != null) ((IDisposable)reservation).Dispose();
    }

    private static Exception TryAcquire(string port, string owner, out object reservation)
    {
        reservation = null;
        try { reservation = Acquire(port, owner); return null; }
        catch (Exception error) { return error; }
    }

    private static Exception Denied(string port, string owner)
    {
        object unexpected;
        var error = TryAcquire(port, owner, out unexpected);
        Dispose(unexpected);
        Assert(error != null, "A reserved COM was reopened: " + port);
        return error;
    }

    private static void Wait(Task task)
    {
        Assert(((IAsyncResult)task).AsyncWaitHandle.WaitOne(WaitMs), "A bounded coordinator regression wait timed out.");
        task.GetAwaiter().GetResult();
    }

    private static Task Release(object reservation, Action cleanup)
    {
        return (Task)Invoke(releaseAsync, null, reservation, cleanup);
    }

    private static string Describe(Exception error, string port, bool chinese)
    {
        return (string)Invoke(describe, null, error, port, chinese);
    }

    public static string[] Run(Assembly assembly)
    {
        var type = assembly.GetType("llcom_plus.Tools.SerialPortAccessCoordinator", true);
        acquire = type.GetMethod("Acquire", Static);
        releaseAsync = type.GetMethod("ReleaseAsync", Static);
        describe = type.GetMethod("GetOpenErrorMessage", Static);
        Assert(acquire != null && releaseAsync != null && describe != null,
            "The shared serial reservation and open-failure diagnostic contract is incomplete.");
        var passed = new List<string>();
        TestAliasesAndIndependentPorts();
        passed.Add("Canonical COM aliases share one owner while a different COM remains independent.");
        TestConcurrentAcquisition();
        passed.Add("Concurrent main/split acquisition has exactly one winner and never grants overlapping owners.");
        TestDelayedRelease();
        passed.Add("Delayed cleanup keeps the COM reserved; disposal and duplicate releases cannot reopen it or run cleanup twice.");
        TestStaleDispose();
        passed.Add("Disposal is idempotent and an old reservation cannot release a newer owner.");
        TestCleanupFailure();
        passed.Add("A cleanup failure is not treated as successful release and never permits an automatic reopen.");
        TestErrorMessages();
        passed.Add("Diagnostics distinguish application ownership, pending release and OS access/resource errors without inventing an external process.");
        return passed.ToArray();
    }

    private static void TestAliasesAndIndependentPorts()
    {
        var first = Acquire("com27001", "main");
        object other = null;
        try
        {
            Denied("COM27001", "split-2");
            Denied(@"\\.\COM27001", "split-3");
            Denied(" COM27001 ", "split-4");
            other = Acquire("COM27002", "independent");
        }
        finally { Dispose(other); Dispose(first); }
        var afterRelease = Acquire("COM27001", "new owner");
        Dispose(afterRelease);
    }

    private static void TestConcurrentAcquisition()
    {
        const int count = 24;
        var start = new ManualResetEventSlim(false);
        var attempts = new Task[count];
        var winners = new List<object>();
        int denied = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var owner = "contender-" + i;
                attempts[i] = Task.Run(() =>
                {
                    Assert(start.Wait(WaitMs), "Concurrent acquisition was not released.");
                    object reservation;
                    var error = TryAcquire("COM27003", owner, out reservation);
                    if (error == null) lock (winners) winners.Add(reservation);
                    else Interlocked.Increment(ref denied);
                });
            }
            start.Set();
            Wait(Task.WhenAll(attempts));
            Assert(winners.Count == 1 && denied == count - 1,
                "A shared COM had more than one owner or acquisition was lost.");
        }
        finally
        {
            start.Set();
            foreach (var winner in winners) Dispose(winner);
            start.Dispose();
        }
    }

    private static void TestDelayedRelease()
    {
        var reservation = Acquire("COM27004", "retiring main");
        var entered = new ManualResetEventSlim(false);
        var finish = new ManualResetEventSlim(false);
        int cleanups = 0;
        Task cleanup = null;
        try
        {
            Invoke(reservation.GetType().GetMethod("BeginRelease", Instance), reservation);
            Assert((bool)reservation.GetType().GetProperty("IsReleasing", Instance).GetValue(reservation, null),
                "BeginRelease did not publish the closing state before cleanup began.");
            Dispose(reservation);
            Denied("COM27004", "premature disposal replacement");
            cleanup = Release(reservation, () =>
            {
                Interlocked.Increment(ref cleanups);
                entered.Set();
                Assert(finish.Wait(WaitMs), "The intentionally delayed cleanup was not released.");
            });
            Assert(entered.Wait(WaitMs), "Cleanup never started.");
            var duplicate = Release(reservation, () => Interlocked.Increment(ref cleanups));
            Assert(ReferenceEquals(cleanup, duplicate), "Repeated release did not return the existing cleanup task.");
            Assert(!cleanup.IsCompleted, "The cleanup task completed before the old driver was released.");
            var pending = Denied("COM27004", "replacement split");
            var chinese = Describe(pending, "COM27004", true);
            var english = Describe(pending, "COM27004", false);
            Assert(chinese.Contains("COM27004") && chinese.Contains("释放") &&
                english.IndexOf("releas", StringComparison.OrdinalIgnoreCase) >= 0,
                "A pending cleanup was misreported as a normal outside-program conflict.");
            var unrelated = Acquire("COM27005", "unrelated split");
            Dispose(unrelated);
            Assert(cleanups == 1, "Cleanup was automatically retried.");
        }
        finally
        {
            finish.Set();
            if (cleanup != null) Wait(cleanup);
            entered.Dispose();
            finish.Dispose();
        }
        var replacement = Acquire("COM27004", "replacement split");
        Dispose(replacement);
    }

    private static void TestStaleDispose()
    {
        var old = Acquire("COM27006", "old owner");
        Dispose(old);
        var current = Acquire("COM27006", "current owner");
        try
        {
            Dispose(old);
            Denied("COM27006", "third owner");
        }
        finally { Dispose(current); }
        var fresh = Acquire("COM27006", "fresh owner");
        Dispose(fresh);
    }

    private static void TestCleanupFailure()
    {
        // This intentionally unresolved synthetic COM remains reserved only for
        // this short-lived test process. Never force-release uncertain handles.
        var reservation = Acquire("COM27007", "failed cleanup");
        int calls = 0;
        var failed = Release(reservation, () =>
        {
            Interlocked.Increment(ref calls);
            throw new IOException("Synthetic driver cleanup did not complete.");
        });
        Exception observed = null;
        try { Wait(failed); }
        catch (Exception error) { observed = error; }
        Assert(observed != null && calls == 1, "A failed cleanup was silently treated as successful or retried.");
        Assert((bool)reservation.GetType().GetProperty("IsReleaseFailed", Instance).GetValue(reservation, null),
            "A failed release did not retain an explicit unconfirmed-cleanup state.");
        Dispose(reservation);
        var pending = Denied("COM27007", "unsafe replacement");
        Assert((bool)pending.GetType().GetProperty("IsReleaseFailed", Instance).GetValue(pending, null),
            "The next owner was not told that the old handle's release had failed.");
    }

    private static void TestErrorMessages()
    {
        var reserved = Acquire("COM27008", "split-window-2");
        try
        {
            var owned = Denied("COM27008", "main");
            var chinese = Describe(owned, "COM27008", true);
            var english = Describe(owned, "COM27008", false);
            Assert(chinese.Contains("COM27008") && english.Contains("COM27008") &&
                chinese.Contains("split-window-2") && english.Contains("split-window-2"),
                "Application-owned conflicts do not identify their actual reservation owner.");
        }
        finally { Dispose(reserved); }
        foreach (var error in new Exception[] {
            new UnauthorizedAccessException("Synthetic access denial"),
            new IOException("Synthetic resource busy", unchecked((int)0x800700AA)),
            new IOException("Synthetic sharing violation", unchecked((int)0x80070020)) })
        {
            var chinese = Describe(error, "COM27009", true);
            var english = Describe(error, "COM27009", false);
            Assert(chinese.Contains("COM27009") && english.Contains("COM27009") &&
                chinese.Contains(error.Message) && english.Contains(error.Message),
                "OS rejection diagnostics lost the selected COM or original driver error.");
            Assert(!chinese.Contains("一定") && !chinese.Contains("确定被") &&
                english.IndexOf("definitely", StringComparison.OrdinalIgnoreCase) < 0,
                "The program claimed an unproven external owner from a generic OS error.");
        }
        var unrelated = new ArgumentException("Synthetic invalid baud parameter");
        var preserved = Describe(unrelated, "COM27010", true);
        Assert(preserved.Contains(unrelated.Message), "An unrelated configuration error was hidden by an occupancy diagnosis.");
    }
}
