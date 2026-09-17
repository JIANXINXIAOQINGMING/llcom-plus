using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, ToolModule> toolModuleCache = new Dictionary<string, ToolModule>();
        private readonly HashSet<string> favoriteToolKeys = new HashSet<string>(StringComparer.Ordinal);
        private bool favoritesLoaded;
        private void LoadToolFavorites()
        {
            if (favoritesLoaded) return;
            favoritesLoaded = true;
            try
            {
                var path = System.IO.Path.Combine(Tools.Global.ProfilePath, "tool-favorites.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 8192) return;
                var keys = JsonConvert.DeserializeObject<string[]>(File.ReadAllText(path));
                if (keys != null) foreach (var key in keys.Take(50).Where(k => k != null && k.Length < 50)) favoriteToolKeys.Add(key);
            }
            catch (Exception ex) { Tools.Logger.AddUartLogDebug("[ToolFavorites] " + ex.Message); }
        }
        private static string ToolCategory(string key)
        {
            switch (key)
            {
                case "SerialMonitor": case "LogAnalysis": case "PowerDiagnostics":
                case "LogReplay": case "CircularSend": case "Plot": return "调试与分析 / Analysis";
                case "Mqtt": case "WinUsb": case "HttpTool": case "TcpClient": return "连接 / Connections";
                default: return "配置与实用工具 / Utilities";
            }
        }
        private void BindToolModules()
        {
            var view = CollectionViewSource.GetDefaultView(toolModules);
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(ToolModule.GroupOrder), ListSortDirection.Ascending));
                view.GroupDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolModule.Group)));
            }
            ToolListBox.ItemsSource = view;
        }
        private void ToggleToolFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (!(ToolListBox.SelectedItem is ToolModule module)) return;
            var next = new HashSet<string>(favoriteToolKeys);
            if (!next.Add(module.Key)) next.Remove(module.Key);
            var path = System.IO.Path.Combine(Tools.Global.ProfilePath, "tool-favorites.json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(temporary, JsonConvert.SerializeObject(next.OrderBy(k => k)), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                favoriteToolKeys.Clear();
                foreach (var key in next) favoriteToolKeys.Add(key);
                module.IsFavorite = favoriteToolKeys.Contains(module.Key);
                BindToolModules();
                ToolListBox.SelectedItem = module;
            }
            catch (Exception ex) { Tools.MessageBox.Show("无法保存常用工具 / Unable to save favorites: " + ex.Message); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }
}
