using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private Dictionary<int, string> workspacePortRoles = new Dictionary<int, string>();
        private FrameworkElement CreateWorkspacePage() => new Pages.WorkspacePage(
            CaptureWorkspace, GetWorkspaceBlockReason, ApplyWorkspace);

        private WorkspaceSnapshot CaptureWorkspace()
        {
            SaveSendList(null, EventArgs.Empty);
            var count = mainSplitPortPage?.SlotCount ?? 1;
            var layout = new WorkspaceLayout
            {
                SplitCount = count,
                ActiveSlot = Math.Max(1, Math.Min(count, lastSerialSendTargetSlot))
            };
            for (int slot = 1; slot <= count; slot++)
                layout.Ports.Add(new WorkspacePortRole
                {
                    Slot = slot,
                    PortName = mainSplitPortPage?.GetSlotPortName(slot) ?? GetSelectedPortName(),
                    Role = workspacePortRoles.TryGetValue(slot, out var role) ? role : "串口 " + slot
                });
            return WorkspaceService.Capture(Global.setting, layout, "Current");
        }

        private string GetWorkspaceBlockReason()
        {
            if (IsQuickWorkflowRunning) return "请先停止快捷发送流程，再加载工作区。";
            if (windowIsClosing || Global.isMainWindowsClosed) return "程序正在关闭。";
            if (isOpeningPort || Global.uart.IsOpen() || (mainSplitPortPage != null &&
                Enumerable.Range(1, mainSplitPortPage.SlotCount).Any(mainSplitPortPage.IsSlotOpen)))
                return "请先手动关闭所有串口，再加载工作区。工作区不会替你断开或重新连接设备。";
            if (ScriptEnv.JavaScriptRunEnv.isRunning || quickSendImportCts != null)
                return "请先停止脚本或等待指令导入完成，再加载工作区。";
            foreach (var module in toolModules)
            {
                var content = module.PeekContent();
                var page = content is Frame frame ? frame.Content : content;
                if (page is Pages.CircularSendPage circular && circular.IsWorkflowRunning ||
                    page is Pages.LogReplayPage replay && replay.IsWorkflowRunning)
                    return "请先停止循环发送、AT 测试和日志回放，再加载工作区。";
            }
            return null;
        }

        private void ApplyWorkspace(WorkspaceSnapshot snapshot)
        {
            var reason = GetWorkspaceBlockReason();
            if (reason != null) throw new InvalidOperationException(reason);
            var canSave = canSaveSendList;
            var syncing = syncingSerialSplitControls;
            canSaveSendList = false;
            syncingSerialSplitControls = true;
            // A loaded configuration never resumes a previous automatic reconnect.
            forcusClosePort = true;
            try
            {
                CloseQuickSendItemSettings();
                Global.setting.ApplyWorkspaceConfiguration(snapshot);
                workspacePortRoles = snapshot.Layout.Ports.ToDictionary(p => p.Slot, p => p.Role);
                lastSerialSendTargetSlot = snapshot.Layout.ActiveSlot;
                ApplySerialSplitLayout();
                syncingSerialSplitControls = true;
                if (snapshot.Layout.SplitCount > 1)
                    mainSplitPortPage.ConfigureWorkspaceLayout(snapshot.Layout);
                else
                {
                    var port = snapshot.Layout.Ports[0].PortName;
                    if (!string.IsNullOrWhiteSpace(port)) Global.uart.SetName(port);
                    SetWorkspaceSelectedPort(port);
                    Global.setting.SetActiveUartProfile(port, usesMainUart: true);
                }
                LoadQuickSendList();
                SetBaudRateComboBoxFromSetting();
                UpdateMainSerialConnectionStatus();
                if (snapshot.Layout.SplitCount > 1) MainSplitPortPage_ActiveSlotChanged(snapshot.Layout.ActiveSlot);
                Global.NotifyUartProfileChanged();
            }
            finally { canSaveSendList = canSave; syncingSerialSplitControls = syncing; }
        }

        private void SetWorkspaceSelectedPort(string port)
        {
            var option = serialPortsListComboBox.Items.Cast<object>().FirstOrDefault(item =>
                string.Equals(ExtractPortName(item?.ToString()), port, StringComparison.OrdinalIgnoreCase));
            if (option == null && !string.IsNullOrEmpty(port))
            {
                option = port + " (工作区 / workspace)";
                serialPortsListComboBox.Items.Add(option);
            }
            serialPortsListComboBox.SelectedItem = option;
            if (option == null) serialPortsListComboBox.Text = "";
        }
    }
}
