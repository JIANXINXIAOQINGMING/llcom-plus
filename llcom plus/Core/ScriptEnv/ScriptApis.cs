using Jint;
using Jint.Native;
using Jint.Native.Object;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace llcom_plus.ScriptEnv
{
    class ScriptApis
    {
        public static event EventHandler PrintScriptLog;

        public static void PrintLog(object log)
        {
            var text = log?.ToString() ?? "";
            Tools.Logger.AddScriptLog(text);
            PrintScriptLog?.Invoke(DateTime.Now.ToString("[HH:mm:ss:ffff]") + text, EventArgs.Empty);
        }

        public static string Utf8ToAsciiHex(string input)
        {
            return BitConverter.ToString(Encoding.GetEncoding("GB2312").GetBytes(input ?? "")).Replace("-", "");
        }

        public static byte[] Ascii2Utf8(byte[] input)
        {
            return Encoding.UTF8.GetBytes(Encoding.Default.GetString(input ?? new byte[0]));
        }

        public static string GetPath()
        {
            return Tools.Global.ProfilePath;
        }

        public static string QuickSendList(int id)
        {
            var settings = Tools.Global.setting;
            if (settings == null || id <= 0)
                return string.Empty;

            // One API call observes exactly one immutable Settings snapshot.
            var snapshot = settings.GetQuickSendSnapshot();
            if (id > snapshot.Count)
                return string.Empty;

            var item = snapshot[id - 1];
            return item.Hex ? "H" + item.Text : "S" + item.Text;
        }

        public static string InputBox(string prompt, string defaultInput = "", string title = null)
        {
            return InputBox(prompt, defaultInput, title, System.Threading.CancellationToken.None);
        }

        public static string InputBox(
            string prompt,
            string defaultInput,
            string title,
            System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var application = System.Windows.Application.Current;
            if (application == null || application.Dispatcher == null)
                throw new InvalidOperationException("A WPF application dispatcher is required for apiInputBox.");

            return new SessionInputDialogRequest(
                application,
                prompt,
                defaultInput,
                title,
                cancellationToken).ShowAndWait();
        }

        private sealed class SessionInputDialogRequest
        {
            private readonly System.Windows.Application application;
            private readonly System.Windows.Threading.Dispatcher dispatcher;
            private readonly string prompt;
            private readonly string defaultInput;
            private readonly string title;
            private readonly System.Threading.CancellationToken cancellationToken;
            private readonly System.Threading.Tasks.TaskCompletionSource<string> completion =
                new System.Threading.Tasks.TaskCompletionSource<string>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            private llcom_plus.InputDialogWindow dialog;
            private int cancellationRequested = 0;

            public SessionInputDialogRequest(
                System.Windows.Application application,
                string prompt,
                string defaultInput,
                string title,
                System.Threading.CancellationToken cancellationToken)
            {
                this.application = application;
                dispatcher = application.Dispatcher;
                this.prompt = prompt;
                this.defaultInput = defaultInput;
                this.title = title;
                this.cancellationToken = cancellationToken;
            }

            public string ShowAndWait()
            {
                using (cancellationToken.Register(Cancel))
                {
                    try
                    {
                        if (dispatcher.CheckAccess())
                            ShowOnDispatcher();
                        else
                            dispatcher.BeginInvoke(new Action(ShowOnDispatcher));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }

                    try
                    {
                        return completion.Task.GetAwaiter().GetResult();
                    }
                    catch (System.Threading.Tasks.TaskCanceledException)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
            }

            private void Cancel()
            {
                if (System.Threading.Interlocked.Exchange(ref cancellationRequested, 1) != 0)
                    return;

                completion.TrySetCanceled();
                try
                {
                    if (dispatcher.CheckAccess())
                        CloseOnDispatcher();
                    else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                        dispatcher.BeginInvoke(new Action(CloseOnDispatcher));
                }
                catch
                {
                    // Completion is already cancelled; shutdown must not strand the script worker.
                }
            }

            private void ShowOnDispatcher()
            {
                if (System.Threading.Volatile.Read(ref cancellationRequested) != 0 ||
                    cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled();
                    return;
                }

                try
                {
                    var localDialog = new llcom_plus.InputDialogWindow(prompt, defaultInput, title);
                    dialog = localDialog;
                    var owner = application.Windows
                        .OfType<System.Windows.Window>()
                        .FirstOrDefault(window => window.IsActive) ?? application.MainWindow;
                    if (owner != null && owner.IsVisible)
                        localDialog.Owner = owner;
                    else
                        localDialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                    if (System.Threading.Volatile.Read(ref cancellationRequested) != 0 ||
                        cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled();
                        return;
                    }

                    var accepted = localDialog.ShowDialog() ?? false;
                    if (System.Threading.Volatile.Read(ref cancellationRequested) != 0 ||
                        cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled();
                    }
                    else
                    {
                        completion.TrySetResult(accepted ? localDialog.Value : null);
                    }
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                        completion.TrySetCanceled();
                    else
                        completion.TrySetException(ex);
                }
                finally
                {
                    dialog = null;
                }
            }

            private void CloseOnDispatcher()
            {
                var localDialog = dialog;
                if (localDialog == null)
                    return;

                try
                {
                    localDialog.Close();
                }
                catch
                {
                }
            }
        }

        public static event EventHandler<Model.LinePlotPoint> LinePlotAdd;

        public static void AddPoint(double n, int l)
        {
            LinePlotAdd?.Invoke(null, new Model.LinePlotPoint { N = n, Line = l });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SendChannelRegistration> SendChannels =
            new System.Collections.Concurrent.ConcurrentDictionary<string, SendChannelRegistration>();

        public static IDisposable SendChannelsRegister(string channel, Func<byte[], object, bool> cb)
        {
            if (channel == null)
                throw new ArgumentNullException(nameof(channel));
            if (cb == null)
                throw new ArgumentNullException(nameof(cb));

            var registration = new SendChannelRegistration(channel, cb);
            SendChannels.AddOrUpdate(channel, registration, (key, previous) => registration);
            return registration;
        }

        public static bool Send(string channel, object data)
        {
            return Send(channel, data, null);
        }

        public static bool Send(string channel, object data, object options)
        {
            if (!SendChannels.TryGetValue(channel, out var registration))
                return false;
            return registration.Callback(ToBytes(data), options);
        }

        private sealed class SendChannelRegistration : IDisposable
        {
            private int disposed = 0;

            public SendChannelRegistration(string channel, Func<byte[], object, bool> callback)
            {
                Channel = channel;
                Callback = callback;
            }

            public string Channel { get; }
            public Func<byte[], object, bool> Callback { get; }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0)
                    return;
                ((ICollection<KeyValuePair<string, SendChannelRegistration>>)SendChannels).Remove(
                    new KeyValuePair<string, SendChannelRegistration>(Channel, this));
            }
        }

        public static void SendChannelsReceived(string channel, object data)
        {
            JavaScriptRunEnv.ChannelReceived(channel, data);
        }

        public static string BytesToString(object data)
        {
            return Tools.Global.GetEncoding().GetString(ToBytes(data));
        }

        public static byte[] StringToBytes(object data)
        {
            return Tools.Global.GetEncoding().GetBytes(data?.ToString() ?? "");
        }

        public static string BytesToHex(object data)
        {
            return Tools.Global.Byte2Hex(ToBytes(data), " ");
        }

        public static byte[] HexToBytes(object data)
        {
            return Tools.Global.Hex2Byte(data?.ToString() ?? "");
        }

        public static byte[] ConcatBytes(params object[] values)
        {
            var result = new List<byte>();
            if (values == null)
                return result.ToArray();
            foreach (var value in values)
                result.AddRange(ToBytes(value));
            return result.ToArray();
        }

        public static byte[] ToBytes(object value)
        {
            if (value == null)
                return new byte[0];
            if (value is JsValue jsValue)
                return ToBytes(jsValue);
            if (value is byte[] bytes)
                return bytes;
            if (value is string text)
                return Tools.Global.GetEncoding().GetBytes(text);
            if (value is char[] chars)
                return Tools.Global.GetEncoding().GetBytes(new string(chars));
            if (value is Array array)
                return ArrayToBytes(array);
            if (value is IEnumerable enumerable)
                return EnumerableToBytes(enumerable);
            return Tools.Global.GetEncoding().GetBytes(value.ToString());
        }

        public static byte[] ToBytes(JsValue value)
        {
            if (value == JsValue.Null || value == JsValue.Undefined)
                return new byte[0];
            var obj = value.ToObject();
            return ToBytes(obj);
        }

        private static byte[] ArrayToBytes(Array array)
        {
            var result = new byte[array.Length];
            for (int i = 0; i < array.Length; i++)
                result[i] = ToByte(array.GetValue(i));
            return result;
        }

        private static byte[] EnumerableToBytes(IEnumerable enumerable)
        {
            var result = new List<byte>();
            foreach (var item in enumerable)
                result.Add(ToByte(item));
            return result.ToArray();
        }

        private static byte ToByte(object value)
        {
            if (value == null)
                return 0;
            if (value is JsValue jsValue)
                value = jsValue.ToObject();
            try
            {
                return Convert.ToByte(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        public static T GetOption<T>(object source, string name, T defaultValue = default(T))
        {
            object value = GetOptionValue(source, name);
            if (value == null)
                return defaultValue;

            try
            {
                if (value is JsValue jsValue)
                {
                    if (jsValue == JsValue.Null || jsValue == JsValue.Undefined)
                        return defaultValue;
                    value = jsValue.ToObject();
                }
                if (typeof(T) == typeof(byte[]))
                    return (T)(object)ToBytes(value);
                if (typeof(T) == typeof(string))
                    return (T)(object)(value?.ToString() ?? "");
                if (value is T typed)
                    return typed;
                return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static object GetOptionValue(object source, string name)
        {
            if (source == null || string.IsNullOrEmpty(name))
                return null;
            if (source is JsValue jsValue)
            {
                if (jsValue == JsValue.Null || jsValue == JsValue.Undefined)
                    return null;
                source = jsValue.ToObject();
            }
            if (source is ObjectInstance objectInstance)
            {
                var jsName = JsValue.FromObject(objectInstance.Engine, name);
                var value = objectInstance.Get(jsName);
                if (value == JsValue.Null || value == JsValue.Undefined)
                    return null;
                return value;
            }
            if (source is IDictionary dictionary && dictionary.Contains(name))
                return dictionary[name];

            var type = source.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property != null)
                return property.GetValue(source);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return field?.GetValue(source);
        }

        public static string UnescapeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return Regex.Replace(text, @"\\(r|n|t|0|x[0-9a-fA-F]{2})", m =>
            {
                var v = m.Groups[1].Value;
                switch (v)
                {
                    case "r":
                        return "\r";
                    case "n":
                        return "\n";
                    case "t":
                        return "\t";
                    case "0":
                        return "\0";
                    default:
                        return ((char)byte.Parse(v.Substring(1), NumberStyles.HexNumber)).ToString();
                }
            });
        }
    }
}
