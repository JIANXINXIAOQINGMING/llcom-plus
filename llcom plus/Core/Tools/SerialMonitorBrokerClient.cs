using Llcom.SerialMonitoring;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    internal sealed class SerialMonitorBrokerClient : IDisposable
    {
        private readonly object gate = new object();
        private readonly string appDirectory;
        private Session current;
        private bool disposed;
        private sealed class Session
        {
            internal readonly Process Process;
            internal readonly object InputGate = new object();
            internal readonly TaskCompletionSource<int> Ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<int> Stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly StringBuilder Diagnostic = new StringBuilder();
            internal readonly uint Com;
            internal readonly int Bits;
            internal readonly Action<byte[]> Packet;
            internal readonly Action<string> Exited;
            internal int StopRequested, Running, HelloSeen, StartSent, StreamsClosed;
            internal Task<int> StopTask;
            internal Session(Process process, uint com, int bits, Action<byte[]> packet, Action<string> exited)
            { Process = process; Com = com; Bits = bits; Packet = packet; Exited = exited; }
        }
        internal SerialMonitorBrokerClient(string appDirectory)
        {
            this.appDirectory = Path.GetFullPath(appDirectory ?? throw new ArgumentNullException(nameof(appDirectory)));
        }
        internal bool IsRunning { get { lock (gate) return current != null && Volatile.Read(ref current.Running) != 0 && Volatile.Read(ref current.StopRequested) == 0; } }
        internal static string GetHelperPath(string appDirectory, string architecture)
        {
            ValidateArchitecture(architecture);
            return Path.Combine(Path.GetFullPath(appDirectory), "Monitor", architecture, "SerialMonitorBroker.exe");
        }
        internal static bool IsHelperAvailable(string appDirectory, string architecture)
        {
            try { ValidateHelper(GetHelperPath(appDirectory, architecture), architecture); return true; }
            catch { return false; }
        }
        private static void ValidateArchitecture(string architecture)
        {
            if (architecture != "x86" && architecture != "x64") throw new ArgumentException("Only x86/x64 monitoring helpers are supported.", nameof(architecture));
        }
        private static void ValidateHelper(string path, string architecture)
        {
            string directory = Path.GetDirectoryName(path), monitor = Path.GetDirectoryName(directory);
            foreach (var entry in new[] { monitor, directory, path, Path.Combine(directory, "serial_monitor.dll") })
            {
                if (!File.Exists(entry) && !Directory.Exists(entry)) throw new FileNotFoundException("The matching-architecture monitoring helper is missing. Reinstall the complete release package.", entry);
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The monitoring helper must use regular packaged files.");
            }
            ushort machine = architecture == "x64" ? (ushort)0x8664 : (ushort)0x14C;
            if (MonitorBrokerProtocol.PeMachine(path) != machine || MonitorBrokerProtocol.PeMachine(Path.Combine(directory, "serial_monitor.dll")) != machine)
                throw new InvalidDataException("The packaged monitoring helper or runtime has the wrong architecture.");
        }
        internal async Task<int> StartAsync(uint pid, uint com, long targetStartUtcTicks, string architecture,
            Action<byte[]> packet, Action<string> exited, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id || com == 0 || com > 4096 || targetStartUtcTicks <= 0)
                throw new ArgumentException("Select a different live process and a valid COM port.");
            string helper = GetHelperPath(appDirectory, architecture);
            ValidateHelper(helper, architecture); // Before starting any child process.
            Session session;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(SerialMonitorBrokerClient));
                if (current != null) throw new InvalidOperationException("Stop the current serial monitoring session first.");
                using (var parent = Process.GetCurrentProcess())
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo(helper,
                            parent.Id.ToString(CultureInfo.InvariantCulture) + " " + parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture))
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                            WorkingDirectory = Path.GetDirectoryName(helper)
                        }
                    };
                    session = new Session(process, com, architecture == "x64" ? 64 : 32, packet, exited);
                    if (!process.Start()) { process.Dispose(); throw new IOException("The monitoring helper did not start."); }
                    current = session;
                }
            }
            _ = Task.Run(() => ReadMessages(session, pid, targetStartUtcTicks));
            _ = Task.Run(() => DrainDiagnostics(session));
            using (var canceled = new CancellationTokenSource())
            using (var registration = token.Register(() => canceled.Cancel()))
            {
                try
                {
                    var timeout = Task.Delay(TimeSpan.FromSeconds(35), canceled.Token);
                    var winner = await Task.WhenAny(session.Ready.Task, timeout).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (winner != session.Ready.Task) throw new TimeoutException("The monitoring helper did not finish its startup handshake.");
                    int status = await session.Ready.Task.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (status != 1) _ = BeginStop(session);
                    return status;
                }
                catch
                {
                    _ = BeginStop(session);
                    throw;
                }
                finally { canceled.Cancel(); }
            }
        }
        private bool IsCurrent(Session session)
        {
            lock (gate) return ReferenceEquals(current, session) && !disposed && Volatile.Read(ref session.StopRequested) == 0;
        }
        private void ReadMessages(Session session, uint pid, long ticks)
        {
            string failure = "The monitoring helper exited.";
            try
            {
                while (true)
                {
                    var frame = MonitorBrokerProtocol.Read(session.Process.StandardOutput.BaseStream);
                    if (frame == null) break;
                    switch (frame.Type)
                    {
                        case MonitorBrokerProtocol.Kind.Hello:
                            if (Interlocked.Exchange(ref session.HelloSeen, 1) != 0 ||
                                BitConverter.ToInt32(frame.Payload, 0) != session.Process.Id ||
                                BitConverter.ToInt32(frame.Payload, 4) != session.Bits ||
                                BitConverter.ToUInt32(frame.Payload, 8) != MonitorBrokerProtocol.NativeAbi)
                                throw new InvalidDataException("The monitoring helper identity or ABI is incompatible.");
                            lock (session.InputGate)
                            {
                                if (Volatile.Read(ref session.StopRequested) == 0)
                                {
                                    Interlocked.Exchange(ref session.StartSent, 1);
                                    MonitorBrokerProtocol.Write(session.Process.StandardInput.BaseStream, MonitorBrokerProtocol.Kind.Start, MonitorBrokerProtocol.Start(pid, session.Com, ticks));
                                }
                            }
                            break;
                        case MonitorBrokerProtocol.Kind.Status:
                            if (Volatile.Read(ref session.StartSent) == 0 || session.Ready.Task.IsCompleted)
                                throw new InvalidDataException("Unexpected monitoring helper startup status.");
                            int status = BitConverter.ToInt32(frame.Payload, 0);
                            if (status == 1 && IsCurrent(session)) Volatile.Write(ref session.Running, 1);
                            session.Ready.TrySetResult(status);
                            break;
                        case MonitorBrokerProtocol.Kind.Packet:
                            if (!MonitorBrokerProtocol.IsPacketValid(frame.Payload, session.Com)) throw new InvalidDataException("Invalid monitoring helper packet metadata.");
                            if (Volatile.Read(ref session.Running) != 0 && IsCurrent(session))
                                session.Packet?.Invoke(frame.Payload);
                            break;
                        case MonitorBrokerProtocol.Kind.Error:
                            failure = Encoding.UTF8.GetString(frame.Payload);
                            session.Ready.TrySetException(new InvalidOperationException(failure));
                            break;
                        case MonitorBrokerProtocol.Kind.Stopped:
                            Volatile.Write(ref session.Running, 0);
                            session.Stopped.TrySetResult(BitConverter.ToInt32(frame.Payload, 0));
                            return;
                        default: throw new InvalidDataException("Unexpected monitoring helper frame direction.");
                    }
                }
            }
            catch (Exception error) { failure = error.GetBaseException().Message; }
            finally
            {
                Volatile.Write(ref session.Running, 0);
                session.Ready.TrySetException(new IOException(failure));
                session.Stopped.TrySetResult(4);
                bool notify;
                lock (gate) { notify = ReferenceEquals(current, session) && !disposed && Volatile.Read(ref session.StopRequested) == 0; }
                if (notify)
                {
                    try { session.Exited?.Invoke(failure); } catch { }
                    _ = BeginStop(session);
                }
            }
        }
        private static void DrainDiagnostics(Session session)
        {
            var buffer = new char[256];
            try
            {
                int read;
                while ((read = session.Process.StandardError.Read(buffer, 0, buffer.Length)) > 0)
                    lock (session.Diagnostic)
                        if (session.Diagnostic.Length < 2048) session.Diagnostic.Append(buffer, 0, Math.Min(read, 2048 - session.Diagnostic.Length));
            }
            catch { }
        }
        internal Task<int> StopAsync()
        {
            lock (gate) return current == null ? Task.FromResult(0) : BeginStop(current);
        }
        private Task<int> BeginStop(Session session)
        {
            lock (gate)
            {
                if (session.StopTask != null) return session.StopTask;
                Interlocked.Exchange(ref session.StopRequested, 1);
                Volatile.Write(ref session.Running, 0);
                session.StopTask = Task.Run(async () =>
                {
                    try
                    {
                        lock (session.InputGate)
                        {
                            try { MonitorBrokerProtocol.Write(session.Process.StandardInput.BaseStream, MonitorBrokerProtocol.Kind.Stop, Array.Empty<byte>()); } catch { }
                            // EOF is also a stop request if a control frame cannot be delivered.
                            try { session.Process.StandardInput.Close(); } catch { }
                        }
                        var completed = await Task.WhenAny(session.Stopped.Task, Task.Delay(TimeSpan.FromSeconds(35))).ConfigureAwait(false);
                        return completed == session.Stopped.Task ? await session.Stopped.Task.ConfigureAwait(false) : 4;
                    }
                    finally
                    {
                        // Dispose handles/pipes only. A helper still performing native
                        // late-initialization cleanup owns its own process lifetime.
                        CloseStreams(session);
                        lock (gate) if (ReferenceEquals(current, session)) current = null;
                    }
                });
                return session.StopTask;
            }
        }
        private static void CloseStreams(Session session)
        {
            if (Interlocked.Exchange(ref session.StreamsClosed, 1) != 0) return;
            try { session.Process.StandardInput.Close(); } catch { }
            try { session.Process.StandardOutput.Close(); } catch { }
            try { session.Process.StandardError.Close(); } catch { }
            session.Process.Dispose();
        }
        public void Dispose()
        {
            Session session;
            lock (gate) { if (disposed) return; disposed = true; session = current; }
            if (session != null) _ = BeginStop(session);
        }
    }
}
