using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace llcom_plus.Tools
{
    /// <summary>Searches a bounded copy of the displayed log, never the receive thread or raw log.</summary>
    internal static class LogTextSearch
    {
        internal const int MaximumCharacters = 1024 * 1024;
        internal const int MaximumMatches = 2000;

        internal sealed class Hit
        {
            public int Index { get; set; }
            public int Length { get; set; }
        }

        internal sealed class Result
        {
            public List<Hit> Hits { get; } = new List<Hit>();
            public bool LimitReached { get; set; }
        }

        internal static Result Find(string text, string query, bool regularExpression, bool matchCase,
            CancellationToken cancellationToken)
        {
            var result = new Result();
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(query))
                return result;
            if (query.Length > 1024)
                throw new ArgumentException("查找内容不能超过 1024 个字符。");
            if ((text ?? string.Empty).Length > MaximumCharacters)
                throw new ArgumentException("搜索快照过大，请缩小范围。");
            text = text ?? string.Empty;
            var stopwatch = Stopwatch.StartNew();
            Action checkBudget = () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds > 750)
                    throw new TimeoutException("查找耗时过长，请简化表达式或缩小范围。");
            };

            if (regularExpression)
            {
                var options = RegexOptions.CultureInvariant | RegexOptions.Multiline;
                if (!matchCase)
                    options |= RegexOptions.IgnoreCase;
                var regex = new Regex(query, options, TimeSpan.FromMilliseconds(75));
                for (var match = regex.Match(text); match.Success; match = match.NextMatch())
                {
                    checkBudget();
                    // Empty matches cannot be meaningfully highlighted in a read-only log.
                    if (match.Length == 0)
                        continue;
                    result.Hits.Add(new Hit { Index = match.Index, Length = match.Length });
                    if (result.Hits.Count >= MaximumMatches)
                    {
                        result.LimitReached = true;
                        break;
                    }
                }
            }
            else
            {
                var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                for (var offset = 0; offset <= text.Length - query.Length;)
                {
                    checkBudget();
                    var index = text.IndexOf(query, offset, comparison);
                    if (index < 0)
                        break;
                    result.Hits.Add(new Hit { Index = index, Length = query.Length });
                    if (result.Hits.Count >= MaximumMatches)
                    {
                        result.LimitReached = true;
                        break;
                    }
                    offset = index + Math.Max(1, query.Length);
                }
            }
            return result;
        }
    }
}
