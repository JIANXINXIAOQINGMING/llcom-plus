using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Runs inside Test-CriticalFixes' isolated STA/resources/profile host. All serial
// requests are simulated and no application dispatcher or device is started.
public static class LogReplayRegressionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object selected;
    private static T Capture<T>() where T : class { return (T)selected; }
    private static object NewTarget(Assembly assembly, string identity)
    {
        return Activator.CreateInstance(assembly.GetType("llcom_plus.Tools.ActiveSerialTarget", true), All, null,
            new object[] { identity, "COM_TEST", new Func<bool>(() => true),
                new Func<byte[], CancellationToken, Action<int>, bool>((bytes, token, commit) => true), true }, null);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object Invoke(MethodInfo method, object target, params object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private sealed class Context : SynchronizationContext
    {
        private readonly Queue<Action> queue = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state) { lock (queue) queue.Enqueue(() => callback(state)); }
        internal void Until(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                Action action = null;
                lock (queue) if (queue.Count > 0) action = queue.Dequeue();
                if (action != null) action(); else Thread.Sleep(1);
                if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException("Replay probe did not finish.");
            }
        }
    }
    private sealed class Pending : IDisposable
    {
        internal object Request;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
        private CancellationTokenRegistration registration;
        internal Pending(object request, CancellationToken token)
        {
            Request = request; Token = token;
            registration = token.Register(() => Completion.TrySetCanceled());
        }
        public void Dispose() { registration.Dispose(); }
    }
    private sealed class Sender : IDisposable
    {
        internal readonly List<Pending> Calls = new List<Pending>();
        public Task<bool> Send<T>(T request, CancellationToken token)
        {
            var call = new Pending(request, token); Calls.Add(call); return call.Completion.Task;
        }
        public void Dispose() { foreach (var call in Calls) call.Dispose(); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly object Page, Target;
        internal readonly Sender Sender = new Sender();
        internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        private readonly Type pageType, stepType;
        internal Fixture(Assembly assembly, FieldInfo sendField, bool receiveOnly = false)
        {
            Target = NewTarget(assembly, "split:fake:slot:1:uart:COM_TEST:1");
            selected = Target;
            var requestType = assembly.GetType("llcom_plus.Tools.UartSendRequest", true);
            sendField.SetValue(null, Delegate.CreateDelegate(sendField.FieldType, Sender,
                typeof(Sender).GetMethod("Send").MakeGenericMethod(requestType)));
            pageType = assembly.GetType("llcom_plus.Pages.LogReplayPage", true);
            stepType = assembly.GetType("llcom_plus.Pages.ReplayStep", true);
            Page = Activator.CreateInstance(pageType);
            Invoke(pageType.GetMethod("Page_Loaded", All), Page, null, null);
            pageType.GetField("replayTarget", All).SetValue(Page, Target);
            pageType.GetField("replayCts", All).SetValue(Page, Cancellation);
            pageType.GetField("receiveCaptureEnabled", All).SetValue(Page, 1);
            var hub = assembly.GetType("llcom_plus.Tools.SerialTraceHub", true);
            pageType.GetField("receiveAfterTraceId", All).SetValue(Page, hub.GetProperty("LastId", All).GetValue(null, null));
            Add(receiveOnly ? "Receive" : "Send", receiveOnly ? new byte[] { 79, 75 } : new byte[] { 65, 84 });
            if (!receiveOnly) Add("Send", new byte[] { 66 });
        }
        private void Add(string direction, byte[] bytes)
        {
            var step = Activator.CreateInstance(stepType);
            stepType.GetProperty("Direction").SetValue(step, Enum.Parse(stepType.GetProperty("Direction").PropertyType, direction), null);
            stepType.GetProperty("Data").SetValue(step, bytes, null);
            ((IList)pageType.GetProperty("Steps").GetValue(Page, null)).Add(step);
        }
        internal Task<bool> Run() { return (Task<bool>)Invoke(pageType.GetMethod("ReplayOnceAsync", All), Page, 1000, Cancellation.Token); }
        internal int ReceivedCount { get { return ((IList)pageType.GetField("receiveBuffer", All).GetValue(Page)).Count; } }
        internal void Unload() { Invoke(pageType.GetMethod("Page_Unloaded", All), Page, null, null); }
        public void Dispose()
        {
            Unload();
            pageType.GetField("replayCts", All).SetValue(Page, null);
            Cancellation.Dispose(); Sender.Dispose();
        }
    }
    private static void Rx(Assembly assembly, string identity, byte[] bytes)
    {
        var hub = assembly.GetType("llcom_plus.Tools.SerialTraceHub", true);
        Invoke(hub.GetMethod("RecordData", All), null, "COM_TEST", identity, false, bytes, 65001, null);
    }
    public static void Run(Assembly assembly)
    {
        var global = assembly.GetType("llcom_plus.Tools.Global", true);
        var send = global.GetField("SendDataAsyncRequest", All);
        var capture = global.GetField("CaptureActiveSerialTargetRequest", All);
        var oldSend = send.GetValue(null); var oldCapture = capture.GetValue(null);
        var oldContext = SynchronizationContext.Current;
        var context = new Context();
        try
        {
            capture.SetValue(null, Delegate.CreateDelegate(capture.FieldType,
                typeof(LogReplayRegressionProbe).GetMethod("Capture", All).MakeGenericMethod(assembly.GetType("llcom_plus.Tools.ActiveSerialTarget", true))));
            SynchronizationContext.SetSynchronizationContext(context);
            using (var f = new Fixture(assembly, send))
            {
                var run = f.Run();
                Check(f.Sender.Calls.Count == 1 && !run.IsCompleted, "Replay advanced before actual send completed.");
                var call = f.Sender.Calls[0]; var type = call.Request.GetType();
                Check(call.Token == f.Cancellation.Token &&
                    (string)type.GetProperty("ExpectedTargetIdentity").GetValue(call.Request, null) == "split:fake:slot:1:uart:COM_TEST:1" &&
                    !(bool)type.GetProperty("ApplySendProcessing").GetValue(call.Request, null),
                    "Replay did not preserve raw bytes, its captured target or stop token.");
                call.Completion.SetResult(true); context.Until(() => f.Sender.Calls.Count == 2);
                Check(!run.IsCompleted, "Replay finished before its final send.");
                f.Sender.Calls[1].Completion.SetResult(true); context.Until(() => run.IsCompleted);
                Check(run.GetAwaiter().GetResult(), "Replay did not finish successful simulated sends.");
            }
            using (var f = new Fixture(assembly, send))
            {
                var run = f.Run(); f.Unload(); context.Until(() => run.IsCompleted);
                Check(run.IsCanceled && f.Sender.Calls.Count == 1, "Leaving replay failed to cancel queued send or allowed another command.");
            }
            using (var f = new Fixture(assembly, send))
            {
                var run = f.Run(); f.Sender.Calls[0].Completion.SetException(new IOException("Synthetic replay write error."));
                context.Until(() => run.IsCompleted);
                Check(run.IsFaulted && f.Sender.Calls.Count == 1, "Replay ignored a send failure or automatically resent.");
                var ignored = run.Exception;
            }
            using (var f = new Fixture(assembly, send))
            {
                var run = f.Run(); selected = NewTarget(assembly, "uart:COM_OTHER:1"); f.Sender.Calls[0].Completion.SetResult(true);
                context.Until(() => run.IsCompleted);
                Check(run.IsFaulted && f.Sender.Calls.Count == 1, "Replay followed a changed target after pending send completion.");
                var ignored = run.Exception;
            }
            using (var f = new Fixture(assembly, send, true))
            {
                var run = f.Run();
                Rx(assembly, "uart:COM_OTHER:1", new byte[] { 79, 75 });
                Rx(assembly, "uart:COM_TEST:2", new byte[] { 79, 75 });
                Check(f.ReceivedCount == 0 && !run.IsCompleted, "Other COM or reopened-generation response reached the replay buffer.");
                Rx(assembly, "uart:COM_TEST:1", new byte[] { 79 });
                Rx(assembly, "uart:COM_TEST:1", new byte[] { 75 });
                context.Until(() => run.IsCompleted);
                Check(run.GetAwaiter().GetResult(), "Main-port split alias or cross-packet reply was lost.");
            }
            using (var f = new Fixture(assembly, send, true))
            {
                var run = f.Run(); selected = NewTarget(assembly, "uart:COM_TEST:2"); context.Until(() => run.IsCompleted);
                Check(run.IsFaulted, "Replay did not stop when the selected target changed while waiting for RX.");
                var ignored = run.Exception;
            }
            using (var f = new Fixture(assembly, send))
            {
                var type = f.Page.GetType();
                type.GetField("replayCts", All).SetValue(f.Page, null);
                var button = type.GetField("SelectLogButton", All).GetValue(f.Page);
                var enabled = button.GetType().GetProperty("IsEnabled");
                enabled.SetValue(button, false, null);
                f.Unload();
                Invoke(type.GetMethod("Page_Loaded", All), f.Page, null, null);
                Check((bool)enabled.GetValue(button, null), "Reloading a cached replay page left its cancelled-load button disabled.");
            }
        }
        finally
        {
            selected = null; send.SetValue(null, oldSend); capture.SetValue(null, oldCapture);
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
}
