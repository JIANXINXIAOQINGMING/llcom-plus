using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Exercises the built backup service in a disposable profile only. No App/window,
// configuration loading, serial ports, network, or updater is started.
public static class QuickSendBackupRetryProbe
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Invoke(Type type, string name, params object[] args)
    {
        try { return type.GetMethod(name, Static).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static object Property(object target, string name)
    {
        return target.GetType().GetProperty(name, Instance).GetValue(target, null);
    }

    private static bool WaitUntil(Func<bool> test, int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (!test())
        {
            if (elapsed.ElapsedMilliseconds >= milliseconds) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    // Mutex ownership is thread-affine. Keep the competing lease on its own
    // thread so the tested service cannot reacquire it recursively.
    private sealed class CompetingLease : IDisposable
    {
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private readonly ManualResetEventSlim release = new ManualResetEventSlim();
        private readonly Thread worker;
        private Exception failure;

        internal CompetingLease(string name)
        {
            worker = new Thread(() =>
            {
                try
                {
                    using (var mutex = new Mutex(false, name))
                    {
                        Assert(mutex.WaitOne(1000), "Could not acquire competing test lease.");
                        ready.Set();
                        release.Wait();
                        mutex.ReleaseMutex();
                    }
                }
                catch (Exception ex) { failure = ex; ready.Set(); }
            });
            worker.IsBackground = true;
            worker.Start();
            Assert(ready.Wait(2000), "Competing lease worker did not start.");
            if (failure != null) throw failure;
        }

        public void Dispose()
        {
            release.Set();
            Assert(worker.Join(2000), "Competing lease worker did not stop.");
            ready.Dispose();
            release.Dispose();
            if (failure != null) throw failure;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Type Service, Settings, Item;
        internal readonly string Profile;
        private readonly FieldInfo profileField;
        private readonly object oldProfile;

        internal Fixture(Assembly assembly, string profile, bool blockedDirectory = false)
        {
            Service = assembly.GetType("llcom_plus.Tools.QuickSendBackupService", true);
            Settings = assembly.GetType("llcom_plus.Model.Settings", true);
            Item = assembly.GetType("llcom_plus.Model.ToSendData", true);
            profileField = assembly.GetType("llcom_plus.Tools.Global", true).GetField("ProfilePath", Static);
            oldProfile = profileField.GetValue(null);
            Profile = profile;
            Invoke(Service, "Shutdown");
            profileField.SetValue(null, Profile + Path.DirectorySeparatorChar);
            if (blockedDirectory) File.WriteAllText(Profile, "isolated test blocker");
            else Directory.CreateDirectory(Profile);
            var initialized = (bool)Invoke(Service, "Initialize", new object[] { null });
            Assert(initialized != blockedDirectory, "Unexpected test store initialization result.");
        }

        internal object CreateSettings(string prefix, int pages = 1)
        {
            var settings = Activator.CreateInstance(Settings, true);
            Settings.GetField("_suspendSave", Instance).SetValue(settings, true);
            ReplaceState(settings, prefix, pages);
            return settings;
        }

        internal object[] StateArguments(string prefix, int pages)
        {
            var itemListType = typeof(List<>).MakeGenericType(Item);
            var lists = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemListType));
            var names = new List<string>();
            for (int index = 0; index < pages; index++)
            {
                var name = prefix + index;
                var item = Activator.CreateInstance(Item);
                Item.GetProperty("id").SetValue(item, 1, null);
                Item.GetProperty("text").SetValue(item, name, null);
                Item.GetProperty("appendCrlf").SetValue(item, true, null);
                var row = (IList)Activator.CreateInstance(itemListType);
                row.Add(item);
                lists.Add(row);
                names.Add(name);
            }
            return new object[] { lists, names, pages - 1 };
        }

        internal void ReplaceState(object settings, string prefix, int pages)
        {
            ReplaceState(settings, StateArguments(prefix, pages));
        }

        internal void ReplaceState(object settings, object[] arguments)
        {
            Settings.GetMethod("SetAllQuickSendState", Instance, null,
                new[] { arguments[0].GetType(), typeof(IList<string>), typeof(int) }, null)
                .Invoke(settings, arguments);
        }

        internal void Schedule(object settings, string reason)
        {
            Invoke(Service, "ScheduleAutoSnapshot", settings, reason);
        }

        internal object Flush() { return Invoke(Service, "FlushPending"); }
        internal bool Pending { get { return (bool)Field("autoSnapshotPending"); } }
        internal object Field(string name)
        {
            // Match the service's synchronization even in the test observer.
            lock (Service.GetField("timerLock", Static).GetValue(null))
                return Service.GetField(name, Static).GetValue(null);
        }
        internal IList Snapshots { get { return (IList)Invoke(Service, "GetSnapshots"); } }
        internal CompetingLease BlockStore()
        {
            return new CompetingLease((string)Invoke(Service, "BuildMutexName"));
        }

        public void Dispose()
        {
            Invoke(Service, "Shutdown");
            profileField.SetValue(null, oldProfile);
        }
    }

    public static void Run(Assembly assembly, string testRoot)
    {
        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "busy")))
        {
            var settings = fixture.CreateSettings("busy");
            using (fixture.BlockStore())
            {
                fixture.Schedule(settings, "lease-busy");
                Assert(Property(fixture.Flush(), "Status").ToString() == "Skipped", "Busy lease should skip this attempt.");
                Assert(fixture.Pending, "Busy lease discarded the only pending snapshot.");
                var before = (long)fixture.Field("nextAutoSnapshotTimestamp");
                Invoke(fixture.Service, "AutoSnapshotTimerCallback", new object[] { null });
                Assert(fixture.Pending && (long)fixture.Field("nextAutoSnapshotTimestamp") == before,
                    "A stale timer callback bypassed the retry deadline.");
            }
            Assert(WaitUntil(() => !fixture.Pending && fixture.Snapshots.Count == 1, 6000),
                "Lease recovery did not automatically save without another edit.");
            fixture.Schedule(settings, "same-content");
            Assert(Property(fixture.Flush(), "Status").ToString() == "Deduplicated" && !fixture.Pending,
                "Deduplicated snapshots should complete pending work.");
            Console.WriteLine("PASS  Busy store retries automatically; retry is throttled and dedupe completes work.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "blocked-profile"), true))
        {
            fixture.Schedule(fixture.CreateSettings("io"), "io-failure");
            Assert(Property(fixture.Flush(), "Status").ToString() == "Failed" && fixture.Pending,
                "IO failure discarded pending work.");
            File.Move(fixture.Profile, fixture.Profile + ".previous-blocker");
            Directory.CreateDirectory(fixture.Profile);
            Assert(WaitUntil(() => !fixture.Pending && fixture.Snapshots.Count == 1, 6000),
                "IO recovery did not automatically save without another edit.");
            Console.WriteLine("PASS  IO failure retries automatically after recovery.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "newer-edit")))
        {
            var oldSettings = fixture.CreateSettings("old");
            var newSettings = fixture.CreateSettings("new");
            using (fixture.BlockStore())
            {
                fixture.Schedule(oldSettings, "older");
                var flush = Task.Run(() => fixture.Flush());
                Assert(WaitUntil(() => !fixture.Pending, 1000), "Old attempt did not start.");
                fixture.Schedule(newSettings, "newer");
                Assert(flush.Wait(2000), "Old attempt did not finish.");
                Assert(Property(flush.Result, "Status").ToString() == "Skipped", "Old attempt should encounter contention.");
                Assert(fixture.Pending && ReferenceEquals(fixture.Field("pendingSettings"), newSettings) &&
                    (string)fixture.Field("pendingReason") == "newer",
                    "Older failed attempt overwrote the newer edit.");
            }
            Assert((bool)Property(fixture.Flush(), "Succeeded"), "Newer pending edit was not writable.");
            Assert((string)Property(fixture.Snapshots[0], "Reason") == "newer", "Wrong edit was backed up.");
            Console.WriteLine("PASS  Older failed attempt cannot overwrite a newer pending edit.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "shutdown-pending")))
        {
            var settings = fixture.CreateSettings("shutdown");
            using (fixture.BlockStore())
            {
                fixture.Schedule(settings, "shutdown");
                fixture.Flush();
                Assert(fixture.Pending, "Expected retry before shutdown.");
                Invoke(fixture.Service, "Shutdown");
            }
            fixture.Schedule(settings, "must-not-resume");
            Invoke(fixture.Service, "AutoSnapshotTimerCallback", new object[] { null });
            Assert(!fixture.Pending && fixture.Snapshots.Count == 0 && fixture.Field("debounceTimer") == null,
                "Shutdown allowed a pending callback or subsequent scheduling to write.");
            Console.WriteLine("PASS  Shutdown cancels pending retries and prevents new automatic writes.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "shutdown-inflight")))
        {
            Task<object> flush;
            Task shutdown;
            using (fixture.BlockStore())
            {
                fixture.Schedule(fixture.CreateSettings("inflight"), "inflight");
                flush = Task.Run(() => fixture.Flush());
                Assert(WaitUntil(() => !fixture.Pending, 1000), "Inflight attempt did not start.");
                var started = new ManualResetEventSlim();
                shutdown = Task.Run(() => { started.Set(); Invoke(fixture.Service, "Shutdown"); });
                Assert(started.Wait(1000), "Shutdown worker did not start.");
                Assert(!shutdown.Wait(30), "Shutdown returned while an automatic write could still complete.");
                started.Dispose();
            }
            Assert(flush.Wait(2000) && shutdown.Wait(2000), "In-flight shutdown did not finish.");
            Assert(fixture.Field("debounceTimer") == null && !fixture.Pending,
                "In-flight attempt scheduled a retry after shutdown.");
            Console.WriteLine("PASS  Shutdown waits for an in-flight automatic write to settle.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "atomic-state")))
        {
            var settings = fixture.CreateSettings("A", 2);
            var stateA = fixture.StateArguments("A", 2);
            var stateB = fixture.StateArguments("B", 1);
            using (var stop = new CancellationTokenSource())
            {
                var writer = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        fixture.ReplaceState(settings, stateA);
                        fixture.ReplaceState(settings, stateB);
                    }
                });
                try
                {
                    for (int sample = 0; sample < 5000; sample++)
                    {
                        var snapshot = Invoke(fixture.Service, "CaptureState", settings);
                        var pages = (IList)Property(snapshot, "QuickSendList");
                        var names = (IList)Property(snapshot, "QuickListNames");
                        Assert(pages.Count == names.Count && (int)Property(snapshot, "Selected") == pages.Count - 1,
                            "Snapshot mixed page count, names or selected index from different states.");
                        for (int index = 0; index < pages.Count; index++)
                            Assert((string)Property(((IList)pages[index])[0], "Text") == (string)names[index],
                                "Snapshot associated a page with another state's name.");
                    }
                }
                finally
                {
                    stop.Cancel();
                    Assert(writer.Wait(2000), "Concurrent state writer did not stop.");
                }
            }
            Console.WriteLine("PASS  Concurrent page replacement captures contents, names and selected index together.");
        }
    }
}
