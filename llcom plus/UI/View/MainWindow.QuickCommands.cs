using llcom_plus.Model;
using llcom_plus.Tools;
using System;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private QuickSendBackupItem deletedQuickCommand;
        private int deletedQuickCommandIndex;
        private ToSendData deletedQuickPlaceholder;
        private string deletedQuickPlaceholderState;
        private string quickCommandSearch = "";
        private int quickCommandSearchIndex = -1;

        private void ClearQuickSendUndo()
        {
            deletedQuickCommand = null;
            deletedQuickPlaceholder = null;
            deletedQuickPlaceholderState = null;
            quickCommandSearchIndex = -1;
            if (UndoQuickCommandButton != null) UndoQuickCommandButton.IsEnabled = false;
        }
        private void RememberQuickSendDeletion(ToSendData item)
        {
            deletedQuickCommand = QuickSendBackupItem.FromModel(item);
            deletedQuickCommandIndex = toSendListItems.IndexOf(item);
            deletedQuickPlaceholder = toSendListItems.Count == 1 ? item : null;
            UndoQuickCommandButton.IsEnabled = true;
        }
        private ToSendData CloneQuickCommand(QuickSendBackupItem item)
        {
            var canSave = canSaveSendList;
            canSaveSendList = false;
            try { return item.ToModel(); }
            finally { canSaveSendList = canSave; }
        }
        private void CaptureQuickSendPlaceholder()
        {
            deletedQuickPlaceholderState = deletedQuickPlaceholder == null ? null :
                Newtonsoft.Json.JsonConvert.SerializeObject(QuickSendBackupItem.FromModel(deletedQuickPlaceholder));
        }
        private void UndoQuickCommand_Click(object sender, RoutedEventArgs e)
        {
            if (deletedQuickCommand == null) return;
            if (toSendListItems.Count >= MaxQuickSendItemsPerPage)
            { Tools.MessageBox.Show("当前页指令数量已达上限。"); return; }
            var restored = CloneQuickCommand(deletedQuickCommand);
            // Remove the last-row placeholder only if it is still blank. Never
            // overwrite edits made after deletion merely to implement undo.
            if (deletedQuickPlaceholder != null && toSendListItems.Contains(deletedQuickPlaceholder) &&
                deletedQuickPlaceholderState != null && string.Equals(deletedQuickPlaceholderState,
                    Newtonsoft.Json.JsonConvert.SerializeObject(QuickSendBackupItem.FromModel(deletedQuickPlaceholder)),
                    StringComparison.Ordinal)) toSendListItems.Remove(deletedQuickPlaceholder);
            toSendListItems.Insert(Math.Max(0, Math.Min(deletedQuickCommandIndex, toSendListItems.Count)), restored);
            ClearQuickSendUndo();
            FinishQuickCommandEdit(restored);
        }
        private void QuickCommandDuplicate_Click(object sender, EventArgs e)
        {
            var original = QuickSendItemSettingsEditor.Item;
            var index = toSendListItems.IndexOf(original);
            if (index < 0) return;
            if (toSendListItems.Count >= MaxQuickSendItemsPerPage)
            { Tools.MessageBox.Show("当前页指令数量已达上限。"); return; }
            var copy = CloneQuickCommand(QuickSendBackupItem.FromModel(original));
            CloseQuickSendItemSettings();
            toSendListItems.Insert(index + 1, copy);
            FinishQuickCommandEdit(copy);
        }
        private void FinishQuickCommandEdit(ToSendData item)
        {
            CheckToSendListId();
            SaveSendList(null, EventArgs.Empty);
            toSendList.SelectedItem = item;
            toSendList.ScrollIntoView(item);
        }
        private void SearchQuickCommand_Click(object sender, RoutedEventArgs e)
        {
            var answer = InputDialog.OpenDialog("查找当前页指令 / Find command", quickCommandSearch,
                "输入内容或发送按钮名称；再次查找会定位下一条。 / Search again to find next.");
            if (!answer.Item1 || string.IsNullOrWhiteSpace(answer.Item2)) return;
            if (quickCommandSearch != answer.Item2) quickCommandSearchIndex = -1;
            quickCommandSearch = answer.Item2;
            for (int offset = 1; offset <= toSendListItems.Count; offset++)
            {
                var index = (quickCommandSearchIndex + offset) % toSendListItems.Count;
                var item = toSendListItems[index];
                if ((item.text ?? "").IndexOf(quickCommandSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (item.commit ?? "").IndexOf(quickCommandSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                quickCommandSearchIndex = index;
                toSendList.SelectedItem = item;
                toSendList.ScrollIntoView(item);
                var box = GetQuickSendNavigationElement(index, 0) as TextBox;
                if (box != null)
                {
                    box.Focus();
                    var at = (item.text ?? "").IndexOf(quickCommandSearch, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0) box.Select(at, quickCommandSearch.Length);
                }
                return;
            }
            Tools.MessageBox.Show("当前页没有匹配的指令 / No matching command on this page.");
        }
    }
}
