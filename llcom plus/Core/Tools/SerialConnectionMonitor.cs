using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace llcom_plus.Tools
{
    // SerialPort.IsOpen can remain true after a USB adapter is removed. Query
    // driver state without reading bytes or changing modem-control outputs.
    internal sealed class SerialConnectionMonitor : IDisposable
    {
        private readonly Func<bool> isOpen;
        private readonly Action probeDriver;
        private readonly Func<string[]> getPortNames;
        private readonly string portName;
        private readonly Action<Exception> onDisconnected;
        private readonly Timer timer;
        private int disposed;
        private int probing;
        private int signaled;

        internal SerialConnectionMonitor(SerialPort port, Action<Exception> onDisconnected)
            : this(() => port.IsOpen, () => { var pendingBytes = port.BytesToRead; },
                SerialPort.GetPortNames, (port ?? throw new ArgumentNullException(nameof(port))).PortName,
                onDisconnected, true)
        {
        }

        internal SerialConnectionMonitor(Func<bool> isOpen, Action probeDriver,
            Func<string[]> getPortNames, string portName, Action<Exception> onDisconnected, bool startTimer)
        {
            this.isOpen = isOpen ?? throw new ArgumentNullException(nameof(isOpen));
            this.probeDriver = probeDriver ?? throw new ArgumentNullException(nameof(probeDriver));
            this.getPortNames = getPortNames ?? throw new ArgumentNullException(nameof(getPortNames));
            this.onDisconnected = onDisconnected;
            this.portName = portName ?? string.Empty;
            timer = new Timer(CheckConnection, null, startTimer ? 1000 : Timeout.Infinite, 1000);
        }

        internal static bool IsDisconnectException(Exception exception)
        {
            while (exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                exception = aggregate.InnerExceptions[0];
            return exception is ObjectDisposedException || exception is IOException ||
                exception is InvalidOperationException || exception is UnauthorizedAccessException;
        }

        private void CheckConnection(object state)
        {
            if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref signaled) != 0 ||
                Interlocked.CompareExchange(ref probing, 1, 0) != 0)
                return;

            Exception failure = null;
            try
            {
                if (!isOpen())
                    throw new IOException("The serial connection is no longer open.");
                // Querying BytesToRead calls ClearCommError on .NET Framework,
                // exposing a dead driver handle even while IsOpen is still true.
                probeDriver();
                string[] names = null;
                try { names = getPortNames(); }
                catch (Exception ex) { Trace("[SerialConnectionMonitor]port scan failed:" + ex.Message); }
                if (names != null && !ContainsPort(names, portName))
                    throw new IOException("The serial device was removed: " + portName);
            }
            catch (Exception ex) when (IsDisconnectException(ex)) { failure = ex; }
            catch (Exception ex) { Trace("[SerialConnectionMonitor]probe failed:" + ex.Message); }
            finally { Interlocked.Exchange(ref probing, 0); }

            if (failure == null || Volatile.Read(ref disposed) != 0 ||
                Interlocked.CompareExchange(ref signaled, 1, 0) != 0)
                return;
            try { timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
            if (Volatile.Read(ref disposed) != 0) return;
            try { onDisconnected?.Invoke(failure); }
            catch (Exception ex) { Trace("[SerialConnectionMonitor]disconnect callback failed:" + ex.Message); }
        }

        private static void Trace(string message) { try { Logger.AddUartLogDebug(message); } catch { } }

        internal static bool ContainsPort(string[] names, string expectedPort)
        {
            return names != null && names.Any(name =>
                string.Equals((name ?? string.Empty).Split('\0')[0].Trim(), expectedPort,
                    StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) timer.Dispose();
        }
    }
}
