using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Llcom.SerialMonitoring
{
    internal static class Program
    {
        // Keep the original target process object referenced until the helper's
        // process lifetime ends, including its final background cleanup threads.
        private static ProcessIdentity targetLifetime;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int MonitorCallback(IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int StartMonitor(uint pid, uint com, MonitorCallback callback);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int StopMonitor();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ReadNativeValue();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static void Signal(ManualResetEvent signal) { try { signal.Set(); } catch (ObjectDisposedException) { } }

        private sealed class CaptureRelay
        {
            private readonly object queueGate = new object(), outputGate;
            private readonly Queue<byte[]> queue = new Queue<byte[]>();
            private readonly AutoResetEvent available = new AutoResetEvent(false);
            private readonly ManualResetEvent allowOutput = new ManualResetEvent(false), stop;
            private readonly Stream output;
            private readonly uint com;
            private readonly Thread writer;
            private long bytes;
            private bool accepting = true;
            private ulong pendingDrops;
            internal int OwnerGone;
            internal CaptureRelay(Stream output, object outputGate, uint com, ManualResetEvent stop)
            {
                this.output = output; this.outputGate = outputGate; this.com = com; this.stop = stop;
                writer = new Thread(WritePackets) { IsBackground = true, Name = "Serial monitor helper output" };
                writer.Start();
            }
            internal int Receive(IntPtr pointer)
            {
                try
                {
                    if (pointer == IntPtr.Zero || stop.WaitOne(0)) return 1;
                    var packet = new byte[MonitorBrokerProtocol.PacketSize];
                    Marshal.Copy(pointer, packet, 0, packet.Length);
                    if (!MonitorBrokerProtocol.IsPacketValid(packet, com)) return 0;
                    ulong nativeDrops = BitConverter.ToUInt64(packet, 56);
                    Array.Clear(packet, 56, 8);
                    lock (queueGate)
                    {
                        if (!accepting || stop.WaitOne(0)) return 1;
                        pendingDrops = MonitorBrokerProtocol.SaturatingAdd(pendingDrops, nativeDrops);
                        if (queue.Count >= 512 || bytes > 2L * 1024 * 1024 - packet.Length)
                        {
                            pendingDrops = MonitorBrokerProtocol.SaturatingAdd(pendingDrops, 1);
                            return 1;
                        }
                        queue.Enqueue(packet); bytes += packet.Length;
                    }
                    available.Set();
                    return 1;
                }
                catch { return 0; } // Never allow a managed exception across the native ABI.
            }
            internal void Started() { allowOutput.Set(); }
            internal void Finish()
            {
                lock (queueGate) { accepting = false; queue.Clear(); bytes = 0; }
                allowOutput.Set(); available.Set();
            }
            private void WritePackets()
            {
                try { WritePacketsCore(); }
                catch (ObjectDisposedException) { }
            }
            private void WritePacketsCore()
            {
                allowOutput.WaitOne();
                while (!stop.WaitOne(0))
                {
                    byte[] packet = null;
                    lock (queueGate)
                    {
                        if (!accepting) return;
                        if (queue.Count > 0)
                        {
                            packet = queue.Dequeue(); bytes -= packet.Length;
                            Buffer.BlockCopy(BitConverter.GetBytes(pendingDrops), 0, packet, 56, 8);
                            pendingDrops = 0;
                        }
                    }
                    if (packet == null) { available.WaitOne(100); continue; }
                    try { lock (outputGate) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Packet, packet); }
                    catch { Interlocked.Exchange(ref OwnerGone, 1); Signal(stop); return; }
                }
            }
        }
        private static T Export<T>(IntPtr module, string name) where T : class
        {
            var pointer = GetProcAddress(module, name);
            if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException("The monitor runtime is missing " + name + ".");
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }
        private static int Main(string[] args)
        {
            Stream input = Console.OpenStandardInput(), output = Console.OpenStandardOutput();
            var outputGate = new object();
            StartMonitor monitor = null;
            StopMonitor unmonitor = null;
            ReadNativeValue cleanupCount = null;
            bool nativeCalled = false;
            int parentGone = 0;
            ProcessIdentity target = null, parent = null;
            CaptureRelay relay = null;
            MonitorCallback callback = null;
            using (var stop = new ManualResetEvent(false))
            {
                try
                {
                    uint parentPid;
                    long parentStarted;
                    if (args.Length != 2 || !uint.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) ||
                        !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentStarted) ||
                        parentPid == (uint)Process.GetCurrentProcess().Id) throw new ArgumentException("Invalid monitor helper parent identity.");
                    parent = new ProcessIdentity(parentPid, parentStarted);
                    var startupParent = parent;
                    new Thread(() =>
                    {
                        try
                        {
                            while (!stop.WaitOne(100))
                                if (startupParent.HasExited)
                                {
                                    Interlocked.Exchange(ref parentGone, 1);
                                    Signal(stop);
                                    return;
                                }
                        }
                        catch (ObjectDisposedException) { }
                    }) { IsBackground = true, Name = "Serial monitor helper parent lifetime" }.Start();
                    lock (outputGate) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Hello,
                        MonitorBrokerProtocol.Hello(Process.GetCurrentProcess().Id, Environment.Is64BitProcess ? 64 : 32));
                    // The initial stdin read also belongs to a background worker.
                    // A leaked inherited pipe handle must not prevent parent-exit
                    // cleanup before native monitoring has even started.
                    var firstCommand = Task.Run(() => MonitorBrokerProtocol.ReadInitial(input));
                    while (!firstCommand.IsCompleted && !stop.WaitOne(50)) { }
                    if (stop.WaitOne(0)) return 0;
                    var command = firstCommand.GetAwaiter().GetResult();
                    if (command == null || command.Type == MonitorBrokerProtocol.Kind.Stop) return 0;
                    if (command.Type != MonitorBrokerProtocol.Kind.Start) throw new InvalidDataException("The helper expected one start command.");
                    uint pid = BitConverter.ToUInt32(command.Payload, 0), com = BitConverter.ToUInt32(command.Payload, 4);
                    long ticks = BitConverter.ToInt64(command.Payload, 8);
                    if (pid == 0 || pid == parentPid || pid == (uint)Process.GetCurrentProcess().Id || com == 0 || com > 4096)
                        throw new ArgumentException("Invalid monitoring target or COM selection.");
                    target = new ProcessIdentity(pid, ticks);
                    targetLifetime = target;
                    string architecture = Environment.Is64BitProcess ? "x64" : "x86";
                    if (target.Architecture != architecture) throw new InvalidOperationException("The target and monitor helper architecture differ.");
                    var nativePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serial_monitor.dll");
                    if ((File.GetAttributes(nativePath) & FileAttributes.ReparsePoint) != 0 ||
                        MonitorBrokerProtocol.PeMachine(nativePath) != (Environment.Is64BitProcess ? 0x8664 : 0x14C))
                        throw new InvalidDataException("The helper runtime architecture or file path is invalid.");
                    // Absolute path plus a restricted dependency search; cwd is never searched.
                    var module = LoadLibraryEx(nativePath, IntPtr.Zero, 0x00000100 | 0x00001000);
                    if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the packaged serial monitor runtime.");
                    if (Export<ReadNativeValue>(module, "SerialMonitorGetAbiVersion")() != MonitorBrokerProtocol.NativeAbi)
                        throw new InvalidDataException("The monitor runtime ABI is incompatible.");
                    monitor = Export<StartMonitor>(module, "MonitorComm");
                    unmonitor = Export<StopMonitor>(module, "UnMonitorComm");
                    cleanupCount = Export<ReadNativeValue>(module, "SerialMonitorGetPendingCleanupCount");
                    relay = new CaptureRelay(output, outputGate, com, stop);
                    callback = relay.Receive;
                    var capturedParent = parent;
                    var capturedTarget = target;
                    var capturedRelay = relay;
                    new Thread(() =>
                    {
                        try
                        {
                            while (!stop.WaitOne(100))
                                if (capturedParent.HasExited || capturedTarget.HasExited)
                                { Interlocked.Exchange(ref capturedRelay.OwnerGone, capturedParent.HasExited ? 1 : 0); Signal(stop); }
                        }
                        catch (ObjectDisposedException) { }
                    }) { IsBackground = true, Name = "Serial monitor helper process lifetime" }.Start();
                    new Thread(() =>
                    {
                        try
                        {
                            var next = MonitorBrokerProtocol.Read(input);
                            if (next == null) Interlocked.Exchange(ref capturedRelay.OwnerGone, 1);
                            else if (next.Type != MonitorBrokerProtocol.Kind.Stop) throw new InvalidDataException("Unexpected helper command.");
                        }
                        catch { Interlocked.Exchange(ref capturedRelay.OwnerGone, 1); }
                        finally { Signal(stop); }
                    }) { IsBackground = true, Name = "Serial monitor helper stop request" }.Start();
                    if (parent.HasExited || target.HasExited || stop.WaitOne(0)) return 0;
                    nativeCalled = true;
                    int status = monitor(pid, com, callback);
                    if (Volatile.Read(ref parentGone) == 0 && Volatile.Read(ref relay.OwnerGone) == 0)
                        lock (outputGate) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Status, BitConverter.GetBytes(status));
                    if (status == 1) { relay.Started(); stop.WaitOne(); }
                    return status == 1 ? 0 : 2;
                }
                catch (Exception error)
                {
                    try { lock (outputGate) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Error, MonitorBrokerProtocol.Error(error.GetBaseException().Message)); }
                    catch { }
                    return 1;
                }
                finally
                {
                    stop.Set();
                    relay?.Finish();
                    int stopped = 0;
                    if (nativeCalled && unmonitor != null)
                    {
                        try { stopped = unmonitor(); } catch { stopped = 4; }
                    }
                    // The helper's native background reapers own late LoadLibrary/
                    // initialization work. Keep the helper alive until they finish
                    // or the original target exits; never kill or unload the target.
                    if (Volatile.Read(ref parentGone) == 0 && (relay == null || Volatile.Read(ref relay.OwnerGone) == 0))
                        try { lock (outputGate) MonitorBrokerProtocol.Write(output, MonitorBrokerProtocol.Kind.Stopped, BitConverter.GetBytes(stopped)); } catch { }
                    if (nativeCalled && cleanupCount != null && target != null)
                    {
                        try { while (!target.HasExited && cleanupCount() != 0) Thread.Sleep(100); }
                        catch { while (!target.HasExited) Thread.Sleep(500); }
                    }
                    GC.KeepAlive(callback);
                    GC.KeepAlive(targetLifetime);
                    parent?.Dispose();
                }
            }
        }
    }
}
