using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    internal sealed class SerialPortReservationException : IOException
    {
        internal SerialPortReservationException(string portName, string owner, bool releasing, bool failed)
            : base(portName + (releasing ? " is still being released by " : " is already owned by ") + owner + ".")
        {
            PortName = portName;
            Owner = owner;
            IsReleasing = releasing;
            IsReleaseFailed = failed;
        }
        public string PortName { get; }
        public string Owner { get; }
        public bool IsReleasing { get; }
        public bool IsReleaseFailed { get; }
    }

    // A closed replacement object is not evidence that Windows has released the
    // retired handle. Reservations outlive pane/Uart generations and are shared
    // across every main/split connection, until its actual cleanup completes.
    internal static class SerialPortAccessCoordinator
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Reservation> Owners =
            new Dictionary<string, Reservation>(StringComparer.OrdinalIgnoreCase);

        internal sealed class Reservation : IDisposable
        {
            private bool released;
            private bool releasing;
            private Exception releaseError;
            private Task cleanupTask;
            internal Reservation(string portName, string owner) { PortName = portName; Owner = owner; }
            public string PortName { get; }
            public string Owner { get; }
            public bool IsReleasing { get { lock (Gate) return releasing; } }
            public bool IsReleaseFailed { get { lock (Gate) return releaseError != null; } }

            public void BeginRelease()
            {
                lock (Gate) { if (!released) releasing = true; }
            }

            public void Dispose()
            {
                lock (Gate)
                {
                    // A stale owner (or a using/finally) cannot release a COM
                    // while background cleanup still owns its driver handle.
                    if (!releasing) CompleteRelease();
                }
            }

            private void CompleteRelease()
            {
                if (released) return;
                if (Owners.TryGetValue(PortName, out var current) && ReferenceEquals(current, this))
                    Owners.Remove(PortName);
                released = true;
            }

            internal Task ReleaseAsync(Action cleanup)
            {
                lock (Gate)
                {
                    if (cleanupTask != null) return cleanupTask;
                    if (released) return Task.CompletedTask;
                    releasing = true;
                    cleanupTask = Task.Run(() =>
                    {
                        try
                        {
                            cleanup();
                            lock (Gate) CompleteRelease();
                        }
                        catch (Exception error)
                        {
                            lock (Gate) releaseError = error;
                            // Keep the reservation if releasing the driver failed;
                            // declaring it available would recreate the same race.
                            throw;
                        }
                    });
                    // Observe faults even when the UI requested nonblocking close.
                    // The returned task remains faulted for callers that await it.
                    cleanupTask.ContinueWith(task => { var ignored = task.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    return cleanupTask;
                }
            }
        }

        public static Reservation Acquire(string portName, string owner)
        {
            var name = Normalize(portName);
            if (name.Length == 0) throw new ArgumentException("A serial port name is required.", nameof(portName));
            lock (Gate)
            {
                if (Owners.TryGetValue(name, out var current))
                    throw new SerialPortReservationException(name, current.Owner, current.IsReleasing, current.IsReleaseFailed);
                var reservation = new Reservation(name, owner ?? "llcom plus");
                Owners.Add(name, reservation);
                return reservation;
            }
        }

        public static Task ReleaseAsync(Reservation reservation, Action cleanup)
        {
            if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
            if (reservation != null) return reservation.ReleaseAsync(cleanup);
            // Closed placeholder objects never acquired an OS port reservation.
            var task = Task.Run(cleanup);
            task.ContinueWith(fault => { var ignored = fault.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return task;
        }

        public static void DisposePort(SerialPort port, Stream baseStream = null, Action<string> trace = null)
        {
            if (port == null && baseStream == null) return;
            bool released = false;
            Exception failure = null;
            try { port?.Close(); }
            catch (Exception error) { failure = error; Trace(trace, "SerialPort.Close: " + error.Message); }
            try { if (port != null) { port.Dispose(); released = true; } }
            catch (Exception error) { failure = error; Trace(trace, "SerialPort.Dispose: " + error.Message); }
            try { if (baseStream != null) { baseStream.Dispose(); released = true; } }
            catch (Exception error) { failure = error; Trace(trace, "BaseStream.Dispose: " + error.Message); }
            if (!released) throw new IOException("The retired serial handle could not be released; reopening is blocked.", failure);
        }

        public static string GetOpenErrorMessage(Exception error, string portName, bool chinese)
        {
            var reservation = error as SerialPortReservationException;
            if (reservation != null)
            {
                if (reservation.IsReleaseFailed)
                    return chinese ? reservation.PortName + " 的旧连接释放失败。为避免重复占用，已阻止重开；请检查驱动并重启软件。" :
                        reservation.PortName + " could not release its old connection. Reopening is blocked; check the driver and restart the application.";
                if (reservation.IsReleasing)
                    return chinese ? reservation.PortName + " 的旧连接正在释放，请稍后手动重新打开；不会自动重发数据。" :
                        reservation.PortName + " is still releasing its old connection. Open it manually once release finishes; no data will be retried.";
                return chinese ? reservation.PortName + " 已由本程序的“" + reservation.Owner + "”占用，请先关闭该连接。" :
                    reservation.PortName + " is already in use by this application's " + reservation.Owner + ". Close that connection first.";
            }
            var native = error as Win32Exception;
            var code = native?.NativeErrorCode ?? (error == null ? 0 : error.HResult & 0xffff);
            if (error is UnauthorizedAccessException ||
                ((error is IOException || native != null) && (code == 5 || code == 32 || code == 33 || code == 170)))
                return (chinese ? Normalize(portName) + " 被系统拒绝打开，可能由其他程序占用，或驱动/权限尚未恢复。请关闭其他串口工具、检查设备后再手动重试。" :
                    "Windows could not open " + Normalize(portName) + ". Another program may own it, or its driver/permissions may not be ready. Close other serial tools and check the device before retrying manually.") +
                    Environment.NewLine + error.Message;
            return error?.Message ?? (chinese ? "串口打开失败。" : "The serial port could not be opened.");
        }

        private static string Normalize(string value)
        {
            var name = (value ?? "").Split('\0')[0].Trim();
            if (name.StartsWith(@"\\.\", StringComparison.Ordinal)) name = name.Substring(4);
            return name.ToUpperInvariant();
        }

        private static void Trace(Action<string> trace, string message)
        {
            try { trace?.Invoke(message); } catch { }
        }
    }
}
