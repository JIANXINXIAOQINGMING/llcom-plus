using Jint;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Threading;

// Uses in-memory sessions, a real dispatcher queue, and fake serial delegates.
// No Application, UART constructor, configuration file, socket, or broker is used.
public static class ScriptSessionSendRegressionProbe
{
    private const BindingFlags AllStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Dispatcher captureDispatcher;
    private static object captureTarget;
    private static int captures;

    public static void InstallDependencyResolution(string outputDirectory)
    {
        // PowerShell does not inherit the application's app.config redirects.
        // Mirror its pinned Unsafe redirect for the in-process Jint probe.
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            new AssemblyName(args.Name).Name == "System.Runtime.CompilerServices.Unsafe"
                ? Assembly.LoadFrom(System.IO.Path.Combine(outputDirectory, "System.Runtime.CompilerServices.Unsafe.dll"))
                : null;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static object Call(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static object NewSession(Type sessionType, long generation)
    {
        return Activator.CreateInstance(sessionType, AllInstance, null, new object[] { generation }, null);
    }
    private static long Number(object value, string name)
    {
        return Convert.ToInt64(value.GetType().GetProperty(name).GetValue(value, null));
    }
    private static T Capture<T>() where T : class
    {
        Interlocked.Increment(ref captures);
        if (captureDispatcher == null) return (T)captureTarget;
        return (T)captureDispatcher.Invoke(new Func<object>(() => captureTarget));
    }
    private static Delegate CaptureDelegate(Type targetType)
    {
        var method = typeof(ScriptSessionSendRegressionProbe).GetMethod("Capture", AllStatic).MakeGenericMethod(targetType);
        return Delegate.CreateDelegate(typeof(Func<>).MakeGenericType(targetType), method);
    }
    private static void ExpectCanceled(Action action, string message)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException(message);
    }
    private static object MakeTarget(Type targetType, Func<bool> isOpen,
        Func<byte[], CancellationToken, Action<int>, bool> send)
    {
        return Activator.CreateInstance(targetType, AllInstance, null,
            new object[] { "fake:new", "Fake COM", isOpen, send, true }, null);
    }

    private static void CheckMqttQueue(Assembly assembly, Type sessionType, List<string> passed)
    {
        var dtoType = assembly.GetType("llcom_plus.ScriptEnv.ScriptApis+MqttChannelData", true);
        var enqueue = sessionType.GetMethod("TryEnqueue", AllInstance);
        var dequeue = sessionType.GetMethod("TryDequeue", AllInstance);
        var snapshot = sessionType.GetMethod("GetQueueSnapshot", AllInstance);
        var retire = sessionType.GetMethod("Retire", AllInstance);
        var session = NewSession(sessionType, -100);
        var topic = "sensor/温度";
        var source = new byte[1024 * 1024];
        source[0] = 47;
        var dto = Activator.CreateInstance(dtoType, AllInstance, null, new object[] { topic, source, 1 }, null);
        var expectedBytes = source.LongLength + Encoding.UTF8.GetByteCount(topic) + sizeof(int) + 4; // "mqtt"
        try
        {
            Assert((bool)Call(enqueue, session, 1, "mqtt", dto), "A bounded MQTT event was rejected.");
            Assert(Number(Call(snapshot, session), "QueuedPayloadBytes") == expectedBytes,
                "Nested MQTT payload/topic bytes were not charged to the queue budget.");
            source[0] = 99;
            object[] args = { null };
            Assert((bool)Call(dequeue, session, args), "The admitted MQTT event was lost.");
            var owned = args[0].GetType().GetProperty("data").GetValue(args[0], null);
            var ownedPayload = (byte[])dtoType.GetProperty("payload").GetValue(owned, null);
            Assert(!ReferenceEquals(owned, dto) && !ReferenceEquals(ownedPayload, source) && ownedPayload[0] == 47,
                "The queued MQTT object or its nested payload aliases the transport buffer.");
            Assert(Number(Call(snapshot, session), "QueuedPayloadBytes") == 0, "Dequeue did not release MQTT byte accounting.");

            var engine = new Engine();
            engine.SetValue("message", owned);
            Assert(engine.Evaluate("message.topic === 'sensor/温度' && message.payload[0] === 47 && message.qos === 1").AsBoolean(),
                "Existing JavaScript lowercase topic/payload/qos fields are incompatible.");
            passed.Add("MQTT queue accounts UTF-8 topic plus nested payload, owns its bytes, and preserves JavaScript fields.");

            Assert((bool)Call(enqueue, session, 2, "mqtt", dto) &&
                (bool)Call(enqueue, session, 3, "mqtt", dto) &&
                (bool)Call(enqueue, session, 4, "mqtt", dto), "Three 1 MiB MQTT events did not fit the byte budget.");
            Assert(!(bool)Call(enqueue, session, 5, "mqtt", dto), "MQTT events bypassed the 4 MiB byte budget.");
            var state = Call(snapshot, session);
            Assert(Number(state, "QueuedEventCount") == 3 && Number(state, "QueuedPayloadBytes") == 3 * expectedBytes &&
                Number(state, "DroppedEventCount") == 1 && Number(state, "DroppedPayloadBytes") == expectedBytes,
                "Rejected MQTT events changed admitted counts or lost drop accounting.");
            Call(retire, session);
            Assert(Number(Call(snapshot, session), "QueuedPayloadBytes") == 0, "Retirement retained MQTT queue bytes.");
            passed.Add("MQTT events obey the shared 4 MiB queue bound and retirement releases retained payloads.");
        }
        finally { Call(retire, session); }
    }

    private static void CheckChannelCompatibility(Assembly assembly, Type apis, List<string> passed)
    {
        var register = apis.GetMethod("SendChannelsRegister", AllStatic);
        var registerCancelable = apis.GetMethod("SendChannelsRegisterCancelable", AllStatic);
        var send = apis.GetMethod("Send", AllStatic, null, new[] { typeof(string), typeof(object), typeof(object) }, null);
        var sessionSend = apis.GetMethod("SendFromSession", AllStatic);
        var channel = "probe:" + Guid.NewGuid().ToString("N");
        var options = new object();
        int calls = 0;
        Func<byte[], object, bool> callback = (bytes, supplied) =>
        {
            calls++;
            Assert(bytes[0] == 7 && ReferenceEquals(options, supplied), "Legacy send data/options changed.");
            return true;
        };
        var first = (IDisposable)Call(register, null, channel, callback);
        var second = (IDisposable)Call(register, null, channel, callback);
        try
        {
            first.Dispose();
            Assert((bool)Call(send, null, channel, new byte[] { 7 }, options) && calls == 1,
                "Disposing a superseded channel registration removed its replacement.");
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                ExpectCanceled(() => Call(sessionSend, null, channel, new byte[] { 7 }, options, cts.Token, null),
                    "A pre-canceled session reached its registered channel.");
            }
            ExpectCanceled(() => Call(sessionSend, null, channel, new byte[] { 7 }, options, CancellationToken.None,
                new Func<bool>(() => false)), "A retired identity reached its registered channel before cancellation completed.");
            Assert(calls == 1, "Canceled or retired sends invoked a legacy callback.");
        }
        finally { first.Dispose(); second.Dispose(); }
        Assert(!(bool)Call(send, null, channel, new byte[] { 7 }, options), "Disposed channel still accepts sends.");
        passed.Add("Public synchronous bool channel API, options, and registration ownership remain compatible; stopped sessions are rejected.");

        using (var cts = new CancellationTokenSource())
        {
            CancellationToken suppliedToken = CancellationToken.None;
            object suppliedOptions = null;
            Func<byte[], object, CancellationToken, Func<bool>, bool> cancelable = (bytes, supplied, token, current) =>
            {
                suppliedToken = token;
                suppliedOptions = supplied;
                Assert(bytes.Length == 1 && bytes[0] == 7, "JavaScript apiSend changed its byte conversion.");
                return false;
            };
            using (var registration = (IDisposable)Call(registerCancelable, null, channel, cancelable))
            {
                var loader = assembly.GetType("llcom_plus.ScriptEnv.JavaScriptLoader", true);
                var factory = loader.GetMethod("CreateEngine", AllStatic, null,
                    new[] { typeof(string), typeof(CancellationToken) }, null);
                var engine = (Engine)Call(factory, null, "script", cts.Token);
                Assert(!engine.Evaluate("apiSend('" + channel + "', [7], {flag: 1})").AsBoolean(),
                    "JavaScript apiSend lost the synchronous false result.");
                Assert(suppliedToken == cts.Token && suppliedOptions != null,
                    "The engine's apiSend binding lost its cancellation token or options.");
            }
        }
        passed.Add("The real JavaScript engine propagates its cancellation token and preserves apiSend options/false return.");
    }

