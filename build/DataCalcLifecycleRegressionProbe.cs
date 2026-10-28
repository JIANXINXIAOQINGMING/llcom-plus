using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Controls;

// Uses isolated controls and the real lifecycle methods, without constructors,
// calculation tasks, dispatcher pumping, files, serial ports or network targets.
public static class DataCalcLifecycleRegressionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Set(object target, string name, object value)
    {
        target.GetType().GetField(name, All).SetValue(target, value);
    }
    private static object Get(object target, string name)
    {
        return target.GetType().GetField(name, All).GetValue(target);
    }
    private static void Call(object target, string name, params object[] args)
    {
        try { target.GetType().GetMethod(name, All).Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    public static void Run(Assembly assembly)
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Use an isolated STA test host.");
        var view = FormatterServices.GetUninitializedObject(assembly.GetType("llcom_plus.Pages.DataCalcFileSendView", true));
        var calculate = new Button { IsEnabled = false };
        Set(view, "CalculateDataButton", calculate);
        using (var cancelled = new CancellationTokenSource())
        {
            Set(view, "calculationCts", cancelled);
            Set(view, "activeCalculationRunId", 11L);
            Call(view, "ManualDataTextBox_TextChanged", null, null);
            Assert(cancelled.IsCancellationRequested && Get(view, "calculationCts") == null &&
                (long)Get(view, "activeCalculationRunId") == 0 && calculate.IsEnabled,
                "Editing input left the Calculate button disabled after detaching its old calculation.");
        }
        using (var send = new CancellationTokenSource())
        using (var cancelled = new CancellationTokenSource())
        {
            Set(view, "sendCts", send);
            Set(view, "calculationCts", cancelled);
            calculate.IsEnabled = false;
            Call(view, "CancelCalculation");
            Assert(cancelled.IsCancellationRequested && !calculate.IsEnabled,
                "Cancelling calculation enabled the Calculate button while a send still owns the view.");
            Set(view, "sendCts", null);
        }
        Set(view, "isUnloaded", true);
        calculate.IsEnabled = false;
        Call(view, "CancelCalculation");
        Assert(!calculate.IsEnabled, "Calculation cancellation altered an unloaded view.");

        var page = FormatterServices.GetUninitializedObject(assembly.GetType("llcom_plus.Pages.DataCalcPage", true));
        var select = new Button();
        var source = new ComboBox();
        var hex = new CheckBox();
        var input = new TextBox();
        var sendButton = new Button();
        Set(page, "SelectFileButton", select);
        Set(page, "SourceComboBox", source);
        Set(page, "HexInputCheckBox", hex);
        Set(page, "ManualDataTextBox", input);
        Set(page, "activeSendButton", sendButton);
        using (var send = new CancellationTokenSource())
        {
            Set(page, "sendCts", send);
            Call(page, "SetSendingState", true);
            Assert(!select.IsEnabled && !source.IsEnabled && !hex.IsEnabled && input.IsReadOnly,
                "Fixture did not enter the real file-send disabled-control state.");
            Call(page, "DataCalcPage_Unloaded", null, null);
            Assert(send.IsCancellationRequested && Get(page, "sendCts") == null,
                "Leaving the calculation page did not detach and cancel its send.");
            Call(page, "DataCalcPage_Loaded", null, null);
            Assert(select.IsEnabled && source.IsEnabled && hex.IsEnabled && !input.IsReadOnly,
                "Returning to the cached calculation page left its source controls disabled.");
        }
        using (var send = new CancellationTokenSource())
        {
            Set(page, "sendCts", send);
            Call(page, "DataCalcPage_Loaded", null, null);
            Assert(!select.IsEnabled && !source.IsEnabled && !hex.IsEnabled && input.IsReadOnly,
                "A loaded page re-enabled inputs while a current send still owned them.");
            Set(page, "sendCts", null);
        }
    }
}
