using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

// Exercises actual page helpers with an interface-only fake. No Page/Application
// constructor, MQTT connection, certificate, user configuration or network is used.
public static class MqttAttemptRegressionProbe
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static object Call(object target, string name, params object[] arguments)
    {
        try { return target.GetType().GetMethod(name, Instance).Invoke(target, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private sealed class Client : RealProxy
    {
        internal bool Connected = true;
        internal int Publishes;
        private object completion;
        private Type completionType;
        internal Client(Type clientType) : base(clientType) { }
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            object result = null;
            if (call.MethodName == "get_IsConnected") result = Connected;
            else if (call.MethodName == "PublishAsync")
            {
                Publishes++;
                var resultType = ((MethodInfo)call.MethodBase).ReturnType.GetGenericArguments()[0];
                completionType = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
                completion = Activator.CreateInstance(completionType);
                result = completionType.GetProperty("Task").GetValue(completion, null);
            }
            else throw new InvalidOperationException("Unexpected fake-client access: " + call.MethodName);
            return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
        }
        internal void Complete()
        {
            completionType.GetMethod("SetResult").Invoke(completion, new object[] { null });
        }
    }
    private static void SetConnection(object page, object client, long attempt)
    {
        var type = page.GetType();
        type.GetField("mqttClient", Instance).SetValue(page, client);
        type.GetField("activeMqttAttempt", Instance).SetValue(page, attempt);
    }
    private static void QueuedAcrossReconnect(object page, object nextClient, long nextAttempt, Action action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Exception failure = null;
        Thread worker = null;
        using (var posted = new ManualResetEvent(false))
        {
            DispatcherHookEventHandler handler = (sender, args) => { if (Thread.CurrentThread == worker) posted.Set(); };
            dispatcher.Hooks.OperationPosted += handler;
            worker = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            worker.IsBackground = true;
            try
            {
                worker.Start();
                Check(posted.WaitOne(5000), "MQTT callback did not reach the real dispatcher queue.");
                SetConnection(page, nextClient, nextAttempt);
                dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
                Check(worker.Join(5000), "MQTT callback did not finish after dispatcher drain.");
                if (failure != null) throw failure;
            }
            finally { dispatcher.Hooks.OperationPosted -= handler; }
        }
    }
    public static string[] Run(Assembly assembly, Assembly mqttAssembly)
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Use an STA test host.");
        var type = assembly.GetType("llcom_plus.Pages.MqttTestPage", true);
        var clientType = mqttAssembly.GetType("MQTTnet.Client.IMqttClient", true);
        var page = FormatterServices.GetUninitializedObject(type);
        typeof(DispatcherObject).GetField("_dispatcher", Instance).SetValue(page, Dispatcher.CurrentDispatcher);
        type.GetField("mqttStateLock", Instance).SetValue(page, new object());
        var state = type.GetField("mqttConnectionState", Instance);
        state.SetValue(page, Enum.Parse(state.FieldType, "Connected"));
        var topics = new ListBox();
        type.GetField("subListBox", Instance).SetValue(page, topics);
        var a = new Client(clientType); var aProxy = a.GetTransparentProxy();
        var b = new Client(clientType); var bProxy = b.GetTransparentProxy();
        var c = new Client(clientType); var cProxy = c.GetTransparentProxy();
        SetConnection(page, aProxy, 11L);
        var message = Activator.CreateInstance(mqttAssembly.GetType("MQTTnet.MqttApplicationMessage", true));
        var passed = new List<string>();

        var captureArgs = new object[] { null, 0L };
        Check((bool)Call(page, "TryCaptureMqttConnection", captureArgs) && ReferenceEquals(captureArgs[0], aProxy) &&
            (long)captureArgs[1] == 11L, "Capture did not preserve one client/attempt pair.");
        passed.Add("MQTT submission atomically captures its original client and attempt.");

        var pending = (Task<bool>)Call(page, "PublishOnCapturedClientAsync", aProxy, 11L, message);
        Check(a.Publishes == 1 && !pending.IsCompleted, "A publish completed before the fake transport.");
        SetConnection(page, bProxy, 12L);
        a.Complete();
        Check(!pending.GetAwaiter().GetResult() && b.Publishes == 0,
            "A pending old publish was redirected to the replacement broker or reported current success.");
        passed.Add("A pending publish awaits actual completion and never reroutes to a replacement broker.");

        pending = (Task<bool>)Call(page, "PublishOnCapturedClientAsync", aProxy, 11L, message);
        Check(!pending.GetAwaiter().GetResult() && a.Publishes == 1 && b.Publishes == 0,
            "A publish queued before reconnect contacted an old or replacement client.");
        pending = (Task<bool>)Call(page, "PublishOnCapturedClientAsync", bProxy, 11L, message);
        Check(!pending.GetAwaiter().GetResult() && b.Publishes == 0, "A reused client with an old attempt published.");
        passed.Add("Retired queued publishes reject both replaced clients and reused clients with old attempts.");

        pending = (Task<bool>)Call(page, "PublishOnCapturedClientAsync", bProxy, 12L, message);
        Check(b.Publishes == 1 && !pending.IsCompleted, "Current publication bypassed transport completion.");
        b.Complete();
        Check(pending.GetAwaiter().GetResult(), "Current publication lost its successful result.");
        passed.Add("Current MQTT publication retains its normal completion result.");

        Call(page, "CompleteSubscription", aProxy, 11L, "old-broker/topic");
        Check(topics.Items.Count == 0, "Old subscription completion polluted the current broker list.");
        Call(page, "CompleteSubscription", bProxy, 12L, "current/topic");
        Call(page, "CompleteSubscription", bProxy, 12L, "current/topic");
        Check(topics.Items.Count == 1 && (string)topics.Items[0] == "current/topic", "Current subscription completion duplicated or lost a topic.");
        passed.Add("Subscription completion validates its captured attempt and retains topic deduplication.");

        QueuedAcrossReconnect(page, cProxy, 13L, () => Call(page, "CompleteSubscription", bProxy, 12L, "queued-old/topic"));
        Check(topics.Items.Count == 1, "A queued subscription callback survived reconnect.");
        passed.Add("A real dispatcher-queued subscription rechecks its broker identity after reconnect.");

        int callbacks = 0;
        Action receive = () => callbacks++;
        QueuedAcrossReconnect(page, bProxy, 14L, () => Call(page, "RunMqttCallback", cProxy, 13L, receive));
        Check(callbacks == 0, "Queued received data reached the replacement connection.");
        Call(page, "RunMqttCallback", bProxy, 14L, receive);
        Check(callbacks == 1, "Current received data was suppressed.");
        passed.Add("MQTT incoming data and logs recheck identity after UI dispatch.");
        return passed.ToArray();
    }
}
