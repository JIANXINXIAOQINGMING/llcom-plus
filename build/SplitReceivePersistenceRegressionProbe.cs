using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Threading;

// Real offscreen WPF dispatcher and isolated files; no serial port is opened.
public static class SplitReceivePersistenceRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static void InstallDependencyResolution(string directory)
    {
        // The PowerShell host does not inherit the application's pinned redirects.
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            new AssemblyName(args.Name).Name == "System.Runtime.CompilerServices.Unsafe"
                ? Assembly.LoadFrom(Path.Combine(directory, "System.Runtime.CompilerServices.Unsafe.dll")) : null;
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static object Get(object target, string name) { return target.GetType().GetField(name, Instance).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Instance).SetValue(target, value); }
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Instance).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void SetProperty(object target, string name, object value)
    {
        target.GetType().GetProperty(name, Instance).SetValue(target, value, null);
    }
    private static int Count(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }
    private static string Read(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
    }
    private static void Pump() { Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle); }
    private static void QueueFlushOnWorker(object slot)
    {
        Exception failure = null;
        var dispatcher = Dispatcher.CurrentDispatcher;
        Thread worker = null;
        using (var posted = new ManualResetEvent(false))
        {
            DispatcherHookEventHandler hook = (sender, args) => { if (Thread.CurrentThread == worker) posted.Set(); };
            dispatcher.Hooks.OperationPosted += hook;
            try
            {
                worker = new Thread(() => { try { Call(slot, "FlushReceivedData", new object[] { null }); } catch (Exception e) { failure = e; } });
                worker.IsBackground = true;
                worker.Start();
                Assert(worker.Join(5000), "Packet persistence unexpectedly waited for the unpumped UI.");
                if (failure != null) throw failure;
                Assert(posted.WaitOne(1000), "The receive packet did not enter the real UI dispatcher queue.");
            }
            finally { dispatcher.Hooks.OperationPosted -= hook; }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly object Slot, Settings, Profile;
        internal readonly string Root, OldText, OldHex;
        internal string NewText, NewHex;
        internal int Delivered;
        private readonly EventInfo receiveEvent;
        private readonly EventHandler<byte[]> handler;
        internal Fixture(Assembly assembly, string root, string name, int encoding = 65001, bool printing = false)
        {
            Root = Path.Combine(root, name);
            Directory.CreateDirectory(Root);
            var global = assembly.GetType("llcom_plus.Tools.Global", true);
            Settings = global.GetField("setting").GetValue(null);
            SetProperty(Settings, "sessionLogEnabled", true);
            SetProperty(Settings, "DisableLog", !printing);
            var page = Activator.CreateInstance(assembly.GetType("llcom_plus.Pages.MultiPortPage", true), new object[] { 1, true, null });
            Call(page, "EnsureSlotsCreated");
            Slot = ((IList)Get(page, "slots"))[0];
            Set(Slot, "connectionGeneration", 11L);
            Set(Slot, "selectedPortName", "COM_AUDIT");
            Profile = Activator.CreateInstance(assembly.GetType("llcom_plus.Model.UartPortProfile", true), true);
            SetProperty(Profile, "encoding", encoding);
            SetProperty(Profile, "timeout", 600000);
            SetProperty(Profile, "maxLength", 1048576U);
            OldText = Path.Combine(Root, "old-string.log");
            OldHex = Path.Combine(Root, "old-hex.log");
            InstallWriters(OldText, OldHex);
            receiveEvent = global.GetEvent("ActiveSerialTargetReceivedEvent", BindingFlags.Public | BindingFlags.Static);
            handler = (sender, bytes) => Interlocked.Increment(ref Delivered);
            receiveEvent.AddEventHandler(null, handler);
        }
        internal long Generation { get { return (long)Get(Slot, "connectionGeneration"); } }
        internal int HistoryCount { get { return ((IList)Get(Slot, "packedLogItems")).Count; } }
        internal void Queue(byte[] bytes, long generation = 11)
        {
            Call(Slot, "QueueReceivedData", bytes, generation, Profile);
        }
        internal void InstallWriters(string text, string hex)
        {
            Set(Slot, "sessionStringLogWriter", new StreamWriter(text, false, new UTF8Encoding(false)) { AutoFlush = true });
            Set(Slot, "sessionHexLogWriter", new StreamWriter(hex, false, new UTF8Encoding(false)) { AutoFlush = true });
        }
        internal void NewWriters()
        {
            Assert(Get(Slot, "sessionStringLogWriter") == null && Get(Slot, "sessionHexLogWriter") == null,
                "A replacement connection inherited the old session's writer.");
            NewText = Path.Combine(Root, "new-string.log");
            NewHex = Path.Combine(Root, "new-hex.log");
            InstallWriters(NewText, NewHex);
        }
        public void Dispose()
        {
            try { Call(Slot, "Close", false, false); }
            finally
            {
                Call(Slot, "DisposeOwnedPort");
                Pump();
                receiveEvent.RemoveEventHandler(null, handler);
            }
        }
    }
    private sealed class GatedStream : MemoryStream
    {
        internal readonly ManualResetEvent Started = new ManualResetEvent(false), Continue = new ManualResetEvent(false);
        internal bool Armed;
        private int blocked;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Armed && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                Started.Set();
                if (!Continue.WaitOne(5000)) throw new IOException("Isolated writer gate timed out.");
            }
            base.Write(buffer, offset, count);
        }
    }
    public static string[] Run(Assembly assembly, string root)
    {
        var passed = new List<string>();
        foreach (var encoding in new[] { 936, 65001 })
        {
            using (var f = new Fixture(assembly, root, "encoding-" + encoding, encoding))
            {
                var bytes = Encoding.GetEncoding(encoding).GetBytes("CROSS_中文\r\n");
                // Cut inside the first Chinese character, rather than at its boundary.
                f.Queue(bytes.Take(7).ToArray());
                f.Queue(bytes.Skip(7).ToArray());
                Assert(Read(f.OldText).Length == 0, "A driver fragment was decoded before packet aggregation.");
                QueueFlushOnWorker(f.Slot);
                Assert(Count(Read(f.OldText), "CROSS_中文") == 1 && !Read(f.OldText).Contains("\uFFFD"),
                    "Cross-read Chinese bytes were lost or decoded independently.");
                Assert(Count(Read(f.OldHex), BitConverter.ToString(bytes).Replace('-', ' ')) == 1,
                    "The original aggregated HEX packet was changed or duplicated.");
                Assert(f.HistoryCount == 0 && f.Delivered == 0, "The UI should still be queued and printing stopped.");
                Pump();
                Assert(f.HistoryCount == 0 && f.Delivered == 1, "Stopped printing suppressed reception or script publication.");
                passed.Add("Cross-read " + encoding + " characters retain original packet decoding; disk persistence precedes queued UI even with printing stopped.");
            }
        }
        using (var f = new Fixture(assembly, root, "queued-reopen", printing: true))
        {
            var old = Encoding.UTF8.GetBytes("OLD_PACKET\r\n");
            f.Queue(old);
            QueueFlushOnWorker(f.Slot);
            Assert(Count(Read(f.OldText), "OLD_PACKET") == 1, "Queued UI retained the only copy of the original packet.");
            Call(f.Slot, "RetireDirectConnection", Get(f.Slot, "serial"), 11L, false);
            Call(f.Slot, "PrepareDirectSessionForOpen");
            f.NewWriters();
            f.Queue(old, 11L); // late driver generation must be rejected
            var next = Encoding.UTF8.GetBytes("NEW_PACKET\r\n");
            f.Queue(next, f.Generation);
            QueueFlushOnWorker(f.Slot);
            Pump();
            Assert(Count(Read(f.OldText), "OLD_PACKET") == 1 && !Read(f.OldText).Contains("NEW_PACKET"), "The original file lost its ownership.");
            Assert(Count(Read(f.NewText), "NEW_PACKET") == 1 && !Read(f.NewText).Contains("OLD_PACKET"), "The new writer inherited old data.");
            Assert(f.Delivered == 1 && f.HistoryCount == 1, "Unexpected reopened receive publication: events=" + f.Delivered + ", history=" + f.HistoryCount + ".");
            passed.Add("A queued packet survives retirement once in its old file; reopening closes old writers and rejects stale driver/UI callbacks.");
        }
        using (var f = new Fixture(assembly, root, "timeout-close"))
        {
            f.Queue(Encoding.UTF8.GetBytes("TAIL_BEFORE_TIMEOUT\r\n"));
            Assert(Read(f.OldText).Length == 0, "The packet timeout fixture flushed prematurely.");
            Call(f.Slot, "RetireDirectConnection", Get(f.Slot, "serial"), 11L, false);
            Call(f.Slot, "PrepareDirectSessionForOpen");
            Assert(Count(Read(f.OldText), "TAIL_BEFORE_TIMEOUT") == 1 && f.Delivered == 0,
                "Retirement dropped its not-yet-timed-out packet or ran UI/script work under cleanup.");
            f.NewWriters();
            Assert(Read(f.NewText).Length == 0, "A reopened session received the old unfinished timeout packet.");
            passed.Add("Retirement persists a not-yet-timed-out tail before resetting the connection, without publishing a cleanup callback.");
        }
        using (var f = new Fixture(assembly, root, "log-restart", printing: true))
        {
            f.Queue(Encoding.UTF8.GetBytes("BEFORE_DIRECTORY_CHANGE\r\n"));
            Call(f.Slot, "RestartSessionLog");
            Assert(Count(Read(f.OldText), "BEFORE_DIRECTORY_CHANGE") == 1 && f.Delivered == 1 && f.HistoryCount == 1,
                "Changing the log destination consumed a packet without publishing it to the live view and script channel.");
            f.NewWriters();
            f.Queue(Encoding.UTF8.GetBytes("AFTER_DIRECTORY_CHANGE\r\n"));
            QueueFlushOnWorker(f.Slot);
            Pump();
            Assert(!Read(f.NewText).Contains("BEFORE_DIRECTORY_CHANGE") && Count(Read(f.NewText), "AFTER_DIRECTORY_CHANGE") == 1 && f.Delivered == 2,
                "Changing the writer duplicated a packet or suppressed the subsequent receive.");
            passed.Add("Changing a log destination retains the pending packet in the old file and publishes it once to the live view and script channel.");
        }
        foreach (var timerFirst in new[] { false, true })
        {
            using (var f = new Fixture(assembly, root, "disable-" + timerFirst, printing: true))
            {
                f.Queue(Encoding.UTF8.GetBytes("TAIL_AT_DISABLE\r\n"));
                SetProperty(f.Settings, "sessionLogEnabled", false);
                // A send can win serial ownership after the flag changes, before the UI closes the writer.
                Call(f.Slot, "WriteSessionLog", "send", Encoding.UTF8.GetBytes("DISABLED_TX"), (int?)65001, false);
                if (timerFirst) QueueFlushOnWorker(f.Slot);
                Call(f.Slot, "CloseSessionLogWriter");
                Pump();
                Call(f.Slot, "FlushReceivedData", new object[] { null });
                Assert(Count(Read(f.OldText), "TAIL_AT_DISABLE") == 1 && !Read(f.OldText).Contains("DISABLED_TX") && f.Delivered == 1,
                    "Disabling logs lost the old tail, logged a new TX, duplicated it, or suppressed its receive event.");
                Assert(Get(f.Slot, "sessionStringLogWriter") == null && Get(f.Slot, "sessionHexLogWriter") == null,
                    "Disabling logs did not release both original writers.");
                f.Queue(Encoding.UTF8.GetBytes("AFTER_DISABLE\r\n"));
                QueueFlushOnWorker(f.Slot);
                Pump();
                Assert(!Read(f.OldText).Contains("AFTER_DISABLE") && f.Delivered == 2,
                    "Disabled logging reopened a file or stopped normal receive publication.");
                passed.Add("Disabling logs preserves the tail exactly once when " + (timerFirst ? "the packet timer" : "the UI close") + " wins, including a racing disabled TX.");
            }
        }
        using (var f = new Fixture(assembly, root, "concurrent-close"))
        using (var gate = new GatedStream())
        using (var hex = new MemoryStream())
        {
            Call(f.Slot, "CloseSessionLog");
            Set(f.Slot, "sessionStringLogWriter", new StreamWriter(gate, new UTF8Encoding(false)) { AutoFlush = true });
            Set(f.Slot, "sessionHexLogWriter", new StreamWriter(hex, new UTF8Encoding(false)) { AutoFlush = true });
            f.Queue(Encoding.UTF8.GetBytes("GATED_RX\r\n"));
            gate.Armed = true;
            Exception writeError = null, closeError = null;
            var write = new Thread(() => { try { Call(f.Slot, "FlushReceivedData", new object[] { null }); } catch (Exception e) { writeError = e; } });
            var close = new Thread(() => { try { Call(f.Slot, "Close", false, false); } catch (Exception e) { closeError = e; } });
            write.IsBackground = close.IsBackground = true;
            write.Start();
            try
            {
                Assert(gate.Started.WaitOne(5000), "The receive packet did not reach the controlled writer gate.");
                close.Start();
                Assert(!close.Join(100), "Close overtook a packet still writing to its original session.");
            }
            finally { gate.Continue.Set(); }
            Assert(write.Join(5000) && close.Join(5000), "File persistence and concurrent close deadlocked.");
            if (writeError != null) throw writeError;
            if (closeError != null) throw closeError;
            Pump();
            Assert(Count(Encoding.UTF8.GetString(gate.ToArray()), "GATED_RX") == 1, "The gated packet was lost or duplicated during close.");
            gate.Started.Dispose(); gate.Continue.Dispose();
            passed.Add("Concurrent close waits for an in-flight packet file write and both settle without lock inversion or duplicate records.");
        }
        return passed.ToArray();
    }
}
