using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    internal sealed class QuickWorkflowStep
    {
        internal QuickWorkflowStep(QuickSendBackupItem command)
        {
            Command = command.Clone();
            Options = new SerialTestOptions(Command.ResponseMode, Command.ExpectedResponse,
                Command.ResponseTimeoutMs, Command.ResponseRetries, false);
        }
        internal QuickSendBackupItem Command { get; }
        internal SerialTestOptions Options { get; }
    }

    internal sealed class QuickWorkflowProgress
    {
        internal int Completed { get; set; }
        internal int Total { get; set; }
        internal int CommandId { get; set; }
        internal SerialTestResult Result { get; set; }
    }

    internal static class QuickSendWorkflow
    {
        // Draft settings may have an empty expectation or unfinished expression.
        // Preserve them in backup/workspace files, but reject them before any send.
        internal static void Validate(QuickSendBackupItem item)
        {
            if (item == null || item.ResponseMode < 0 || item.ResponseMode > 3 ||
                item.ResponseTimeoutMs < 100 || item.ResponseTimeoutMs > 600000 ||
                item.ResponseRetries < 0 || item.ResponseRetries > 10 ||
                (item.ExpectedResponse ?? "").Length > 1024)
                throw new InvalidDataException("指令流程设置无效：超时应为 100～600000 ms，重试为 0～10，预期回复最多 1024 字符。");
        }

        internal static List<QuickWorkflowStep> CreatePlan(IEnumerable<QuickSendBackupItem> commands)
        {
            var result = new List<QuickWorkflowStep>();
            foreach (var item in commands ?? Enumerable.Empty<QuickSendBackupItem>())
            {
                if (item == null || item.SkipInWorkflow || string.IsNullOrWhiteSpace(item.Text)) continue;
                try
                {
                    Validate(item);
                    if (item.Hex)
                    {
                        var hex = new string(item.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c)))
                            throw new ArgumentException("HEX 指令须由完整十六进制字节组成，例如 41 54 0D 0A。");
                    }
                    result.Add(new QuickWorkflowStep(item));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException)
                { throw new InvalidDataException("第 " + item.Id + " 条指令：" + ex.Message, ex); }
                if (result.Count > 2000) throw new InvalidDataException("当前页流程最多 2000 条指令。");
            }
            if (result.Count == 0) throw new InvalidDataException("当前页没有可运行的指令。请添加内容，并取消“流程中跳过”。");
            return result;
        }

        internal static async Task<SerialTestResult> RunAsync(IReadOnlyList<QuickWorkflowStep> plan,
            string identity, Func<QuickWorkflowStep, CancellationToken, Task> send,
            Func<bool> connectionOpen, Action<QuickWorkflowProgress> progress, CancellationToken token)
        {
            if (plan == null || plan.Count == 0 || send == null) throw new ArgumentException("流程不能为空。");
            SerialTestResult last = null;
            for (int i = 0; i < plan.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var step = plan[i];
                progress?.Invoke(new QuickWorkflowProgress { Completed = i, Total = plan.Count, CommandId = step.Command.Id });
                last = await SerialTestRunner.ExecuteAsync(step.Options, identity,
                    cancellation => send(step, cancellation), connectionOpen, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                progress?.Invoke(new QuickWorkflowProgress { Completed = i + 1, Total = plan.Count,
                    CommandId = step.Command.Id, Result = last });
                if (!last.Success) return last;
            }
            return last;
        }
    }
}
