using llcom_plus.Model;
using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace llcom_plus
{
    public partial class QuickSendItemSettings : UserControl
    {
        private bool refreshing;
        internal ToSendData Item => DataContext as ToSendData;
        public event EventHandler CloseRequested;

        public QuickSendItemSettings()
        {
            InitializeComponent();
        }

        internal sealed class ScriptChoice
        {
            public string Name { get; set; }
            public string DisplayName { get; set; }
            public override string ToString() => DisplayName;
        }

        private string Text(string key) => TryFindResource(key) as string ?? key;

        internal void SetItem(ToSendData item)
        {
            refreshing = true;
            try
            {
                // Refreshing a popup, including an unavailable legacy script,
                // must never mutate or normalize the user's saved command.
                DataContext = item;
                SettingsTitle.Text = item == null ? Text("QuickSendItemSettings") :
                    string.Format(Text("QuickSendSettingsTitle"), item.id);
                ScriptWarning.Visibility = Visibility.Collapsed;
                var choices = new List<ScriptChoice>
                {
                    new ScriptChoice { Name = "", DisplayName = Text("QuickSendSettingsScriptDefault") }
                };
                if (item != null)
                {
                    try
                    {
                        var directory = Path.Combine(Global.ProfilePath, "user_script_recv_convert");
                        if (Directory.Exists(directory))
                        {
                            foreach (var file in Directory.GetFiles(directory, "*.js").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                            {
                                var name = Path.GetFileNameWithoutExtension(file);
                                if (Global.TryGetProfileScriptPath("user_script_recv_convert", name, out var normalized, out _))
                                    choices.Add(new ScriptChoice { Name = normalized, DisplayName = normalized });
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                    {
                        ShowWarning("QuickSendSettingsScriptReadError");
                    }
                }
                var current = item?.recvScriptPath ?? "";
                var selected = choices.FirstOrDefault(choice => string.Equals(choice.Name, current, StringComparison.OrdinalIgnoreCase));
                if (selected == null)
                {
                    selected = new ScriptChoice { Name = current, DisplayName = current + " " + Text("QuickSendSettingsScriptMissing") };
                    choices.Add(selected);
                    ShowWarning("QuickSendSettingsScriptKept");
                }
                ScriptComboBox.ItemsSource = choices;
                ScriptComboBox.SelectedItem = selected;
            }
            finally
            {
                refreshing = false;
            }
        }

        private void ScriptComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = Item;
            if (refreshing || item == null || !(ScriptComboBox.SelectedItem is ScriptChoice choice))
                return;
            if (string.Equals(item.recvScriptPath ?? "", choice.Name, StringComparison.Ordinal))
                return;

            string name = "";
            if (!string.IsNullOrEmpty(choice.Name) &&
                (!Global.TryGetProfileScriptPath("user_script_recv_convert", choice.Name, out name, out var path) || !File.Exists(path)))
            {
                SetItem(item);
                ShowWarning("QuickSendSettingsScriptKept");
                return;
            }
            item.recvScriptPath = name;
            // Parameters are independent; selecting the global script does not erase them.
            ScriptWarning.Visibility = Visibility.Collapsed;
        }

        private void ShowWarning(string key)
        {
            ScriptWarning.Text = Text(key);
            ScriptWarning.Visibility = Visibility.Visible;
        }

        internal bool CommitWorkflowFields()
        {
            // A text-to-number binding failure must not silently run with an old value.
            ResponseTimeoutTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ResponseRetriesTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            return !Validation.GetHasError(ResponseTimeoutTextBox) && !Validation.GetHasError(ResponseRetriesTextBox);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
