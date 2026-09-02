using Jint;
using Jint.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.ScriptEnv
{
    class JavaScriptRunEnv
    {
        internal const int MaxQueuedEventCount = 512;
        internal const long MaxQueuedPayloadBytes = 4L * 1024 * 1024;
        internal const string QueueDropPolicy = "DropNewest";
        private const int WorkerStopWaitMilliseconds = 1500;
        private const long UnknownPayloadBytes = 64;

        /// <summary>
        /// Kept for compatibility. This event is raised only when a session faults.
        /// A normal StopScript("") raises ScriptStopped instead.
        /// </summary>
        public static event EventHandler ScriptRunError;
        public static event EventHandler ScriptStopped;

        private static readonly ConcurrentDictionary<int, ScriptTimer> timerPool =
            new ConcurrentDictionary<int, ScriptTimer>();
        private static readonly object stateLock = new object();
        private static readonly object timerPoolLock = new object();
        private static RuntimeSession currentSession = null;
        private static long generation = 0;
        private static long droppedEventCount = 0;
        private static long droppedPayloadBytes = 0;

        public static bool isRunning = false;
        public static bool canRun = false;

        public static long DroppedEventCount => Interlocked.Read(ref droppedEventCount);
        public static long DroppedPayloadBytes => Interlocked.Read(ref droppedPayloadBytes);

        private static void AddTrigger(int id, string type = "timer", object data = null)
        {
            AddTrigger(GetCurrentSession(), id, type, data);
        }

        private static void AddTrigger(RuntimeSession session, int id, string type = "timer", object data = null)
        {
            if (!IsCurrentSession(session))
                return;

            session.TryEnqueue(id, type ?? string.Empty, data);
        }

        public static void RunCommand(string command)
        {
            AddTrigger(-1, "cmd", command ?? string.Empty);
        }

        public static void ChannelReceived(string channel, object data)
        {
            AddTrigger(-1, channel ?? string.Empty, data);
        }

        public static int StartTimer(int id, int time)
        {
            return StartTimer(GetCurrentSession(), id, time);
        }

        private static int StartTimer(RuntimeSession session, int id, int time)
        {
            if (!IsCurrentSession(session) || session.Token.IsCancellationRequested)
                return 0;

            ScriptTimer timer = null;
            ScriptTimer oldTimer = null;
            var registered = false;
            try
            {
                timer = new ScriptTimer(session, id, Math.Max(1, time));
                timer.Timer.Elapsed += (sender, e) => TimerElapsed(timer);

                lock (stateLock)
                {
                    if (!ReferenceEquals(currentSession, session) ||
                        generation != session.Generation ||
                        !isRunning ||
                        session.Token.IsCancellationRequested)
                    {
                        return 0;
                    }

                    lock (timerPoolLock)
                    {
                        timerPool.TryGetValue(id, out oldTimer);
                        timerPool[id] = timer;
                        registered = true;
                    }
                }

                oldTimer?.Dispose();
                if (!timer.TryStart())
                {
                    RemoveTimer(timer);
                    timer.Dispose();
                    return 0;
                }
                return 1;
            }
            finally
            {
                if (!registered)
                    timer?.Dispose();
            }
        }

        public static void StopTimer(int id)
        {
            StopTimer(GetCurrentSession(), id);
        }

        private static void StopTimer(RuntimeSession session, int id)
        {
            if (session == null)
                return;

            ScriptTimer timer = null;
            lock (timerPoolLock)
            {
                if (timerPool.TryGetValue(id, out var current) &&
                    ReferenceEquals(current.Session, session))
                {
                    timerPool.TryRemove(id, out timer);
                }
            }
            timer?.Dispose();
        }

        private static void TimerElapsed(ScriptTimer timer)
        {
            var wasCancelled = timer.Token.IsCancellationRequested;
            var shouldTrigger = RemoveTimer(timer);
            timer.Dispose();
            if (shouldTrigger && !wasCancelled)
                AddTrigger(timer.Session, timer.Id);
        }

        private static bool RemoveTimer(ScriptTimer timer)
        {
            lock (timerPoolLock)
            {
                if (!timerPool.TryGetValue(timer.Id, out var current) ||
                    !ReferenceEquals(current, timer))
                {
                    return false;
                }
                timerPool.TryRemove(timer.Id, out _);
                return true;
            }
        }

        private static void DisposeTimers(RuntimeSession session)
        {
            if (session == null)
                return;

            var timers = new List<ScriptTimer>();
            lock (timerPoolLock)
            {
                foreach (var item in timerPool)
                {
                    if (!ReferenceEquals(item.Value.Session, session))
                        continue;
                    if (timerPool.TryGetValue(item.Key, out var current) &&
                        ReferenceEquals(current, item.Value) &&
                        timerPool.TryRemove(item.Key, out var removed))
                    {
                        timers.Add(removed);
                    }
                }
            }

            foreach (var timer in timers)
                timer.Dispose();
        }

        public static void StopScript(string ex)
        {
            var session = GetCurrentSession();
            if (session != null)
            {
                StopScript(session, ex);
                return;
            }

            lock (stateLock)
            {
                isRunning = false;
                canRun = false;
            }
            ReportScriptStopped(ex);
        }

        private static bool StopScript(RuntimeSession session, string ex)
        {
            lock (stateLock)
            {
                if (!ReferenceEquals(currentSession, session) ||
                    generation != session.Generation)
                {
                    return false;
                }

                currentSession = null;
                generation++;
                isRunning = false;
                canRun = false;
            }

            RetireSession(session);
            ReportScriptStopped(ex);
            return true;
        }

        private static void ReportScriptStopped(string ex)
        {
            if (string.IsNullOrEmpty(ex))
            {
                RaiseLifecycleEvent(ScriptStopped, "ScriptStopped");
                ScriptApis.PrintLog("JavaScript代码已停止");
                return;
            }

            RaiseLifecycleEvent(ScriptRunError, "ScriptRunError");
            ScriptApis.PrintLog("JavaScript代码报错了：\r\n" + ex);
        }

        private static void RaiseLifecycleEvent(EventHandler handlers, string eventName)
        {
            if (handlers == null)
                return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Tools.Logger.AddScriptLog($"[{eventName}] handler error: {ex.Message}");
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static bool New(string file)
        {
            var validPath = Tools.Global.TryGetProfileScriptPathFromRelativePath(
                "user_script_run",
                file,
                out _,
                out var scriptPath);
            var fileExists = validPath && File.Exists(scriptPath);
            RuntimeSession previousSession;
            RuntimeSession session = null;

            lock (stateLock)
            {
                previousSession = currentSession;
                generation++;
                if (fileExists)
                {
                    session = new RuntimeSession(generation);
                    currentSession = session;
                    isRunning = true;
                }
                else
                {
                    currentSession = null;
                    isRunning = false;
                }
                canRun = false;
            }

            RetireSession(previousSession);
            if (!fileExists)
            {
                var detail = validPath ? scriptPath : (file ?? string.Empty);
                ScriptApis.PrintLog("JavaScript脚本文件不存在或路径无效：\r\n" + detail);
                return false;
            }

            try
            {
                if (!session.StartWorker(() => RunSessionWorker(session, scriptPath)))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                StopScript(session, ex.ToString());
                return false;
            }
        }

        private static void RunSessionWorker(RuntimeSession session, string scriptPath)
        {
            Exception fatalError = null;
            try
            {
                while (!Volatile.Read(ref canRun))
                {
                    if (!IsCurrentSession(session) || session.Token.WaitHandle.WaitOne(100))
                        return;
                }
                if (!IsCurrentSession(session) || session.Token.IsCancellationRequested)
                    return;

                ScriptApis.PrintLog("JavaScript脚本启动：" + Path.GetFileName(scriptPath));
                var localEngine = JavaScriptLoader.CreateEngine("script", session.Token);
                localEngine.SetValue("apiStartTimer", new Func<int, int, int>((id, time) =>
                    StartTimer(session, id, time)));
                localEngine.SetValue("apiStopTimer", new Action<int>(id => StopTimer(session, id)));
                localEngine.SetValue("apiSetCb", new Action<string, JsValue>((channel, callback) =>
                {
                    if (!session.Token.IsCancellationRequested)
                        session.ChannelCallbacks[channel ?? string.Empty] = callback;
                }));

                // Engine creation, top-level initialization, commands and every callback all
                // execute on this one dedicated worker thread.
                localEngine.Execute(File.ReadAllText(scriptPath), scriptPath);

                lock (stateLock)
                {
                    if (!ReferenceEquals(currentSession, session) ||
                        generation != session.Generation ||
                        !isRunning ||
                        session.Token.IsCancellationRequested)
                    {
                        return;
                    }
                }

                while (IsCurrentSession(session) && !session.Token.IsCancellationRequested)
                {
                    if (!session.TryDequeue(out var trigger))
                    {
                        session.WaitForWorkOrCancellation();
                        continue;
                    }

                    if (!IsCurrentSession(session) || session.Token.IsCancellationRequested)
                        return;

                    try
                    {
                        if (trigger.type == "cmd")
                        {
                            localEngine.Execute(trigger.data?.ToString() ?? string.Empty);
                            continue;
                        }

                        if (session.ChannelCallbacks.TryGetValue(trigger.type, out var callback) &&
                            callback != JsValue.Null &&
                            callback != JsValue.Undefined)
                        {
                            localEngine.Invoke(callback, trigger.data);
                            continue;
                        }

                        var onTrigger = localEngine.GetValue("onTrigger");
                        if (onTrigger != JsValue.Null && onTrigger != JsValue.Undefined)
                            localEngine.Invoke(onTrigger, trigger.id, trigger.type, trigger.data);
                    }
                    catch (Exception ex)
                    {
                        if (!IsCurrentSession(session) || session.Token.IsCancellationRequested)
                            return;
                        ScriptApis.PrintLog("回调报错：\r\n" + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsCurrentSession(session) && !session.Token.IsCancellationRequested)
                    fatalError = ex;
            }
            finally
            {
                if (fatalError != null)
                    StopScript(session, fatalError.ToString());
            }
        }

        private static RuntimeSession GetCurrentSession()
        {
            lock (stateLock)
                return currentSession;
        }

        private static bool IsCurrentSession(RuntimeSession session)
        {
            if (session == null || session.IsRetired)
                return false;

            lock (stateLock)
            {
                return ReferenceEquals(currentSession, session) &&
                    generation == session.Generation &&
                    isRunning;
            }
        }

        private static void RetireSession(RuntimeSession session)
        {
            if (session == null)
                return;

            // Reject producers and release queued payload/callback references immediately.
            // Cancellation interrupts Jint and session input boxes. Never wait here: New and
            // StopScript are UI entry points, so even a CLR callback that ignores cancellation
            // must not stall the dispatcher.
            session.Retire();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DisposeTimers(session);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Tools.Logger.AddScriptLog(
                            $"[RuntimeSession {session.Generation}] timer cleanup error: {ex.Message}");
                    }
                    catch
                    {
                    }
                }

                if (session.WaitForWorker(WorkerStopWaitMilliseconds))
                    return;

                try
                {
                    Tools.Logger.AddScriptLog(
                        $"[RuntimeSession {session.Generation}] worker did not exit within " +
                        $"{WorkerStopWaitMilliseconds} ms after retirement.");
                }
                catch
                {
                }
            });
        }

        private static void ObserveDroppedEvent(RuntimeSession session, long sessionDropCount, long payloadBytes)
        {
            Interlocked.Increment(ref droppedEventCount);
            Interlocked.Add(ref droppedPayloadBytes, payloadBytes);

            // Counters are always observable. Log sparsely and away from the producer thread.
            if (sessionDropCount != 1 && (sessionDropCount & (sessionDropCount - 1)) != 0)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!IsCurrentSession(session))
                    return;
                ScriptApis.PrintLog(
                    $"脚本事件队列达到上限，已按 {QueueDropPolicy} 策略丢弃 {sessionDropCount} 个事件。");
            });
        }

        private static long GetPayloadByteCount(object data)
        {
            if (data == null)
                return 0;
            if (data is byte[] bytes)
                return bytes.LongLength;
            if (data is string text)
                return Encoding.UTF8.GetByteCount(text);
            if (data is char[] characters)
                return Math.Min(long.MaxValue, characters.LongLength * sizeof(char));
            if (data is Array array)
            {
                try
                {
                    return Buffer.ByteLength(array);
                }
                catch
                {
                    return Math.Min(long.MaxValue, array.LongLength * IntPtr.Size);
                }
            }
            return UnknownPayloadBytes;
        }

        internal sealed class ScriptQueueSnapshot
        {
            public int QueuedEventCount { get; set; }
            public long QueuedPayloadBytes { get; set; }
            public long DroppedEventCount { get; set; }
            public long DroppedPayloadBytes { get; set; }
            public long RejectedAfterRetireCount { get; set; }
            public int CallbackCount { get; set; }
            public bool IsRetired { get; set; }
        }

        private sealed class RuntimeSession
        {
            private readonly CancellationTokenSource cancellationTokenSource =
                new CancellationTokenSource();
            private readonly AutoResetEvent workAvailable = new AutoResetEvent(false);
            private readonly object queueLock = new object();
            private readonly Queue<ScriptPool> pendingEvents = new Queue<ScriptPool>();
            private readonly TaskCompletionSource<bool> workerCompletion =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private Thread worker;
            private long queuedPayloadBytes = 0;
            private long droppedEvents = 0;
            private long droppedBytes = 0;
            private long rejectedAfterRetire = 0;
            private int acceptingEvents = 1;
            private int retired = 0;
            private int workerStarted = 0;
            private int cancellationCompleted = 0;
            private int resourcesDisposed = 0;

            public RuntimeSession(long generation)
            {
                Generation = generation;
                Token = cancellationTokenSource.Token;
            }

            public long Generation { get; }
            public CancellationToken Token { get; }
            public ConcurrentDictionary<string, JsValue> ChannelCallbacks { get; } =
                new ConcurrentDictionary<string, JsValue>();
            public bool IsRetired => Volatile.Read(ref retired) != 0;

            public bool StartWorker(ThreadStart body)
            {
                if (body == null)
                    throw new ArgumentNullException(nameof(body));
                if (Interlocked.CompareExchange(ref workerStarted, 1, 0) != 0)
                    return false;

                if (IsRetired)
                {
                    workerCompletion.TrySetResult(true);
                    TryDisposeResources();
                    return false;
                }

                worker = new Thread(() =>
                {
                    try
                    {
                        body();
                    }
                    finally
                    {
                        workerCompletion.TrySetResult(true);
                        TryDisposeResources();
                    }
                })
                {
                    IsBackground = true,
                    Name = "llcom JavaScript runtime " + Generation
                };

                try
                {
                    worker.Start();
                    return true;
                }
                catch
                {
                    workerCompletion.TrySetResult(true);
                    TryDisposeResources();
                    throw;
                }
            }

            public bool TryEnqueue(int id, string type, object data)
            {
                if (IsRetired || Volatile.Read(ref acceptingEvents) == 0)
                {
                    Interlocked.Increment(ref rejectedAfterRetire);
                    return false;
                }

                var normalizedType = type ?? string.Empty;
                var dataBytes = GetPayloadByteCount(data);
                var typeBytes = (long)Encoding.UTF8.GetByteCount(normalizedType);
                var payloadBytes = dataBytes > long.MaxValue - typeBytes
                    ? long.MaxValue
                    : dataBytes + typeBytes;
                long sessionDropCount = 0;
                lock (queueLock)
                {
                    if (acceptingEvents == 0 || IsRetired)
                    {
                        Interlocked.Increment(ref rejectedAfterRetire);
                        return false;
                    }

                    if (pendingEvents.Count >= MaxQueuedEventCount ||
                        payloadBytes > MaxQueuedPayloadBytes ||
                        queuedPayloadBytes > MaxQueuedPayloadBytes - payloadBytes)
                    {
                        droppedEvents++;
                        droppedBytes += payloadBytes;
                        sessionDropCount = droppedEvents;
                    }
                    else
                    {
                        object ownedData = data;
                        if (data is byte[] externalBytes)
                        {
                            var copy = new byte[externalBytes.Length];
                            Buffer.BlockCopy(externalBytes, 0, copy, 0, externalBytes.Length);
                            ownedData = copy;
                        }

                        pendingEvents.Enqueue(new ScriptPool
                        {
                            id = id,
                            type = normalizedType,
                            data = ownedData,
                            payloadBytes = payloadBytes
                        });
                        queuedPayloadBytes += payloadBytes;
                        workAvailable.Set();
                        return true;
                    }
                }

                ObserveDroppedEvent(this, sessionDropCount, payloadBytes);
                return false;
            }

            public bool TryDequeue(out ScriptPool trigger)
            {
                lock (queueLock)
                {
                    if (pendingEvents.Count == 0)
                    {
                        trigger = null;
                        return false;
                    }

                    trigger = pendingEvents.Dequeue();
                    queuedPayloadBytes -= trigger.payloadBytes;
                    if (queuedPayloadBytes < 0)
                        queuedPayloadBytes = 0;
                    return true;
                }
            }

            public void WaitForWorkOrCancellation()
            {
                if (IsRetired || Token.IsCancellationRequested)
                    return;
                WaitHandle.WaitAny(new WaitHandle[] { workAvailable, Token.WaitHandle });
            }

            public ScriptQueueSnapshot GetQueueSnapshot()
            {
                lock (queueLock)
                {
                    return new ScriptQueueSnapshot
                    {
                        QueuedEventCount = pendingEvents.Count,
                        QueuedPayloadBytes = queuedPayloadBytes,
                        DroppedEventCount = droppedEvents,
                        DroppedPayloadBytes = droppedBytes,
                        RejectedAfterRetireCount = Interlocked.Read(ref rejectedAfterRetire),
                        CallbackCount = ChannelCallbacks.Count,
                        IsRetired = IsRetired
                    };
                }
            }

            public void Retire()
            {
                if (Interlocked.Exchange(ref retired, 1) != 0)
                    return;

                Volatile.Write(ref acceptingEvents, 0);
                lock (queueLock)
                {
                    pendingEvents.Clear();
                    queuedPayloadBytes = 0;
                }
                ChannelCallbacks.Clear();

                try
                {
                    workAvailable.Set();
                }
                catch (ObjectDisposedException)
                {
                }

                if (Volatile.Read(ref workerStarted) == 0)
                {
                    Volatile.Write(ref cancellationCompleted, 1);
                    workerCompletion.TrySetResult(true);
                }
                else
                {
                    // Cancellation callbacks may enter UI/CLR code. Run them away from the
                    // caller so Stop/New never inherit an unbounded callback wait.
                    if (!ThreadPool.QueueUserWorkItem(_ => CancelSessionToken()))
                        Volatile.Write(ref cancellationCompleted, 1);
                }
                TryDisposeResources();
            }

            private void CancelSessionToken()
            {
                try
                {
                    cancellationTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    try
                    {
                        Tools.Logger.AddScriptLog(
                            $"[RuntimeSession {Generation}] cancellation callback error: {ex.Message}");
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    Volatile.Write(ref cancellationCompleted, 1);
                    TryDisposeResources();
                }
            }

            public bool WaitForWorker(int timeoutMilliseconds)
            {
                var localWorker = worker;
                if (localWorker == null || ReferenceEquals(Thread.CurrentThread, localWorker))
                    return true;

                try
                {
                    return workerCompletion.Task.Wait(Math.Max(0, timeoutMilliseconds));
                }
                catch (AggregateException)
                {
                    return workerCompletion.Task.IsCompleted;
                }
            }

            private void TryDisposeResources()
            {
                if (!IsRetired ||
                    !workerCompletion.Task.IsCompleted ||
                    Volatile.Read(ref cancellationCompleted) == 0)
                {
                    return;
                }
                if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0)
                    return;

                workAvailable.Dispose();
                cancellationTokenSource.Dispose();
            }
        }

        private sealed class ScriptTimer : IDisposable
        {
            private readonly CancellationTokenSource cancellationTokenSource;
            private readonly object disposeLock = new object();
            private bool disposed = false;

            public ScriptTimer(RuntimeSession session, int id, int interval)
            {
                Session = session;
                Id = id;
                cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                Token = cancellationTokenSource.Token;
                Timer = new System.Timers.Timer(interval) { AutoReset = false };
            }

            public RuntimeSession Session { get; }
            public int Id { get; }
            public CancellationToken Token { get; }
            public System.Timers.Timer Timer { get; }

            public bool TryStart()
            {
                lock (disposeLock)
                {
                    if (disposed)
                        return false;
                    Timer.Start();
                    return true;
                }
            }

            public void Dispose()
            {
                lock (disposeLock)
                {
                    if (disposed)
                        return;
                    disposed = true;
                    try { Timer.Stop(); } catch { }
                    try { cancellationTokenSource.Cancel(); } catch { }
                    Timer.Dispose();
                    cancellationTokenSource.Dispose();
                }
            }
        }
    }

    class ScriptPool
    {
        public int id { get; set; }
        public string type { get; set; }
        public object data { get; set; }
        public long payloadBytes { get; set; }
    }
}
