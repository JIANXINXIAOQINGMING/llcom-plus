using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls.Primitives;

// Runs against the same unshown window and disposable profile as the editing suite.
public static class QuickCommandDeletionRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] RowProperties = {
        "id", "text", "hex", "commit", "recvScriptPath", "recvScriptPara", "appendCrlf",
        "disableSuggestion", "responseMode", "expectedResponse", "responseTimeoutMs", "responseRetries", "skipInWorkflow"
    };
    private static readonly string[] UndoFields = {
        "deletedQuickCommand", "deletedQuickCommandIndex", "deletedQuickPlaceholder", "deletedQuickPlaceholderState"
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static object Call(Type type, object target, string name, params object[] arguments)
    {
        try
        {
            var method = type.GetMethods(target == null ? Static : Instance)
                .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object Property(object target, string name)
    {
        return target.GetType().GetProperty(name, Instance).GetValue(target, null);
    }
    private static void Set(object target, string name, object value)
    {
        target.GetType().GetProperty(name, Instance).SetValue(target, value, null);
    }
    private static object[] RowState(object row)
    {
        return RowProperties.Select(name => Property(row, name)).ToArray();
    }
    private sealed class BusyStore : IDisposable
    {
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private readonly ManualResetEventSlim release = new ManualResetEventSlim();
        private readonly Thread worker;
        private Exception failure;
        internal BusyStore(string name)
        {
            worker = new Thread(() => {
                try
                {
                    using (var mutex = new Mutex(false, name))
                    {
                        if (!mutex.WaitOne(1000)) throw new InvalidOperationException("Could not block the snapshot store.");
                        ready.Set();
                        release.Wait();
                        mutex.ReleaseMutex();
                    }
                }
                catch (Exception ex) { failure = ex; ready.Set(); }
            }) { IsBackground = true };
            worker.Start();
            Assert(ready.Wait(2000), "Competing snapshot lease did not start.");
            if (failure != null) throw failure;
        }
        public void Dispose()
        {
            release.Set();
            Assert(worker.Join(2000), "Competing snapshot lease did not stop.");
            ready.Dispose(); release.Dispose();
            if (failure != null) throw failure;
        }
    }

    public static string[] Run(Assembly assembly, object window, object settings, string testRoot)
    {
        var passed = new List<string>();
        var main = window.GetType();
        var service = assembly.GetType("llcom_plus.Tools.QuickSendBackupService", true);
        var global = assembly.GetType("llcom_plus.Tools.Global", true);
        var profile = global.GetField("ProfilePath", Static);
        var originalProfile = profile.GetValue(null);
        var canSave = main.GetField("canSaveSendList", Instance);
        var originalCanSave = canSave.GetValue(window);
        var rows = (IList)main.GetField("toSendListItems", Instance).GetValue(window);
        var list = (Selector)Call(main, window, "FindName", "toSendList");
        try
        {
            foreach (var blocked in new[] { "busy", "unwritable" })
            foreach (var rowCount in new[] { 2, 1 })
            {
                Call(service, null, "Shutdown");
                var caseDirectory = Path.Combine(testRoot, "delete-" + blocked + "-" + rowCount);
                Directory.CreateDirectory(caseDirectory);
                profile.SetValue(null, caseDirectory + Path.DirectorySeparatorChar);
                canSave.SetValue(window, false);
                try
                {
                    rows.Clear();
                    for (int index = 0; index < rowCount; index++)
                    {
                        var row = Call(main, window, "CreateBlankQuickSendItem", index + 1);
                        Set(row, "text", "AT+DELETE_" + blocked + "_" + index);
                        Set(row, "hex", true);
                        Set(row, "appendCrlf", false);
                        Set(row, "expectedResponse", "DELETE_OK");
                        Set(row, "responseMode", 2);
                        rows.Add(row);
                    }
                }
                finally { canSave.SetValue(window, true); }
                Call(main, window, "SaveSendList", null, EventArgs.Empty);
                // Freeze automatic snapshots so only the explicit delete backup is tested.
                Call(service, null, "Shutdown");
                var target = rows[0];
                var previousUndo = Call(main, window, "CreateBlankQuickSendItem", 99);
                Set(previousUndo, "text", "Previous pending undo");
                Call(main, window, "RememberQuickSendDeletion", previousUndo);
                Call(main, window, "CaptureQuickSendPlaceholder");
                list.SelectedItem = target;
                var selected = list.SelectedItem;
                var originals = rows.Cast<object>().ToArray();
                var states = originals.Select(RowState).ToArray();
                var undo = UndoFields.Select(name => main.GetField(name, Instance).GetValue(window)).ToArray();
                var undoButton = Call(main, window, "FindName", "UndoQuickCommandButton");
                var undoEnabled = Property(undoButton, "IsEnabled");
                var settingsPath = Path.Combine(caseDirectory, "settings.json");
                var diskBytes = File.ReadAllBytes(settingsPath);
                var snapshotPath = Path.Combine(caseDirectory, "quick-send-backups");
                BusyStore competing = null;
                if (blocked == "busy")
                {
                    Directory.CreateDirectory(snapshotPath);
                    competing = new BusyStore((string)Call(service, null, "BuildMutexName"));
                }
                else File.WriteAllText(snapshotPath, "Test blocker: a file occupies the snapshot directory path.");
                try
                {
                    var arguments = new object[] { target, null };
                    Assert(!(bool)Call(main, window, "TryRemoveQuickSendItem", arguments), "Deletion ignored its failed backup.");
                    Assert(!string.IsNullOrWhiteSpace((string)arguments[1]), "A blocked deletion did not explain its failure.");
                    Assert(rows.Count == originals.Length && ReferenceEquals(list.SelectedItem, selected),
                        "A blocked deletion changed the collection or selection.");
                    for (int index = 0; index < originals.Length; index++)
                        Assert(ReferenceEquals(rows[index], originals[index]) && RowState(rows[index]).SequenceEqual(states[index]),
                            "A blocked deletion changed a row object or its options.");
                    for (int index = 0; index < UndoFields.Length; index++)
                        Assert(object.Equals(undo[index], main.GetField(UndoFields[index], Instance).GetValue(window)),
                            "A blocked deletion replaced an earlier undo record.");
                    Assert(object.Equals(undoEnabled, Property(undoButton, "IsEnabled")), "A blocked deletion changed undo availability.");
                    Assert(File.ReadAllBytes(settingsPath).SequenceEqual(diskBytes), "A blocked deletion modified settings on disk.");
                    var persisted = (IList)Property(settings, "quickSend");
                    Assert(persisted.Count == originals.Length && persisted.Cast<object>().SequenceEqual(originals),
                        "A blocked deletion changed the settings collection.");
                }
                finally
                {
                    if (competing != null) competing.Dispose();
                    else File.Delete(snapshotPath);
                }
                passed.Add(blocked + " snapshot store preserves " + rowCount + " row(s), selection, previous undo, model identities/options and settings bytes.");

                var successArguments = new object[] { target, null };
                Assert((bool)Call(main, window, "TryRemoveQuickSendItem", successArguments) && successArguments[1] == null,
                    "Deletion did not recover when the snapshot store became writable.");
                Assert(rows.Count == 1 && (rowCount == 1 ? (string)Property(rows[0], "text") == "" : !rows.Contains(target)),
                    "Recovered deletion did not remove/clear its captured row.");
                Assert(((IList)Call(service, null, "GetSnapshots")).Count > 0, "Recovered deletion lacks its durable pre-delete snapshot.");
                Call(main, window, "UndoQuickCommand_Click", null, null);
                Assert(rows.Count == rowCount && RowState(rows[0]).Skip(1).SequenceEqual(states[0].Skip(1)),
                    "Undo after recovered deletion lost command content or options.");
                Assert(File.ReadAllText(settingsPath).Contains("AT+DELETE_" + blocked + "_0"),
                    "Undo after recovery did not persist the restored command.");
                passed.Add(blocked + " snapshot store recovery restores deletion and undo for " + rowCount + " row(s).");
            }
        }
        finally
        {
            Call(service, null, "Shutdown");
            profile.SetValue(null, originalProfile);
            canSave.SetValue(window, originalCanSave);
            Call(main, window, "ClearQuickSendUndo");
        }
        return passed.ToArray();
    }
}
