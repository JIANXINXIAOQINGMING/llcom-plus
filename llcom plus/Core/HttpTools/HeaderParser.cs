using System;
using System.Collections.Generic;

namespace llcom_plus.HttpTools
{
    public static class HeaderParser
    {
        public static Dictionary<string, string> Parse(string headerText)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(headerText))
                return headers;

            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                    throw new FormatException($"第 {i + 1} 行请求头格式错误，应为 Key: Value。");

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();

                if (string.IsNullOrWhiteSpace(key))
                    throw new FormatException($"第 {i + 1} 行请求头 Key 不能为空。");

                headers[key] = value;
            }

            return headers;
        }
    }
}
