using System;
using System.Collections;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

// Reflection-only probes keep internal application types out of the harness.
// No App, window, actual configuration load, network, or SerialPort.Open is used.
public static class WorkspaceProbe
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int checks;

    private const string Sample = @"{
      'Format':'llcom-plus.workspace','Version':1,'Name':'Module A','SavedAtUtc':'2026-09-10T00:00:00Z',
      'Layout':{'SplitCount':2,'ActiveSlot':1,'Ports':[{'Slot':1,'PortName':'COM1','Role':'AT'},{'Slot':2,'PortName':'COM2','Role':'Debug'}]},
      'SerialProfiles':{'COM1':{'baudRate':9600,'sendScript':'trusted-converter'},'COM2':{'baudRate':57600}},
      'QuickSendPages':[[{'id':1,'text':'AT','hex':false,'commit':'Send','recvScriptPath':'trusted-receive','recvScriptPara':'','appendCrlf':true,'disableSuggestion':false}]],
      'PageNames':['Commands'],'SelectedPage':0
    }";

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Pass(string message) { checks++; Console.WriteLine("PASS " + message); }
    private static object P(object obj, string name) { return obj.GetType().GetProperty(name, Instance).GetValue(obj, null); }
    private static void Set(object obj, string name, object value) { obj.GetType().GetProperty(name, Instance).SetValue(obj, value, null); }
    private static object Call(Type type, string name, params object[] args)
    {
        try { return type.GetMethod(name, Static).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object CallInstance(object obj, string name, params object[] args)
    {
        try { return obj.GetType().GetMethod(name, Instance).Invoke(obj, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException ||
                ex.GetType().Namespace == "Newtonsoft.Json") return;
            throw;
        }
        throw new InvalidOperationException(message);
    }
    private static Delegate CaptureDelegate(Type snapshotType, object value)
    {
        return Expression.Lambda(typeof(Func<>).MakeGenericType(snapshotType), Expression.Constant(value, snapshotType)).Compile();
    }
    private static Delegate ApplyDelegate(Type snapshotType, Action<object> action)
    {
        var parameter = Expression.Parameter(snapshotType, "state");
        return Expression.Lambda(typeof(Action<>).MakeGenericType(snapshotType),
            Expression.Invoke(Expression.Constant(action), Expression.Convert(parameter, typeof(object))), parameter).Compile();
    }

    public static void Run(Assembly assembly, string root)
    {
        checks = 0;
        var service = assembly.GetType("llcom_plus.Tools.WorkspaceService", true);
        var snapshots = assembly.GetType("llcom_plus.Tools.WorkspaceSnapshot", true);
        var settingsType = assembly.GetType("llcom_plus.Model.Settings", true);
        var backup = assembly.GetType("llcom_plus.Tools.QuickSendBackupService", true);
        var global = assembly.GetType("llcom_plus.Tools.Global", true);
        var profileField = global.GetField("ProfilePath", Static);
        var oldProfile = profileField.GetValue(null);
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(profile);
        Call(backup, "Shutdown");
        profileField.SetValue(null, profile + Path.DirectorySeparatorChar);
        try
        {
            var fixture = Path.Combine(root, "fixture.json");
            File.WriteAllText(fixture, Sample);
            var original = Call(service, "Read", fixture);
            var savedPath = (string)Call(service, "Save", profile, original, false);
            var loaded = Call(service, "Load", profile, "Module A");
            Assert((int)P(P(loaded, "Layout"), "SplitCount") == 2 && (string)P(loaded, "Name") == "Module A", "Workspace round-trip lost data.");
            Pass("Versioned workspace round-trip with COM roles and two panes.");

            var invalidWorkspace = Path.Combine(profile, "workspaces", "Invalid.workspace.json");
            File.WriteAllText(invalidWorkspace, Sample.Replace("'Version':1", "'Version':99"));
            int validCount = 0, invalidCount = 0;
            foreach (var item in (IEnumerable)Call(service, "List", profile))
            {
                if ((string)P(item, "Name") == "Module A" && P(item, "Error") == null) validCount++;
                if ((string)P(item, "Name") == "Invalid" && !string.IsNullOrEmpty((string)P(item, "Error"))) invalidCount++;
            }
            Assert(validCount == 1 && invalidCount == 1, "One invalid workspace hid the other valid workspaces.");
            File.Delete(invalidWorkspace);
            Pass("A corrupt workspace is reported individually without hiding valid workspaces.");

            Reject(() => Call(service, "ValidateName", "../escape"), "Path traversal was accepted.");
            Reject(() => Call(service, "ValidateName", "CON"), "Device filename was accepted.");
            Reject(() => Call(service, "Save", profile, original, false), "Unconfirmed overwrite was accepted.");
            Pass("Managed names reject traversal/device paths and unconfirmed overwrite.");

            File.WriteAllText(fixture, Sample.Replace("'Version':1", "'Version':99"));
            Reject(() => Call(service, "Read", fixture), "Unknown schema version was accepted.");
            File.WriteAllText(fixture, Sample.Replace("'Version':1", "'Version':1,'mqttPassword':'secret'"));
            Reject(() => Call(service, "Read", fixture), "Unknown top-level field was accepted.");
            File.WriteAllText(fixture, Sample.Replace("'baudRate':9600", "'baudRate':9600,'password':'secret'"));
            Reject(() => Call(service, "Read", fixture), "Unknown profile field was accepted.");
            Pass("Unknown schemas, settings and credential fields are rejected.");

            File.WriteAllText(fixture, Sample.Replace("'PortName':'COM2'", "'PortName':'COM1'"));
            Reject(() => Call(service, "Read", fixture), "Duplicate COM allocation was accepted.");
            File.WriteAllText(fixture, Sample.Replace("'baudRate':9600", "'baudRate':-1"));
            Reject(() => Call(service, "Read", fixture), "Invalid baud rate was accepted.");
            File.WriteAllText(fixture, Sample.Replace("'PortName':'COM1'", "'PortName':null"));
            Reject(() => Call(service, "Read", fixture), "Null COM was accepted.");
            Pass("Invalid port allocation and invalid serial parameters are rejected.");

            File.WriteAllText(fixture, Sample.Replace("trusted-receive", "../../execute.js"));
            Reject(() => Call(service, "Read", fixture), "Script path traversal was accepted.");
            var stripped = Call(service, "Clone", original, false);
            var profileOne = CallInstance(stripped, "GetPortProfile", "COM1");
            Assert((string)P(profileOne, "sendScript") == "default", "Untrusted conversion script was activated.");
            var firstItem = ((IList)((IList)P(stripped, "QuickSendPages"))[0])[0];
            Assert((string)P(firstItem, "ReceiveScriptPath") == "", "Untrusted receive script was activated.");
            Assert((string)P(CallInstance(original, "GetPortProfile", "COM1"), "sendScript") == "trusted-converter", "Script sanitization mutated the original.");
            Pass("Script paths are validated and script references are opt-in without mutating saved data.");

            File.WriteAllText(fixture, new string(' ', 2 * 1024 * 1024 + 1));
            Reject(() => Call(service, "Read", fixture), "Oversized workspace was accepted.");
            File.WriteAllText(fixture, Sample.Replace("'Version':1", "'Version':1,'Version':1"));
            Reject(() => Call(service, "Read", fixture), "Duplicate JSON field was accepted.");
            Pass("Oversized files and duplicate JSON properties are rejected.");

            var settings = Activator.CreateInstance(settingsType, true);
            settingsType.GetField("_activeUartProfileName", Instance).SetValue(settings, "COM1");
            settingsType.GetField("_mqttUser", Instance).SetValue(settings, "DO-NOT-EXPORT-USER");
            settingsType.GetField("_mqttPassword", Instance).SetValue(settings, "DO-NOT-EXPORT-SECRET");
            CallInstance(settings, "ApplyWorkspaceConfiguration", stripped);
            Assert((int)P(settings, "baudRate") == 9600 && (int)P(settings, "serialSplitScreenCount") == 2,
                "Active settings overwrote imported port configuration.");
            Assert((int)P(CallInstance(settings, "GetUartProfileSnapshot", "COM2"), "baudRate") == 57600, "Secondary COM profile was not isolated.");
            var current = Call(service, "Capture", settings, P(stripped, "Layout"), "Before restore");
            var export = Path.Combine(root, "export.json");
            Call(service, "Export", export, current);
            var exported = File.ReadAllText(export);
            Assert(!exported.Contains("DO-NOT-EXPORT") && !exported.Contains("mqtt"), "Workspace leaked unrelated account settings.");
            Pass("Atomic settings application preserves per-COM values and excludes account data.");

            var capture = CaptureDelegate(snapshots, current);
            int applyCalls = 0;
            var apply = ApplyDelegate(snapshots, s => { applyCalls++; });
            Reject(() => Call(service, "Restore", profile, settings, original, capture,
                new Func<string>(() => "A serial port is open"), apply, false), "Open-port guard was ignored.");
            Assert(applyCalls == 0, "Guarded restore still mutated configuration.");
            Pass("Open-port / active-job guard prevents mutation.");

            var blocked = Path.Combine(root, "blocked-profile");
            Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "quick-send-backups"), "isolated backup directory blocker");
            profileField.SetValue(null, blocked + Path.DirectorySeparatorChar);
            Reject(() => Call(service, "Restore", blocked, settings, original, capture,
                new Func<string>(() => null), apply, false), "Restore continued after backup failure.");
            Assert(applyCalls == 0, "Backup failure still mutated configuration.");
            profileField.SetValue(null, profile + Path.DirectorySeparatorChar);
            Pass("Mandatory pre-restore backup failure prevents all configuration mutation.");

            object applied = null;
            var actualApply = ApplyDelegate(snapshots, s => { applied = s; });
            Call(service, "Restore", profile, settings, original, capture, new Func<string>(() => null), actualApply, false);
            Assert(applied != null && (string)P(CallInstance(applied, "GetPortProfile", "COM1"), "sendScript") == "default", "Default restore trusted scripts.");
            Assert(File.Exists(Path.Combine(profile, "workspaces", ".before-restore.json")), "Full configuration recovery was not saved.");
            Pass("Successful restore saves recovery state and defaults to scripts disabled.");

            int restoreCalls = 0;
            var failingApply = ApplyDelegate(snapshots, s => { restoreCalls++; if (restoreCalls == 1) throw new IOException("Synthetic apply failure"); applied = s; });
            Reject(() => Call(service, "Restore", profile, settings, original, capture,
                new Func<string>(() => null), failingApply, false), "Apply failure did not propagate.");
            Assert(restoreCalls == 2 && (string)P(applied, "Name") == "Before restore", "Failed restore did not reapply prior configuration.");
            Pass("Failed application rolls back to the captured prior configuration.");

            var trashPath = (string)Call(service, "Delete", profile, "Module A");
            Assert(!File.Exists(savedPath) && File.Exists(trashPath), "Deleted workspace is not recoverable.");
            Assert((string)P(Call(service, "Read", trashPath), "Name") == "Module A", "Recoverable workspace content changed.");
            Pass("Workspace deletion is recoverable and leaves command history untouched.");
            Assert(checks == 13, "Not all workspace checks executed.");
        }
        finally
        {
            Call(backup, "Shutdown");
            profileField.SetValue(null, oldProfile);
        }
    }
}
