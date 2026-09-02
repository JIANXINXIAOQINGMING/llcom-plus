using Jint;
using Jint.Native;
using System;
using System.Collections;
using System.IO;
using System.Threading;

namespace llcom_plus.ScriptEnv
{
    class JavaScriptLoader
    {
        private static readonly TimeSpan SendScriptTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RunScriptTimeout = TimeSpan.FromSeconds(10);

        public static Engine CreateEngine(string type = "script")
        {
            return CreateEngine(type, CancellationToken.None);
        }

        public static Engine CreateEngine(string type, CancellationToken cancellationToken)
        {
            var timeout = type == "send" ? SendScriptTimeout : RunScriptTimeout;
            var engine = new Engine(options =>
            {
                options.TimeoutInterval(timeout);
                if (cancellationToken.CanBeCanceled)
                    options.CancellationToken(cancellationToken);
            });

            engine.SetValue("apiUtf8ToHex", new Func<string, string>(ScriptApis.Utf8ToAsciiHex));
            engine.SetValue("apiAscii2Utf8", new Func<byte[], byte[]>(ScriptApis.Ascii2Utf8));
            engine.SetValue("apiGetPath", new Func<string>(ScriptApis.GetPath));
            engine.SetValue("apiPrintLog", new Action<object>(ScriptApis.PrintLog));
            engine.SetValue("apiQuickSendList", new Func<int, string>(ScriptApis.QuickSendList));
            engine.SetValue("__apiInputBox", new Func<string, string, string, string>((prompt, defaultInput, title) =>
                ScriptApis.InputBox(prompt, defaultInput, title, cancellationToken)));
            engine.SetValue("apiAddPoint", new Action<double, int>(ScriptApis.AddPoint));
            engine.SetValue("__apiSend", new Func<string, object, object, bool>(ScriptApis.Send));
            engine.SetValue("apiBytesToString", new Func<object, string>(ScriptApis.BytesToString));
            engine.SetValue("apiStringToBytes", new Func<object, byte[]>(ScriptApis.StringToBytes));
            engine.SetValue("apiHexToBytes", new Func<object, byte[]>(ScriptApis.HexToBytes));
            engine.SetValue("apiBytesToHex", new Func<object, string>(ScriptApis.BytesToHex));
            engine.SetValue("apiToBytes", new Func<object, byte[]>(ScriptApis.ToBytes));
            engine.SetValue("apiConcatBytes", new Func<object[], byte[]>(ScriptApis.ConcatBytes));
            engine.SetValue("apiUnescapeText", new Func<string, string>(ScriptApis.UnescapeText));
            engine.SetValue("apiSleep", new Action<double>(milliseconds =>
                Sleep(milliseconds, timeout, cancellationToken)));
            engine.SetValue("console", new ConsoleBridge());
            engine.SetValue("apiRequire", new Action<string>(name =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(engine, name);
                cancellationToken.ThrowIfCancellationRequested();
            }));

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
function sleep(ms) { apiSleep(Number(ms)); }
");
            return engine;
        }

        public static byte[] Run(string file, ArrayList args = null, string path = "user_script_send_convert/")
        {
            if (!TryGetConverterScriptPath(path, file, out var scriptPath))
                throw new ArgumentException("Script path must be a safe JavaScript basename in an allowed converter root.", nameof(file));
            if (!File.Exists(scriptPath))
                return new byte[0];

            using (var runTokenSource = new CancellationTokenSource())
            {
                runTokenSource.CancelAfter(SendScriptTimeout);
                var scriptRunner = CreateEngine("send", runTokenSource.Token);

                if (args != null)
                {
                    for (int i = 0; i < args.Count; i += 2)
                        scriptRunner.SetValue((string)args[i], args[i + 1]);
                }

                var script = File.ReadAllText(scriptPath);
                var result = scriptRunner.Evaluate("(function(){\r\n" + script + "\r\n})()", scriptPath);
                if (result == JsValue.Null || result == JsValue.Undefined)
                    return null;
                return ScriptApis.ToBytes(result);
            }
        }

        public static void ClearRun()
        {
            // Kept for API compatibility. Run uses an isolated engine, so there is no shared state to clear.
        }

        private static bool TryGetConverterScriptPath(string path, string file, out string scriptPath)
        {
            scriptPath = string.Empty;
            var directoryName = (path ?? string.Empty)
                .Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(directoryName, "user_script_send_convert", StringComparison.Ordinal) &&
                !string.Equals(directoryName, "user_script_recv_convert", StringComparison.Ordinal))
            {
                return false;
            }

            return Tools.Global.TryGetProfileScriptPath(
                directoryName,
                file,
                out _,
                out scriptPath);
        }

        private static void Sleep(
            double milliseconds,
            TimeSpan maximumDelay,
            CancellationToken cancellationToken)
        {
            if (double.IsNaN(milliseconds) ||
                double.IsInfinity(milliseconds) ||
                milliseconds < 0 ||
                milliseconds > maximumDelay.TotalMilliseconds ||
                milliseconds != Math.Truncate(milliseconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(milliseconds),
                    $"sleep must be a whole number between 0 and {(int)maximumDelay.TotalMilliseconds} milliseconds.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            System.Threading.Tasks.Task.Delay((int)milliseconds, cancellationToken).GetAwaiter().GetResult();
        }

        private static void Require(Engine engine, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (!Tools.Global.IsValidScriptFileName(name))
                throw new ArgumentException("JavaScript require accepts only a safe basename.", nameof(name));

            var allowedRoots = new[]
            {
                Path.Combine(Tools.Global.ProfilePath, "user_script_run", "requires"),
                Path.Combine(Tools.Global.ProfilePath, "core_script"),
                Tools.Global.ProfilePath
            };
            var normalizedName = Tools.Global.NormalizeScriptFileName(name);
            foreach (var root in allowedRoots)
            {
                if (!Tools.Global.TryGetCanonicalScriptPath(
                        root,
                        normalizedName,
                        out _,
                        out var candidate))
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    engine.Execute(File.ReadAllText(candidate), candidate);
                    return;
                }
            }

            throw new FileNotFoundException(
                "JavaScript require file not found in an allowed script root",
                normalizedName + ".js");
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
