using llcom_plus.ScriptEnv;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Model
{
    /// <summary>
    /// Serializes the temporary DTR wake lifecycle for one logical serial endpoint.
    /// The caller still owns its normal serial write/lifecycle locks; this helper never
    /// retries a write and never keeps a caller's lifecycle lock during a wake delay.
    /// </summary>
    internal sealed class DtrWakeController : IDisposable
    {
        private interface IDtrLine
        {
            bool Enabled { get; set; }
        }

        private sealed class SerialPortDtrLine : IDtrLine
        {
            private readonly SerialPort port;

            internal SerialPortDtrLine(SerialPort port)
            {
                this.port = port ?? throw new ArgumentNullException(nameof(port));
            }

            public bool Enabled
            {
                get { return port.DtrEnable; }
                set { port.DtrEnable = value; }
            }
        }

        private sealed class WakeSession
        {
            internal object ConnectionKey;
            internal long Generation;
            internal Func<bool> IsConnectionCurrent;
            internal IDtrLine DtrLine;
            internal bool OriginalDtr;
            internal long UserDtrRevision;
            internal long ActivityRevision;
            internal int IdleMilliseconds;
            internal bool SendInProgress;
            internal bool Initializing;
            internal Action<string> Trace;
        }

        private sealed class RestoreTimerState
        {
            internal WakeSession Session;
            internal long ActivityRevision;
        }

        private sealed class ProbeDtrLine : IDtrLine
        {
            private bool enabled;

            internal ProbeDtrLine(bool initialValue)
            {
                enabled = initialValue;
            }

            public bool Enabled
            {
                get { return enabled; }
                set
                {
                    enabled = value;
                    SetCount++;
                }
            }

            internal int SetCount { get; private set; }
        }

        private readonly object stateLock = new object();
        private readonly object sendExecutionLock = new object();
        private readonly object lineOperationLock = new object();
        private WakeSession activeSession;
        private Timer restoreTimer;
        private RestoreTimerState restoreTimerState;
        private long userDtrRevision;
        private long lifecycleRevision;
        private bool disposed;

        internal void ExecuteWithWake(
            SerialPort port,
            long connectionGeneration,
            Func<bool> isConnectionCurrent,
            UartPortProfile profile,
            CancellationToken cancellationToken,
            Action send,
            Action<string> trace = null)
        {
            ExecuteWithWakeCore(
                port,
                connectionGeneration,
                isConnectionCurrent,
                profile,
                cancellationToken,
                send,
                port == null ? null : new SerialPortDtrLine(port), trace);
        }

        internal void RenewAfterReceive(
            SerialPort port,
            long connectionGeneration,
            Func<bool> isConnectionCurrent,
            UartPortProfile profile)
        {
            if (port == null)
                return;

            RenewAfterReceiveCore(
                port,
                connectionGeneration,
                isConnectionCurrent,
                profile,
                new SerialPortDtrLine(port));
        }

        internal void InvalidateConnection(SerialPort port, long connectionGeneration)
        {
            InvalidateConnectionCore(port, connectionGeneration);
        }

        internal void ApplyUserDtr(SerialPort port, long connectionGeneration, bool enabled)
        {
            ApplyUserDtrCore(
                port,
                connectionGeneration,
                port == null ? null : new SerialPortDtrLine(port),
                enabled);
        }

        internal void ApplyConfiguredDtr(SerialPort port, long connectionGeneration, bool enabled)
        {
            if (port == null)
                return;

            var line = new SerialPortDtrLine(port);
            lock (lineOperationLock)
            {
                bool keepAwake;
                lock (stateLock)
                {
                    ThrowIfDisposed();
                    keepAwake = IsMatchingSessionUnsafe(port, connectionGeneration);
                }
                line.Enabled = keepAwake || enabled;
            }
        }

        private void ExecuteWithWakeCore(
            object connectionKey,
            long connectionGeneration,
            Func<bool> isConnectionCurrent,
            UartPortProfile profile,
            CancellationToken cancellationToken,
            Action send,
            IDtrLine dtrLine,
            Action<string> trace = null)
        {
            if (send == null)
                throw new ArgumentNullException(nameof(send));

            lock (sendExecutionLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                profile = profile ?? new UartPortProfile();
                bool hasWakeSession;
                lock (stateLock)
                {
                    ThrowIfDisposed();
                    hasWakeSession = IsMatchingSessionUnsafe(connectionKey, connectionGeneration);
                }
                if (!profile.dtrWakeBeforeSend && !hasWakeSession)
                {
                    send();
                    return;
                }
                if (connectionKey == null || dtrLine == null || isConnectionCurrent == null)
                    throw new IOException("Serial port is not open.");

                TraceSafely(trace, "DTR wake requested / 请求唤醒");
                bool wakeDelayRequired;
                WakeSession wakeSession = null;
                try
                {
                    wakeSession = BeginSend(
                        connectionKey,
                        connectionGeneration,
                        isConnectionCurrent,
                        dtrLine,
                        profile,
                        cancellationToken,
                        out wakeDelayRequired);
                    if (wakeSession != null) wakeSession.Trace = trace;
                    else TraceSafely(trace, "DTR held by user; no automatic restore / DTR 已手动保持，不自动恢复");
                    if (wakeSession != null)
                    {
                        if (wakeDelayRequired)
                        {
                            TraceSafely(trace, $"DTR asserted; waiting / 等待唤醒 {Settings.NormalizeDtrWakeDelayMilliseconds(profile.dtrWakeDelayMs)} ms");
                            WaitForWakeDelay(
                                wakeSession,
                                Settings.NormalizeDtrWakeDelayMilliseconds(profile.dtrWakeDelayMs),
                                cancellationToken);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsActiveSession(wakeSession) || !IsCurrent(wakeSession.IsConnectionCurrent))
                            throw new IOException("The serial connection changed during DTR wake-up.");
                    }
                    else if (!IsCurrent(isConnectionCurrent))
                    {
                        throw new IOException("The captured serial connection is no longer open.");
                    }

                    // Exactly one invocation: a failure may mean the driver accepted a
                    // prefix, so replaying arbitrary application data would be unsafe.
                    TraceSafely(trace, "Wake wait complete; sending / 唤醒等待完成，开始发送");
                    send();
                    TraceSafely(trace, wakeSession != null
                        ? "Write drained; waiting for idle / 发送缓冲已清空，等待空闲"
                        : "Write drained; manual DTR unchanged / 发送缓冲已清空，保持手动 DTR");
                }
                catch (Exception ex)
                {
                    TraceSafely(trace, "Wake/send failed / 唤醒或发送失败: " + ex.Message);
                    throw;
                }
                finally
                {
                    if (wakeSession != null)
                        CompleteSend(wakeSession);
                }
            }
        }

        private static void TraceSafely(Action<string> trace, string message)
        {
            try { trace?.Invoke(message); } catch { }
        }

        private WakeSession BeginSend(
            object connectionKey,
            long connectionGeneration,
            Func<bool> isConnectionCurrent,
            IDtrLine dtrLine,
            UartPortProfile profile,
            CancellationToken cancellationToken,
            out bool wakeDelayRequired)
        {
            wakeDelayRequired = false;
            long expectedLifecycleRevision;
            lock (stateLock)
                expectedLifecycleRevision = lifecycleRevision;
            if (!IsCurrent(isConnectionCurrent))
                throw new IOException("The captured serial connection is no longer open.");

            Timer retiredTimer = null;
            WakeSession session = null;
            var createdSession = false;
            try
            {
                lock (lineOperationLock)
                {
                    try
                    {
                        lock (stateLock)
                        {
                            ThrowIfDisposed();
                            cancellationToken.ThrowIfCancellationRequested();
                            if (expectedLifecycleRevision != lifecycleRevision)
                                throw new IOException("The serial connection changed before DTR wake-up.");

                            if (IsMatchingSessionUnsafe(connectionKey, connectionGeneration) &&
                                activeSession.UserDtrRevision == userDtrRevision)
                            {
                                session = activeSession;
                                session.IsConnectionCurrent = isConnectionCurrent;
                                session.DtrLine = dtrLine;
                            }
                            else
                            {
                                // A setting change may disable wake while the previous
                                // TX is still held awake. Preserve that existing session,
                                // but never assert a new wake edge after it has expired.
                                if (!profile.dtrWakeBeforeSend)
                                    return null;
                                retiredTimer = ClearSessionUnsafe();
                                session = new WakeSession
                                {
                                    ConnectionKey = connectionKey,
                                    Generation = connectionGeneration,
                                    IsConnectionCurrent = isConnectionCurrent,
                                    DtrLine = dtrLine,
                                    UserDtrRevision = userDtrRevision,
                                    Initializing = true
                                };
                                activeSession = session;
                                createdSession = true;
                            }

                            session.IdleMilliseconds =
                                Settings.NormalizeDtrWakeIdleMilliseconds(profile.dtrWakeIdleMs);
                            session.SendInProgress = true;
                            session.ActivityRevision = unchecked(session.ActivityRevision + 1);
                            if (restoreTimer != null)
                            {
                                retiredTimer = restoreTimer;
                                restoreTimer = null;
                                restoreTimerState = null;
                            }
                        }

                        // SerialPort control-line access can enter the driver. Keep it
                        // outside stateLock so close/invalidate and RX renewal stay live.
                        var dtrWasAsserted = dtrLine.Enabled;
                        if (!dtrWasAsserted)
                        {
                            dtrLine.Enabled = true;
                            wakeDelayRequired = true;
                        }

                        lock (stateLock)
                        {
                            if (!ReferenceEquals(activeSession, session) ||
                                session.UserDtrRevision != userDtrRevision ||
                                disposed)
                            {
                                return session;
                            }

                            if (createdSession)
                            {
                                session.OriginalDtr = dtrWasAsserted;
                                session.Initializing = false;
                                if (dtrWasAsserted)
                                {
                                    // The user already asserted DTR. There is nothing
                                    // automatic to restore and no wake edge to delay for.
                                    ClearSessionUnsafe();
                                    return null;
                                }
                            }
                        }
                    }
                    catch
                    {
                        lock (stateLock)
                        {
                            if (ReferenceEquals(activeSession, session))
                                ClearSessionUnsafe();
                        }
                        throw;
                    }
                }
                return session;
            }
            finally
            {
                DisposeTimer(retiredTimer);
            }
        }

        private void WaitForWakeDelay(
            WakeSession session,
            int delayMilliseconds,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < delayMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsActiveSession(session) || !IsCurrent(session.IsConnectionCurrent))
                    throw new IOException("The serial connection changed during DTR wake-up.");

                var remaining = delayMilliseconds - (int)stopwatch.ElapsedMilliseconds;
                var slice = Math.Min(20, Math.Max(1, remaining));
                if (cancellationToken.WaitHandle.WaitOne(slice))
                    cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveSession(session) || !IsCurrent(session.IsConnectionCurrent))
                throw new IOException("The serial connection changed during DTR wake-up.");
        }

        private void CompleteSend(WakeSession session)
        {
            var connectionIsCurrent = IsCurrent(session.IsConnectionCurrent);
            Timer retiredTimer = null;
            lock (stateLock)
            {
                if (!ReferenceEquals(activeSession, session))
                    return;

                session.SendInProgress = false;
                if (disposed ||
                    !connectionIsCurrent ||
                    session.UserDtrRevision != userDtrRevision)
                {
                    retiredTimer = ClearSessionUnsafe();
                }
                else
                {
                    retiredTimer = ScheduleRestoreUnsafe(session);
                }
            }
            DisposeTimer(retiredTimer);
        }

        private void RenewAfterReceiveCore(
            object connectionKey,
            long connectionGeneration,
            Func<bool> isConnectionCurrent,
            UartPortProfile profile,
            IDtrLine dtrLine)
        {
            if (isConnectionCurrent == null ||
                !IsCurrent(isConnectionCurrent))
            {
                return;
            }

            Timer retiredTimer = null;
            lock (lineOperationLock)
            lock (stateLock)
            {
                if (disposed ||
                    !IsMatchingSessionUnsafe(connectionKey, connectionGeneration) ||
                    activeSession.UserDtrRevision != userDtrRevision ||
                    activeSession.Initializing)
                {
                    return;
                }

                activeSession.IsConnectionCurrent = isConnectionCurrent;
                activeSession.DtrLine = dtrLine;
                if (profile != null)
                    activeSession.IdleMilliseconds =
                        Settings.NormalizeDtrWakeIdleMilliseconds(profile.dtrWakeIdleMs);
                if (activeSession.SendInProgress)
                {
                    activeSession.ActivityRevision = unchecked(activeSession.ActivityRevision + 1);
                    if (restoreTimer != null)
                    {
                        retiredTimer = restoreTimer;
                        restoreTimer = null;
                        restoreTimerState = null;
                    }
                }
                else
                {
                    retiredTimer = ScheduleRestoreUnsafe(activeSession);
                }
            }
            DisposeTimer(retiredTimer);
        }

        private Timer ScheduleRestoreUnsafe(WakeSession session)
        {
            var retiredTimer = restoreTimer;
            session.ActivityRevision = unchecked(session.ActivityRevision + 1);
            var timerState = new RestoreTimerState
            {
                Session = session,
                ActivityRevision = session.ActivityRevision
            };

            try
            {
                var nextTimer = new Timer(
                    RestoreAfterIdle,
                    timerState,
                    Timeout.Infinite,
                    Timeout.Infinite);
                restoreTimer = nextTimer;
                restoreTimerState = timerState;
                // Publish ownership before arming: an idle value of zero may queue
                // the callback immediately on another thread.
                nextTimer.Change(session.IdleMilliseconds, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                // Keeping DTR asserted is safer than throwing after bytes may already
                // have been committed. A later RX/TX activity can schedule it again.
                restoreTimer = null;
                restoreTimerState = null;
                Log($"[DtrWake]unable to schedule idle restore: {ex.Message}");
            }

            return retiredTimer;
        }

        private void RestoreAfterIdle(object state)
        {
            var timerState = state as RestoreTimerState;
            if (timerState == null)
                return;

            WakeSession session;
            lock (stateLock)
            {
                if (!IsCurrentTimerUnsafe(timerState))
                    return;
                session = timerState.Session;
            }

            // Never invoke caller code while holding stateLock. Close/reconnect paths
            // commonly hold their lifecycle lock while invalidating this controller.
            var connectionIsCurrent = IsCurrent(session.IsConnectionCurrent);
            Timer retiredTimer = null;
            Exception restoreError = null;
            bool restored = false;
            if (!connectionIsCurrent)
            {
                lock (stateLock)
                {
                    if (IsCurrentTimerUnsafe(timerState))
                        retiredTimer = ClearSessionUnsafe();
                }
                DisposeTimer(retiredTimer);
                return;
            }

            lock (lineOperationLock)
            {
                IDtrLine lineToRestore = null;
                bool restoreValue = false;
                lock (stateLock)
                {
                    if (!IsCurrentTimerUnsafe(timerState))
                        return;

                    if (session.UserDtrRevision == userDtrRevision &&
                        !session.SendInProgress &&
                        !session.Initializing)
                    {
                        lineToRestore = session.DtrLine;
                        restoreValue = session.OriginalDtr;
                    }
                    retiredTimer = ClearSessionUnsafe();
                }

                if (lineToRestore != null)
                {
                    try
                    {
                        lineToRestore.Enabled = restoreValue;
                        restored = true;
                    }
                    catch (Exception ex)
                    {
                        restoreError = ex;
                    }
                }
            }
            DisposeTimer(retiredTimer);

            // A restore failure must not turn a successful send into an apparent send
            // failure and tempt a caller to duplicate arbitrary data.
            if (restoreError != null)
            {
                Log($"[DtrWake]idle restore skipped: {restoreError.Message}");
                TraceSafely(session.Trace, "DTR idle restore failed / 空闲恢复失败: " + restoreError.Message);
            }
            else if (restored)
                TraceSafely(session.Trace, "DTR restored after idle / 空闲后已恢复 DTR");
        }

        private void ApplyUserDtrCore(
            object connectionKey,
            long connectionGeneration,
            IDtrLine dtrLine,
            bool enabled)
        {
            Timer retiredTimer = null;
            try
            {
                lock (lineOperationLock)
                {
                    lock (stateLock)
                    {
                        ThrowIfDisposed();
                        userDtrRevision = unchecked(userDtrRevision + 1);
                        retiredTimer = ClearSessionUnsafe();
                    }
                    if (connectionKey != null && dtrLine != null)
                        dtrLine.Enabled = enabled;
                }
            }
            finally
            {
                DisposeTimer(retiredTimer);
            }
        }

        private void InvalidateConnectionCore(object connectionKey, long connectionGeneration)
        {
            Timer retiredTimer = null;
            lock (lineOperationLock)
            lock (stateLock)
            {
                lifecycleRevision = unchecked(lifecycleRevision + 1);
                if (IsMatchingSessionUnsafe(connectionKey, connectionGeneration))
                    retiredTimer = ClearSessionUnsafe();
            }
            DisposeTimer(retiredTimer);
        }

        private bool IsActiveSession(WakeSession session)
        {
            lock (stateLock)
            {
                return !disposed &&
                    ReferenceEquals(activeSession, session) &&
                    session.UserDtrRevision == userDtrRevision;
            }
        }

        private bool IsMatchingSessionUnsafe(object connectionKey, long connectionGeneration)
        {
            return activeSession != null &&
                ReferenceEquals(activeSession.ConnectionKey, connectionKey) &&
                activeSession.Generation == connectionGeneration;
        }

        private bool IsCurrentTimerUnsafe(RestoreTimerState timerState)
        {
            return !disposed &&
                ReferenceEquals(activeSession, timerState.Session) &&
                ReferenceEquals(restoreTimerState, timerState) &&
                timerState.ActivityRevision == timerState.Session.ActivityRevision;
        }

        private Timer ClearSessionUnsafe()
        {
            if (activeSession != null)
                activeSession.ActivityRevision = unchecked(activeSession.ActivityRevision + 1);
            activeSession = null;
            var retiredTimer = restoreTimer;
            restoreTimer = null;
            restoreTimerState = null;
            return retiredTimer;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(DtrWakeController));
        }

        private static bool IsCurrent(Func<bool> predicate)
        {
            try
            {
                return predicate?.Invoke() == true;
            }
            catch
            {
                return false;
            }
        }

        private static void DisposeTimer(Timer timer)
        {
            try { timer?.Dispose(); }
            catch { }
        }

        private static void Log(string message)
        {
            try { Tools.Logger.AddUartLogDebug(message); }
            catch { }
        }

        public void Dispose()
        {
            Timer retiredTimer;
            lock (lineOperationLock)
            lock (stateLock)
            {
                if (disposed)
                    return;
                disposed = true;
                retiredTimer = ClearSessionUnsafe();
            }
            DisposeTimer(retiredTimer);
        }

        internal static bool ProbeDtrWakeLifecycleBehavior()
        {
            var profile = new UartPortProfile
            {
                dtrWakeBeforeSend = true,
                dtrWakeDelayMs = 0,
                dtrWakeIdleMs = 10000
            };

            var receiveController = new DtrWakeController();
            var receiveLine = new ProbeDtrLine(false);
            var receiveKey = new object();
            var receiveCurrent = true;
            var receiveSendCount = 0;
            var assertedDuringSend = false;
            receiveController.ExecuteWithWakeCore(
                receiveKey,
                7,
                () => receiveCurrent,
                profile,
                CancellationToken.None,
                () =>
                {
                    receiveSendCount++;
                    assertedDuringSend = receiveLine.Enabled;
                },
                receiveLine);
            var firstReceiveTimer = receiveController.restoreTimerState;
            receiveController.RenewAfterReceiveCore(
                receiveKey,
                7,
                () => receiveCurrent,
                profile,
                receiveLine);
            var renewedReceiveTimer = receiveController.restoreTimerState;
            receiveController.RestoreAfterIdle(firstReceiveTimer);
            var obsoleteTimerIgnored = receiveLine.Enabled;
            receiveController.RestoreAfterIdle(renewedReceiveTimer);
            var receiveEventuallyRestored = !receiveLine.Enabled;
            receiveController.Dispose();

            var manualController = new DtrWakeController();
            var manualLine = new ProbeDtrLine(false);
            var manualKey = new object();
            manualController.ExecuteWithWakeCore(
                manualKey,
                11,
                () => true,
                profile,
                CancellationToken.None,
                () => manualController.ApplyUserDtrCore(manualKey, 11, manualLine, true),
                manualLine);
            var manualChangePreserved = manualLine.Enabled && manualController.activeSession == null;
            manualController.Dispose();

            var staleController = new DtrWakeController();
            var staleLine = new ProbeDtrLine(false);
            var staleKey = new object();
            var staleCurrent = true;
            staleController.ExecuteWithWakeCore(
                staleKey,
                13,
                () => staleCurrent,
                profile,
                CancellationToken.None,
                () => { },
                staleLine);
            var staleTimer = staleController.restoreTimerState;
            staleCurrent = false;
            staleController.InvalidateConnectionCore(staleKey, 13);
            var staleSetCount = staleLine.SetCount;
            staleController.RestoreAfterIdle(staleTimer);
            var staleTimerDidNotTouchRetiredConnection =
                staleLine.SetCount == staleSetCount && staleLine.Enabled;
            staleController.Dispose();

            var throwingController = new DtrWakeController();
            var throwingLine = new ProbeDtrLine(false);
            var throwingKey = new object();
            var throwingSendCount = 0;
            try
            {
                throwingController.ExecuteWithWakeCore(
                    throwingKey,
                    17,
                    () => true,
                    profile,
                    CancellationToken.None,
                    () =>
                    {
                        throwingSendCount++;
                        throw new IOException("probe write failure");
                    },
                    throwingLine);
            }
            catch (IOException)
            {
            }
            var throwingTimer = throwingController.restoreTimerState;
            if (throwingTimer != null)
                throwingController.RestoreAfterIdle(throwingTimer);
            var failedSendWasNotRetried = throwingSendCount == 1 && !throwingLine.Enabled;
            throwingController.Dispose();

            var canceledController = new DtrWakeController();
            var canceledLine = new ProbeDtrLine(false);
            var canceledSendCount = 0;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    canceledController.ExecuteWithWakeCore(
                        new object(),
                        19,
                        () => true,
                        profile,
                        cancellation.Token,
                        () => canceledSendCount++,
                        canceledLine);
                }
                catch (OperationCanceledException)
                {
                }
            }
            var cancellationPreventedWakeAndSend =
                canceledSendCount == 0 && !canceledLine.Enabled;
            canceledController.Dispose();

            var disabledController = new DtrWakeController();
            var disabledLine = new ProbeDtrLine(false);
            var disabledObservedDtr = true;
            var disabledProfile = new UartPortProfile { dtrWakeBeforeSend = false };
            disabledController.ExecuteWithWakeCore(
                new object(),
                23,
                () => true,
                disabledProfile,
                CancellationToken.None,
                () => disabledObservedDtr = disabledLine.Enabled,
                disabledLine);
            disabledController.Dispose();

            return receiveSendCount == 1 &&
                assertedDuringSend &&
                firstReceiveTimer != null &&
                renewedReceiveTimer != null &&
                !ReferenceEquals(firstReceiveTimer, renewedReceiveTimer) &&
                obsoleteTimerIgnored &&
                receiveEventuallyRestored &&
                manualChangePreserved &&
                staleTimerDidNotTouchRetiredConnection &&
                failedSendWasNotRetried &&
                cancellationPreventedWakeAndSend &&
                !disabledObservedDtr;
        }
    }

    internal sealed class UartSendEventArgs : EventArgs
    {
        internal UartSendEventArgs(string sessionStringLogOverride)
            : this(sessionStringLogOverride, null)
        {
        }

        internal UartSendEventArgs(string sessionStringLogOverride, string portName)
        {
            SessionStringLogOverride = sessionStringLogOverride;
            PortName = portName;
        }

        internal string SessionStringLogOverride { get; }
        internal string PortName { get; }
    }

    internal sealed class UartReceiveEventArgs : EventArgs
    {
        internal UartReceiveEventArgs(Uart.ConnectionLease connection)
        {
            Connection = connection;
        }

        internal Uart.ConnectionLease Connection { get; }
        internal bool IsCurrent => Connection?.IsCurrent == true;
        internal UartPortProfile Profile => Connection?.Profile;
    }

    class Uart
    {
        private const int SerialWriteTimeoutMilliseconds = 5000;
        private const int SerialDisposeTimeoutMilliseconds = 5000;
        private const int MaxRetiredSerialPorts = 8;

        //废弃的串口对象，存放处，尝试fix[System.ObjectDisposedException: 已关闭 Safe handle]
        //https://drdump.com/Problem.aspx?ProblemID=524533
        private List<SerialPort> useless = new List<SerialPort>();

        public SerialPort serial = new SerialPort();
        public event EventHandler UartDataRecived;
        public event EventHandler UartDataSent;
        public event EventHandler UartDataRawSent;
        private Stream lastPortBaseStream = null;
        private readonly object sendLock = new object();
        private readonly object receiveLock = new object();
        private readonly object lifecycleLock = new object();
        private readonly object dtrSettingLock = new object();
        private readonly DtrWakeController dtrWakeController = new DtrWakeController();
        private readonly SerialSendQueue uiSendQueue = new SerialSendQueue();
        private volatile bool directReceiveMode;
        private int directReceiveScheduled;
        private long connectionGeneration;
        private bool isShuttingDown;
        private bool _rts = false;
        // DTR 的上升沿会让不少 USB 串口设备复位。默认保持关闭，确实需要
        // DTR 的设备再由用户按端口开启，避免“写入成功但设备正在重启”。
        private bool _dtr = false;
        private Tools.SerialPinMonitor pinMonitor;
        private UartPortProfile runtimeProfileOverride;

        internal void SetRuntimeProfileOverride(UartPortProfile profile)
        {
            Volatile.Write(
                ref runtimeProfileOverride,
                profile == null ? null : Settings.CreateNormalizedUartProfileSnapshot(profile));
            ApplyFlowControl();
        }

        internal void ClearRuntimeProfileOverride()
        {
            Volatile.Write(ref runtimeProfileOverride, null);
            ApplyFlowControl();
        }

        private UartPortProfile GetRuntimeProfile()
        {
            return Volatile.Read(ref runtimeProfileOverride) ??
                Tools.Global.setting?.GetCurrentUartProfileSnapshot() ??
                new UartPortProfile();
        }

        internal int GetRuntimeEncodingCodePage()
        {
            return GetRuntimeProfile().encoding;
        }

        internal sealed class ConnectionLease
        {
            private readonly Uart owner;

            internal ConnectionLease(
                Uart owner,
                SerialPort port,
                long generation,
                string portName,
                UartPortProfile profile)
            {
                this.owner = owner;
                Port = port;
                Generation = generation;
                PortName = portName ?? string.Empty;
                Profile = Settings.CreateNormalizedUartProfileSnapshot(profile);
            }

            internal SerialPort Port { get; }
            internal long Generation { get; }
            internal string PortName { get; }
            internal UartPortProfile Profile { get; }
            internal string Identity => $"uart:{PortName}:{Generation}";
            internal string DisplayName => string.IsNullOrWhiteSpace(PortName) ? "Serial" : PortName;
            internal bool IsOpen => owner.IsConnectionOpen(this);
            internal bool IsCurrent => owner.IsConnectionCurrent(this);

            internal bool Send(
                byte[] data,
                CancellationToken token,
                Action<int> committedBytes,
                bool raiseEvents,
                byte[] dataRaw = null,
                string sessionStringLogOverride = null)
            {
                if (data == null)
                    throw new ArgumentNullException(nameof(data));
                if (!IsOpen)
                    return false;
                if (data.Length == 0)
                    return true;

                owner.SendDataForLease(this, data, token, dataRaw, raiseEvents, committedBytes, sessionStringLogOverride);
                return true;
            }

            internal Task<bool> SendAsync(
                byte[] data,
                CancellationToken token,
                Action<int> committedBytes,
                bool raiseEvents,
                byte[] dataRaw = null,
                string sessionStringLogOverride = null)
            {
                if (data == null)
                    throw new ArgumentNullException(nameof(data));
                // Queue ownership includes the payload: subsequent editor/script
                // mutations must not alter a send already accepted by the UI.
                var capturedData = (byte[])data.Clone();
                var capturedRaw = dataRaw == null ? null : (byte[])dataRaw.Clone();
                return owner.uiSendQueue.Enqueue(
                    () => Send(capturedData, token, committedBytes, raiseEvents, capturedRaw, sessionStringLogOverride),
                    capturedData.LongLength + (capturedRaw?.LongLength ?? 0));
            }
        }

        internal ConnectionLease CaptureConnectionLease()
        {
            lock (lifecycleLock)
            {
                var port = serial;
                var portName = string.Empty;
                try { portName = port?.PortName ?? string.Empty; }
                catch (Exception ex) when (IsClosedSerialException(ex)) { }
                return new ConnectionLease(
                    this,
                    port,
                    connectionGeneration,
                    portName,
                    GetRuntimeProfile());
            }
        }

        private ConnectionLease CaptureConnectionLease(SerialPort expectedPort)
        {
            lock (lifecycleLock)
            {
                if (expectedPort == null || !ReferenceEquals(expectedPort, serial) || isShuttingDown)
                    return null;

                var portName = string.Empty;
                try { portName = expectedPort.PortName ?? string.Empty; }
                catch (Exception ex) when (IsClosedSerialException(ex)) { }
                return new ConnectionLease(
                    this,
                    expectedPort,
                    connectionGeneration,
                    portName,
                    GetRuntimeProfile());
            }
        }

        private bool IsConnectionOpen(ConnectionLease lease)
        {
            lock (lifecycleLock)
            {
                if (!IsConnectionCurrentUnsafe(lease))
                    return false;

                try { return lease.Port?.IsOpen == true; }
                catch (Exception ex) when (IsClosedSerialException(ex)) { return false; }
            }
        }

        private bool IsConnectionCurrentUnsafe(ConnectionLease lease)
        {
            return lease != null &&
                !isShuttingDown &&
                lease.Generation == connectionGeneration &&
                ReferenceEquals(lease.Port, serial);
        }

        private bool IsConnectionCurrent(ConnectionLease lease)
        {
            lock (lifecycleLock)
                return IsConnectionCurrentUnsafe(lease);
        }

        public bool Rts
        {
            get
            {
                return _rts;
            }
            set
            {
                _rts = value;
                if (!IsHardwareFlowControl())
                    TryApplyControlLine(port => port.RtsEnable = value);
            }
        }
        public bool Dtr
        {
            get
            {
                lock (dtrSettingLock)
                    return _dtr;
            }
            set
            {
                lock (lifecycleLock)
                lock (dtrSettingLock)
                {
                    _dtr = value;
                    try
                    {
                        dtrWakeController.ApplyUserDtr(
                            serial,
                            Interlocked.Read(ref connectionGeneration),
                            value);
                    }
                    catch (Exception ex) when (IsClosedSerialException(ex))
                    {
                    }
                }
            }
        }

        internal void SetConfiguredDtr(bool value)
        {
            lock (lifecycleLock)
            lock (dtrSettingLock)
            {
                _dtr = value;
                try
                {
                    dtrWakeController.ApplyConfiguredDtr(serial, connectionGeneration, value);
                }
                catch (Exception ex) when (IsClosedSerialException(ex)) { }
            }
        }

        private static readonly object objLock = new object();
        
        /// <summary>
        /// 初始化串口各个触发函数
        /// </summary>
        public Uart()
        {
            //声明接收到事件
            serial.DataReceived += Serial_DataReceived;
            ConfigureSerialDevice(serial);
            AttachPinMonitor(serial);
            var readThread = new Thread(ReadData)
            {
                IsBackground = true
            };
            readThread.Start();

            // Script sends resolve the active serial target at call time. The returned
            // lease then pins the chosen page/slot/connection for this one send.
            ScriptApis.SendChannelsRegister("uart", (data, _) =>
            {
                if (data == null)
                    return false;

                var target = Tools.Global.CaptureActiveSerialTarget();
                return target?.IsOpen == true &&
                    target.Send(data, CancellationToken.None, null);
            });
        }

        /// <summary>
        /// The first split pane keeps using the already-open main UART. While the split
        /// page is visible, read it directly from DataReceived just like the independently
        /// owned split ports. This avoids mixing the main UART's delayed wait-handle reader
        /// with direct readers from the other panes without closing or reopening the port.
        /// </summary>
        public void SetDirectReceiveMode(bool enabled)
        {
            if (directReceiveMode == enabled)
                return;

            directReceiveMode = enabled;
            lock (receiveLock)
                pendingReceivePort = null;

            // Wake a pending delayed read so it can observe the new mode immediately.
            WaitUartReceive.Set();
            if (enabled)
            {
                var currentPort = serial;
                if (currentPort != null &&
                    Interlocked.CompareExchange(ref directReceiveScheduled, 1, 0) == 0)
                {
                    ThreadPool.QueueUserWorkItem(_ => ReadDataDirect(currentPort));
                }
            }
            Tools.Logger.AddUartLogDebug($"[UartReceive]direct mode={enabled}");
        }

        /// <summary>
        /// 刷新串口对象
        /// </summary>
        private void refreshSerialDevice(bool waitForDispose = false)
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown)
                    return;

                RefreshSerialDeviceCore(waitForDispose);
            }
        }

        private void RefreshSerialDeviceCore(bool waitForDispose)
        {
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]start");
            var retiredGeneration = connectionGeneration;
            connectionGeneration = unchecked(connectionGeneration + 1);
            if (connectionGeneration == 0)
                connectionGeneration = 1;
            var oldSerial = serial;
            dtrWakeController.InvalidateConnection(oldSerial, retiredGeneration);
            var oldBaseStream = lastPortBaseStream;
            lastPortBaseStream = null;
            pinMonitor?.Dispose();
            pinMonitor = null;
            try
            {
                if (oldSerial != null)
                    oldSerial.DataReceived -= Serial_DataReceived;
            }
            catch (Exception e)
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]sync DataReceived unsubscribe error:{e.Message}");
            }
            lock (receiveLock)
            {
                if (ReferenceEquals(pendingReceivePort, oldSerial))
                    pendingReceivePort = null;
            }
            DisposeSerialResources(oldSerial, oldBaseStream, waitForDispose);
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]new");
            lock(useless)//短暂保留旧对象，避免系统回调还在引用；同时限制数量避免长期增长。
            {
                useless.Add(oldSerial);
                while (useless.Count > MaxRetiredSerialPorts)
                    useless.RemoveAt(0);
            }
            serial = new SerialPort();
            //声明接收到事件
            serial.DataReceived += Serial_DataReceived;
            ConfigureSerialDevice(serial);
            AttachPinMonitor(serial);
            Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]done");
        }

        private void AttachPinMonitor(SerialPort port)
        {
            pinMonitor?.Dispose();
            pinMonitor = port == null
                ? null
                : new Tools.SerialPinMonitor(port, Tools.Global.NotifySerialPinStatusChanged);
        }

        private void DisposeSerialResources(SerialPort port, Stream baseStream, bool waitForDispose)
        {
            Action dispose = () =>
            {
                try
                {
                    if (port != null)
                        port.DataReceived -= Serial_DataReceived;
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.DataReceived unsubscribe error:{e.Message}");
                }

                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Close");
                    if (port?.IsOpen == true)
                        port.Close();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Close error:{e.Message}");
                }

                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Dispose");
                    port?.Dispose();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]SerialPort.Dispose error:{e.Message}");
                }

                try
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose");
                    baseStream?.Dispose();
                }
                catch (Exception e)
                {
                    Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]BaseStream.Dispose error:{e.Message}");
                }
            };

            var task = Task.Run(dispose);
            if (waitForDispose && !task.Wait(SerialDisposeTimeoutMilliseconds))
            {
                Tools.Logger.AddUartLogDebug($"[refreshSerialDevice]dispose timeout {SerialDisposeTimeoutMilliseconds}ms");
                throw new TimeoutException($"串口释放超过 {SerialDisposeTimeoutMilliseconds} 毫秒，后台仍在尝试释放。");
            }
        }

        private void ConfigureSerialDevice(SerialPort port)
        {
            if (port == null)
                return;

            var profile = GetRuntimeProfile();
            port.BaudRate = profile.baudRate;
            port.Parity = (Parity)profile.parity;
            port.DataBits = profile.dataBits;
            port.StopBits = (StopBits)profile.stopBit;

            port.WriteTimeout = SerialWriteTimeoutMilliseconds;
            ApplyControlLines(port);
        }

        public void ApplyFlowControl()
        {
            lock (lifecycleLock)
                ApplyControlLines(serial);
        }

        private void ApplyControlLines(SerialPort port)
        {
            if (port == null)
                return;

            try
            {
                var handshake = GetHandshake();
                if (handshake == Handshake.RequestToSend)
                {
                    try
                    {
                        port.RtsEnable = false;
                    }
                    catch
                    {
                    }
                }
                port.Handshake = handshake;
                if (handshake != Handshake.RequestToSend)
                    port.RtsEnable = Rts;
                dtrWakeController.ApplyConfiguredDtr(
                    port,
                    Interlocked.Read(ref connectionGeneration),
                    Dtr);
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
            }
        }

        private void TryApplyControlLine(Action<SerialPort> apply)
        {
            try
            {
                var port = serial;
                if (port != null)
                    apply(port);
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
            }
        }

        private void SetSerialOption(Action<SerialPort> apply)
        {
            try
            {
                apply(serial);
            }
            catch (ObjectDisposedException)
            {
                refreshSerialDevice(waitForDispose: false);
                apply(serial);
            }
        }

        public void SetBaudRate(int baudRate)
        {
            SetSerialOption(port => port.BaudRate = baudRate);
        }

        public void SetParity(Parity parity)
        {
            SetSerialOption(port => port.Parity = parity);
        }

        public void SetDataBits(int dataBits)
        {
            SetSerialOption(port => port.DataBits = dataBits);
        }

        public void SetStopBits(StopBits stopBits)
        {
            SetSerialOption(port => port.StopBits = stopBits);
        }

        private bool IsHardwareFlowControl()
        {
            return GetRuntimeProfile().flowControl == 1;
        }

        private Handshake GetHandshake()
        {
            switch (GetRuntimeProfile().flowControl)
            {
                case 1:
                    return Handshake.RequestToSend;
                case 2:
                    return Handshake.XOnXOff;
                default:
                    return Handshake.None;
            }
        }

        /// <summary>
        /// 获取串口设备COM名
        /// </summary>
        /// <returns></returns>
        public string GetName()
        {
            try
            {
                return serial?.PortName ?? "";
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
                return "";
            }
        }

        /// <summary>
        /// 设置串口设备COM名
        /// </summary>
        /// <returns></returns>
        public void SetName(string s)
        {
            try
            {
                serial.PortName = s;
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
                refreshSerialDevice(waitForDispose: false);
                serial.PortName = s;
            }
        }

        /// <summary>
        /// 查看串口打开状态
        /// </summary>
        /// <returns></returns>
        public bool IsOpen()
        {
            try
            {
                return serial?.IsOpen == true;
            }
            catch (Exception ex) when (IsClosedSerialException(ex))
            {
                return false;
            }
        }

        /// <summary>
        /// 开启串口
        /// </summary>
        public void Open()
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown || Tools.Global.isMainWindowsClosed)
                    throw new ObjectDisposedException(nameof(Uart), "程序正在退出，不能再打开串口。");

                string temp = GetName();
                Tools.Logger.AddUartLogDebug($"[UartOpen]refreshSerialDevice");
                refreshSerialDevice();
                serial.PortName = temp;
                Tools.Logger.AddUartLogDebug($"[UartOpen]open");
                serial.Open();
                lastPortBaseStream = serial.BaseStream;
                pinMonitor?.Arm();
                Tools.Logger.AddUartLogDebug($"[UartOpen]done");
            }
        }

        /// <summary>
        /// 关闭串口
        /// </summary>
        public void Close(bool waitForDispose = false)
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown)
                    return;

                Tools.Logger.AddUartLogDebug($"[UartClose]refreshSerialDevice");
                refreshSerialDevice(waitForDispose);
                WaitUartReceive.Set();
                Tools.Logger.AddUartLogDebug($"[UartClose]done");
            }
        }

        public void Shutdown()
        {
            lock (lifecycleLock)
            {
                if (isShuttingDown)
                    return;

                isShuttingDown = true;
                uiSendQueue.Stop();
                var retiredGeneration = connectionGeneration;
                connectionGeneration = unchecked(connectionGeneration + 1);
                if (connectionGeneration == 0)
                    connectionGeneration = 1;
                var oldSerial = serial;
                dtrWakeController.InvalidateConnection(oldSerial, retiredGeneration);
                var oldBaseStream = lastPortBaseStream;
                pinMonitor?.Dispose();
                pinMonitor = null;
                serial = null;
                lastPortBaseStream = null;
                lock (receiveLock)
                    pendingReceivePort = null;

                DisposeSerialResources(oldSerial, oldBaseStream, waitForDispose: true);
                dtrWakeController.Dispose();
                WaitUartReceive.Set();
                Tools.Logger.AddUartLogDebug("[UartShutdown]all serial resources released");
            }
        }

        /// <summary>
        /// 发送数据
        /// </summary>
        /// <param name="data">数据内容</param>
        public void SendData(byte[] data, byte[] dataRaw = null, bool raiseEvents = true)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            SendDataForLease(
                CaptureConnectionLease(),
                data,
                CancellationToken.None,
                dataRaw,
                raiseEvents,
                null);
        }

        public void SendDataCancelable(byte[] data, CancellationToken cancellationToken, byte[] dataRaw = null, bool raiseEvents = true)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            SendDataForLease(
                CaptureConnectionLease(),
                data,
                cancellationToken,
                dataRaw,
                raiseEvents,
                null);
        }

        private void SendDataForLease(
            ConnectionLease lease,
            byte[] data,
            CancellationToken cancellationToken,
            byte[] dataRaw,
            bool raiseEvents,
            Action<int> committedBytes,
            string sessionStringLogOverride = null)
        {
            var profile = lease?.Profile ?? new UartPortProfile();
            lock (sendLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Tools.SerialTraceHub.RecordEvent(lease?.PortName, lease?.Identity, Tools.SerialTraceKind.Info,
                    $"TX request / 发送请求: {data.Length} bytes, baud={profile.baudRate}, flow={profile.flowControl}, DTR={profile.dtr}, RTS={profile.rts}, wake={profile.dtrWakeBeforeSend}");
                try
                {
                dtrWakeController.ExecuteWithWake(
                    lease?.Port,
                    lease?.Generation ?? 0,
                    () => IsConnectionOpen(lease),
                    profile,
                    cancellationToken,
                    () => WriteData(lease, data, profile, cancellationToken, committedBytes),
                    message => Tools.SerialTraceHub.RecordEvent(lease?.PortName, lease?.Identity,
                        Tools.SerialTraceKind.Wake, message));
                }
                catch (Exception ex)
                {
                    Tools.SerialTraceHub.RecordEvent(lease?.PortName, lease?.Identity,
                        ex is OperationCanceledException ? Tools.SerialTraceKind.Info : Tools.SerialTraceKind.Error,
                        "TX incomplete / 发送未完成: " + ex.Message);
                    throw;
                }
                Tools.Global.setting.SentCount += data.Length;
            }

            if (!raiseEvents)
                return;

            // External subscribers are invoked after the send serialization lock is released.
            if (dataRaw != null && profile.showSend && ByteArrayEquals(dataRaw, data))
                dataRaw = null;
            if (dataRaw != null && profile.showSendRaw)
                UartDataRawSent?.Invoke(dataRaw, EventArgs.Empty);
            if (profile.showSend)
                UartDataSent?.Invoke(data, new UartSendEventArgs(sessionStringLogOverride, lease?.PortName));
        }

        private void WriteData(
            ConnectionLease lease,
            byte[] data,
            UartPortProfile profile,
            CancellationToken cancellationToken,
            Action<int> committedBytes)
        {
            profile = profile ?? new UartPortProfile();
            var packetSize = Math.Max(0, profile.sendThrottlePacketSize);
            var delayMs = Math.Max(0, profile.sendThrottleDelayMs);
            if (packetSize == 0 || delayMs == 0 || data.Length <= packetSize)
            {
                WritePort(lease, data, 0, data.Length, cancellationToken, committedBytes);
                return;
            }

            for (int offset = 0; offset < data.Length; offset += packetSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(packetSize, data.Length - offset);
                WritePort(lease, data, offset, count, cancellationToken, committedBytes);
                if (delayMs > 0 && offset + count < data.Length &&
                    cancellationToken.WaitHandle.WaitOne(delayMs))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private void WritePort(
            ConnectionLease lease,
            byte[] data,
            int offset,
            int count,
            CancellationToken cancellationToken,
            Action<int> committedBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = lease?.Port;
            var portName = lease?.PortName ?? string.Empty;
            var invokingCommittedCallback = false;

            try
            {
                if (port == null)
                    throw new IOException("Serial port is not open.");

                string diagnostic;
                try
                {
                    diagnostic =
                        $"[UartWrite]port={portName},baud={port.BaudRate},parity={port.Parity}," +
                        $"dataBits={port.DataBits},stopBits={port.StopBits},handshake={port.Handshake}," +
                        $"dtr={SafeGetDtr(port)},rts={SafeGetRts(port)},bytes={count},generation={lease.Generation}";
                }
                catch
                {
                    diagnostic = $"[UartWrite]port={portName},bytes={count},generation={lease.Generation}";
                }
                Tools.Logger.AddUartLogDebug(diagnostic);

                // lifecycleLock protects only the actual Write and its identity check.
                // Close/reopen may proceed while the driver buffer is draining.
                lock (lifecycleLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsConnectionCurrentUnsafe(lease) || !port.IsOpen)
                        throw new IOException("The captured serial connection is no longer open.");
                    port.Write(data, offset, count);
                }

                Tools.SerialTraceHub.RecordBuffer(portName, lease.Identity, true,
                    data, offset, count, lease.Profile.encoding);
                invokingCommittedCallback = true;
                committedBytes?.Invoke(count);
                invokingCommittedCallback = false;

                WaitForWriteDrain(lease, count, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception) when (invokingCommittedCallback)
            {
                throw;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Serial write canceled.", ex, cancellationToken);
            }
            catch (Exception ex) when (IsClosedSerialException(UnwrapSerialException(ex)))
            {
                var cause = UnwrapSerialException(ex);
                var notifyClosed = false;
                lock (lifecycleLock)
                {
                    // A delayed failure from a retired SerialPort must never replace or
                    // close the newer connection that now occupies Global.uart.serial.
                    if (IsConnectionCurrentUnsafe(lease))
                    {
                        RefreshSerialDeviceCore(waitForDispose: false);
                        notifyClosed = true;
                    }
                }

                if (notifyClosed)
                    Tools.Global.NotifyUartPortClosed(portName);
                throw new IOException("Serial port was closed while writing.", cause);
            }
        }

        private void WaitForWriteDrain(
            ConnectionLease lease,
            int byteCount,
            CancellationToken cancellationToken)
        {
            var port = lease.Port;
            var baudRate = Math.Max(1, port.BaudRate);
            var estimatedMilliseconds = (long)Math.Ceiling(byteCount * 11000d / baudRate);
            var timeoutMilliseconds = Math.Max(SerialWriteTimeoutMilliseconds, estimatedMilliseconds + 2000L);
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsConnectionCurrent(lease))
                    throw new IOException("The captured serial connection changed while draining.");
                if (port.BytesToWrite <= 0)
                    return;
                if (stopwatch.ElapsedMilliseconds > timeoutMilliseconds)
                    throw new TimeoutException("等待串口发送缓冲区清空超时。");
                Thread.Sleep(2);
            }
        }

        private static Exception UnwrapSerialException(Exception ex)
        {
            return ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
        }

        private static bool SafeGetDtr(SerialPort port)
        {
            try { return port?.DtrEnable == true; }
            catch { return false; }
        }

        private static bool SafeGetRts(SerialPort port)
        {
            try { return port?.RtsEnable == true; }
            catch { return false; }
        }

        private static bool IsClosedSerialException(Exception ex)
        {
            return ex is ObjectDisposedException ||
                ex is IOException ||
                ex is InvalidOperationException;
        }

        private static bool ByteArrayEquals(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }

        //收到串口事件的信号量
        public EventWaitHandle WaitUartReceive = new AutoResetEvent(false);
        private SerialPort pendingReceivePort = null;
        //接收到事件
        private void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var eventPort = sender as SerialPort;
            RenewDtrWakeAfterReceive(eventPort);
            if (directReceiveMode)
            {
                if (eventPort == null || !ReferenceEquals(eventPort, serial))
                {
                    Tools.Logger.AddUartLogDebug("[UartReceive]ignored stale direct DataReceived callback");
                    return;
                }

                if (Interlocked.CompareExchange(ref directReceiveScheduled, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(_ => ReadDataDirect(eventPort));
                return;
            }

            lock (receiveLock)
            {
                // SerialPort.Close/Dispose 之后，驱动仍可能补发旧对象的回调。
                // 旧回调绝不能覆盖当前串口，否则当前串口已经触发的接收信号会被读错对象。
                if (eventPort == null || !ReferenceEquals(eventPort, serial))
                {
                    Tools.Logger.AddUartLogDebug("[UartReceive]ignored stale DataReceived callback");
                    return;
                }
                pendingReceivePort = eventPort;
            }
            WaitUartReceive.Set();
        }

        private void ReadDataDirect(SerialPort eventPort)
        {
            try
            {
                var connection = CaptureConnectionLease(eventPort);
                if (connection == null)
                    return;
                var profile = connection.Profile;
                var timeout = profile.timeout;
                Thread.Sleep(timeout > 0 ? timeout : 10);
                if (!directReceiveMode || Tools.Global.isMainWindowsClosed)
                    return;

                var result = new List<byte>();
                while (directReceiveMode)
                {
                    try
                    {
                        byte[] traceBlock;
                        int traceCount;
                        lock (receiveLock)
                        {
                            if (!directReceiveMode ||
                                eventPort == null ||
                                !ReferenceEquals(eventPort, serial) ||
                                connection.Generation != Interlocked.Read(ref connectionGeneration) ||
                                !eventPort.IsOpen)
                            {
                                break;
                            }

                            var length = eventPort.BytesToRead;
                            if (length <= 0)
                                break;

                            var block = new byte[Math.Min(length, 65536)];
                            var read = eventPort.Read(block, 0, block.Length);
                            if (read <= 0)
                                break;
                            if (read == block.Length)
                                result.AddRange(block);
                            else
                                result.AddRange(block.Take(read));
                            traceBlock = block;
                            traceCount = read;
                        }
                        Tools.SerialTraceHub.RecordBuffer(connection.PortName, connection.Identity,
                            false, traceBlock, 0, traceCount, profile.encoding);
                        RenewDtrWakeAfterReceive(eventPort);
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.AddUartLogDebug($"[UartReceive]direct read error:{ex.Message}");
                        break;
                    }

                    if (result.Count > profile.maxLength)
                        break;

                    if (profile.bitDelay && timeout > 0)
                        Thread.Sleep(timeout);
                    else if (timeout < 0)
                        Thread.Sleep(10);
                }

                PublishReceivedData(result, connection);
            }
            finally
            {
                Interlocked.Exchange(ref directReceiveScheduled, 0);

                // Data may have arrived after the final BytesToRead check while this
                // callback was still marked as scheduled. Drain it without waiting for
                // another driver notification.
                try
                {
                    var currentPort = serial;
                    if (directReceiveMode &&
                        currentPort != null &&
                        currentPort.IsOpen &&
                        currentPort.BytesToRead > 0 &&
                        Interlocked.CompareExchange(ref directReceiveScheduled, 1, 0) == 0)
                    {
                        ThreadPool.QueueUserWorkItem(_ => ReadDataDirect(currentPort));
                    }
                }
                catch
                {
                }
            }
        }

        private void PublishReceivedData(ICollection<byte> result, ConnectionLease connection)
        {
            if (result == null || result.Count == 0 || Tools.Global.isMainWindowsClosed ||
                !IsConnectionCurrent(connection))
                return;

            Tools.Global.setting.ReceivedCount += result.Count;
            var data = result.ToArray();
            var eventArgs = new UartReceiveEventArgs(connection);
            var handlers = UartDataRecived;
            if (handlers != null)
            {
                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    try
                    {
                        if (!eventArgs.IsCurrent)
                            break;
                        handler(data, eventArgs);
                    }
                    catch (Exception ex)
                    {
                        // One UI/log subscriber must not prevent the split-pane subscriber
                        // from receiving the same bytes.
                        Tools.Logger.AddUartLogDebug(
                            $"[UartReceive]subscriber {handler.Method.Name} error:{ex.Message}");
                    }
                }
            }
        }

        private void RenewDtrWakeAfterReceive(SerialPort eventPort)
        {
            // Called before packet aggregation and after each actual read, always
            // outside receiveLock (close takes lifecycleLock before receiveLock).
            var lease = CaptureConnectionLease(eventPort);
            if (lease == null)
                return;
            dtrWakeController.RenewAfterReceive(
                lease.Port,
                lease.Generation,
                () => IsConnectionOpen(lease),
                lease.Profile);
        }

        /// <summary>
        /// 单独开个线程接收数据
        /// </summary>
        private void ReadData()
        {
            WaitUartReceive.Reset();
            while (true)
            {
                WaitUartReceive.WaitOne();
                if (Tools.Global.isMainWindowsClosed)
                    return;
                // A packet belongs to exactly one connection. Never reselect serial
                // inside the aggregation loop: a COM switch may happen during Sleep.
                var connection = CaptureConnectionLease();
                var readPort = connection.Port;
                var profile = connection.Profile;
                if (profile.timeout > 0)
                    System.Threading.Thread.Sleep(profile.timeout);//等待时间
                else
                    System.Threading.Thread.Sleep(10);//等待时间默认给个10ms吧，防止中文被分割
                if (directReceiveMode)
                    continue;
                if (Tools.Global.isMainWindowsClosed)
                    return;
                List<byte> result = new List<byte>();
                while (true)//循环读
                {
                    try
                    {
                        byte[] traceBlock;
                        int traceCount;
                        lock (receiveLock)
                        {
                            if (directReceiveMode ||
                                !ReferenceEquals(readPort, serial) ||
                                connection.Generation != Interlocked.Read(ref connectionGeneration))
                                break;

                            if (readPort == null || !readPort.IsOpen)//串口被关了，不读了
                                break;

                            int length = readPort.BytesToRead;
                            if (length == 0)//没数据，退出去
                                break;
                            byte[] rev = new byte[Math.Min(length, 65536)];
                            var read = readPort.Read(rev, 0, rev.Length);//读数据
                            if (read <= 0)
                                break;
                            if (read == rev.Length)
                                result.AddRange(rev);//加到list末尾
                            else
                                result.AddRange(rev.Take(read));
                            traceBlock = rev;
                            traceCount = read;
                        }
                        Tools.SerialTraceHub.RecordBuffer(connection.PortName, connection.Identity,
                            false, traceBlock, 0, traceCount, profile.encoding);
                        RenewDtrWakeAfterReceive(readPort);
                    }
                    catch (Exception ex)
                    {
                        Tools.Logger.AddUartLogDebug($"[UartReceive]read error:{ex.Message}");
                        break;
                    }

                    if (result.Count > profile.maxLength)//长度超了
                        break;
                    if (profile.bitDelay && profile.timeout > 0)//如果是设置了等待间隔时间
                    {
                        System.Threading.Thread.Sleep(profile.timeout);//等待时间
                    }
                    else if (profile.timeout < 0)//如果是设置了等待间隔时间
                    {
                        System.Threading.Thread.Sleep(10);//等待时间默认给个10ms吧，防止中文被分割
                    }
                }
                if (Tools.Global.isMainWindowsClosed)
                    return;
                PublishReceivedData(result, connection);
            }
        }
    }
}
