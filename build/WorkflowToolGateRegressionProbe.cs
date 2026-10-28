using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

// No Application/Page/Window constructor, file, driver or network is used.
public static class WorkflowToolGateRegressionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static object Call(object target, string name, params object[] arguments)
    {
        try { return target.GetType().GetMethod(name, All).Invoke(target, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Set(object target, string name, object value)
    {
        target.GetType().GetField(name, All).SetValue(target, value);
    }
    private static object Get(object target, string name)
    {
        return target.GetType().GetProperty(name, All).GetValue(target, null);
    }
    private static void Rejected(Action action)
    {
        try { action(); throw new Exception("A conflicting raw tool operation was accepted."); }
        catch (InvalidOperationException) { }
    }
    public static string[] Run(Assembly assembly)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Use STA Windows PowerShell.");
        var passed = new List<string>();
        var type = assembly.GetType("llcom_plus.MainWindow", true);
        var window = FormatterServices.GetUninitializedObject(type);
        typeof(DispatcherObject).GetField("_dispatcher", All).SetValue(window, Dispatcher.CurrentDispatcher);
        var targetType = assembly.GetType("llcom_plus.Tools.ActiveSerialTarget", true);
        int writes = 0, committed = 0;
        var original = Activator.CreateInstance(targetType, All, null,
            new object[] { "uart:WORKFLOW_TEST:1", "WORKFLOW_TEST", new Func<bool>(() => true),
                new Func<byte[], CancellationToken, Action<int>, bool>((data, token, commit) =>
                { writes++; if (commit != null) commit(data.Length); return true; }), false }, null);
        var captured = Call(window, "GuardToolSerialTarget", original);
        Check((string)Get(captured, "Identity") == "uart:WORKFLOW_TEST:1" &&
            (bool)Get(captured, "IsOpen") && !(bool)Get(captured, "SupportsResumableCommits"),
            "The tool guard changed connection identity, open state or resume policy.");
        passed.Add("Guarded tool leases preserve connection identity, open state and resume policy.");

        var bytes = new byte[] { 65, 84 };
        Action<int> record = count => committed += count;
        using (var workflow = new CancellationTokenSource())
        {
            Set(window, "quickWorkflowCts", workflow);
            Rejected(() => Call(captured, "Send", bytes, CancellationToken.None, record));
            Set(window, "quickWorkflowDispatching", true);
            Rejected(() => Call(captured, "Send", bytes, CancellationToken.None, record));
            Check(writes == 0 && committed == 0 && (bool)Get(captured, "IsOpen"),
                "A raw tool write bypassed quick workflow protection or changed connection availability.");
            Rejected(() => Call(window, "EnsureActiveSerialTargetOpenForTools"));
            Set(window, "quickWorkflowDispatching", false);
            Set(window, "quickWorkflowCts", null);
        }
        passed.Add("A previously captured tool lease cannot write during quick execution, including its internal dispatch bypass.");
        passed.Add("File tools reject automatic port-opening before touching a driver while quick execution is active.");

        Check((bool)Call(captured, "Send", bytes, CancellationToken.None, record) && writes == 1 && committed == 2,
            "An unblocked captured tool no longer reports its real send and byte count.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool stopped = false;
            try { Call(captured, "Send", bytes, cancelled.Token, record); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && writes == 1, "A cancelled tool lease reached the transport.");
        }
        passed.Add("After quick execution ends, the same tool lease preserves delivery, commits and cancellation.");

        var moduleType = type.GetNestedType("ToolModule", BindingFlags.NonPublic);
        var modules = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(moduleType));
        Set(window, "toolModules", modules);
        foreach (var pageName in new[] { "DataCalcFileSendView", "DataCalcPage" })
        {
            var pageType = assembly.GetType("llcom_plus.Pages." + pageName, true);
            var page = FormatterServices.GetUninitializedObject(pageType);
            var module = Activator.CreateInstance(moduleType, All, null,
                new object[] { "DataCalc", "DataCalc", new Func<FrameworkElement>(() => null) }, null);
            Set(module, "content", page);
            modules.Add(module);
            Check(!(bool)Call(window, "OtherSerialWorkflowRunning"), "An idle file tool blocks quick execution.");
            using (var send = new CancellationTokenSource())
            {
                Set(page, "sendCts", send);
                Check((bool)Call(window, "OtherSerialWorkflowRunning"), "An active file tool was omitted from quick startup checks.");
                Set(page, "sendCts", null);
            }
            Check(!(bool)Call(window, "OtherSerialWorkflowRunning"), "A completed file tool remains busy.");
            modules.Clear();
        }
        passed.Add("Both file-send views block quick startup only while their sending operation owns the page.");
        return passed.ToArray();
    }
}
