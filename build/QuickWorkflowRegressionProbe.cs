using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Pure services and immutable DTOs only. No Global initializer, live user files,
// WPF windows or physical serial connection are required by this probe.
public static class QuickWorkflowRegressionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type itemType, workflow, stepType, hub, json, backup;
    private static object P(object obj, string name) { return obj.GetType().GetProperty(name, All).GetValue(obj, null); }
    private static void Set(object obj, string name, object value) { obj.GetType().GetProperty(name, All).SetValue(obj, value, null); }
    private static object Call(Type type, string name, object instance, params object[] args)
    {
        try { return type.GetMethod(name, All).Invoke(instance, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object Read(string value, Type type)
    {
        return json.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) }).Invoke(null, new object[] { value, type });
    }
    private static string Write(object value)
    {
        return (string)json.GetMethod("SerializeObject", new[] { typeof(object) }).Invoke(null, new[] { value });
    }
    private static object Item(int id, string expected)
    {
        return Read("{'id':" + id + ",'text':'AT" + id + "','appendCrlf':true,'responseMode':2,'expectedResponse':'" + expected + "','responseTimeoutMs':100}", itemType);
    }
    private static object Plan(params object[] rows)
    {
        var array = Array.CreateInstance(itemType, rows.Length);
        for (int i = 0; i < rows.Length; i++) array.SetValue(rows[i], i);
        return Call(workflow, "CreatePlan", null, array);
    }
    private static Task RunPlan(object plan, Func<object, CancellationToken, Task> send,
        CancellationToken token = default(CancellationToken), Func<bool> open = null)
    {
        var step = Expression.Parameter(stepType, "step");
        var cancellation = Expression.Parameter(typeof(CancellationToken), "token");
        var adapter = Expression.Lambda(typeof(Func<,,>).MakeGenericType(stepType, typeof(CancellationToken), typeof(Task)),
            Expression.Invoke(Expression.Constant(send), Expression.Convert(step, typeof(object)), cancellation), step, cancellation).Compile();
        return (Task)Call(workflow, "RunAsync", null, plan, "uart:COM_WORKFLOW:1", adapter,
            open ?? new Func<bool>(() => true), null, token);
    }
    private static object Finish(Task task)
    {
        Assert(task.Wait(5000), "Synthetic workflow did not complete within five seconds.");
        return task.GetType().GetProperty("Result").GetValue(task, null);
    }
    private static void Rx(string response)
    {
        Call(hub, "RecordData", null, "COM_WORKFLOW", "uart:COM_WORKFLOW:1", false, Encoding.UTF8.GetBytes(response), 65001, null);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid plan was accepted.");
    }
    public static string[] Run(Assembly assembly)
    {
        itemType = assembly.GetType("llcom_plus.Tools.QuickSendBackupItem", true);
        workflow = assembly.GetType("llcom_plus.Tools.QuickSendWorkflow", true);
        stepType = assembly.GetType("llcom_plus.Tools.QuickWorkflowStep", true);
        hub = assembly.GetType("llcom_plus.Tools.SerialTraceHub", true);
        backup = assembly.GetType("llcom_plus.Tools.QuickSendBackupService", true);
        json = AppDomain.CurrentDomain.GetAssemblies().Single(candidate => candidate.GetName().Name == "Newtonsoft.Json")
            .GetType("Newtonsoft.Json.JsonConvert", true);
        var passed = new List<string>();
        const string legacy = "{\"id\":1,\"text\":\"AT\",\"hex\":false,\"commit\":\"Send\",\"recvScriptPath\":\"\",\"recvScriptPara\":\"\",\"appendCrlf\":true,\"disableSuggestion\":false}";
        var old = Read(legacy, itemType);
        Assert((int)P(old, "ResponseMode") == 0 && (int)P(old, "ResponseTimeoutMs") == 5000 &&
            (int)P(old, "ResponseRetries") == 0 && !(bool)P(old, "SkipInWorkflow"), "Legacy defaults changed.");
        Assert(Write(old) == legacy, "Default workflow properties changed canonical legacy backup bytes.");
        var content = Read("{'quickSendList':[[" + legacy + "]],'quickListNames':['Commands']}", assembly.GetType("llcom_plus.Tools.QuickSendSnapshotContent", true));
        var state = Activator.CreateInstance(assembly.GetType("llcom_plus.Tools.QuickSendBackupState", true), true);
        Set(state, "QuickSendList", P(content, "QuickSendList"));
        Set(state, "QuickListNames", P(content, "QuickListNames"));
        var originalContent = "{\"quickSendList\":[[" + legacy + "]],\"quickListNames\":[\"Commands\"]}";
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(originalContent))).Replace("-", "");
        Assert((string)Call(backup, "ComputeContentHash", null, state) == hash, "Legacy snapshot hash no longer verifies.");
        passed.Add("Old quick-send files preserve defaults and byte-identical backup content hashes.");

        var configured = Item(2, "CONNECTED");
        Set(configured, "ResponseRetries", 2); Set(configured, "SkipInWorkflow", true);
        Set(configured, "ReceiveScriptPath", "converter"); Set(configured, "ReceiveScriptParameter", "参数");
        var cloned = Call(itemType, "Clone", configured);
        var model = Call(itemType, "ToModel", cloned);
        var roundTrip = Call(itemType, "FromModel", null, model);
        Assert(Write(configured) == Write(roundTrip), "Backup/model clone dropped response configuration.");
        Set(cloned, "ExpectedResponse", "changed");
        Assert((string)P(configured, "ExpectedResponse") == "CONNECTED", "Clone mutated original expectation.");
        Assert((bool)Call(itemType, "HasUserContent", Read("{'appendCrlf':true,'expectedResponse':'DRAFT'}", itemType)), "Draft workflow is mistaken for disposable blank content.");
        passed.Add("Backup/model/duplicate round-trips preserve new fields, scripts and CRLF without aliasing.");

        var workspaceType = assembly.GetType("llcom_plus.Tools.WorkspaceSnapshot", true);
        var workspace = Read("{'Format':'llcom-plus.workspace','Version':1,'Name':'Flow','SavedAtUtc':'2026-09-11T00:00:00Z','Layout':{'SplitCount':1,'ActiveSlot':1,'Ports':[{'Slot':1,'PortName':'COM1','Role':'AT'}]},'SerialProfiles':{'COM1':{}},'QuickSendPages':[[" + Write(configured) + "]],'PageNames':['FTP'],'SelectedPage':0}", workspaceType);
        var workspaceClone = Call(assembly.GetType("llcom_plus.Tools.WorkspaceService", true), "Clone", null, workspace, true);
        Assert(Write(workspaceClone).Contains("CONNECTED") && Write(workspaceClone).Contains("responseRetries"), "Workspace clone lost reply settings.");
        var untrusted = Call(assembly.GetType("llcom_plus.Tools.WorkspaceService", true), "Clone", null, workspace, false);
        Assert(Write(untrusted).Contains("CONNECTED") && !Write(untrusted).Contains("converter"), "Untrusted workspace did not retain flow while removing script references.");
        passed.Add("Workspace cloning retains response flow independently of trusted script references.");

        var a = Item(1, "OK"); var b = Item(2, "CONNECTED"); var c = Item(3, "CHDIR OK");
        var skip = Item(4, ""); Set(skip, "SkipInWorkflow", true);
        var plan = Plan(a, skip, b, c, Read("{'text':''}", itemType));
        Assert(((ICollection)plan).Count == 3, "Plan failed to skip opted-out and empty commands.");
        Set(a, "Text", "changed after run");
        Assert((string)P(P(((IList)plan)[0], "Command"), "Text") == "AT1", "Run plan follows live edits.");
        Reject(() => Plan(Item(1, "OK"), Item(2, "")));
        var malformed = Item(1, "OK"); Set(malformed, "Hex", true); Set(malformed, "Text", "4 Z"); Reject(() => Plan(malformed));
        passed.Add("Whole-page validation happens before sending; order and run snapshots ignore later edits.");

        int sends = 0;
        var firstSend = new TaskCompletionSource<bool>();
        var pending = RunPlan(plan, (step, token) =>
        {
            sends++;
            var id = (int)P(P(step, "Command"), "Id");
            Rx(id == 1 ? "OK\r\n" : id == 2 ? "CONNECTED\r\n" : "CHDIR OK\r\n");
            return id == 1 ? firstSend.Task : Task.CompletedTask;
        });
        Assert(sends == 1 && !pending.IsCompleted, "Next command ran before actual write completed.");
        firstSend.SetResult(true);
        Assert((bool)P(Finish(pending), "Success") && sends == 3, "Response-driven FTP-style chain did not finish in order.");
        passed.Add("Three-stage reply-driven sequence waits for actual send and each matching response before next.");

        sends = 0;
        var failed = Finish(RunPlan(plan, (step, token) => { sends++; Rx("ERROR\r\n"); return Task.CompletedTask; }));
        Assert(!(bool)P(failed, "Success") && sends == 1, "Timeout continued to a dependent command or resent by default.");
        passed.Add("Unmatched response stops the chain; later commands are not sent and default retries are zero.");

        sends = 0;
        failed = Finish(RunPlan(plan, (step, token) => { sends++; Rx("OK"); return Task.CompletedTask; }));
        Assert(!(bool)P(failed, "Success") && (string)P(failed, "Outcome") == "Timeout" && sends == 1,
            "An unterminated OK line released a dependent command at the response deadline.");
        passed.Add("An unterminated exact-line response times out and never sends the dependent command.");

        var retry = Item(1, "OK"); Set(retry, "ResponseRetries", 1); sends = 0;
        var retryResult = Finish(RunPlan(Plan(retry), (step, token) => { sends++; if (sends == 2) Rx("OK\r\n"); return Task.CompletedTask; }));
        Assert((bool)P(retryResult, "Success") && sends == 2, "Explicit timeout retry count ignored.");
        passed.Add("Only explicitly configured timeout retries resend a command.");

        sends = 0;
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = RunPlan(plan, (step, token) => { sends++; cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
            try { cancelled.GetAwaiter().GetResult(); throw new InvalidOperationException("Cancelled workflow returned success."); }
            catch (OperationCanceledException) { }
            Assert(sends == 1, "Cancellation sent a dependent command.");
        }
        sends = 0;
        var writeFailure = RunPlan(Plan(retry), (step, token) => { sends++; throw new IOException("Synthetic write failure"); });
        try { writeFailure.GetAwaiter().GetResult(); throw new InvalidOperationException("Write failure was swallowed."); }
        catch (IOException) { }
        Assert(sends == 1, "Write failure was retried despite uncertain hardware delivery.");
        passed.Add("Stop cancellation prevents the next command; write errors never auto-retry.");
        return passed.ToArray();
    }
}
