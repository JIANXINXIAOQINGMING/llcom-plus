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
        public static event EventHandler ScriptRunError;
        private static Engine engine = null;
        private static CancellationTokenSource tokenSource = null;
        private static readonly ConcurrentDictionary<int, CancellationTokenSource> timerPool =
            new ConcurrentDictionary<int, CancellationTokenSource>();
        private static readonly ConcurrentQueue<ScriptPool> toRun = new ConcurrentQueue<ScriptPool>();
        private static readonly Dictionary<string, JsValue> channelCallbacks = new Dictionary<string, JsValue>();
        private static readonly object engineLock = new object();

        public static bool isRunning = false;
        public static bool canRun = false;

        private static void AddTrigger(int id, string type = "timer", object data = null)
        {
            if (!isRunning)
                return;
            toRun.Enqueue(new ScriptPool { id = id, type = type, data = data });
            RunTrigger();
        }

        public static void RunCommand(string command)
        {
            AddTrigger(-1, "cmd", command ?? "");
        }

        public static void ChannelReceived(string channel, object data)
        {
            if (!isRunning)
                return;
            toRun.Enqueue(new ScriptPool { id = -1, type = channel, data = data });
            RunTrigger();
        }

        private static void RunTrigger()
        {
            if (!canRun || engine == null)
                return;

            lock (engineLock)
            {
                try
                {
                    while (toRun.TryDequeue(out var temp))
                    {
                        if (tokenSource == null || tokenSource.IsCancellationRequested)
                            return;
                        try
                        {
                            if (temp.type == "cmd")
                            {
                                engine.Execute(temp.data?.ToString() ?? "");
                                continue;
                            }

                            if (channelCallbacks.TryGetValue(temp.type, out var callback) &&
                                callback != JsValue.Null &&
                                callback != JsValue.Undefined)
                            {
                                engine.Invoke(callback, temp.data);
                                continue;
                            }

                            var onTrigger = engine.GetValue("onTrigger");
                            if (onTrigger != JsValue.Null && onTrigger != JsValue.Undefined)
                                engine.Invoke(onTrigger, temp.id, temp.type, temp.data);
                        }
                        catch (Exception ex)
                        {
                            ScriptApis.PrintLog("回调报错：\r\n" + ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    StopScript(ex.ToString());
                }
            }
        }

        public static int StartTimer(int id, int time)
        {
            var timerToken = new CancellationTokenSource();
            if (timerPool.ContainsKey(id))
            {
                try
                {
                    if (timerPool.TryRemove(id, out var oldToken))
                        oldToken.Cancel();
                }
                catch { }
            }

            timerPool.TryAdd(id, timerToken);
            var timer = new System.Timers.Timer(Math.Max(1, time));
            timer.Elapsed += (sender, e) =>
            {
                if (timerToken == null || timerToken.IsCancellationRequested || !isRunning)
                    return;
                timerPool.TryRemove(id, out _);
                AddTrigger(id);
                ((System.Timers.Timer)sender).Dispose();
            };
            timer.AutoReset = false;
            timer.Start();
            return 1;
        }

        public static void StopTimer(int id)
        {
            if (!timerPool.ContainsKey(id))
                return;
            try
            {
                if (timerPool.TryRemove(id, out var oldToken))
                    oldToken.Cancel();
            }
            catch { }
        }

        private static void ResetRuntime()
        {
            foreach (var item in timerPool)
                item.Value.Cancel();
            tokenSource?.Cancel();
            timerPool.Clear();
            channelCallbacks.Clear();
            engine = null;
            while (toRun.TryDequeue(out _)) { }
        }

        public static void StopScript(string ex)
        {
            ScriptRunError?.Invoke(null, EventArgs.Empty);
            if (!string.IsNullOrEmpty(ex))
                ScriptApis.PrintLog("JavaScript代码报错了：\r\n" + ex);
            else
                ScriptApis.PrintLog("JavaScript代码已停止");

            ResetRuntime();
            isRunning = false;
            canRun = false;
        }

        public static bool New(string file)
        {
            ResetRuntime();
            canRun = false;

            var scriptPath = Tools.Global.ProfilePath + file;
            if (!File.Exists(scriptPath))
            {
                isRunning = false;
                ScriptApis.PrintLog("JavaScript脚本文件不存在：\r\n" + scriptPath);
                return false;
            }

            isRunning = true;
            var currentTokenSource = new CancellationTokenSource();
            tokenSource = currentTokenSource;

            Task.Run(() =>
            {
                while (!canRun && !currentTokenSource.IsCancellationRequested)
                    Task.Delay(100).Wait();
                if (currentTokenSource.IsCancellationRequested)
                    return;

                try
                {
                    ScriptApis.PrintLog("JavaScript脚本启动：" + Path.GetFileName(scriptPath));
                    var localEngine = JavaScriptLoader.CreateEngine();
                    localEngine.SetValue("apiStartTimer", new Func<int, int, int>(StartTimer));
                    localEngine.SetValue("apiStopTimer", new Action<int>(StopTimer));
                    localEngine.SetValue("apiSetCb", new Action<string, JsValue>((channel, cb) =>
                    {
                        channelCallbacks[channel] = cb;
                    }));

                    lock (engineLock)
                    {
                        engine = localEngine;
                        engine.Execute(File.ReadAllText(scriptPath), scriptPath);
                    }
                }
                catch (Exception ex)
                {
                    StopScript(ex.ToString());
                }
            }, currentTokenSource.Token);

            return true;
        }
    }

    class ScriptPool
    {
        public int id { get; set; }
        public string type { get; set; }
        public object data { get; set; }
    }
}
