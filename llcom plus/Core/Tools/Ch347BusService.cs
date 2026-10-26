using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    /// <summary>
    /// Owns one explicit CH347 connection. All SDK work, including health checks
    /// and close, runs on the same serial worker. Cancellation never resends I/O.
    /// </summary>
    internal sealed class Ch347BusService : IDisposable
    {
        internal const int QueueLimit = 64;
        private readonly ICh347Backend backend;
        private readonly object sync = new object();
        private readonly Queue<WorkItem> pending = new Queue<WorkItem>();
        private readonly Timer healthTimer;
        private bool workerRunning;
        private bool disposed;
        private bool connecting;
        private bool connected;
        private bool healthQueued;
        private long generation;
        private Ch347DeviceInfo device;
        private Task disconnectTask = Task.CompletedTask;

        public Ch347BusService() : this(new Ch347Native()) { }

        public Ch347BusService(ICh347Backend backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            healthTimer = new Timer(CheckHealth, null, Timeout.Infinite, Timeout.Infinite);
        }

        public event Action<Ch347BusLogEntry> Logged;
        public event Action StateChanged;

        public bool IsConnected { get { lock (sync) return connected && !disposed; } }
        public Ch347DeviceInfo ConnectedDevice { get { lock (sync) return device?.Clone(); } }

        public Task<IReadOnlyList<Ch347DeviceInfo>> EnumerateAsync(CancellationToken token)
        {
            return Enqueue(() =>
            {
                var found = backend.Enumerate();
                var copies = new List<Ch347DeviceInfo>();
                foreach (var item in found)
                    if (item != null) copies.Add(item.Clone());
                return (IReadOnlyList<Ch347DeviceInfo>)copies;
            }, token, null, false);
        }

        public async Task ConnectAsync(Ch347DeviceInfo selectedDevice, CancellationToken token)
        {
            if (selectedDevice == null || selectedDevice.Identity.Length == 0)
                throw new ArgumentException("Refresh and select a CH347 device before connecting.", nameof(selectedDevice));
            token.ThrowIfCancellationRequested();
            var selection = selectedDevice.Clone();
            long lease;
            Task<bool> work;
            lock (sync)
            {
                ThrowIfDisposed();
                if (connected || connecting)
                    throw new InvalidOperationException("Disconnect the current CH347 interface before opening another connection.");
                lease = ++generation;
                connecting = true;
                try
                {
                    work = EnqueueLocked(() =>
                    {
                        var opened = false;
                        try
                        {
                            var actual = backend.Open(selection.Clone());
                            opened = true;
                            token.ThrowIfCancellationRequested();
                            if (actual == null || actual.Identity != selection.Identity || !actual.SupportsAnyBus)
                                throw new IOException("The selected CH347 interface changed. Refresh and reconnect.");
                            lock (sync)
                            {
                                VerifyGeneration(lease);
                                token.ThrowIfCancellationRequested();
                                device = actual.Clone();
                                connected = true;
                                connecting = false;
                                healthTimer.Change(1000, 1000);
                            }
                            Emit("Connected. Transactions use this connection only.", actual.Identity);
                            RaiseStateChanged();
                            return true;
                        }
                        catch
                        {
                            if (opened) TryClose();
                            throw;
                        }
                    }, token, lease, false);
                }
                catch { connecting = false; throw; }
            }
            try { await work.ConfigureAwait(false); }
            finally
            {
                lock (sync)
                {
                    if (generation == lease) connecting = false;
                }
                RaiseStateChanged();
            }
        }

        public Task DisconnectAsync()
        {
            string identity;
            Task close;
            lock (sync)
            {
                if (disposed) return disconnectTask;
                if (!connected && !connecting) return disconnectTask;
                identity = device?.Identity ?? "";
                ++generation;
                connected = false;
                connecting = false;
                device = null;
                healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
                CancelConnectionItemsLocked();
                // A lifecycle close has a reserved slot: a full transaction
                // queue must never prevent explicit disconnection.
                close = disconnectTask.IsCompleted
                    ? EnqueueLocked(() => { backend.Close(); return true; }, CancellationToken.None, null, true)
                    : disconnectTask;
                disconnectTask = close;
            }
            Emit("Disconnected. Queued transactions were cancelled; an active SDK call is allowed to finish before close.", identity);
            RaiseStateChanged();
            return close;
        }

        public Task<Ch347TransferResult> ExecuteAsync(Ch347TransferRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var snapshot = request.Clone();
            snapshot.Validate();
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                ThrowIfDisposed();
                if (!connected || device == null) throw new InvalidOperationException("Connect a CH347 interface before sending a transaction.");
                if (!device.SupportsProtocol(snapshot.Protocol))
                    throw new InvalidOperationException(snapshot.Protocol + " is unavailable in the selected CH347 mode.");
                var lease = generation;
                var target = device.Clone();
                return EnqueueLocked(() => ExecuteCore(snapshot, target, lease, token), token, lease, false);
            }
        }

        public Task<IReadOnlyList<int>> ScanI2cAsync(int speedCode, int timeoutMs, CancellationToken token)
        {
            var probe = new Ch347TransferRequest
            {
                Protocol = Ch347BusProtocol.I2c, I2cSpeedCode = speedCode,
                TimeoutMs = timeoutMs, ProbeOnly = true
            };
            probe.Validate();
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                ThrowIfDisposed();
                if (!connected || device == null) throw new InvalidOperationException("Connect a CH347 interface before scanning.");
                if (!device.SupportsI2c)
                    throw new InvalidOperationException("I2C address scanning is unavailable in the selected CH347 mode.");
                var lease = generation;
                var target = device.Clone();
                // One queue item keeps a manually requested scan bound to the
                // original device. Each address is attempted exactly once.
                return EnqueueLocked(() =>
                {
                    var found = new List<int>();
                    Emit("I2C address scan started (08–77); only address bytes are sent.", target.Identity);
                    for (var address = 0x08; address <= 0x77; address++)
                    {
                        token.ThrowIfCancellationRequested();
                        probe.I2cAddress = address;
                        var result = ExecuteCore(probe, target, lease, token, false);
                        if (result.Success && result.AckCount.GetValueOrDefault() > 0)
                            found.Add(address);
                        else if (!result.AckCount.HasValue)
                            throw new IOException("I2C scan stopped after an SDK failure. No address was retried. " + result.Message);
                    }
                    Emit("I2C scan completed: " + found.Count + " address(es) acknowledged.", target.Identity);
                    return (IReadOnlyList<int>)found;
                }, token, lease, false);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                ++generation;
                connected = false;
                connecting = false;
                device = null;
                healthTimer.Dispose();
                while (pending.Count > 0) pending.Dequeue().Cancel();
                // Do not block the UI or close a handle underneath native I/O.
                disconnectTask = EnqueueLocked(() =>
                {
                    try { backend.Close(); }
                    finally { backend.Dispose(); }
                    return true;
                }, CancellationToken.None, null, true);
            }
            RaiseStateChanged();
        }

        private Ch347TransferResult ExecuteCore(Ch347TransferRequest request, Ch347DeviceInfo target,
            long lease, CancellationToken token, bool writeLog = true)
        {
            token.ThrowIfCancellationRequested();
            lock (sync) VerifyGeneration(lease);
            if (!IsPresent(target))
            {
                InvalidateOnWorker(lease, target.Identity);
                throw new IOException("CH347 was removed or its interface changed. Reconnect explicitly to continue.");
            }
            token.ThrowIfCancellationRequested();
            lock (sync) VerifyGeneration(lease);
            var watch = Stopwatch.StartNew();
            var source = "CH347 #" + target.Index + " / " + request.Protocol;
            var traceIdentity = "ch347:" + target.Identity + ":" + lease;
            if (writeLog) SerialTraceHub.RecordEvent(source, traceIdentity, SerialTraceKind.Info,
                Describe(request) + ", write=" + request.WriteData.Length + ", read=" + request.ReadLength +
                ", timeout=" + request.TimeoutMs + " ms");
            Ch347TransferResult result;
            try
            {
                result = backend.Transfer(request);
                if (result == null) throw new IOException("CH347 returned no transaction result.");
            }
            catch
            {
                if (!IsPresent(target)) InvalidateOnWorker(lease, target.Identity);
                SerialTraceHub.RecordEvent(source, traceIdentity, SerialTraceKind.Error,
                    "Bus transaction failed; completion unknown. No automatic retry.");
                Emit("Transaction failed; its completion may be unknown. No automatic retry.", target.Identity);
                throw;
            }
            watch.Stop();
            var copy = new Ch347TransferResult
            {
                Success = result.Success,
                ReadData = result.ReadData == null ? Array.Empty<byte>() : (byte[])result.ReadData.Clone(),
                AckCount = result.AckCount,
                Duration = result.Duration == TimeSpan.Zero ? watch.Elapsed : result.Duration,
                Message = result.Message ?? ""
            };
            if (writeLog)
            {
                Emit(Describe(request) + " TX[" + Ch347TransferRequest.ToHex(request.WriteData) + "] RX[" +
                    Ch347TransferRequest.ToHex(copy.ReadData) + "] " +
                    (copy.AckCount.HasValue ? "ACK=" + copy.AckCount + " " : "") +
                    copy.Duration.TotalMilliseconds.ToString("0.0") + " ms " + copy.Message, target.Identity);
                if (copy.Success)
                {
                    SerialTraceHub.RecordData(source, traceIdentity, true, WireData(request));
                    SerialTraceHub.RecordData(source, traceIdentity, false, copy.ReadData);
                }
                else SerialTraceHub.RecordEvent(source, traceIdentity, SerialTraceKind.Error, copy.Message);
            }
            // Preserve evidence of already completed I/O under its original
            // identity, even when cancellation or disconnection arrives late.
            if (!IsPresent(target))
            {
                InvalidateOnWorker(lease, target.Identity);
                throw new IOException("CH347 disconnected during the transaction. Completion is unknown; no automatic retry.");
            }
            lock (sync) VerifyGeneration(lease);
            if (token.IsCancellationRequested)
            {
                Emit("Cancellation arrived after a transaction had started; already clocked bytes cannot be withdrawn.", target.Identity);
                token.ThrowIfCancellationRequested();
            }
            return copy;
        }

        private static byte[] WireData(Ch347TransferRequest request)
        {
            if (request.Protocol == Ch347BusProtocol.I2c)
            {
                var bytes = new byte[request.WriteData.Length + 1];
                bytes[0] = (byte)((request.I2cAddress << 1) |
                    (request.WriteData.Length == 0 && request.ReadLength > 0 ? 1 : 0));
                Buffer.BlockCopy(request.WriteData, 0, bytes, 1, request.WriteData.Length);
                return bytes;
            }
            if (request.Protocol == Ch347BusProtocol.Jtag)
                return (byte[])request.WriteData.Clone();
            var length = request.SpiOperation == Ch347SpiOperation.WriteThenRead
                ? request.WriteData.Length + request.ReadLength : Math.Max(request.WriteData.Length, request.ReadLength);
            var clocks = new byte[length];
            for (var i = 0; i < length; i++) clocks[i] = request.SpiDummyByte;
            Buffer.BlockCopy(request.WriteData, 0, clocks, 0, request.WriteData.Length);
            return clocks;
        }

        private static string Describe(Ch347TransferRequest request)
        {
            switch (request.Protocol)
            {
                case Ch347BusProtocol.I2c:
                    return "I2C @" + request.I2cAddress.ToString("X2");
                case Ch347BusProtocol.Spi:
                    return "SPI CS" + request.SpiChipSelect + " " + request.SpiOperation;
                case Ch347BusProtocol.Jtag:
                    return "JTAG " + request.JtagOperation + " " + request.JtagBitLength + " bits, clock=" + request.JtagClockCode;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Protocol));
            }
        }

        private Task<T> Enqueue<T>(Func<T> operation, CancellationToken token, long? lease, bool lifecycle)
        {
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                ThrowIfDisposed();
                return EnqueueLocked(operation, token, lease, lifecycle);
            }
        }

        private Task<T> EnqueueLocked<T>(Func<T> operation, CancellationToken token, long? lease, bool lifecycle)
        {
            if (!lifecycle && pending.Count >= QueueLimit)
                throw new InvalidOperationException("The CH347 queue is full (64 pending operations). Wait or cancel before submitting more.");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executionStarted = 0;
            var item = new WorkItem
            {
                Generation = lease,
                Token = token,
                Run = () =>
                {
                    Interlocked.Exchange(ref executionStarted, 1);
                    if (token.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                    try { completion.TrySetResult(operation()); }
                    catch (OperationCanceledException) { completion.TrySetCanceled(); }
                    catch (Exception error) { completion.TrySetException(error); }
                },
                Fail = error => completion.TrySetException(error),
                CancelTask = () => completion.TrySetCanceled()
            };
            item.Registration = token.Register(() =>
            {
                // Once native work starts, cancellation must await its finite
                // completion; it cannot imply bytes already clocked were recalled.
                if (Volatile.Read(ref executionStarted) == 0) completion.TrySetCanceled();
            });
            pending.Enqueue(item);
            if (!workerRunning)
            {
                workerRunning = true;
                ThreadPool.QueueUserWorkItem(_ => DrainQueue());
            }
            return completion.Task;
        }

        private void DrainQueue()
        {
            while (true)
            {
                WorkItem item;
                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        workerRunning = false;
                        return;
                    }
                    item = pending.Dequeue();
                }
                try
                {
                    if (item.Token.IsCancellationRequested) item.CancelTask();
                    else
                    {
                        var valid = true;
                        lock (sync)
                            if (item.Generation.HasValue && (disposed || item.Generation.Value != generation)) valid = false;
                        if (valid) item.Run();
                        else item.CancelTask();
                    }
                }
                catch (Exception error) { item.Fail(error); }
                finally { item.Registration.Dispose(); }
            }
        }

        private void CheckHealth(object state)
        {
            lock (sync)
            {
                if (disposed || !connected || healthQueued || pending.Count > 0 || workerRunning) return;
                healthQueued = true;
                var lease = generation;
                var target = device.Clone();
                var health = EnqueueLocked(() =>
                {
                    if (!IsPresent(target)) InvalidateOnWorker(lease, target.Identity);
                    return true;
                }, CancellationToken.None, lease, false);
                health.ContinueWith(_ => { lock (sync) healthQueued = false; }, TaskScheduler.Default);
            }
        }

        private bool IsPresent(Ch347DeviceInfo target)
        {
            try { return backend.IsPresent(target); }
            catch { return false; }
        }

        private void InvalidateOnWorker(long lease, string identity)
        {
            lock (sync)
            {
                if (generation != lease || disposed) return;
                ++generation;
                connected = false;
                connecting = false;
                device = null;
                healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
                CancelConnectionItemsLocked();
            }
            TryClose();
            Emit("CH347 disconnected or changed. Pending transactions were cancelled; refresh and reconnect explicitly.", identity);
            RaiseStateChanged();
        }

        private void CancelConnectionItemsLocked()
        {
            var keep = new Queue<WorkItem>();
            while (pending.Count > 0)
            {
                var item = pending.Dequeue();
                if (item.Generation.HasValue) item.Cancel();
                else keep.Enqueue(item);
            }
            while (keep.Count > 0) pending.Enqueue(keep.Dequeue());
        }

        private void VerifyGeneration(long lease)
        {
            if (disposed || generation != lease)
                throw new OperationCanceledException("The CH347 connection changed; this transaction was not carried over.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(Ch347BusService));
        }

        private void TryClose()
        {
            try { backend.Close(); }
            catch (Exception error) { Emit("CH347 close failed: " + error.Message, ""); }
        }

        private void Emit(string text, string identity)
        {
            try { Logged?.Invoke(new Ch347BusLogEntry { DeviceIdentity = identity, Text = text }); }
            catch { /* A UI observer must not interrupt the device worker. */ }
        }

        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); }
            catch { /* Notifications do not own the connection lifecycle. */ }
        }

        private sealed class WorkItem
        {
            public long? Generation;
            public CancellationToken Token;
            public CancellationTokenRegistration Registration;
            public Action Run;
            public Action<Exception> Fail;
            public Action CancelTask;
            public void Cancel()
            {
                CancelTask();
                Registration.Dispose();
            }
        }
    }
}
