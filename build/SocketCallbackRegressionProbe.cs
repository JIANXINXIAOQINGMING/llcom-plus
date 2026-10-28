using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Threading;

// No Page constructor, Application, socket, OpenSSL/SSH process or network is used.
public static class SocketCallbackRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Call(MethodInfo method, object target, params object[] args)
    {
        try { method.Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void QueuedAcrossReconnect(object page, FieldInfo epoch, Action publish)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Exception failure = null;
        Thread worker = null;
        using (var posted = new ManualResetEvent(false))
        {
            DispatcherHookEventHandler handler = (sender, args) =>
            {
                if (Thread.CurrentThread == worker) posted.Set();
            };
            dispatcher.Hooks.OperationPosted += handler;
            worker = new Thread(() => { try { publish(); } catch (Exception ex) { failure = ex; } });
            worker.IsBackground = true;
            try
            {
                worker.Start();
                Assert(posted.WaitOne(5000), "Interactive callback did not reach the real dispatcher queue.");
                epoch.SetValue(page, (long)epoch.GetValue(page) + 1);
                dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
                Assert(worker.Join(5000), "Interactive callback did not return after dispatch.");
                if (failure != null) throw failure;
            }
            finally { dispatcher.Hooks.OperationPosted -= handler; }
        }
    }
    public static string[] Run(Assembly assembly)
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Use an STA host.");
        var pageType = assembly.GetType("llcom_plus.Pages.SocketClientPage", true);
        var page = FormatterServices.GetUninitializedObject(pageType);
        var dispatcherField = typeof(DispatcherObject).GetField("_dispatcher", Instance);
        Assert(dispatcherField != null, "Dispatcher backing field is unavailable.");
        dispatcherField.SetValue(page, Dispatcher.CurrentDispatcher);
        var epoch = pageType.GetField("nativeSocketEpoch", Instance);
        epoch.SetValue(page, 10L);
        var publish = pageType.GetMethod("PublishInteractiveData", Instance);
        var run = pageType.GetMethod("RunInteractiveCallback", Instance);
        Assert(publish != null && run != null, "Interactive connection callback guards are missing.");
        int delivered = 0;
        EventHandler<byte[]> observer = (sender, bytes) => { delivered++; Assert(bytes[0] == 65, "Callback changed the payload."); };
        var received = pageType.GetEvent("DataRecived");
        received.AddEventHandler(page, observer);
        var passed = new List<string>();
        try
        {
            Call(publish, page, 9L, new byte[] { 65 });
            Assert(delivered == 0, "A retired SSH/OpenSSL process delivered data to the new channel.");
            Call(publish, page, 10L, new byte[] { 65 });
            Assert(delivered == 1, "The current process lost its incoming data.");
            passed.Add("SSH/OpenSSL receive callbacks accept only their captured connection generation.");

            Call(publish, page, 10L, null);
            Call(publish, page, 10L, new byte[0]);
            Assert(delivered == 1, "Empty transport reads published a spurious event.");
            passed.Add("Empty transport callbacks do not reach log or script subscribers.");

            QueuedAcrossReconnect(page, epoch, () => Call(publish, page, 10L, new byte[] { 65 }));
            Assert(delivered == 1, "A queued old callback was delivered after reconnect.");
            Call(publish, page, 11L, new byte[] { 65 });
            Assert(delivered == 2, "Reconnect did not permit the replacement process's data.");
            passed.Add("A real queued dispatcher callback rechecks identity after reconnect, while new data still works.");

            int diagnostics = 0;
            Action detail = () => diagnostics++;
            QueuedAcrossReconnect(page, epoch, () => Call(run, page, 11L, detail));
            Assert(diagnostics == 0, "Queued diagnostics leaked from a retired process.");
            Call(run, page, 12L, detail);
            Assert(diagnostics == 1, "Current diagnostics were suppressed.");
            passed.Add("OpenSSL diagnostics use the same generation check before publication.");
        }
        finally { received.RemoveEventHandler(page, observer); }
        return passed.ToArray();
    }
}
