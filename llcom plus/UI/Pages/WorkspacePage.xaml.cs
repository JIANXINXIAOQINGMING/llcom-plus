using llcom_plus.Tools;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus.Pages
{
    public partial class WorkspacePage : Page
    {
        private readonly Func<WorkspaceSnapshot> capture;
        private readonly Func<string> blockReason;
        private readonly Action<WorkspaceSnapshot> apply;
        private bool initialized;

        public WorkspacePage() : this(null, null, null) { }

        internal WorkspacePage(Func<WorkspaceSnapshot> captureCurrent, Func<string> getLoadBlockReason,
            Action<WorkspaceSnapshot> applyConfiguration)
        {
            capture = captureCurrent;
            blockReason = getLoadBlockReason;
            apply = applyConfiguration;
            InitializeComponent();
        }

        private bool Chinese => (Global.setting?.language ?? "zh").StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        private string T(string chinese, string english) => Chinese ? chinese : english;

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            TitleText.Text = T("工作区", "Workspaces");
            DescriptionText.Text = T("保存项目的串口配置、分屏和快捷指令。加载只恢复配置，不连接、不发送、不启动脚本。",
                "Save project ports, split layout and quick commands. Loading never connects, sends or starts scripts.");
            NameBox.ToolTip = T("工作区名称，例如：模块 A · 联调", "Workspace name, e.g. Module A · integration");
            SaveButton.Content = T("保存当前配置", "Save current");
            LoadButton.Content = T("加载选中", "Load selected");
            ImportButton.Content = T("导入…", "Import…");
            ExportButton.Content = T("导出…", "Export…");
            DeleteButton.Content = T("删除", "Delete");
            ScriptReferencesCheck.Content = T("应用脚本引用（仅限可信工作区）", "Apply script references (trusted workspaces only)");
            ScriptNoticeText.Text = T("默认不应用脚本引用。工作区不包含脚本文件和账号配置；指令正文或脚本参数可能含敏感内容，分享前请检查。",
                "Script references are disabled by default. No script files or account settings are included. Review command text and parameters before sharing: they may contain sensitive data.");
            SaveButton.IsEnabled = capture != null;
            if (!initialized)
            {
                NameBox.Text = T("我的工作区", "My workspace");
                initialized = true;
            }
            RefreshList();
        }

        private void RefreshList(string selectedName = null)
        {
            Run(() =>
            {
                var name = selectedName ?? (WorkspaceList.SelectedItem as WorkspaceInfo)?.Name;
                var items = WorkspaceService.List(Global.ProfilePath);
                WorkspaceList.ItemsSource = items;
                WorkspaceList.SelectedItem = items.FirstOrDefault(i => i.Name == name);
                RefreshSelection();
            });
        }

        private void WorkspaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LoadButton != null) RefreshSelection();
        }

        private void RefreshSelection()
        {
            var selected = WorkspaceList.SelectedItem as WorkspaceInfo;
            var valid = selected != null && selected.Error == null;
            LoadButton.IsEnabled = valid && capture != null && apply != null && blockReason != null;
            ExportButton.IsEnabled = valid;
            DeleteButton.IsEnabled = selected != null;
            ScriptReferencesCheck.IsChecked = false;
            DetailsText.Text = selected == null ? T("选择工作区可查看和加载。", "Select a workspace to inspect and load it.") : selected.Error ?? "";
            if (!valid) return;
            Run(() =>
            {
                var state = WorkspaceService.Load(Global.ProfilePath, selected.Name);
                DetailsText.Text = T("分屏", "Panes") + ": " + state.Layout.SplitCount + "  ·  " +
                    string.Join(" / ", state.Layout.Ports.OrderBy(p => p.Slot).Select(p =>
                        string.IsNullOrEmpty(p.PortName) ? T("未分配", "Unassigned") :
                        p.PortName + (string.IsNullOrEmpty(p.Role) ? "" : " (" + p.Role + ")"))) +
                    "\n" + state.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            });
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Run(() =>
            {
                if (capture == null) throw new InvalidOperationException(T("主窗口尚未就绪。", "Host is not ready."));
                var name = NameBox.Text.Trim();
                WorkspaceService.ValidateName(name);
                var exists = WorkspaceService.List(Global.ProfilePath).Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                if (exists && !Confirm(T("覆盖同名工作区？只替换此工作区快照，不删除当前快捷指令。", "Replace this saved workspace? Current commands will not be deleted."))) return;
                var state = capture();
                state.Name = name;
                state.SavedAtUtc = DateTime.UtcNow;
                WorkspaceService.Save(Global.ProfilePath, state, exists);
                RefreshList(name);
                StatusText.Text = T("已保存。", "Saved.");
            });
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            Run(() =>
            {
                var selected = WorkspaceList.SelectedItem as WorkspaceInfo;
                if (selected == null) return;
                var reason = blockReason?.Invoke();
                if (!string.IsNullOrEmpty(reason)) throw new InvalidOperationException(reason);
                var state = WorkspaceService.Load(Global.ProfilePath, selected.Name);
                var includeScripts = ScriptReferencesCheck.IsChecked == true;
                if (!Confirm(T("加载将替换当前快捷指令、串口配置和分屏布局；先自动备份当前配置。是否继续？",
                    "Replace current quick commands, port settings and layout? A recovery snapshot will be saved first.") +
                    (includeScripts ? "\n\n" + T("已启用脚本引用：后续手动连接或发送时可能运行本地同名脚本。", "Script references are enabled: local scripts may run after you manually connect or send.") : ""))) return;
                WorkspaceService.Restore(Global.ProfilePath, Global.setting, state, capture, blockReason, apply, includeScripts);
                ScriptReferencesCheck.IsChecked = false;
                StatusText.Text = T("已加载配置。串口保持关闭；请核对端口后手动连接。", "Configuration loaded. Ports remain closed; check them before connecting manually.");
            });
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "llcom plus workspace (*.workspace.json;*.json)|*.workspace.json;*.json", CheckFileExists = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            Run(() =>
            {
                var state = WorkspaceService.Read(dialog.FileName);
                var exists = WorkspaceService.List(Global.ProfilePath).Any(i => string.Equals(i.Name, state.Name, StringComparison.OrdinalIgnoreCase));
                if (exists && !Confirm(T("已存在同名工作区，覆盖保存？导入不会立即加载。", "Replace the workspace with the same name? Import does not load it."))) return;
                WorkspaceService.Save(Global.ProfilePath, state, exists);
                RefreshList(state.Name);
                StatusText.Text = T("已导入到列表，未修改当前配置。", "Imported to the list; current configuration is unchanged.");
            });
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var selected = WorkspaceList.SelectedItem as WorkspaceInfo;
            if (selected == null) return;
            var dialog = new SaveFileDialog { FileName = selected.Name + ".workspace.json", Filter = "llcom plus workspace (*.workspace.json)|*.workspace.json", OverwritePrompt = true };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            Run(() =>
            {
                WorkspaceService.Export(dialog.FileName, WorkspaceService.Load(Global.ProfilePath, selected.Name));
                StatusText.Text = T("已导出。分享前请检查指令中的敏感信息。", "Exported. Review commands for sensitive information before sharing.");
            });
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var selected = WorkspaceList.SelectedItem as WorkspaceInfo;
            if (selected == null || !Confirm(T("从列表删除这个工作区？不会删除当前指令或历史快照。最近 20 个删除项仍保留在 workspaces/.deleted。",
                "Remove this saved workspace? Current commands and history are kept. The last 20 deleted workspaces remain in workspaces/.deleted."))) return;
            Run(() =>
            {
                WorkspaceService.Delete(Global.ProfilePath, selected.Name);
                RefreshList();
                StatusText.Text = T("已移入工作区恢复目录。", "Moved to the workspace recovery folder.");
            });
        }

        private bool Confirm(string message)
        {
            var owner = Window.GetWindow(this);
            return (owner == null ? System.Windows.MessageBox.Show(message, T("工作区", "Workspace"), MessageBoxButton.YesNo, MessageBoxImage.Question)
                : System.Windows.MessageBox.Show(owner, message, T("工作区", "Workspace"), MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { StatusText.Text = T("操作未完成：", "Could not complete: ") + ex.Message; }
        }
    }
}
