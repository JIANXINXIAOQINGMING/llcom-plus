using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;

namespace llcom_plus.Tools
{
    internal sealed class SerialPinStatusSnapshot : EventArgs
    {
        public string PortName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public SerialPinChange ChangeType { get; set; }
        public IReadOnlyList<string> ChangedLines { get; set; } = new string[0];
        public bool? Cts { get; set; }
        public bool? Dsr { get; set; }
        public bool? Dcd { get; set; }
        public bool? Ri { get; set; }
    }

    /// <summary>
    /// Watches modem-control input lines on an open SerialPort. CTS/DSR/DCD are
    /// available through SerialPort itself; RI is read through the underlying
    /// Windows communication handle when the runtime exposes it.
    /// </summary>
    internal sealed class SerialPinMonitor : IDisposable
    {
        private const uint MsCtsOn = 0x0010;
        private const uint MsDsrOn = 0x0020;
        private const uint MsRingOn = 0x0040;
        private const uint MsRlsdOn = 0x0080;
        private const uint TrackedStatusMask = MsCtsOn | MsDsrOn | MsRingOn | MsRlsdOn;

        private readonly SerialPort port;
        private readonly Action<SerialPinStatusSnapshot> onChanged;
        private readonly object syncRoot = new object();
        private bool armed;
        private bool disposed;
        private uint? lastRawStatus;

        public SerialPinMonitor(SerialPort port, Action<SerialPinStatusSnapshot> onChanged)
        {
            this.port = port ?? throw new ArgumentNullException(nameof(port));
            this.onChanged = onChanged;
            port.PinChanged += Port_PinChanged;
        }

        public void Arm()
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;

                armed = true;
                lastRawStatus = TryReadRawStatus(port, out var rawStatus)
                    ? (uint?)rawStatus
                    : null;
            }
        }

        public void Disarm()
        {
            lock (syncRoot)
            {
                armed = false;
                lastRawStatus = null;
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                    return;

                disposed = true;
                armed = false;
                lastRawStatus = null;
            }

            try { port.PinChanged -= Port_PinChanged; } catch { }
        }

        private void Port_PinChanged(object sender, SerialPinChangedEventArgs e)
        {
            SerialPinStatusSnapshot snapshot;
            lock (syncRoot)
            {
                if (disposed || !armed || !ReferenceEquals(sender, port))
                    return;

                snapshot = CreateSnapshot(e.EventType);
                if (snapshot == null)
                    return;
            }

            try { onChanged?.Invoke(snapshot); }
            catch (Exception ex)
            {
                Logger.AddUartLogDebug("[SerialPinMonitor]notification error:" + ex.Message);
            }
        }

        private SerialPinStatusSnapshot CreateSnapshot(SerialPinChange changeType)
        {
            var changedLines = new List<string>();
            bool? cts = TryReadLine(() => port.CtsHolding);
            bool? dsr = TryReadLine(() => port.DsrHolding);
            bool? dcd = TryReadLine(() => port.CDHolding);
            bool? ri = null;

            if (TryReadRawStatus(port, out var rawStatus))
            {
                cts = (rawStatus & MsCtsOn) != 0;
                dsr = (rawStatus & MsDsrOn) != 0;
                dcd = (rawStatus & MsRlsdOn) != 0;
                ri = (rawStatus & MsRingOn) != 0;

                if (lastRawStatus.HasValue)
                    AddChangedLines(changedLines, (lastRawStatus.Value ^ rawStatus) & TrackedStatusMask);
                lastRawStatus = rawStatus;
            }

            if (changedLines.Count == 0)
                AddChangedLines(changedLines, changeType);

            if (changedLines.Count == 0)
                return null;

            var portName = string.Empty;
            try { portName = port.PortName ?? string.Empty; } catch { }

            return new SerialPinStatusSnapshot
            {
                PortName = portName,
                Timestamp = DateTime.Now,
                ChangeType = changeType,
                ChangedLines = changedLines,
                Cts = cts,
                Dsr = dsr,
                Dcd = dcd,
                Ri = ri
            };
        }

        private static bool? TryReadLine(Func<bool> read)
        {
            try { return read(); }
            catch { return null; }
        }

        private static void AddChangedLines(ICollection<string> lines, uint changedStatus)
        {
            if ((changedStatus & MsCtsOn) != 0)
                lines.Add("CTS");
            if ((changedStatus & MsDsrOn) != 0)
                lines.Add("DSR");
            if ((changedStatus & MsRlsdOn) != 0)
                lines.Add("DCD");
            if ((changedStatus & MsRingOn) != 0)
                lines.Add("RI");
        }

        private static void AddChangedLines(ICollection<string> lines, SerialPinChange changeType)
        {
            if ((changeType & SerialPinChange.CtsChanged) != 0)
                lines.Add("CTS");
            if ((changeType & SerialPinChange.DsrChanged) != 0)
                lines.Add("DSR");
            if ((changeType & SerialPinChange.CDChanged) != 0)
                lines.Add("DCD");
            if ((changeType & SerialPinChange.Ring) != 0)
                lines.Add("RI");
            if ((changeType & SerialPinChange.Break) != 0)
                lines.Add("BREAK");
        }

        private static bool TryReadRawStatus(SerialPort serialPort, out uint modemStatus)
        {
            modemStatus = 0;
            SafeFileHandle handle = null;
            var addedReference = false;

            try
            {
                if (serialPort == null || !serialPort.IsOpen)
                    return false;

                var stream = serialPort.BaseStream;
                if (stream == null)
                    return false;

                var streamType = stream.GetType();
                var handleField = streamType.GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic);
                if (handleField == null)
                {
                    foreach (var field in streamType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    {
                        if (typeof(SafeFileHandle).IsAssignableFrom(field.FieldType))
                        {
                            handleField = field;
                            break;
                        }
                    }
                }

                handle = handleField?.GetValue(stream) as SafeFileHandle;
                if (handle == null || handle.IsInvalid || handle.IsClosed)
                    return false;

                handle.DangerousAddRef(ref addedReference);
                return GetCommModemStatus(handle, out modemStatus);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (addedReference)
                {
                    try { handle?.DangerousRelease(); } catch { }
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCommModemStatus(SafeFileHandle hFile, out uint lpModemStat);
    }
}