    private static void CheckUartCapture(Assembly assembly, Type apis, Type runtime, Type sessionType, List<string> passed)
    {
        var currentField = runtime.GetField("currentSession", AllStatic);
        var generationField = runtime.GetField("generation", AllStatic);
        var runningField = runtime.GetField("isRunning", AllStatic);
        var originalSession = currentField.GetValue(null);
        var originalGeneration = generationField.GetValue(null);
        var originalRunning = runningField.GetValue(null);
        var retire = sessionType.GetMethod("Retire", AllInstance);
        var isCurrent = runtime.GetMethod("IsCurrentSession", AllStatic);
        var uartSend = apis.GetMethod("SendUartFromSession", AllStatic);
        var targetType = assembly.GetType("llcom_plus.Tools.ActiveSerialTarget", true);
        var old = NewSession(sessionType, 1001);
        var replacement = NewSession(sessionType, 1002);
        var oldToken = (CancellationToken)sessionType.GetProperty("Token").GetValue(old, null);
        var newToken = (CancellationToken)sessionType.GetProperty("Token").GetValue(replacement, null);
        var current = new Func<bool>(() => (bool)Call(isCurrent, null, old));
        int writes = 0;
        CancellationToken writeToken = CancellationToken.None;
        captureTarget = MakeTarget(targetType, () => true, (bytes, token, committed) =>
        {
            writes++;
            writeToken = token;
            Assert(bytes.Length == 1 && bytes[0] == 65, "UART capture changed the original bytes.");
            return true;
        });
        var capture = CaptureDelegate(targetType);
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            currentField.SetValue(null, old);
            generationField.SetValue(null, 1001L);
            runningField.SetValue(null, true);
            captureDispatcher = dispatcher;
            captures = 0;
            Exception failure = null;
            Thread worker = null;
            using (var posted = new ManualResetEvent(false))
            {
                DispatcherHookEventHandler hook = (sender, args) =>
                {
                    if (Thread.CurrentThread == worker) posted.Set();
                };
                dispatcher.Hooks.OperationPosted += hook;
                worker = new Thread(() =>
                {
                    try { Call(uartSend, null, new byte[] { 65 }, oldToken, current, capture); }
                    catch (Exception ex) { failure = ex; }
                });
                worker.IsBackground = true;
                try
                {
                    worker.Start();
                    Assert(posted.WaitOne(5000), "Script send did not enter the real UI capture queue.");
                    Call(retire, old);
                    currentField.SetValue(null, replacement);
                    generationField.SetValue(null, 1002L);
                    Assert(!oldToken.IsCancellationRequested, "The identity test unexpectedly depended on token cancellation.");
                    dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
                    Assert(worker.Join(5000), "Retired script send blocked after UI capture returned.");
                    Assert(failure is OperationCanceledException && writes == 0 && captures == 1,
                        "An old script queued at UI capture sent to the replacement COM target.");
                }
                finally
                {
                    dispatcher.Hooks.OperationPosted -= hook;
                    if (worker.IsAlive)
                    {
                        dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
                        worker.Join(1000);
                    }
                }
            }
            passed.Add("A real queued UI capture rejects a retired script before touching the replacement COM, even before token cancellation.");

            captureDispatcher = null;
            Assert((bool)Call(uartSend, null, new byte[] { 65 }, newToken,
                new Func<bool>(() => (bool)Call(isCurrent, null, replacement)), capture), "Current script send was rejected.");
            Assert(writes == 1 && writeToken == newToken, "The current script did not pass its session token to UART write.");
            captures = 0;
            ExpectCanceled(() => Call(uartSend, null, new byte[] { 65 }, oldToken, current, capture),
                "Already-retired script attempted another UART capture.");
            Assert(captures == 0 && writes == 1, "A stopped session touched the UI or wrote bytes again.");
            passed.Add("Current sessions send normally with their own token; already-retired sessions cannot even capture a COM target.");
        }
        finally
        {
            captureDispatcher = null;
            captureTarget = null;
            currentField.SetValue(null, originalSession);
            generationField.SetValue(null, originalGeneration);
            runningField.SetValue(null, originalRunning);
            Call(retire, old);
            Call(retire, replacement);
        }
    }

    public static string[] Run(Assembly assembly)
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Use an STA host.");
        var apis = assembly.GetType("llcom_plus.ScriptEnv.ScriptApis", true);
        var runtime = assembly.GetType("llcom_plus.ScriptEnv.JavaScriptRunEnv", true);
        var sessionType = runtime.GetNestedType("RuntimeSession", BindingFlags.NonPublic);
        Assert(sessionType != null, "RuntimeSession is missing.");
        var passed = new List<string>();
        CheckMqttQueue(assembly, sessionType, passed);
        CheckChannelCompatibility(assembly, apis, passed);
        CheckUartCapture(assembly, apis, runtime, sessionType, passed);
        return passed.ToArray();
    }
}
