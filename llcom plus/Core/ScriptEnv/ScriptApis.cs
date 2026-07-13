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
            if (Tools.Global.setting.quickSend.Count < id || id <= 0)
                return "";
            if (Tools.Global.setting.quickSend[id - 1].hex)
                return "H" + Tools.Global.setting.quickSend[id - 1].text;
            return "S" + Tools.Global.setting.quickSend[id - 1].text;
        }

        public static string InputBox(string prompt, string defaultInput = "", string title = null)
        {
            Tuple<bool, string> ret = App.Current.Dispatcher.Invoke(() =>
                Tools.InputDialog.OpenDialog(prompt, defaultInput, title));
            return ret.Item1 ? ret.Item2 : null;
        }

        public static event EventHandler<Model.LinePlotPoint> LinePlotAdd;

        public static void AddPoint(double n, int l)
        {
            LinePlotAdd?.Invoke(null, new Model.LinePlotPoint { N = n, Line = l });
        }

        private static readonly Dictionary<string, Func<byte[], object, bool>> SendChannels =
            new Dictionary<string, Func<byte[], object, bool>>();

        public static void SendChannelsRegister(string channel, Func<byte[], object, bool> cb)
        {
            SendChannels[channel] = cb;
        }

        public static bool Send(string channel, object data)
        {
            return Send(channel, data, null);
        }

        public static bool Send(string channel, object data, object options)
        {
            if (!SendChannels.ContainsKey(channel))
                return false;
            return SendChannels[channel](ToBytes(data), options);
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
