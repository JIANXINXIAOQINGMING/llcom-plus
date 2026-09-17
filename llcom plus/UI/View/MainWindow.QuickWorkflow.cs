using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private CancellationTokenSource quickWorkflowCts;
        private bool quickWorkflowDispatching;
        private Dictionary<int, llcom_plus.Model.ToSendData> quickWorkflowSourceRows;
        internal bool IsQuickWorkflowRunning => quickWorkflowCts != null;

        private async void QuickWorkflowRun_Click(object sender, RoutedEventArgs e)
        {
            if (IsQuickWorkflowRunning || windowIsClosing) return;
            CancellationTokenSource source = null;
            try
            {
                if (QuickSendItemSettingsPopup.IsOpen && !QuickSendItemSettingsEditor.CommitWorkflowFields())
                    throw new InvalidDataException("请先修正指令设置中的超时或重试次数：必须是整数。");
                var plan = QuickSendWorkflow.CreatePlan(toSendListItems.Select(QuickSendBackupItem.FromModel));
                foreach (var step in plan)
                {
                    if (!string.IsNullOrWhiteSpace(step.Command.ReceiveScriptPath) &&
                        (!Global.TryGetProfileScriptPath("user_script_recv_convert", step.Command.ReceiveScriptPath,
                            out _, out var scriptPath) || !File.Exists(scriptPath)))
                        throw new InvalidOperationException("第 " + step.Command.Id + " 条指令的接收脚本不存在，请先修正设置。尚未发送任何指令。");
                }
                var target = Global.CaptureActiveSerialTarget();
                if (IsMainSendTargetSelected() || target == null || !target.IsOpen ||
                    target.Identity.StartsWith("serial-all:", StringComparison.Ordinal))
                    throw new InvalidOperationException("请先选择并打开一个明确的串口；回复流程不支持“全部”广播或网络目标。");
                if (ScriptEnv.JavaScriptRunEnv.isRunning || quickSendImportCts != null || OtherSerialWorkflowRunning())
                    throw new InvalidOperationException("请先停止脚本、循环发送及日志回放，并等待导入完成，再运行快捷指令流程。");
                var uncheckedCount = plan.Count(step => step.Options.Mode == SerialTestMatchMode.None);
                var retryCount = plan.Count(step => step.Options.Retries > 0);
                var prompt = "向 " + target.DisplayName + " 顺序执行当前页 " + plan.Count + " 条指令？\r\n" +
                    (uncheckedCount == 0 ? "每条指令均需匹配回复才继续。" : uncheckedCount + " 条未设置等待回复，发送完成后会直接继续。") +
                    (retryCount == 0 ? "\r\n不自动重发；未匹配、断开或发送失败时停止。" : "\r\n注意：" + retryCount + " 条已显式配置超时重发，请确认可重复执行。") +
                    "\r\n运行期间请勿使用其他工具向该串口发送；编辑配置仅对下一次运行生效。";
                if (System.Windows.MessageBox.Show(this, prompt, "运行快捷指令流程", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                // Recheck after the modal prompt: a port may close while confirming.
                AssertQuickWorkflowTarget(target);
                SaveSendList(null, EventArgs.Empty);
                CloseQuickSendItemSettings();
                source = new CancellationTokenSource();
                quickWorkflowCts = source;
                quickWorkflowSourceRows = toSendListItems.GroupBy(row => row.id)
                    .ToDictionary(group => group.Key, group => group.First());
                SetQuickWorkflowRunning(true);
                Global.ProgramClosedEvent += QuickWorkflow_ProgramClosed;
                var token = source.Token;
                var result = await QuickSendWorkflow.RunAsync(plan, target.Identity,
                    (step, cancel) => SendQuickWorkflowStepAsync(step, target, cancel),
                    () => target.IsOpen, progress => UpdateQuickWorkflowProgress(source, target.DisplayName, progress), token);
                if (windowIsClosing || Global.isMainWindowsClosed) return;
                SetQuickWorkflowStatus(result.Success
                    ? "流程完成 · " + plan.Count + " 条" + (uncheckedCount == 0 ? "均已验证回复" : "（其中 " + uncheckedCount + " 条未验证回复）")
                    : "流程已停止 · " + result.Details);
            }
            catch (OperationCanceledException)
            {
                if (!windowIsClosing) QuickWorkflowStatusText.Text = "流程已停止；已发出的指令不会撤回。";
            }
            catch (Exception ex)
            {
                if (!windowIsClosing && !Global.isMainWindowsClosed)
                {
                    SetQuickWorkflowStatus("流程未完成 · " + ex.Message);
                    Tools.MessageBox.Show("快捷指令流程已停止\r\n" + ex.Message);
                }
            }
            finally
            {
                if (source != null)
                {
                    Global.ProgramClosedEvent -= QuickWorkflow_ProgramClosed;
                    if (ReferenceEquals(quickWorkflowCts, source)) quickWorkflowCts = null;
                    quickWorkflowSourceRows = null;
                    source.Dispose();
                    SetQuickWorkflowRunning(false);
                }
            }
        }

        private bool OtherSerialWorkflowRunning()
        {
            foreach (var module in toolModules)
            {
                var content = module.PeekContent();
                var page = content is Frame frame ? frame.Content : content;
                if (page is Pages.CircularSendPage circular && circular.IsWorkflowRunning ||
                    page is Pages.LogReplayPage replay && replay.IsWorkflowRunning) return true;
            }
            return false;
        }

        private void AssertQuickWorkflowTarget(ActiveSerialTarget expected)
        {
            var selected = Global.CaptureActiveSerialTarget();
            if (windowIsClosing || Global.isMainWindowsClosed || IsMainSendTargetSelected() ||
                !expected.IsOpen || selected == null || !selected.IsOpen || selected.Identity != expected.Identity)
                throw new InvalidOperationException("串口连接或发送目标已改变。流程停止，不会改发到另一串口，请重新开始。");
        }

        private async Task SendQuickWorkflowStepAsync(QuickWorkflowStep step, ActiveSerialTarget target, CancellationToken token)
        {
            var pending = Dispatcher.Invoke(new Func<Task>(() =>
            {
                token.ThrowIfCancellationRequested();
                AssertQuickWorkflowTarget(target);
                var item = step.Command;
                var data = item.Hex ? Global.Hex2Byte(item.Text) : Global.GetEncoding().GetBytes(item.Text);
                var receiveScript = recvScriptBackup;
                if (!string.IsNullOrWhiteSpace(item.ReceiveScriptPath))
                {
                    if (!Global.TryGetProfileScriptPath("user_script_recv_convert", item.ReceiveScriptPath,
                        out receiveScript, out var scriptPath) || !File.Exists(scriptPath))
                        throw new InvalidOperationException("第 " + item.Id + " 条指令的接收脚本不存在，未发送。请先修正脚本设置。");
                }
                SetReceiveScriptContext(receiveScript, item.ReceiveScriptParameter ?? "", data);
                quickWorkflowDispatching = true;
                try
                {
                    // The ordinary UI pipeline captures the actual connection and profile
                    // synchronously. The bypass ends before awaiting hardware completion.
                    return sendUartData(data, item.Hex, true, item.Hex ? item.Text : null,
                        item.AppendCrlf, item.Text, token, propagateErrors: true, autoOpen: false);
                }
                finally { quickWorkflowDispatching = false; }
            }));
            await pending.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        private void UpdateQuickWorkflowProgress(CancellationTokenSource source, string target, QuickWorkflowProgress progress)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(quickWorkflowCts, source) || windowIsClosing) return;
                QuickWorkflowStatusText.Text = target + " · " + progress.Completed + "/" + progress.Total +
                    " · 第 " + progress.CommandId + " 条" + (progress.Result == null ? "发送 / 等待回复…" :
                    progress.Result.Success ? "完成" : "未匹配，已停止");
                if (quickWorkflowSourceRows != null && quickWorkflowSourceRows.TryGetValue(progress.CommandId, out var row) &&
                    toSendListItems.Contains(row))
                { toSendList.SelectedItem = row; toSendList.ScrollIntoView(row); }
            }));
        }

        private void SetQuickWorkflowStatus(string message)
        {
            var summary = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
            QuickWorkflowStatusText.Text = summary.Length > 150 ? summary.Substring(0, 150) + "…" : summary;
            QuickWorkflowStatusText.ToolTip = message;
        }

        private void SetQuickWorkflowRunning(bool running)
        {
            QuickWorkflowRunButton.IsEnabled = !running;
            QuickWorkflowStopButton.IsEnabled = running;
        }
        private void QuickWorkflowStop_Click(object sender, RoutedEventArgs e) => CancelQuickWorkflow();
        private void QuickWorkflow_ProgramClosed(object sender, EventArgs e) => CancelQuickWorkflow();
        private void CancelQuickWorkflow()
        {
            try { quickWorkflowCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        private bool BlockManualSendDuringQuickWorkflow(bool propagateErrors = false)
        {
            if (!IsQuickWorkflowRunning || quickWorkflowDispatching) return false;
            const string message = "快捷指令流程正在等待回复；请先停止流程，再手动发送或运行其他发送任务。";
            if (propagateErrors) throw new InvalidOperationException(message);
            QuickWorkflowStatusText.Text = message;
            return true;
        }
    }
}
