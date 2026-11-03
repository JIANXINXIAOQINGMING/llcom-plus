using Llcom.SerialMonitoring;
using llcom_plus.Tools;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pass(string text) { Console.WriteLine("PASS " + text); }
    private static byte[] Packet(uint com, ulong drops = 0)
    {
        var data = new byte[MonitorBrokerProtocol.PacketSize];
        Action<int, byte[]> put = (offset, bytes) => Buffer.BlockCopy(bytes, 0, data, offset, bytes.Length);
        put(0, BitConverter.GetBytes(0x334D534CU)); put(4, BitConverter.GetBytes((ushort)3)); put(6, BitConverter.GetBytes((ushort)64));
        put(8, BitConverter.GetBytes(71UL)); put(16, BitConverter.GetBytes(1UL)); put(24, BitConverter.GetBytes(com));
        put(28, BitConverter.GetBytes(3U)); put(40, BitConverter.GetBytes(3U)); put(48, BitConverter.GetBytes(3U));
        put(52, BitConverter.GetBytes(3U)); put(56, BitConverter.GetBytes(drops));
        data[64] = 65; data[65] = 66; data[66] = 67;
        return data;
    }
    private static int Fake()
    {
        bool stopObserved = false;
        try
        {
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Hello,
            MonitorBrokerProtocol.Hello(Process.GetCurrentProcess().Id, Environment.Is64BitProcess ? 64 : 32));
        var start = MonitorBrokerProtocol.ReadInitial(input);
        if (start == null || start.Type == MonitorBrokerProtocol.Kind.Stop)
        { stopObserved = true; MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Stopped, BitConverter.GetBytes(2)); return 0; }
        uint com = BitConverter.ToUInt32(start.Payload, 4);
        if (com == 80) return 0;
        if (com == 83) { Console.Error.WriteLine("FAKE_MONITOR_STARTUP_FAILURE_83"); return 83; }
        if (com == 81) Thread.Sleep(300);
        MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Status, BitConverter.GetBytes(1));
        if (com == 78)
        {
            var header = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(MonitorBrokerProtocol.Magic), 0, header, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)1), 0, header, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)MonitorBrokerProtocol.Kind.Packet), 0, header, 6, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue), 0, header, 8, 4);
            output.Write(header, 0, header.Length); output.Flush();
        }
        else if (com == 79)
        {
            var bad = Packet(com); bad[0] = 0;
            MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Packet, bad);
        }
        else if (com == 77) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Packet, Packet(com));
        var stop = MonitorBrokerProtocol.Read(input);
        stopObserved = stop == null || stop.Type == MonitorBrokerProtocol.Kind.Stop;
        if (com == 82) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Packet, Packet(com));
        MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Stopped, BitConverter.GetBytes(2));
        return stop == null || stop.Type == MonitorBrokerProtocol.Kind.Stop ? 0 : 1;
        }
        catch (IOException error) when (stopObserved)
        {
            // Retirement can close stdout after the stop request was consumed.
            // Keep the diagnostic, but do not classify this expected shutdown
            // as an unexpected startup failure.
            Console.Error.WriteLine("Fake monitoring helper pipe closed after stop: " + error);
            return 0;
        }
    }
    private sealed class ShortStream : MemoryStream
    {
        internal ShortStream(byte[] data) : base(data) { }
        public override int Read(byte[] buffer, int offset, int count) { return base.Read(buffer, offset, Math.Min(count, 1)); }
    }
    private static void ProtocolChecks()
    {
        var memory = new MemoryStream();
        MonitorBrokerProtocol.Write(memory, MonitorBrokerProtocol.Kind.Packet, Packet(77, ulong.MaxValue));
        using (var partial = new ShortStream(memory.ToArray()))
            Assert(MonitorBrokerProtocol.IsPacketValid(MonitorBrokerProtocol.Read(partial).Payload, 77), "Fragmented reads lost a protocol frame.");
        Assert(!MonitorBrokerProtocol.IsLengthValid(MonitorBrokerProtocol.Kind.Packet, int.MaxValue), "Oversize frame accepted.");
        Assert(!MonitorBrokerProtocol.IsLengthValid((MonitorBrokerProtocol.Kind)99, 0), "Unknown frame accepted.");
        var bad = Packet(77); bad[52] = 8;
        Assert(!MonitorBrokerProtocol.IsPacketValid(bad, 77) && !MonitorBrokerProtocol.IsPacketValid(Packet(77), 78), "Packet bounds/COM checks were bypassed.");
        byte[] truncated = memory.ToArray(); Array.Resize(ref truncated, truncated.Length - 1);
        try { MonitorBrokerProtocol.Read(new MemoryStream(truncated)); throw new Exception("Truncated frame accepted."); }
        catch (EndOfStreamException) { }
        Pass("Framing tolerates partial reads and rejects oversized, unknown, truncated, wrong-COM and invalid-flags frames.");

        byte[] frame = memory.ToArray();
        byte[] preamble = new UTF8Encoding(true).GetPreamble();
        using (var initial = new ShortStream(frame))
            Assert(MonitorBrokerProtocol.IsPacketValid(MonitorBrokerProtocol.ReadInitial(initial).Payload, 77),
                "The initial frame requires an unnecessary preamble.");
        using (var initial = new ShortStream(JoinBytes(preamble, frame)))
            Assert(MonitorBrokerProtocol.IsPacketValid(MonitorBrokerProtocol.ReadInitial(initial).Payload, 77),
                "The initial frame rejected the .NET Framework stdin UTF-8 preamble.");
        foreach (var invalid in new[] {
            new byte[] { 0xEF }, new byte[] { 0xEF, 0xBB },
            JoinBytes(new byte[] { 0xEF, 0xBC, 0xBF }, frame),
            JoinBytes(new byte[] { 0xEF, 0xBB, 0x00 }, frame),
            JoinBytes(preamble, JoinBytes(preamble, frame)) })
        {
            bool rejected = false;
            try { MonitorBrokerProtocol.ReadInitial(new ShortStream(invalid)); }
            catch (InvalidDataException) { rejected = true; }
            catch (EndOfStreamException) { rejected = true; }
            Assert(rejected, "A malformed, truncated, or repeated initial preamble was accepted.");
        }
        using (var later = new ShortStream(JoinBytes(frame, JoinBytes(preamble, frame))))
        {
            Assert(MonitorBrokerProtocol.ReadInitial(later) != null, "The first frame was lost.");
            bool rejected = false;
            try { MonitorBrokerProtocol.Read(later); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "A preamble after the first frame was accepted by the strict reader.");
        }
        Pass("Initial frames support no BOM or one exact UTF-8 BOM; malformed, truncated, repeated and later preambles are rejected.");
    }
    private static byte[] JoinBytes(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
    private static async Task ClientChecks(string root, string oppositeExecutable)
    {
        string arch = Environment.Is64BitProcess ? "x64" : "x86";
        string helper = SerialMonitorBrokerClient.GetHelperPath(root, arch);
        CopyFakeHelper(Assembly.GetExecutingAssembly().Location, helper);
        string opposite = arch == "x64" ? "x86" : "x64";
        string oppositeHelper = SerialMonitorBrokerClient.GetHelperPath(root, opposite);
        CopyFakeHelper(oppositeExecutable, oppositeHelper);
        using (var cross = new SerialMonitorBrokerClient(root))
        using (var crossData = new ManualResetEvent(false))
        {
            Assert(await cross.StartAsync(424242, 77, 1, opposite, bytes => crossData.Set(), null, CancellationToken.None) == 1 &&
                crossData.WaitOne(3000), "The opposite-architecture helper could not deliver its fixed-width packet.");
            Assert(await cross.StopAsync() == 2, "The opposite-architecture helper did not stop.");
        }
        Pass("The real " + arch + " client starts the " + opposite + " fake helper and exchanges packet/stop frames across bitness.");
        using (var missing = new SerialMonitorBrokerClient(Path.Combine(root, "missing")))
        {
            try { await missing.StartAsync(424242, 77, 1, arch, null, null, CancellationToken.None); throw new Exception("Missing helper accepted."); }
            catch (FileNotFoundException) { }
            Assert(await missing.StopAsync() == 0 && !missing.IsRunning, "Empty stop started a child.");
        }
        Pass("Missing helper fails before process creation; empty stop/dispose remain immediate.");
        using (var client = new SerialMonitorBrokerClient(root))
        using (var delivered = new AutoResetEvent(false))
        {
            int packets = 0;
            Action<byte[]> receive = bytes => { Interlocked.Increment(ref packets); delivered.Set(); };
            Assert(await client.StartAsync(424242, 77, 1, arch, receive, null, CancellationToken.None) == 1, "Fake helper startup failed.");
            Assert(delivered.WaitOne(3000) && packets == 1 && client.IsRunning, "Current helper packet did not arrive.");
            Assert(await client.StopAsync() == 2 && !client.IsRunning, "Stop acknowledgment was lost.");
            Assert(await client.StartAsync(424242, 82, 1, arch, receive, null, CancellationToken.None) == 1, "Restart failed.");
            Assert(await client.StopAsync() == 2 && packets == 1, "A post-stop old helper packet reached the new session.");
            Assert(await client.StartAsync(424242, 77, 1, arch, receive, null, CancellationToken.None) == 1, "Second restart failed.");
            Assert(delivered.WaitOne(3000) && packets == 2, "New session did not receive its own data.");
            await client.StopAsync();
            Pass("Actual inherited stdout/stdin handshake, delivery, stop, restart and late-packet rejection work without native injection.");
        }
        foreach (uint com in new uint[] { 78, 79 })
            using (var client = new SerialMonitorBrokerClient(root))
            using (var failed = new ManualResetEvent(false))
            {
                int callbacks = 0;
                await client.StartAsync(424242, com, 1, arch, bytes => callbacks++, reason => failed.Set(), CancellationToken.None);
                Assert(failed.WaitOne(3000), "Malformed helper stream was not rejected.");
                await client.StopAsync();
                Assert(callbacks == 0 && !client.IsRunning, "Malformed data escaped validation.");
            }
        Pass("An oversized frame and malformed native packet end the session before any data callback.");
        using (var client = new SerialMonitorBrokerClient(root))
        {
            try { await client.StartAsync(424242, 80, 1, arch, null, null, CancellationToken.None); throw new Exception("Unexpected helper EOF accepted."); }
            catch (IOException) { }
            await client.StopAsync();
        }
        using (var client = new SerialMonitorBrokerClient(root))
        {
            try { await client.StartAsync(424242, 83, 1, arch, null, null, CancellationToken.None); throw new Exception("Unexpected helper startup failure accepted."); }
            catch (IOException error)
            {
                Assert(error.Message.Contains("FAKE_MONITOR_STARTUP_FAILURE_83") &&
                    error.Message.Contains("0x00000053"), "Helper startup failure lost its stderr diagnostic or hexadecimal exit code: " + error.Message);
            }
            await client.StopAsync();
        }
        using (var client = new SerialMonitorBrokerClient(root))
        using (var canceled = new CancellationTokenSource())
        {
            canceled.CancelAfter(50);
            try { await client.StartAsync(424242, 81, 1, arch, null, null, canceled.Token); throw new Exception("Canceled startup accepted."); }
            catch (OperationCanceledException) { }
            Assert(await client.StopAsync() == 2 && !client.IsRunning, "Canceled startup did not complete a graceful stop.");
        }
        Pass("Unexpected helper EOF preserves bounded stderr/exit diagnostics; cancellation during pending startup settles without target/process killing.");
    }
    private static void CopyFakeHelper(string executable, string helper)
    {
        string sourceConfig = executable + ".config";
        Assert(File.Exists(sourceConfig), "The fake monitoring helper runtime configuration is missing: " + sourceConfig);
        string configuration = File.ReadAllText(sourceConfig);
        Assert(configuration.Contains("version=\"v4.0\"") && configuration.Contains("sku=\".NETFramework,Version=v4.8\""),
            "The fake monitoring helper must explicitly select .NET Framework 4.8.");
        Directory.CreateDirectory(Path.GetDirectoryName(helper));
        File.Copy(executable, helper);
        File.Copy(sourceConfig, helper + ".config");
        Assert(File.ReadAllText(helper + ".config") == configuration, "Copied helper runtime configuration differs from its source.");
        File.Copy(executable, Path.Combine(Path.GetDirectoryName(helper), "serial_monitor.dll"));
    }
    private static void RelayChecks(string actualHelper)
    {
        var assembly = Assembly.LoadFrom(actualHelper);
        var relayType = assembly.GetType("Llcom.SerialMonitoring.Program+CaptureRelay", true);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        using (var stop = new ManualResetEvent(false))
        {
            var output = new MemoryStream(); var outputGate = new object();
            var relay = Activator.CreateInstance(relayType, flags, null, new object[] { output, outputGate, 77U, stop }, null);
            var receive = relayType.GetMethod("Receive", flags);
            var pointer = Marshal.AllocHGlobal(MonitorBrokerProtocol.PacketSize);
            try
            {
                Marshal.Copy(Packet(77, 2), 0, pointer, MonitorBrokerProtocol.PacketSize);
                for (int i = 0; i < 300; i++) receive.Invoke(relay, new object[] { pointer });
                long queued = (long)relayType.GetField("bytes", flags).GetValue(relay);
                Assert(queued <= 2L * 1024 * 1024 && queued > 0, "Helper retained an unbounded packet queue.");
                relayType.GetMethod("Started", flags).Invoke(relay, null);
                Assert(SpinWait.SpinUntil(() => { lock (outputGate) return output.Length >= 12 + MonitorBrokerProtocol.PacketSize; }, 3000), "Relay did not forward queued packets.");
                lock (outputGate)
                {
                    var first = MonitorBrokerProtocol.Read(new MemoryStream(output.ToArray()));
                    ulong expected = 600UL + (ulong)(300 - queued / MonitorBrokerProtocol.PacketSize);
                    Assert(BitConverter.ToUInt64(first.Payload, 56) == expected, "Dropped helper packets lost upstream native-drop deltas or counted them twice.");
                }
                stop.Set(); relayType.GetMethod("Finish", flags).Invoke(relay, null);
                Assert((int)receive.Invoke(relay, new object[] { new IntPtr(1) }) == 1, "Stopped callbacks still read native pointers.");
            }
            finally { Marshal.FreeHGlobal(pointer); stop.Set(); relayType.GetMethod("Finish", flags).Invoke(relay, null); }
        }
        Pass("The real helper callback queue stays within 2 MiB, never waits for stdout, and preserves both native and helper drop counts.");
    }
    private static Process OwnedTarget()
    {
        return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--owned-target")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
    }
    private static void FinishOwned(Process process)
    {
        try { process.StandardInput.Close(); } catch { }
        Assert(process.WaitForExit(3000), "An owned no-native probe process did not finish after EOF.");
        process.Dispose();
    }
    private static Process ActualHelper(string path, Process parent, long ticks)
    {
        return Process.Start(new ProcessStartInfo(path, parent.Id + " " + ticks)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
    }
    private static void IdentityAndParentChecks(string actualHelper)
    {
        using (var self = Process.GetCurrentProcess())
        {
            var helper = ActualHelper(actualHelper, self, self.StartTime.ToUniversalTime().Ticks + 1);
            try
            {
                var error = MonitorBrokerProtocol.Read(helper.StandardOutput.BaseStream);
                Assert(error.Type == MonitorBrokerProtocol.Kind.Error, "A stale parent identity was accepted.");
                Assert(helper.WaitForExit(3000), "Stale-parent rejection did not terminate before native monitoring.");
            }
            finally { helper.StandardInput.Close(); helper.Dispose(); }
            var target = OwnedTarget();
            try
            {
                helper = ActualHelper(actualHelper, self, self.StartTime.ToUniversalTime().Ticks);
                try
                {
                    Assert(MonitorBrokerProtocol.Read(helper.StandardOutput.BaseStream).Type == MonitorBrokerProtocol.Kind.Hello, "Real helper hello missing.");
                    MonitorBrokerProtocol.Write(helper.StandardInput.BaseStream, MonitorBrokerProtocol.Kind.Start,
                        MonitorBrokerProtocol.Start((uint)target.Id, 77, target.StartTime.ToUniversalTime().Ticks + 1));
                    Assert(MonitorBrokerProtocol.Read(helper.StandardOutput.BaseStream).Type == MonitorBrokerProtocol.Kind.Error,
                        "A stale target identity was accepted before loading native code.");
                    Assert(helper.WaitForExit(3000), "Stale-target rejection did not finish.");
                }
                finally { helper.StandardInput.Close(); helper.Dispose(); }
            }
            finally { FinishOwned(target); }
        }
        var parent = OwnedTarget();
        var child = ActualHelper(actualHelper, parent, parent.StartTime.ToUniversalTime().Ticks);
        try
        {
            Assert(MonitorBrokerProtocol.Read(child.StandardOutput.BaseStream).Type == MonitorBrokerProtocol.Kind.Hello, "Waiting helper hello missing.");
            FinishOwned(parent); parent = null;
            // Deliberately keep this root-owned stdin pipe open. The helper's
            // process-handle watcher must still notice its declared parent died.
            Assert(child.WaitForExit(3000), "Parent exit was hidden by an inherited stdin writer still open elsewhere.");
        }
        finally
        {
            child.StandardInput.Close(); child.Dispose();
            if (parent != null) FinishOwned(parent);
        }
        Pass("The actual helper rejects reused parent/target identities and exits on parent death even with stdin held open; native monitoring was never entered.");
    }
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--owned-target")
        {
            // A redirected StreamWriter may already have emitted its UTF-8
            // preamble. Stay alive until EOF, rather than the first byte.
            Console.OpenStandardInput().CopyTo(Stream.Null);
            return 0;
        }
        if (args.Length == 2 && int.TryParse(args[0], out _))
        {
            try { return Fake(); }
            catch (Exception error) { Console.Error.WriteLine("Fake monitoring helper failed: " + error); return 1; }
        }
        if (args.Length != 3) { Console.Error.WriteLine("Expected isolated test root, actual helper, and opposite-architecture test executable."); return 1; }
        Encoding previousInputEncoding = Console.InputEncoding;
        try
        {
            // Process.StandardInput uses Console.InputEncoding on .NET Framework.
            // UTF-8 with a preamble reproduces CI independently of the local code page.
            Console.InputEncoding = new UTF8Encoding(true);
            Assert(BitConverter.ToString(Console.InputEncoding.GetPreamble()) == "EF-BB-BF", "The CI stdin preamble scenario was not enabled.");
            Pass("CI stdin encoding simulation is active: UTF-8 preamble EF-BB-BF.");
            ProtocolChecks(); ClientChecks(args[0], args[2]).GetAwaiter().GetResult(); RelayChecks(args[1]); IdentityAndParentChecks(args[1]); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Console.InputEncoding = previousInputEncoding; }
    }
}
