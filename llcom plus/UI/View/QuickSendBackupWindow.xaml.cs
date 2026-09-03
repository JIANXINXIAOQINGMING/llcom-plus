using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace llcom_plus
{
    public partial class QuickSendBackupWindow : Window
    {
        internal QuickSendBackupInfo SelectedSnapshot { get; private set; }

        public QuickSendBackupWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => RefreshSnapshots();
        }

        private string ResourceText(string key, string fallback)
        {
            return TryFindResource(key) as string ?? fallback;
        }

        private QuickSendBackupRow SelectedRow => SnapshotsGrid.SelectedItem as QuickSendBackupRow;

        private void RefreshSnapshots()
        {
            var rows = QuickSendBackupService.GetSnapshots()
                .Select(item => new QuickSendBackupRow(item))
                .ToList();
            SnapshotsGrid.ItemsSource = rows;
            EmptyMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (rows.Count > 0)
                SnapshotsGrid.SelectedIndex = 0;
        }

        private bool TryGetSelected(out QuickSendBackupInfo snapshot)
        {
            snapshot = SelectedRow?.Snapshot;
            if (snapshot != null)
                return true;
            Tools.MessageBox.Show(ResourceText("QuickSendBackupSelectFirst", "Select a snapshot first."));
            return false;
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelected(out var snapshot))
                return;
            SelectedSnapshot = snapshot;
            DialogResult = true;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelected(out var snapshot))
                return;
            if (!QuickSendBackupService.TryGetExportSource(snapshot.FilePath, out var source, out var suggestedName))
            {
                Tools.MessageBox.Show("Snapshot validation failed.");
                return;
            }

            var dialog = new System.Windows.Forms.SaveFileDialog
            {
                FileName = suggestedName,
                Filter = "JSON (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            try
            {
                File.Copy(source, dialog.FileName, true);
                Tools.MessageBox.Show(ResourceText("QuickSendBackupExportDone", "Snapshot exported."));
            }
            catch (Exception ex)
            {
                Tools.MessageBox.Show(ex.Message);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSelected(out var snapshot))
                return;
            var answer = System.Windows.MessageBox.Show(
                this,
                ResourceText("QuickSendBackupDeleteConfirm", "Delete the selected snapshot?"),
                ResourceText("QuickSendBackupWindowTitle", "Quick send backup and restore"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;

            if (!QuickSendBackupService.Delete(snapshot, out var error))
            {
                Tools.MessageBox.Show(error);
                return;
            }
            RefreshSnapshots();
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(QuickSendBackupService.BackupDirectory);
                Process.Start(QuickSendBackupService.BackupDirectory);
            }
            catch (Exception ex)
            {
                Tools.MessageBox.Show(ex.Message);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private sealed class QuickSendBackupRow
        {
            internal QuickSendBackupRow(QuickSendBackupInfo snapshot)
            {
                Snapshot = snapshot;
                TimeText = snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                Reason = FormatReason(snapshot.Reason);
                AppVersion = snapshot.AppVersion;
                PageCount = snapshot.PageCount;
                ItemCount = snapshot.NonEmptyItemCount;
            }

            internal QuickSendBackupInfo Snapshot { get; }
            public string TimeText { get; }
            public string Reason { get; }
            public string AppVersion { get; }
            public int PageCount { get; }
            public int ItemCount { get; }

            private static string FormatReason(string reason)
            {
                var value = (reason ?? string.Empty).Trim();
                var normalized = value.ToLowerInvariant();
                var chinese = (Tools.Global.setting?.language ?? string.Empty)
                    .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                if (normalized == "auto" || normalized == "change")
                    return chinese ? "自动保存" : "Automatic";
                if (normalized == "startup")
                    return chinese ? "启动保护" : "Startup";
                if (normalized.StartsWith("pre-delete", StringComparison.Ordinal))
                    return chinese ? "删除前" : "Before deletion";
                if (normalized == "pre-import")
                    return chinese ? "导入前" : "Before import";
                if (normalized == "pre-restore")
                    return chinese ? "恢复前" : "Before restore";
                if (normalized == "restore")
                    return chinese ? "恢复完成" : "Restored";
                if (normalized.IndexOf("pre-upgrade", StringComparison.Ordinal) >= 0)
                    return chinese ? "升级前" : "Before upgrade";
                if (normalized.IndexOf("recovery", StringComparison.Ordinal) >= 0 ||
                    normalized.IndexOf("legacy", StringComparison.Ordinal) >= 0)
                    return chinese ? "旧备份恢复" : "Legacy recovery";
                return value;
            }
        }
    }
}
