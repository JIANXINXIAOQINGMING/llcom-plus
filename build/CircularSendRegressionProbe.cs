using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Run only in the existing isolated-profile, STA critical-test host. No window is
// shown, no serial target is opened, and all send completions are controlled here.
public static class CircularSendRegressionProbe
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Invoke(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static T NoTarget<T>() where T : class { return null; }

    // Drain only continuations owned by this test, not the WPF dispatcher. Pumping
    // the application dispatcher could execute the normal App.StartupUri callback.
    private sealed class TestContext : SynchronizationContext
    {
        private readonly Queue<Action> work = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state)
        {
            lock (work) work.Enqueue(() => callback(state));
        }
        internal void Drain()
        {
            while (true)
            {
                Action action;
                lock (work)
                {
                    if (work.Count == 0) return;
                    action = work.Dequeue();
                }
                action();
            }
        }
        internal void Until(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                Drain();
                if (clock.ElapsedMilliseconds > 5000)
                    throw new TimeoutException("Circular-send test continuation did not finish.");
                Thread.Sleep(1);
            }
            Drain();
        }
    }

    private sealed class Pending : IDisposable
    {
        internal readonly object Request;
        internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
        private readonly CancellationTokenRegistration registration;
        internal Pending(object request, CancellationToken token)
        {
            Request = request;
            Token = token;
            registration = token.Register(() => Completion.TrySetCanceled());
        }
        public void Dispose() { registration.Dispose(); }
    }

    private sealed class Sender : IDisposable
    {
        internal readonly List<Pending> Calls = new List<Pending>();
        public Task<bool> Send<T>(T request, CancellationToken token)
        {
            var pending = new Pending(request, token);
            Calls.Add(pending);
            return pending.Completion.Task;
        }
        public void Dispose()
        {
            foreach (var pending in Calls) pending.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Type stepType;
        private readonly Type itemType;
        private readonly MethodInfo runLoop;
        private readonly object page;
        internal readonly Sender Sender = new Sender();
        internal readonly object First, Second;
        internal readonly IList Plan;

        internal Fixture(Assembly assembly, FieldInfo requestField, int firstDelay)
        {
            var requestType = assembly.GetType("llcom_plus.Tools.UartSendRequest", true);
            var method = typeof(Sender).GetMethod("Send").MakeGenericMethod(requestType);
            requestField.SetValue(null, Delegate.CreateDelegate(requestField.FieldType, Sender, method));
            var pageType = assembly.GetType("llcom_plus.Pages.CircularSendPage", true);
            page = Activator.CreateInstance(pageType, true);
            runLoop = pageType.GetMethod("RunLoopAsync", Instance);
            stepType = assembly.GetType("llcom_plus.Pages.CircularSendStep", true);
            itemType = assembly.GetType("llcom_plus.Pages.CircularSendItem", true);
            First = Activator.CreateInstance(itemType);
            Second = Activator.CreateInstance(itemType);
            itemType.GetProperty("Index").SetValue(First, 1, null);
            itemType.GetProperty("Index").SetValue(Second, 2, null);
            Plan = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stepType));
            Add(First, "AT", false, firstDelay);
            Add(Second, "41 54", true, 0);
        }
        private void Add(object item, string command, bool hex, int delay)
        {
            Plan.Add(Activator.CreateInstance(stepType, Instance, null,
                new object[] { item, command, hex, delay }, null));
        }
        internal string Status(object item)
        {
            return (string)itemType.GetProperty("Status").GetValue(item, null);
        }
        internal Task Run(CancellationToken token)
        {
            return (Task)Invoke(runLoop, page, Plan, 1, token);
        }
        public void Dispose() { Sender.Dispose(); }
    }

    public static void Run(Assembly assembly)
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
            "Circular-send UI regression requires the isolated STA test host.");
        var global = assembly.GetType("llcom_plus.Tools.Global", true);
        var asyncField = global.GetField("SendDataAsyncRequest", Static);
        var openField = global.GetField("IsActiveSerialTargetOpenRequest", Static);
        var captureField = global.GetField("CaptureActiveSerialTargetRequest", Static);
        var originalSend = asyncField.GetValue(null);
        var originalOpen = openField.GetValue(null);
        var originalCapture = captureField.GetValue(null);
        var originalContext = SynchronizationContext.Current;
        var context = new TestContext();
        try
        {
            openField.SetValue(null, new Func<bool>(() => true));
            var targetType = assembly.GetType("llcom_plus.Tools.ActiveSerialTarget", true);
            var noTarget = typeof(CircularSendRegressionProbe).GetMethod("NoTarget", Static).MakeGenericMethod(targetType);
            captureField.SetValue(null, Delegate.CreateDelegate(captureField.FieldType, noTarget));
            SynchronizationContext.SetSynchronizationContext(context);
            using (var f = new Fixture(assembly, asyncField, 120))
            using (var cts = new CancellationTokenSource())
            {
                var run = f.Run(cts.Token);
                Assert(f.Sender.Calls.Count == 1 && !run.IsCompleted,
                    "Loop queued multiple commands or completed before the first actual send.");
                string sending = f.Status(f.First);
                context.Drain();
                Assert(f.Status(f.First) == sending && f.Status(f.Second) == string.Empty,
                    "A pending send was marked successful or the next command was started.");
                var first = f.Sender.Calls[0];
                Assert(first.Token == cts.Token, "Loop cancellation did not reach the send request.");
                Assert((string)first.Request.GetType().GetProperty("SourceText").GetValue(first.Request, null) == "AT",
                    "Text source was not preserved for the captured COM encoding and send script.");
                var clock = Stopwatch.StartNew();
                first.Completion.SetResult(true);
                context.Until(() => f.Sender.Calls.Count == 2);
                Assert(clock.ElapsedMilliseconds >= 90,
                    "Configured delay did not start after the actual send completed.");
                Assert(f.Status(f.First) != sending && !run.IsCompleted,
                    "First result was not committed correctly before the second pending send.");
                f.Sender.Calls[1].Completion.SetResult(true);
                context.Until(() => run.IsCompleted);
                run.GetAwaiter().GetResult();
            }

            using (var f = new Fixture(assembly, asyncField, 0))
            using (var cts = new CancellationTokenSource())
            {
                var run = f.Run(cts.Token);
                cts.Cancel();
                context.Until(() => run.IsCompleted);
                Assert(run.IsCanceled && f.Sender.Calls.Count == 1,
                    "Stop did not cancel the pending send or allowed subsequent commands.");
            }

            using (var f = new Fixture(assembly, asyncField, 0))
            {
                var run = f.Run(CancellationToken.None);
                f.Sender.Calls[0].Completion.SetException(new IOException("Synthetic send failure."));
                context.Until(() => run.IsCompleted);
                Assert(run.IsFaulted && run.Exception.InnerException is IOException && f.Sender.Calls.Count == 1,
                    "Actual send failure was swallowed or the loop continued after it.");
            }

            using (var f = new Fixture(assembly, asyncField, 0))
            {
                var run = f.Run(CancellationToken.None);
                f.Sender.Calls[0].Completion.SetResult(false);
                context.Until(() => run.IsCompleted);
                Assert(run.IsFaulted && run.Exception.InnerException is InvalidOperationException && f.Sender.Calls.Count == 1,
                    "Rejected send request was reported as successful.");
            }

            using (var sender = new Sender())
            {
                var requestType = assembly.GetType("llcom_plus.Tools.UartSendRequest", true);
                var method = typeof(Sender).GetMethod("Send").MakeGenericMethod(requestType);
                asyncField.SetValue(null, Delegate.CreateDelegate(asyncField.FieldType, sender, method));
                var request = Activator.CreateInstance(requestType, true);
                byte[] bytes = { 65, 84 };
                requestType.GetProperty("Data").SetValue(request, bytes, null);
                var requestAsync = global.GetMethod("RequestSendDataAsync", Static);
                var pending = (Task<bool>)Invoke(requestAsync, null, request, CancellationToken.None);
                bytes[0] = 0;
                var captured = (byte[])requestType.GetProperty("Data").GetValue(sender.Calls[0].Request, null);
                Assert(captured[0] == 65 && !pending.IsCompleted,
                    "Queued request shared mutable input or completed before the handler.");
                sender.Calls[0].Completion.SetResult(true);
                Assert(pending.GetAwaiter().GetResult(), "Async request lost its completion result.");
                asyncField.SetValue(null, null);
                var unavailable = (Task<bool>)Invoke(requestAsync, null, request, CancellationToken.None);
                Assert(!unavailable.GetAwaiter().GetResult(),
                    "Missing async handler incorrectly fell back to fire-and-forget success.");
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    bool cancelled = false;
                    try { Invoke(requestAsync, null, request, cts.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled, "Pre-cancelled send request was accepted.");
                }
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
            asyncField.SetValue(null, originalSend);
            openField.SetValue(null, originalOpen);
            captureField.SetValue(null, originalCapture);
        }
    }
}
