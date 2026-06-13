using Jint;
using Jint.Native;
using System;
using System.Collections;
using System.IO;

namespace llcom_plus.ScriptEnv
{
    class JavaScriptLoader
    {
        private static Engine scriptRunner = null;

        public static Engine CreateEngine(string type = "script")
        {
            var engine = new Engine(options =>
            {
                options.TimeoutInterval(TimeSpan.FromSeconds(type == "send" ? 3 : 10));
            });

            engine.SetValue("apiUtf8ToHex", new Func<string, string>(ScriptApis.Utf8ToAsciiHex));
            engine.SetValue("apiAscii2Utf8", new Func<byte[], byte[]>(ScriptApis.Ascii2Utf8));
            engine.SetValue("apiGetPath", new Func<string>(ScriptApis.GetPath));
            engine.SetValue("apiPrintLog", new Action<object>(ScriptApis.PrintLog));
            engine.SetValue("apiQuickSendList", new Func<int, string>(ScriptApis.QuickSendList));
            engine.SetValue("__apiInputBox", new Func<string, string, string, string>(ScriptApis.InputBox));
            engine.SetValue("apiAddPoint", new Action<double, int>(ScriptApis.AddPoint));
            engine.SetValue("__apiSend", new Func<string, object, object, bool>(ScriptApis.Send));
            engine.SetValue("apiBytesToString", new Func<object, string>(ScriptApis.BytesToString));
            engine.SetValue("apiStringToBytes", new Func<object, byte[]>(ScriptApis.StringToBytes));
            engine.SetValue("apiHexToBytes", new Func<object, byte[]>(ScriptApis.HexToBytes));
            engine.SetValue("apiBytesToHex", new Func<object, string>(ScriptApis.BytesToHex));
            engine.SetValue("apiToBytes", new Func<object, byte[]>(ScriptApis.ToBytes));
            engine.SetValue("apiConcatBytes", new Func<object[], byte[]>(ScriptApis.ConcatBytes));
            engine.SetValue("apiUnescapeText", new Func<string, string>(ScriptApis.UnescapeText));
            engine.SetValue("apiSleep", new Action<int>(System.Threading.Thread.Sleep));
            engine.SetValue("console", new ConsoleBridge());
            engine.SetValue("apiRequire", new Action<string>(name => Require(engine, name)));

            engine.Execute(@"
function bytesToString(data) { return apiBytesToString(data); }
function stringToBytes(data) { return apiStringToBytes(String(data)); }
function hexToBytes(data) { return apiHexToBytes(String(data)); }
function bytesToHex(data) { return apiBytesToHex(data); }
function apiSend(channel, data, options) { return __apiSend(channel, data, options === undefined ? null : options); }
function apiInputBox(prompt, defaultInput, title) {
    return __apiInputBox(String(prompt), defaultInput === undefined ? '' : String(defaultInput), title === undefined ? null : String(title));
}
function concatBytes() {
    var result = [];
    for (var i = 0; i < arguments.length; i++) {
        var data = apiToBytes(arguments[i]);
        var len = data.length === undefined ? data.Length : data.length;
        for (var j = 0; j < len; j++) {
            result.push(data[j] & 0xff);
        }
    }
    return result;
}
function sleep(ms) { apiSleep(ms | 0); }
");
            return engine;
        }

        public static byte[] Run(string file, ArrayList args = null, string path = "user_script_send_convert/")
        {
            var scriptPath = Tools.Global.ProfilePath + path + file;
            if (!File.Exists(scriptPath))
                return new byte[0];

            if (scriptRunner == null)
                scriptRunner = CreateEngine("send");

            lock (scriptRunner)
            {
                if (args != null)
                {
                    for (int i = 0; i < args.Count; i += 2)
                        scriptRunner.SetValue((string)args[i], args[i + 1]);
                }

                try
                {
                    var script = File.ReadAllText(scriptPath);
                    var result = scriptRunner.Evaluate("(function(){\r\n" + script + "\r\n})()", scriptPath);
                    if (result == JsValue.Null || result == JsValue.Undefined)
                        return null;
                    return ScriptApis.ToBytes(result);
                }
                catch
                {
                    scriptRunner = null;
                    throw;
                }
            }
        }

        public static void ClearRun()
        {
            scriptRunner = null;
        }

        private static void Require(Engine engine, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            var file = name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? name : name + ".js";
            var candidates = new[]
            {
                Path.Combine(Tools.Global.ProfilePath, "user_script_run", "requires", file),
                Path.Combine(Tools.Global.ProfilePath, "core_script", file),
                Path.Combine(Tools.Global.ProfilePath, file)
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    engine.Execute(File.ReadAllText(candidate), candidate);
                    return;
                }
            }
            throw new FileNotFoundException("JavaScript require file not found", file);
        }

        private class ConsoleBridge
        {
            public void log(object data)
            {
                ScriptApis.PrintLog(data);
            }

            public void error(object data)
            {
                ScriptApis.PrintLog(data);
            }
        }
    }
}
