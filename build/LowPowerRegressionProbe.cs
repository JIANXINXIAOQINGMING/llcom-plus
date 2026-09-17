using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Hardware-free tests against the built application's controller and send queue.
public static class LowPowerRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Instance).Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly object Controller, Line, Profile, Key = new object();
        internal Func<bool> Current = () => true;
        internal Action<string> Trace;
        internal Fixture(Assembly assembly)
        {
            var type = assembly.GetType("llcom_plus.Model.DtrWakeController", true);
            Controller = Activator.CreateInstance(type, true);
            Line = Activator.CreateInstance(type.GetNestedType("ProbeDtrLine", BindingFlags.NonPublic),
                Instance, null, new object[] { false }, null);
            Profile = Activator.CreateInstance(assembly.GetType("llcom_plus.Model.UartPortProfile", true), true);
            Set("dtrWakeBeforeSend", true);
            Set("dtrWakeDelayMs", 0);
            Set("dtrWakeIdleMs", 60000);
        }
        internal void Set(string name, object value) { Profile.GetType().GetProperty(name).SetValue(Profile, value, null); }
        internal bool Enabled { get { return (bool)Line.GetType().GetProperty("Enabled").GetValue(Line, null); } }
        internal object Timer { get { return Controller.GetType().GetField("restoreTimerState", Instance).GetValue(Controller); } }
        internal void Send(Action send, CancellationToken token = default(CancellationToken))
        {
            Call(Controller, "ExecuteWithWakeCore", Key, 1L, Current, Profile, token, send, Line, Trace);
        }
        internal void Receive() { Call(Controller, "RenewAfterReceiveCore", Key, 1L, Current, Profile, Line); }
        internal void Fire(object timer) { Call(Controller, "RestoreAfterIdle", timer); }
        internal void Manual(bool enabled) { Call(Controller, "ApplyUserDtrCore", Key, 1L, Line, enabled); }
        internal void Invalidate() { Call(Controller, "InvalidateConnectionCore", Key, 1L); }
        public void Dispose() { ((IDisposable)Controller).Dispose(); }
    }

    public static void Run(Assembly assembly)
    {
        var controllerType = assembly.GetType("llcom_plus.Model.DtrWakeController", true);
        Assert((bool)controllerType.GetMethod("ProbeDtrWakeLifecycleBehavior", Static).Invoke(null, null),
            "Base wake lifecycle, RX renewal, manual DTR and no-retry regression failed.");

        using (var f = new Fixture(assembly))
        {
            int observations = 0, sends = 0;
            f.Trace = message => { observations++; throw new InvalidOperationException("Synthetic observer failure"); };
            f.Send(() => { sends++; Assert(f.Enabled, "Trace observer interrupted DTR assertion."); });
            Assert(observations > 0 && sends == 1, "Trace observer failure interrupted or repeated a write.");
            f.Fire(f.Timer);
            Assert(!f.Enabled, "Trace observer failure prevented DTR restoration.");
        }

        for (int i = 0; i < 40; i++)
        {
            using (var f = new Fixture(assembly))
            {
                f.Set("dtrWakeIdleMs", 0);
                f.Send(() => Assert(f.Enabled, "DTR was not asserted during send."));
                Assert(SpinWait.SpinUntil(() => !f.Enabled, 2000), "Zero idle failed to restore DTR.");
            }
        }

        using (var f = new Fixture(assembly))
        {
            f.Send(() => { });
            var oldTimer = f.Timer;
            f.Set("dtrWakeBeforeSend", false);
            f.Send(() => {
                f.Fire(oldTimer);
                Assert(f.Enabled, "Disabling wake allowed an old timer to interrupt active TX.");
            });
            oldTimer = f.Timer;
            f.Receive();
            f.Fire(oldTimer);
            Assert(f.Enabled, "Disabling wake stopped RX renewal of an existing wake session.");
            f.Fire(f.Timer);
            Assert(!f.Enabled, "Existing session did not restore after final RX.");
            f.Send(() => Assert(!f.Enabled, "Disabled wake asserted a fresh DTR edge."));
        }

        using (var f = new Fixture(assembly))
        using (var cancellation = new CancellationTokenSource())
        {
            int sends = 0;
            f.Set("dtrWakeDelayMs", 10000);
            var task = Task.Run(() => {
                try { f.Send(() => sends++, cancellation.Token); return false; }
                catch (OperationCanceledException) { return true; }
            });
            Assert(SpinWait.SpinUntil(() => f.Enabled, 2000), "Wake did not begin.");
            cancellation.Cancel();
            Assert(task.Wait(2000) && task.Result && sends == 0, "Cancellation during wake still sent data or blocked.");
            f.Fire(f.Timer);
            Assert(!f.Enabled, "Canceled wake did not restore its DTR state.");
        }

        using (var f = new Fixture(assembly))
        {
            int sends = 0;
            f.Set("dtrWakeDelayMs", 10000);
            var task = Task.Run(() => {
                try { f.Send(() => sends++); return false; }
                catch (System.IO.IOException) { return true; }
            });
            Assert(SpinWait.SpinUntil(() => f.Enabled, 2000), "Manual-override probe did not wake.");
            f.Manual(false);
            Assert(task.Wait(2000) && task.Result && sends == 0 && !f.Enabled,
                "Manual DTR change failed to interrupt wake wait or was overwritten.");
        }

        using (var f = new Fixture(assembly))
        {
            int sends = 0;
            f.Current = () => { f.Invalidate(); return true; };
            bool failed = false;
            try { f.Send(() => sends++); } catch (System.IO.IOException) { failed = true; }
            Assert(failed && sends == 0 && !f.Enabled, "Stale wake asserted DTR after lifecycle invalidation.");
        }

        TestQueue(assembly);
    }

    private static void TestQueue(Assembly assembly)
    {
        var type = assembly.GetType("llcom_plus.Model.SerialSendQueue", true);
        var queue = Activator.CreateInstance(type, true);
        Func<Func<bool>, long, Task<bool>> enqueue = (send, bytes) =>
            (Task<bool>)Call(queue, "Enqueue", send, bytes);
        var order = new List<int>();
        using (var started = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var first = enqueue(() => {
                started.Set();
                if (!release.Wait(3000)) throw new TimeoutException("Queue probe release timed out.");
                order.Add(1); return true;
            }, 1);
            try
            {
                Assert(started.Wait(2000), "Queue did not run in the background.");
                var failure = enqueue(() => { order.Add(2); throw new InvalidOperationException("expected"); }, 1);
                var last = enqueue(() => { order.Add(3); return true; }, 1);
                Assert(!first.IsCompleted && !last.IsCompleted, "Queue reordered or blocked the caller.");
                release.Set();
                Assert(last.Wait(2000) && last.Result, "A failed send poisoned subsequent queue items.");
                Assert(failure.IsFaulted && failure.Exception != null && string.Join(",", order) == "1,2,3",
                    "Queue did not preserve submission order through failure.");
            }
            finally { release.Set(); }
        }
        var oversized = enqueue(() => true, 17L * 1024 * 1024);
        Assert(oversized.IsFaulted && oversized.Exception != null, "Queue ignored its byte limit.");
        Call(queue, "Stop");
        Assert(enqueue(() => true, 1).IsCanceled, "Stopped queue accepted new work.");

        queue = Activator.CreateInstance(type, true);
        using (var release = new ManualResetEventSlim())
        {
            int ran = 0;
            var first = enqueue(() => { release.Wait(3000); return true; }, 1);
            var pending = new List<Task<bool>>();
            try
            {
                for (int i = 0; i < 255; i++) pending.Add(enqueue(() => { Interlocked.Increment(ref ran); return true; }, 1));
                var rejected = enqueue(() => true, 1);
                Assert(rejected.IsFaulted && rejected.Exception != null, "Queue ignored its item limit.");
                Call(queue, "Stop");
                release.Set();
                try { Task.WaitAll(pending.ToArray(), 3000); } catch (AggregateException) { }
                Assert(ran == 0 && pending.TrueForAll(t => t.IsCompleted), "Stopping queue ran deferred sends.");
                foreach (var task in pending) { var observed = task.Exception; }
            }
            finally { release.Set(); Call(queue, "Stop"); }
        }
    }
}
