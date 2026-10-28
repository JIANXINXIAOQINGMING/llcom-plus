using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    private static void RejectRestore(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException is IOException || ex.InnerException is UnauthorizedAccessException) return;
            throw ex.InnerException;
        }
        throw new InvalidOperationException(message);
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
        internal object Capture(object settings) { return Invoke(Service, "CaptureState", settings); }
        internal void Restore(object settings, object state)
        {
            Settings.GetField("_suspendSave", Instance).SetValue(settings, false);
            Settings.GetMethod("ApplyQuickSendRestore", Instance).Invoke(settings, new[] { state, "pre-restore" });
        }
        internal void Import(object settings, object[] incoming, bool allPages, bool includeNames = true)
        {
            Settings.GetField("_suspendSave", Instance).SetValue(settings, false);
            Settings.GetMethod("ApplyQuickSendImport", Instance).Invoke(settings,
                new[] { incoming[0], includeNames ? incoming[1] : null, (object)allPages });
        }
        internal void AssertState(object settings, string prefix, int pageCount)
        {
            var state = Capture(settings);
            var pages = (IList)Property(state, "QuickSendList");
            var names = (IList)Property(state, "QuickListNames");
            Assert(pages.Count == pageCount && names.Count == pageCount &&
                (int)Property(state, "Selected") == pageCount - 1, "Restore changed the page structure unexpectedly.");
            for (int index = 0; index < pageCount; index++)
                Assert((string)names[index] == prefix + index &&
                    (string)Property(((IList)pages[index])[0], "Text") == prefix + index,
                    "Restore changed the wrong command or page name.");
        }
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

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "restore-blocked-backup")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var incoming = fixture.Capture(fixture.CreateSettings("replacement"));
            var backupDirectory = Path.Combine(fixture.Profile, "quick-send-backups");
            Directory.Delete(backupDirectory);
            File.WriteAllText(backupDirectory, "isolated backup directory blocker");
            RejectRestore(() => fixture.Restore(settings, incoming), "Restore continued without a recovery snapshot.");
            fixture.AssertState(settings, "keep", 2);
            Assert(!File.Exists(Path.Combine(fixture.Profile, "settings.json")), "Failed backup still committed replacement settings.");
            Console.WriteLine("PASS  An unwritable recovery directory blocks restore before changing any current command.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "restore-busy-backup")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var incoming = fixture.Capture(fixture.CreateSettings("replacement"));
            using (fixture.BlockStore())
                RejectRestore(() => fixture.Restore(settings, incoming), "Restore continued while the backup store was busy.");
            fixture.AssertState(settings, "keep", 2);
            Assert(fixture.Snapshots.Count == 0, "Busy-store restore unexpectedly wrote a recovery snapshot.");
            Console.WriteLine("PASS  A busy backup store blocks restore without replacing the current pages.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "restore-commit-failure")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var incoming = fixture.Capture(fixture.CreateSettings("replacement"));
            var settingsPath = Path.Combine(fixture.Profile, "settings.json");
            const string previousFile = "{\"sentinel\":\"previous settings\"}";
            File.WriteAllText(settingsPath, previousFile);
            using (var held = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                RejectRestore(() => fixture.Restore(settings, incoming), "A blocked settings commit unexpectedly succeeded.");
            fixture.AssertState(settings, "keep", 2);
            Assert(File.ReadAllText(settingsPath) == previousFile, "Failed commit changed the previous settings file.");
            Assert(fixture.Snapshots.Count == 1 && !fixture.Pending, "Failed restore did not preserve its recovery snapshot or scheduled replacement backup.");
            Assert(Directory.GetFiles(fixture.Profile, "settings.json.*.tmp").Length == 0, "Failed restore left a settings temporary file.");
            Console.WriteLine("PASS  A failed settings commit rolls memory back and preserves the existing file and recovery snapshot.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "restore-success")))
        {
            var settings = fixture.CreateSettings("previous", 2);
            var incoming = fixture.Capture(fixture.CreateSettings("restored", 3));
            fixture.Restore(settings, incoming);
            fixture.AssertState(settings, "restored", 3);
            var persisted = File.ReadAllText(Path.Combine(fixture.Profile, "settings.json"));
            Assert(persisted.Contains("restored2") && !persisted.Contains("previous0"), "Successful restore was not persisted.");
            var prior = fixture.Snapshots.Cast<object>().FirstOrDefault(item =>
                (string)Property(item, "Reason") == "pre-restore");
            Assert(prior != null, "Successful restore lost its prior recovery snapshot.");
            var priorPath = (string)Property(prior, "FilePath");
            Assert(File.ReadAllText(priorPath).Contains("previous0"), "Recovery snapshot does not contain the pre-restore commands.");
            Console.WriteLine("PASS  A successful restore saves all pages and selection while retaining the previous commands for recovery.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "import-blocked-backup")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var directory = Path.Combine(fixture.Profile, "quick-send-backups");
            Directory.Delete(directory);
            File.WriteAllText(directory, "isolated import backup blocker");
            foreach (bool all in new[] { false, true })
            {
                var incoming = fixture.StateArguments("incoming", all ? 3 : 1);
                RejectRestore(() => fixture.Import(settings, incoming, all), "Import continued without a pre-import snapshot.");
                fixture.AssertState(settings, "keep", 2);
                Assert(!File.Exists(Path.Combine(fixture.Profile, "settings.json")), "Failed import backup still changed settings.");
            }
            Console.WriteLine("PASS  Single-page and all-page imports stop before mutation when the recovery backup is unwritable.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "import-busy-backup")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            using (fixture.BlockStore())
                foreach (bool all in new[] { false, true })
                {
                    var incoming = fixture.StateArguments("incoming", all ? 3 : 1);
                    RejectRestore(() => fixture.Import(settings, incoming, all), "Import continued while its backup was skipped.");
                    fixture.AssertState(settings, "keep", 2);
                }
            Assert(fixture.Snapshots.Count == 0, "Contended import unexpectedly wrote a backup.");
            Console.WriteLine("PASS  A busy backup store blocks both import modes without changing current commands.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "import-commit-failure")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var originalRows = (IList)fixture.Settings.GetMethod("GetAllQuickSendLists", Instance).Invoke(settings, null);
            var path = Path.Combine(fixture.Profile, "settings.json");
            const string original = "{\"sentinel\":\"before import\"}";
            File.WriteAllText(path, original);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                foreach (bool all in new[] { false, true })
                {
                    var incoming = fixture.StateArguments("incoming", all ? 3 : 1);
                    RejectRestore(() => fixture.Import(settings, incoming, all), "Import ignored a failed atomic settings commit.");
                    fixture.AssertState(settings, "keep", 2);
                    var restoredRows = (IList)fixture.Settings.GetMethod("GetAllQuickSendLists", Instance).Invoke(settings, null);
                    for (int index = 0; index < originalRows.Count; index++)
                        Assert(ReferenceEquals(((IList)originalRows[index])[0], ((IList)restoredRows[index])[0]),
                            "Failed import replaced a retained UI row object instead of restoring its original reference.");
                    Assert(File.ReadAllText(path) == original, "Failed import changed the original settings file.");
                }
            Assert(fixture.Snapshots.Count == 1 && !fixture.Pending &&
                Directory.GetFiles(fixture.Profile, "settings.json.*.tmp").Length == 0,
                "Failed imports lost the pre-import snapshot, scheduled failed state, or leaked temporary files.");
            Console.WriteLine("PASS  Failed single/all-page commits roll back contents, names and selection without a second disk-write rollback.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "import-single-success")))
        {
            var settings = fixture.CreateSettings("original", 2);
            var incoming = fixture.StateArguments("imported", 1);
            fixture.Import(settings, incoming, false);
            var state = fixture.Capture(settings);
            var pages = (IList)Property(state, "QuickSendList");
            var names = (IList)Property(state, "QuickListNames");
            Assert(pages.Count == 2 && (int)Property(state, "Selected") == 1 &&
                (string)Property(((IList)pages[0])[0], "Text") == "original0" &&
                (string)Property(((IList)pages[1])[0], "Text") == "imported0" &&
                (string)names[0] == "original0" && (string)names[1] == "original1",
                "Single-page import changed another page, its name or the selected index.");
            var snapshot = fixture.Snapshots.Cast<object>().Single(item => (string)Property(item, "Reason") == "pre-import");
            Assert(File.ReadAllText((string)Property(snapshot, "FilePath")).Contains("original1") &&
                File.ReadAllText(Path.Combine(fixture.Profile, "settings.json")).Contains("imported0"),
                "Successful single-page import lost the prior configuration or did not persist.");
            Console.WriteLine("PASS  Single-page import replaces only the selected page and preserves every page name with a recovery snapshot.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "import-all-success")))
        {
            var settings = fixture.CreateSettings("original", 2);
            fixture.Import(settings, fixture.StateArguments("imported", 3), true);
            var state = fixture.Capture(settings);
            var pages = (IList)Property(state, "QuickSendList");
            var names = (IList)Property(state, "QuickListNames");
            Assert(pages.Count == 3 && names.Count == 3 && (int)Property(state, "Selected") == 1,
                "All-page import did not preserve the selected index or complete page/name state.");
            for (int index = 0; index < pages.Count; index++)
                Assert((string)Property(((IList)pages[index])[0], "Text") == "imported" + index &&
                    (string)names[index] == "imported" + index, "All-page import mixed names and commands.");
            var persisted = File.ReadAllText(Path.Combine(fixture.Profile, "settings.json"));
            Assert(persisted.Contains("imported2") && !persisted.Contains("original0"), "All-page import was only partly persisted.");
            Console.WriteLine("PASS  All-page import commits contents and names together and keeps a still-valid selected page.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "import-legacy-success")))
        {
            var settings = fixture.CreateSettings("original", 3);
            fixture.Import(settings, fixture.StateArguments("legacy", 2), true, false);
            var state = fixture.Capture(settings);
            var pages = (IList)Property(state, "QuickSendList");
            var names = (IList)Property(state, "QuickListNames");
            Assert(pages.Count == 2 && names.Count == 2 && (int)Property(state, "Selected") == 0 &&
                (string)names[0] == "original0" && (string)names[1] == "original1" &&
                (string)Property(((IList)pages[0])[0], "Text") == "legacy0" &&
                (string)Property(((IList)pages[1])[0], "Text") == "legacy1",
                "Legacy import without names did not preserve existing names and reset invalid selection.");
            Console.WriteLine("PASS  Legacy all-page imports without names retain compatible page names and reset an invalid selection to the first page.");
        }

        foreach (string operation in new[] { "add", "select", "remove" })
        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "page-" + operation + "-commit-failure")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var originalRows = (IList)fixture.Settings.GetMethod("GetAllQuickSendLists", Instance).Invoke(settings, null);
            fixture.Settings.GetField("_suspendSave", Instance).SetValue(settings, false);
            var path = Path.Combine(fixture.Profile, "settings.json");
            const string original = "{\"sentinel\":\"before page operation\"}";
            File.WriteAllText(path, original);
            Action changePage = () =>
            {
                if (operation == "add") fixture.Settings.GetMethod("AddQuickSendPage", Instance).Invoke(settings, null);
                else if (operation == "select") fixture.Settings.GetProperty("quickSendSelect", Instance).SetValue(settings, 0, null);
                else Assert((bool)fixture.Settings.GetMethod("RemoveQuickSendPage", Instance).Invoke(settings, new object[] { 0 }),
                    "Removing one of two valid pages was unexpectedly rejected.");
            };
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                RejectRestore(changePage, "Page " + operation + " ignored the failed settings commit.");
                fixture.AssertState(settings, "keep", 2);
                var restoredRows = (IList)fixture.Settings.GetMethod("GetAllQuickSendLists", Instance).Invoke(settings, null);
                for (int index = 0; index < originalRows.Count; index++)
                    Assert(ReferenceEquals(((IList)originalRows[index])[0], ((IList)restoredRows[index])[0]),
                        "Page " + operation + " rollback replaced the retained UI command object.");
            }
            Assert(File.ReadAllText(path) == original && Directory.GetFiles(fixture.Profile, "settings.json.*.tmp").Length == 0,
                "Failed page " + operation + " changed the previous file or leaked its temporary file.");

            changePage();
            var state = fixture.Capture(settings);
            var pages = (IList)Property(state, "QuickSendList");
            var names = (IList)Property(state, "QuickListNames");
            int selected = (int)Property(state, "Selected");
            if (operation == "add")
                Assert(pages.Count == 3 && names.Count == 3 && selected == 2 &&
                    (string)Property(((IList)pages[0])[0], "Text") == "keep0" &&
                    (string)Property(((IList)pages[1])[0], "Text") == "keep1",
                    "Adding a page after write recovery changed earlier commands or selection.");
            else if (operation == "select")
                Assert(pages.Count == 2 && names.Count == 2 && selected == 0 &&
                    (string)Property(((IList)pages[0])[0], "Text") == "keep0" &&
                    (string)Property(((IList)pages[1])[0], "Text") == "keep1",
                    "Selecting a page after write recovery changed page contents.");
            else
                Assert(pages.Count == 1 && names.Count == 1 && selected == 0 &&
                    (string)Property(((IList)pages[0])[0], "Text") == "keep1" && (string)names[0] == "keep1",
                    "Removing a page after write recovery removed the wrong page or name.");
            var persisted = File.ReadAllText(path);
            Assert(persisted != original && persisted.Contains("\"quickSendSelect\":" + selected),
                "Recovered page " + operation + " did not persist its selected index.");
            Console.WriteLine("PASS  Page " + operation + " rolls back original rows/names/selection on an exclusive-file failure and succeeds after write recovery.");
        }

        using (var fixture = new Fixture(assembly, Path.Combine(testRoot, "page-delete-blocked-backup")))
        {
            var settings = fixture.CreateSettings("keep", 2);
            var originalRows = (IList)fixture.Settings.GetMethod("GetAllQuickSendLists", Instance).Invoke(settings, null);
            fixture.Settings.GetField("_suspendSave", Instance).SetValue(settings, false);
            var path = Path.Combine(fixture.Profile, "settings.json");
            const string original = "{\"sentinel\":\"before page deletion\"}";
            File.WriteAllText(path, original);
            string directory = Path.Combine(fixture.Profile, "quick-send-backups");
            Directory.Delete(directory);
            File.WriteAllText(directory, "isolated page-deletion backup blocker");
            RejectRestore(() => fixture.Settings.GetMethod("RemoveQuickSendPage", Instance).Invoke(settings, new object[] { 0 }),
                "Page deletion continued without a recovery snapshot.");
            fixture.AssertState(settings, "keep", 2);
            var restoredRows = (IList)fixture.Settings.GetMethod("GetAllQuickSendLists", Instance).Invoke(settings, null);
            Assert(ReferenceEquals(((IList)originalRows[0])[0], ((IList)restoredRows[0])[0]) &&
                ReferenceEquals(((IList)originalRows[1])[0], ((IList)restoredRows[1])[0]) && File.ReadAllText(path) == original,
                "A failed pre-deletion backup changed commands, their UI identities or the settings file.");
            Console.WriteLine("PASS  An unwritable backup directory blocks page deletion without changing data, names, selection or row identities.");
        }
    }
}
