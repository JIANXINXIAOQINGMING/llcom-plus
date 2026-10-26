using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

// All driver, open-state and enumeration operations are injected. No SerialPort
// is opened and no timer is started; the production probe is invoked directly.
public static class SerialConnectionMonitorRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static ConstructorInfo constructor;
    private static MethodInfo checkConnection, classify, containsPort;

    private static object NewMonitor(Func<bool> isOpen, Action probeDriver,
        Func<string[]> names, Action<Exception> callback)
    {
        return constructor.Invoke(new object[] { isOpen, probeDriver, names, "COM11", callback, false });
    }

    private static void Check(object monitor)
    {
        try { checkConnection.Invoke(monitor, new object[] { null }); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static bool IsDisconnect(Exception exception)
    {
        return (bool)classify.Invoke(null, new object[] { exception });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static string[] Run(Assembly assembly)
    {
        var type = assembly.GetType("llcom_plus.Tools.SerialConnectionMonitor", true);
        constructor = type.GetConstructor(Instance, null,
            new[] { typeof(Func<bool>), typeof(Action), typeof(Func<string[]>), typeof(string), typeof(Action<Exception>), typeof(bool) }, null);
        Assert(constructor != null, "The real serial monitor must expose injectable driver operations for offline verification.");
        checkConnection = type.GetMethod("CheckConnection", Instance);
        classify = type.GetMethod("IsDisconnectException", Static);
        containsPort = type.GetMethod("ContainsPort", Static);
        var passed = new List<string>();
        int signals = 0;
        Exception observed = null;
        var accessDenied = new UnauthorizedAccessException("unplugged driver denies access");
        var monitor = NewMonitor(() => true, () => { throw accessDenied; },
            () => new[] { "COM11" }, error => { signals++; observed = error; });
        using ((IDisposable)monitor)
        {
            Check(monitor);
            Check(monitor);
            Assert(signals == 1 && ReferenceEquals(observed, accessDenied),
                "A stale IsOpen=true handle must signal access-denied removal exactly once.");
        }
        passed.Add("A quiet adapter with stale IsOpen=true retires once on driver access denial.");

        signals = 0;
        monitor = NewMonitor(() => true, () => { }, () => new[] { "COM1", "COM12" }, error => signals++);
        using ((IDisposable)monitor)
        {
            Check(monitor);
            Check(monitor);
            Assert(signals == 1, "Removing COM11 from a successful scan must signal removal once.");
        }
        passed.Add("A successful scan detects the missing captured COM without matching a different port.");

        signals = 0;
        bool scanFails = true;
        monitor = NewMonitor(() => true, () => { },
            () => { if (scanFails) throw new IOException("enumeration unavailable"); return new string[0]; },
            error => signals++);
        using ((IDisposable)monitor)
        {
            Check(monitor);
            Assert(signals == 0, "An enumeration failure is not proof that an open adapter was removed.");
            scanFails = false;
            Check(monitor);
            Assert(signals == 1, "A failed scan must not disable subsequent successful removal detection.");
        }
        passed.Add("Enumeration failures leave connections intact and monitoring continues.");

        signals = 0;
        monitor = NewMonitor(() => true, () => { }, () => null, error => signals++);
        using ((IDisposable)monitor)
        {
            Check(monitor);
            Assert(signals == 0, "Unknown port inventory must not be treated as an empty inventory.");
        }
        passed.Add("An unknown inventory is not treated as all ports being absent.");

        signals = 0;
        int reads = 0;
        monitor = NewMonitor(() => false, () => reads++, () => new[] { "COM11" }, error => signals++);
        using ((IDisposable)monitor)
        {
            Check(monitor);
            Assert(signals == 1 && reads == 0, "A closed handle must retire without querying the driver.");
        }
        passed.Add("A driver-reported closed connection retires without an additional read query.");

        signals = 0;
        Exception transient = new TimeoutException("temporary driver delay");
        monitor = NewMonitor(() => true, () => { if (transient != null) throw transient; },
            () => new[] { "COM11" }, error => signals++);
        using ((IDisposable)monitor)
        {
            Check(monitor);
            transient = new OperationCanceledException("operation canceled");
            Check(monitor);
            Assert(signals == 0, "Timeout/cancellation alone must not close a serial connection.");
            transient = new IOException("device no longer connected");
            Check(monitor);
            Assert(signals == 1, "A transient probe failure must not prevent a later real disconnect.");
        }
        passed.Add("Timeout and cancellation do not masquerade as removal or stop later health checks.");

        signals = 0;
        reads = 0;
        monitor = NewMonitor(() => true, () => { reads++; throw new IOException("removed"); },
            () => new[] { "COM11" }, error => signals++);
        ((IDisposable)monitor).Dispose();
        Check(monitor);
        Assert(signals == 0 && reads == 0, "Disposed monitors must not probe or notify the retired holder.");
        passed.Add("Disposal cancels stale checks before they touch the driver or notify a holder.");

        signals = 0;
        object disposedDuringProbe = null;
        disposedDuringProbe = NewMonitor(() => true,
            () => { ((IDisposable)disposedDuringProbe).Dispose(); throw new IOException("removed while closing"); },
            () => new[] { "COM11" }, error => signals++);
        using ((IDisposable)disposedDuringProbe)
        {
            Check(disposedDuringProbe);
            Assert(signals == 0, "Disposal during an in-flight query must suppress its delayed disconnect callback.");
        }
        passed.Add("A query finishing after disposal cannot publish a delayed disconnect.");

        signals = 0;
        monitor = NewMonitor(() => true, () => { throw new IOException("removed"); },
            () => new[] { "COM11" }, error => { signals++; throw new InvalidOperationException("failing observer"); });
        using ((IDisposable)monitor)
        {
            Check(monitor);
            Check(monitor);
            Assert(signals == 1, "A failing disconnect observer must not escape or repeatedly run.");
        }
        passed.Add("Observer failures stay inside the monitor and do not repeat the callback.");

        reads = 0;
        object reentrant = null;
        reentrant = NewMonitor(() => true, () => { reads++; Check(reentrant); },
            () => new[] { "COM11" }, error => { throw new InvalidOperationException("unexpected removal"); });
        using ((IDisposable)reentrant)
        {
            Check(reentrant);
            Assert(reads == 1, "A pending probe must exclude overlapping/reentrant health checks.");
        }
        passed.Add("Only one driver health query can run at a time.");

        Assert(IsDisconnect(new UnauthorizedAccessException()) && IsDisconnect(new IOException()) &&
            IsDisconnect(new ObjectDisposedException("serial")) && IsDisconnect(new InvalidOperationException()) &&
            IsDisconnect(new AggregateException(new IOException())) && !IsDisconnect(new TimeoutException()) &&
            !IsDisconnect(new OperationCanceledException()) && !IsDisconnect(new ArgumentException()) &&
            !IsDisconnect(new AggregateException(new IOException(), new ArgumentException())),
            "Serial disconnection classification must handle access denial without swallowing unrelated failures.");
        passed.Add("Disconnect classification includes access denial and excludes timeout/cancellation/unrelated faults.");

        Assert((bool)containsPort.Invoke(null, new object[] { new[] { " com11\0stale", "COM12" }, "COM11" }) &&
            !(bool)containsPort.Invoke(null, new object[] { new[] { "COM1", "COM110" }, "COM11" }) &&
            !(bool)containsPort.Invoke(null, new object[] { null, "COM11" }),
            "Port lookup must normalize case/NUL suffixes and use exact COM identities.");
        passed.Add("Port lookup tolerates driver name suffixes while keeping exact COM identity.");
        return passed.ToArray();
    }
}
