using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.Serialization;

// No Global access, normal Uart/PortSlot constructor, serial Open, application
// startup, dispatcher pump, file write, or hardware access is used by this probe.
public static class SerialReceiveIsolationRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Instance);
        Assert(field != null, "Required field missing: " + name);
        field.SetValue(target, value);
    }

    private static object GetField(object target, string name)
    {
        return target.GetType().GetField(name, Instance).GetValue(target);
    }

    private static object Get(object target, string name)
    {
        return target.GetType().GetProperty(name, Instance).GetValue(target, null);
    }

    private static void Set(object target, string name, object value)
    {
        target.GetType().GetProperty(name, Instance).SetValue(target, value, null);
    }

    private static object Call(object target, string name, params object[] arguments)
    {
        try { return target.GetType().GetMethod(name, Instance).Invoke(target, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    public static string[] Run(Assembly assembly)
    {
        var passed = new List<string>();
        var uartType = assembly.GetType("llcom_plus.Model.Uart", true);
        var profileType = assembly.GetType("llcom_plus.Model.UartPortProfile", true);
        var leaseType = uartType.GetNestedType("ConnectionLease", BindingFlags.NonPublic);
        var eventType = assembly.GetType("llcom_plus.Model.UartReceiveEventArgs", true);
        var uart = FormatterServices.GetUninitializedObject(uartType);
        SetField(uart, "lifecycleLock", new object());
        SetField(uart, "connectionGeneration", 11L);
        var profile = Activator.CreateInstance(profileType, true);
        Set(profile, "encoding", 65001);
        Set(profile, "showHexFormat", 1);
        Set(profile, "recvScript", "captured-script");

        // Unopened SerialPort objects serve solely as distinct identities.
        using (var port = new SerialPort())
        using (var replacement = new SerialPort())
        {
            SetField(uart, "serial", port);
            var lease = Activator.CreateInstance(leaseType, Instance, null,
                new object[] { uart, port, 11L, "COM_TEST", profile }, null);
            var eventArgs = Activator.CreateInstance(eventType, Instance, null, new[] { lease }, null);
            Assert((bool)Get(lease, "IsCurrent") && (bool)Get(eventArgs, "IsCurrent"),
                "A matching unopened connection identity should remain current.");
            Assert(!(bool)Get(lease, "IsOpen"), "The fixture must never open a serial port.");
            passed.Add("Main UART lease distinguishes current identity from an actually open serial port.");

            SetField(uart, "connectionGeneration", 12L);
            Assert(!(bool)Get(lease, "IsCurrent") && !(bool)Get(eventArgs, "IsCurrent"),
                "Old-generation receive events survived a reconnect.");
            SetField(uart, "connectionGeneration", 11L);
            SetField(uart, "serial", replacement);
            Assert(!(bool)Get(lease, "IsCurrent") && !(bool)Get(eventArgs, "IsCurrent"),
                "Receive events survived replacement of their serial object.");
            SetField(uart, "serial", port);
            SetField(uart, "isShuttingDown", true);
            Assert(!(bool)Get(lease, "IsCurrent") && !(bool)Get(eventArgs, "IsCurrent"),
                "Receive events remained current after shutdown.");
            SetField(uart, "isShuttingDown", false);
            passed.Add("Main UART receive events reject changed generation, replaced port, and shutdown.");

            var capturedProfile = Get(lease, "Profile");
            Assert(!ReferenceEquals(capturedProfile, profile), "A lease retained the mutable live profile.");
            Set(profile, "encoding", 936);
            Set(profile, "showHexFormat", 2);
            Set(profile, "recvScript", "later-script");
            Assert((int)Get(capturedProfile, "encoding") == 65001 &&
                (int)Get(capturedProfile, "showHexFormat") == 1 &&
                (string)Get(capturedProfile, "recvScript") == "captured-script",
                "Later settings changes altered a captured receive profile.");
            Assert(ReferenceEquals(Get(eventArgs, "Profile"), capturedProfile),
                "Receive event metadata lost its packet's captured profile.");
            passed.Add("A receive lease independently captures decoding, formatting, and script settings.");
        }

        var pageType = assembly.GetType("llcom_plus.Pages.MultiPortPage", true);
        var slotType = pageType.GetNestedType("PortSlot", BindingFlags.NonPublic);
        var slot = FormatterServices.GetUninitializedObject(slotType);
        SetField(slot, "serialLock", new object());
        SetField(slot, "receiveBufferLock", new object());
        SetField(slot, "pendingReceiveData", new List<byte>());
        SetField(slot, "connectionGeneration", 11L);
        Set(profile, "maxLength", 1048576U); // Prevent immediate flush/UI access.
        Set(profile, "timeout", 500);
        Set(profile, "bitDelay", false);
        var input = new byte[] { 1, 2 };
        Call(slot, "QueueReceivedData", input, 11L, profile);
        input[0] = 99;
        var pending = (List<byte>)GetField(slot, "pendingReceiveData");
        var pendingProfile = GetField(slot, "pendingReceiveProfile");
        Assert(pending.Count == 2 && pending[0] == 1 && pending[1] == 2,
            "Split receive buffering retained the caller's mutable byte array.");
        Assert(!ReferenceEquals(pendingProfile, profile), "Split receive buffering retained a live profile.");
        Set(profile, "timeout", 999);
        Set(profile, "encoding", 65001);
        Set(profile, "recvScript", "newer-script");
        Assert((int)Get(pendingProfile, "timeout") == 500 &&
            (int)Get(pendingProfile, "encoding") == 936 &&
            (string)Get(pendingProfile, "recvScript") == "later-script",
            "A buffered split packet's profile changed after queueing.");
        Call(slot, "QueueReceivedData", new byte[] { 3 }, 11L, profile);
        Assert(pending.Count == 3 && ReferenceEquals(pendingProfile, GetField(slot, "pendingReceiveProfile")),
            "A single aggregate packet must retain the original decoding profile.");
        passed.Add("Split aggregation copies incoming bytes and preserves the first block's profile snapshot.");

        SetField(slot, "connectionGeneration", 12L);
        Call(slot, "QueueReceivedData", new byte[] { 4, 5 }, 12L, profile);
        Assert(pending.Count == 2 && pending[0] == 4 && pending[1] == 5 &&
            (long)GetField(slot, "pendingReceiveGeneration") == 12L,
            "Split buffering mixed bytes from separate connection generations.");
        Assert((int)Get(GetField(slot, "pendingReceiveProfile"), "timeout") == 999,
            "The next connection did not capture its own profile.");
        Call(slot, "QueueReceivedData", new byte[] { 6 }, 11L, profile);
        Assert(pending.Count == 2, "An old receive callback appended to the new connection's packet.");
        passed.Add("Split buffering separates generations and rejects late callbacks from the old connection.");

        SetField(slot, "serialTransition", true);
        Call(slot, "QueueReceivedData", new byte[] { 7 }, 12L, profile);
        Assert(pending.Count == 2, "Split buffering accepted input during connection transition.");
        SetField(slot, "serialTransition", false);
        SetField(slot, "serialDisposed", true);
        Call(slot, "QueueReceivedData", new byte[] { 8 }, 12L, profile);
        Assert(pending.Count == 2, "A disposed split pane accepted another receive callback.");
        SetField(slot, "serialDisposed", false);
        Call(slot, "QueueReceivedData", null, 12L, profile);
        Call(slot, "QueueReceivedData", new byte[0], 12L, profile);
        Assert(pending.Count == 2, "Empty input altered pending receive data.");
        passed.Add("Split queues reject transition, disposed-pane, and empty-input callbacks.");

        Call(slot, "ResetReceiveBuffer");
        Assert(pending.Count == 0 && GetField(slot, "pendingReceiveProfile") == null &&
            !(bool)GetField(slot, "receiveFlushScheduled"),
            "Resetting split receive buffers retained stale profile or flush state.");
        passed.Add("Resetting a split receive buffer clears payload, captured profile, and scheduled state.");
        return passed.ToArray();
    }
}
