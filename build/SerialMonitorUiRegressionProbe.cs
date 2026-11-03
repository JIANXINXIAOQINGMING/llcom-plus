using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

// Exercises only metadata reads, in-memory frames and never-started brokers.
// No MonitorComm entry point, process injection, serial port or network is used.
public static class SerialMonitorUiRegressionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static object Field(object target, string name) { return target.GetType().GetField(name, All).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, All).SetValue(target, value); }
    private static object Call(Type type, object target, string name, params object[] args)
    {
        try { return type.GetMethod(name, All).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static byte[] Frame(ulong protocolGeneration = 44)
    {
        var data = new byte[8256];
        using (var stream = new MemoryStream(data))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0x334D534Cu); writer.Write((ushort)3); writer.Write((ushort)64);
            writer.Write(protocolGeneration); writer.Write(7UL); writer.Write(3u); writer.Write(3u);
            writer.Write(0x123456789UL); writer.Write(3u); writer.Write(0u); writer.Write(3u);
            writer.Write(3u); writer.Write(0UL); writer.Write(new byte[] { 1, 2, 3 });
        }
        return data;
    }
    private static int QueueCount(object page) { return ((ICollection)Field(page, "callbackQueue")).Count; }
    public static string[] Run(Assembly assembly, string outputDirectory)
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Run the UI probe in STA.");
        var passed = new List<string>();
        var pageType = assembly.GetType("llcom_plus.Pages.SerialMonitorPage", true);
        var architectureType = pageType.GetNestedType("ProcessArchitecture", BindingFlags.NonPublic);
        var x86 = Enum.Parse(architectureType, "X86");
        var x64 = Enum.Parse(architectureType, "X64");
        var unknown = Enum.Parse(architectureType, "Unknown");
        var arm64 = Enum.Parse(architectureType, "Arm64");
        Assert((bool)Call(pageType, null, "IsTargetArchitectureSupported", x86, false) &&
            (bool)Call(pageType, null, "IsTargetArchitectureSupported", x86, true) &&
            (bool)Call(pageType, null, "IsTargetArchitectureSupported", x64, true) &&
            !(bool)Call(pageType, null, "IsTargetArchitectureSupported", x64, false) &&
            !(bool)Call(pageType, null, "IsTargetArchitectureSupported", arm64, true) &&
            !(bool)Call(pageType, null, "IsTargetArchitectureSupported", unknown, true),
            "Architecture policy must choose x86/x64 by target while rejecting unsupported system/unknown modes.");
        Assert((string)Call(pageType, null, "ArchitectureText", x86) == "x86", "x86 is still mislabeled disabled.");
        passed.Add("Architecture selection accepts x86 and x64 targets independently of UI bitness, while ARM64/unknown and x64-on-32-bit remain rejected.");

        var processChoice = Activator.CreateInstance(pageType.GetNestedType("ProcessChoice", BindingFlags.NonPublic), true);
        processChoice.GetType().GetProperty("StartTimeUtcTicks").SetValue(processChoice, 123L, null);
        processChoice.GetType().GetProperty("Architecture").SetValue(processChoice, x86, null);
        Assert((bool)Call(pageType, null, "ProcessSnapshotMatches", processChoice, x86, 123L) &&
            !(bool)Call(pageType, null, "ProcessSnapshotMatches", processChoice, x86, 124L) &&
            !(bool)Call(pageType, null, "ProcessSnapshotMatches", processChoice, x64, 123L),
            "A reused PID or changed architecture must not match the captured target.");
        using (var self = Process.GetCurrentProcess())
        {
            object[] identity = { (uint)self.Id, null, 0L };
            Assert((bool)Call(pageType, null, "TryReadProcessIdentity", identity), "Could not inspect the probe's own process using limited query access.");
            Assert(identity[1].Equals(Environment.Is64BitProcess ? x64 : x86) &&
                (long)identity[2] == self.StartTime.ToUniversalTime().Ticks,
                "Process identity read lost architecture or creation-time precision.");
            processChoice.GetType().GetProperty("Pid").SetValue(processChoice, (uint)self.Id, null);
            var refusedSelf = false;
            try { Call(pageType, null, "ValidateTargetSnapshot", processChoice); }
            catch (InvalidOperationException) { refusedSelf = true; }
            Assert(refusedSelf, "The UI must not attempt to monitor its own process.");
        }
        passed.Add("Captured creation time and architecture reject PID replacement; limited-access metadata matches the probe process and self-monitoring is refused.");

        var page = Activator.CreateInstance(pageType);
        var generation = (int)Call(pageType, page, "ResetCallbackQueueForStart", 3u);
        var valid = Frame();
        Call(pageType, page, "BrokerPacketReceived", valid, generation);
        Assert(QueueCount(page) == 1 && (long)Field(page, "queuedPayloadBytes") == 3,
            "A valid broker frame did not enter the bounded callback queue.");
        valid[64] = 99;
        var peek = Field(page, "callbackQueue").GetType().GetMethod("Peek").Invoke(Field(page, "callbackQueue"), null);
        Assert(((byte[])peek.GetType().GetProperty("Data").GetValue(peek, null))[0] == 1,
            "Queued UI data aliases the helper input frame.");
        Call(pageType, page, "BrokerPacketReceived", new byte[8255], generation);
        var malformed = Frame(); malformed[0] = 0;
        Call(pageType, page, "BrokerPacketReceived", malformed, generation);
        malformed = Frame(); Array.Copy(BitConverter.GetBytes(uint.MaxValue), 0, malformed, 48, 4);
        Call(pageType, page, "BrokerPacketReceived", malformed, generation);
        malformed = Frame(); Array.Copy(BitConverter.GetBytes(4u), 0, malformed, 24, 4);
        Call(pageType, page, "BrokerPacketReceived", malformed, generation);
        Assert(QueueCount(page) == 1, "Malformed, oversized or other-COM frames reached the view queue.");
        passed.Add("The broker boundary validates complete ABI frames and owns payload copies; malformed lengths, headers and COM identities are rejected.");

        Set(page, "queuedPayloadBytes", 2L * 1024 * 1024);
        Call(pageType, page, "BrokerPacketReceived", Frame(), generation);
        Assert(QueueCount(page) == 1 && (long)Field(page, "droppedPacketCount") == 1,
            "A broker frame bypassed the shared UI byte budget.");
        var nextGeneration = (int)Call(pageType, page, "ResetCallbackQueueForStart", 3u);
        Call(pageType, page, "BrokerPacketReceived", Frame(99), generation);
        Assert(QueueCount(page) == 0 && (ulong)Field(page, "nativeProtocolGeneration") == 0,
            "An old helper callback established protocol identity in the new session.");
        Call(pageType, page, "BrokerPacketReceived", Frame(100), nextGeneration);
        Call(pageType, page, "DrainCallbackQueue", generation);
        Assert(QueueCount(page) == 1 && (ulong)Field(page, "nativeProtocolGeneration") == 100,
            "An old queued dispatcher drain consumed new-session records.");
        passed.Add("The UI byte limit and callback generations survive broker restart; old frames and already-queued drains cannot enter a replacement session.");

        var brokerType = assembly.GetType("llcom_plus.Tools.SerialMonitorBrokerClient", true);
        var client = Activator.CreateInstance(brokerType, All, null, new object[] { outputDirectory }, null);
        var oldClient = Activator.CreateInstance(brokerType, All, null, new object[] { outputDirectory }, null);
        using (var source = new CancellationTokenSource())
        {
            try
            {
                Set(page, "broker", client);
                Set(page, "monitorCancellation", source);
                Set(page, "starting", true);
                var pendingStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(page, "startRequestSettled", pendingStart.Task);
                Call(pageType, page, "BrokerExited", oldClient, nextGeneration, "old exit");
                Assert(ReferenceEquals(Field(page, "broker"), client) && (bool)Field(page, "starting"),
                    "An old helper exit canceled the replacement startup.");
                var stop = (Task)Call(pageType, page, "StopMonitoringAsync", new object[] { null });
                Assert(!stop.IsCompleted && source.Token.IsCancellationRequested &&
                    ReferenceEquals(stop, Call(pageType, page, "StopMonitoringAsync", new object[] { null })),
                    "Stop must cancel immediately, share one cleanup task, and retain the token source until startup settles.");
                pendingStart.SetResult(true);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!stop.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                    Thread.Sleep(10);
                }
                Assert(stop.IsCompleted && !stop.IsFaulted && source.IsCancellationRequested &&
                    Field(page, "broker") == null && !(bool)Field(page, "starting") &&
                    !(bool)Field(page, "connected") && QueueCount(page) == 0 && (int)Field(page, "callbacksEnabled") == 0,
                    "Stopping a pending UI startup did not cancel and retire all state.");
            }
            finally { ((IDisposable)client).Dispose(); ((IDisposable)oldClient).Dispose(); }
        }
        Call(pageType, page, "StopAndClearCallbackQueue");
        Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
        passed.Add("Old broker exits cannot cancel replacement startup; pending startup cancellation retires the queue and retains its token source until its request settles, without launching a helper.");
        return passed.ToArray();
    }
}
