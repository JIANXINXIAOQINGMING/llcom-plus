using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;
using FontAwesome.WPF;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using Path = System.IO.Path;

using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml;

namespace llcom_plus
{
    /// <summary>
    /// SettingWindow.xaml 的交互逻辑
    /// </summary>
    public partial class SettingWindow : Window
    {
        private const string SendScriptDirectory = "user_script_send_convert";
        private const string ReceiveScriptDirectory = "user_script_recv_convert";

        public SettingWindow()
        {
            InitializeComponent();
        }

        //重载锁，防止逻辑卡死
        private static bool fileLoading = false;
        private static bool fileLoadingRev = false;
        //上次打开文件名
        private static string lastScriptFile = "";
        private static string lastScriptFileRev = "";
        private bool loaded = false;
        private bool refreshingUartControls = false;

        /// <summary>
        /// 加载脚本文件
        /// </summary>
        /// <param name="fileName">文件名，不带.js</param>
        private void loadScriptFile(string fileName)
        {
            if (!Tools.Global.TryGetProfileScriptPath(
                    SendScriptDirectory,
                    fileName,
                    out var normalizedName,
                    out var scriptPath) ||
                !File.Exists(scriptPath))
            {
                if (!Tools.Global.TryGetProfileScriptPath(
                        SendScriptDirectory,
                        "default",
                        out normalizedName,
                        out scriptPath))
                {
                    throw new InvalidOperationException("发送脚本目录无效。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                if (!File.Exists(scriptPath))
                    File.Create(scriptPath).Close();
            }

            Tools.Global.setting.sendScript = normalizedName;
            textEditor.Text = File.ReadAllText(scriptPath);

            var scriptRoot = Path.GetDirectoryName(scriptPath);
            fileLoading = true;
            try
            {
                scriptFileList.Items.Clear();
                foreach (var file in new DirectoryInfo(scriptRoot).GetFiles("*.js", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file.Name);
                    if (!Tools.Global.TryGetCanonicalScriptPath(
                            scriptRoot,
                            name,
                            out name,
                            out var canonicalPath) ||
                        !string.Equals(
                            Path.GetFullPath(file.FullName),
                            canonicalPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    scriptFileList.Items.Add(name);
                    if (string.Equals(name, normalizedName, StringComparison.Ordinal))
                        scriptFileList.SelectedIndex = scriptFileList.Items.Count - 1;
                }
            }
            finally
            {
                fileLoading = false;
            }

            lastScriptFile = normalizedName;
            ScriptEnv.JavaScriptLoader.ClearRun();
        }
        private void loadScriptFileRev(string fileName)
        {
            if (!Tools.Global.TryGetProfileScriptPath(
                    ReceiveScriptDirectory,
                    fileName,
                    out var normalizedName,
                    out var scriptPath) ||
                !File.Exists(scriptPath))
            {
                if (!Tools.Global.TryGetProfileScriptPath(
                        ReceiveScriptDirectory,
                        "default",
                        out normalizedName,
                        out scriptPath))
                {
                    throw new InvalidOperationException("接收脚本目录无效。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                if (!File.Exists(scriptPath))
                    File.Create(scriptPath).Close();
            }

            Tools.Global.setting.recvScript = normalizedName;
            textEditorRev.Text = File.ReadAllText(scriptPath);

            var scriptRoot = Path.GetDirectoryName(scriptPath);
            fileLoadingRev = true;
            try
            {
                scriptFileListRev.Items.Clear();
                foreach (var file in new DirectoryInfo(scriptRoot).GetFiles("*.js", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(file.Name);
                    if (!Tools.Global.TryGetCanonicalScriptPath(
                            scriptRoot,
                            name,
                            out name,
                            out var canonicalPath) ||
                        !string.Equals(
                            Path.GetFullPath(file.FullName),
                            canonicalPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    scriptFileListRev.Items.Add(name);
                    if (string.Equals(name, normalizedName, StringComparison.Ordinal))
                        scriptFileListRev.SelectedIndex = scriptFileListRev.Items.Count - 1;
                }
            }
            finally
            {
                fileLoadingRev = false;
            }

            lastScriptFileRev = normalizedName;
            ScriptEnv.JavaScriptLoader.ClearRun();
        }

        /// <summary>
        /// 保存脚本文件
        /// </summary>
        /// <param name="fileName">文件名，不带.js</param>
        private void saveScriptFile(string fileName)
        {
            if (!Tools.Global.TryGetProfileScriptPath(
                    SendScriptDirectory,
                    fileName,
                    out _,
                    out var scriptPath))
            {
                throw new ArgumentException("发送脚本名称无效。", nameof(fileName));
            }

            File.WriteAllText(scriptPath, textEditor.Text);
            ScriptEnv.JavaScriptLoader.ClearRun();
        }

        private void saveScriptFileRev(string fileName)
        {
            if (!Tools.Global.TryGetProfileScriptPath(
                    ReceiveScriptDirectory,
                    fileName,
                    out _,
                    out var scriptPath))
            {
                throw new ArgumentException("接收脚本名称无效。", nameof(fileName));
            }

            File.WriteAllText(scriptPath, textEditorRev.Text);
            ScriptEnv.JavaScriptLoader.ClearRun();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (loaded)
            {
                RefreshUartSettingControls();
                return;
            }
            loaded = true;

            this.DataContext = Tools.Global.setting;

            //重写关闭响应代码
            this.Closing += SettingWindow_Closing;

            showHexComboBox.DataContext = Tools.Global.setting;
            //scriptTestHexCheck.DataContext = Tools.Global.setting;
            //scriptTestHexCheckRev.DataContext = Tools.Global.setting;

            //快速搜索
            SearchPanel.Install(textEditor.TextArea);
            SearchPanel.Install(textEditorRev.TextArea);
            Tools.EditorTheme.Apply(textEditor);
            Tools.EditorTheme.Apply(textEditorRev);
            Tools.Global.ThemeChanged += Global_ThemeChanged;
            RefreshLogColorSwatches();
            //加载上次打开的文件
            loadScriptFile(Tools.Global.setting.sendScript);
            loadScriptFileRev(Tools.Global.setting.recvScript);
            MainWindow.recvScriptBackup = Tools.Global.setting.recvScript;
            //加载编码
            var el = Encoding.GetEncodings();
            List<EncodingInfo> encodingList = new List<EncodingInfo>(el);
            //先排个序，美观点
            encodingList.Sort((x, y) => x.CodePage - y.CodePage);
            foreach (var en in encodingList)
            {
                ComboBoxItem c = new ComboBoxItem();
                c.Content = $"[{en.CodePage}] {en.Name}";
                c.Tag = en.CodePage;
                int index = encodingComboBox.Items.Add(c);
                if (Tools.Global.setting.encoding == en.CodePage)//现在用的编码
                    encodingComboBox.SelectedIndex = index;
            }
            RefreshUartSettingControls();
            Tools.Global.UartProfileChangedEvent += Global_UartProfileChangedEvent;
        }

        private void Global_UartProfileChangedEvent(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                if (lastScriptFile != "")
                    saveScriptFile(lastScriptFile);
                if (lastScriptFileRev != "")
                    saveScriptFileRev(lastScriptFileRev);

                RefreshUartSettingControls();
                loadScriptFile(Tools.Global.setting.sendScript);
                loadScriptFileRev(Tools.Global.setting.recvScript);
                MainWindow.recvScriptBackup = Tools.Global.setting.recvScript;
            });
        }

        private void Global_ThemeChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Tools.EditorTheme.Apply(textEditor);
                Tools.EditorTheme.Apply(textEditorRev);
                RefreshLogColorSwatches();
            }));
        }

        private void LogColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || Tools.Global.setting == null)
                return;

            var kind = button.Tag as string ?? "";
            var brush = kind == "sent"
                ? Tools.Logger.GetLogDataBrush(true)
                : kind == "received"
                    ? Tools.Logger.GetLogDataBrush(false)
                    : Tools.Logger.GetLogErrorBrush();

            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = System.Drawing.Color.FromArgb(
                    brush.Color.A,
                    brush.Color.R,
                    brush.Color.G,
                    brush.Color.B);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                var selected = dialog.Color;
                var value = $"#{selected.A:X2}{selected.R:X2}{selected.G:X2}{selected.B:X2}";
                switch (kind)
                {
                    case "sent":
                        Tools.Global.setting.logSentColor = value;
                        break;
                    case "received":
                        Tools.Global.setting.logReceivedColor = value;
                        break;
                    default:
                        Tools.Global.setting.logErrorColor = value;
                        break;
                }
            }

            RefreshLogColorSwatches();
            Tools.Global.NotifyLogColorsChanged();
        }

        private void ResetLogColorsButton_Click(object sender, RoutedEventArgs e)
        {
            if (Tools.Global.setting == null)
                return;

            Tools.Global.setting.logSentColor = "";
            Tools.Global.setting.logReceivedColor = "";
            Tools.Global.setting.logErrorColor = "";
            RefreshLogColorSwatches();
            Tools.Global.NotifyLogColorsChanged();
        }

        private void RefreshLogColorSwatches()
        {
            if (SentColorSwatch == null || ReceivedColorSwatch == null || ErrorColorSwatch == null)
                return;

            SentColorSwatch.Background = Tools.Logger.GetLogDataBrush(true);
            ReceivedColorSwatch.Background = Tools.Logger.GetLogDataBrush(false);
            ErrorColorSwatch.Background = Tools.Logger.GetLogErrorBrush();
        }

        private void RefreshUartSettingControls()
        {
            refreshingUartControls = true;
            try
            {
                dataBitsComboBox.SelectedIndex = Math.Max(0, Math.Min(3, Tools.Global.setting.dataBits - 5));
                stopBitComboBox.SelectedIndex = Math.Max(0, Math.Min(2, Tools.Global.setting.stopBit - 1));
                dataCheckComboBox.SelectedIndex = Math.Max(0, Math.Min(4, Tools.Global.setting.parity));

                for (int i = 0; i < encodingComboBox.Items.Count; i++)
                {
                    if ((int)((ComboBoxItem)encodingComboBox.Items[i]).Tag == Tools.Global.setting.encoding)
                    {
                        encodingComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally
            {
                refreshingUartControls = false;
            }
        }

        private void SettingWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //自动保存脚本
            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);
            if (lastScriptFileRev != "")
                saveScriptFileRev(lastScriptFileRev);
            if (Tools.Global.isMainWindowsClosed)
            {
                //说明软件关了
                Tools.Global.UartProfileChangedEvent -= Global_UartProfileChangedEvent;
                Tools.Global.ThemeChanged -= Global_ThemeChanged;
                e.Cancel = false;
            }
            else
            {
                e.Cancel = true;//取消这次关闭事件
                Hide();//隐藏窗口，以便下次调用show
            }
        }

        private void ApiDocumentButton_Click(object sender, RoutedEventArgs e)
        {
            var localDoc = System.IO.Path.Combine(Tools.Global.AppPath, Tools.Global.apiDocumentUrl);
            System.Diagnostics.Process.Start(File.Exists(localDoc) ? localDoc : Tools.Global.apiDocumentUrl);
        }

        private void OpenScriptFolderButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("explorer.exe", Tools.Global.GetTrueProfilePath() + "user_script_send_convert");
        }

        private void DataBitsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (refreshingUartControls)
                return;
            if(dataBitsComboBox.SelectedItem != null)
            {
                Tools.Global.setting.dataBits = dataBitsComboBox.SelectedIndex + 5;
            }
        }

        private void StopBitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (refreshingUartControls)
                return;
            if (stopBitComboBox.SelectedItem != null)
            {
                Tools.Global.setting.stopBit = stopBitComboBox.SelectedIndex + 1;
            }
        }

        private void DataCheckComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (refreshingUartControls)
                return;
            if (dataCheckComboBox.SelectedItem != null)
            {
                Tools.Global.setting.parity = dataCheckComboBox.SelectedIndex;
                //Tools.MessageBox.Show((dataCheckComboBox.SelectedItem as ComboBoxItem).Content.ToString());
            }
        }

        private void NewScriptButton_Click(object sender, RoutedEventArgs e)
        {
            scriptTestWrapPanel.Visibility = Visibility.Collapsed;
            newScriptFileWrapPanel.Visibility = Visibility.Visible;
        }

        private void TestScriptButton_Click(object sender, RoutedEventArgs e)
        {
            newScriptFileWrapPanel.Visibility = Visibility.Collapsed;
            scriptTestWrapPanel.Visibility = Visibility.Visible;
        }

        private void ScriptFileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (scriptFileList.SelectedItem != null && !fileLoading)
            {
                if (lastScriptFile != "")
                    saveScriptFile(lastScriptFile);
                string fileName = scriptFileList.SelectedItem as string;
                loadScriptFile(fileName);
            }
        }

        private void NewScriptFileCancelButton_Click(object sender, RoutedEventArgs e)
        {
            newScriptFileWrapPanel.Visibility = Visibility.Collapsed;
        }

        private void NewScriptFileButton_Click(object sender, RoutedEventArgs e)
        {
            var scriptName = NormalizeScriptFileName(newScriptFileNameTextBox.Text);
            if (!Tools.Global.TryGetProfileScriptPath(
                    SendScriptDirectory,
                    scriptName,
                    out scriptName,
                    out var scriptPath))
            {
                Tools.MessageBox.Show(string.IsNullOrWhiteSpace(scriptName)
                    ? TryFindResource("ScriptNoName") as string ?? "?!"
                    : TryFindResource("ScriptInvalidName") as string ?? "?!");
                return;
            }
            newScriptFileNameTextBox.Text = scriptName;
            if (File.Exists(scriptPath))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptExist") as string ?? "?!");
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.Create(scriptPath).Close();
                loadScriptFile(scriptName);
            }
            catch
            {
                Tools.MessageBox.Show(TryFindResource("ScriptCreateFail") as string ?? "?!");
                return;
            }
            newScriptFileWrapPanel.Visibility = Visibility.Collapsed;
        }

        private void ScriptTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (scriptFileList.SelectedItem != null && !fileLoading)
            {
                try
                {
                    saveScriptFile(scriptFileList.SelectedItem as string);
                    byte[] r = ScriptEnv.JavaScriptLoader.Run($"{scriptFileList.SelectedItem as string}.js",
                                        new System.Collections.ArrayList{"uartData",
                                            scriptTestHexCheck.IsChecked == true ? Tools.Global.Hex2Byte(scriptTestTextBox.Text) :
                                            Tools.Global.GetEncoding().GetBytes(scriptTestTextBox.Text)});
                    if (r == null)
                    {
                        Tools.MessageBox.Show($"{TryFindResource("SettingScriptRunResult") as string ?? "?!"}\r\nnull");
                        return;
                    }
                    Tools.MessageBox.Show($"{TryFindResource("SettingScriptRunResult") as string ?? "?!"}\r\nHEX：" + Tools.Global.Byte2Hex(r) +
                        $"\r\n{TryFindResource("SettingScriptRawText") as string ?? "?!"}" + Tools.Global.Byte2Readable(r));
                }
                catch(Exception ex)
                {
                    Tools.MessageBox.Show($"{TryFindResource("ErrorScript") as string ?? "?!"}\r\n" + ex.ToString());
                }

            }
        }

        private void ScriptTestCancelButton_Click(object sender, RoutedEventArgs e)
        {
            scriptTestWrapPanel.Visibility = Visibility.Collapsed;
        }

        private void TextEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            //自动保存脚本
            if (lastScriptFile != "")
                saveScriptFile(lastScriptFile);
        }

        private void OpenLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", Tools.Global.GetTrueProfilePath() + "logs");
            }
            catch
            {
                Tools.MessageBox.Show($"尝试打开文件夹失败，请自行打开该路径：{Tools.Global.GetTrueProfilePath()}logs");
            }
        }

        private void encodingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (refreshingUartControls)
                return;
            ComboBox c = sender as ComboBox;
            if (c?.SelectedItem == null)
                return;
            if ((int)((ComboBoxItem)c.SelectedItem).Tag == Tools.Global.setting.encoding)
                return;
            Tools.Global.setting.encoding = (int)((ComboBoxItem)c.SelectedItem).Tag;
        }

        private void scriptFileListRev_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (scriptFileListRev.SelectedItem != null && !fileLoadingRev)
            {
                if (lastScriptFileRev != "")
                    saveScriptFileRev(lastScriptFileRev);
                string fileName = scriptFileListRev.SelectedItem as string;
                loadScriptFileRev(fileName);
                MainWindow.recvScriptBackup = fileName;
            }
        }

        private void newScriptButtonRev_Click(object sender, RoutedEventArgs e)
        {
            scriptTestWrapPanelRev.Visibility = Visibility.Collapsed;
            newScriptFileWrapPanelRev.Visibility = Visibility.Visible;
        }

        private void testScriptButtonRev_Click(object sender, RoutedEventArgs e)
        {
            newScriptFileWrapPanelRev.Visibility = Visibility.Collapsed;
            scriptTestWrapPanelRev.Visibility = Visibility.Visible;
        }

        private void openScriptFolderButtonRev_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start("explorer.exe", Tools.Global.GetTrueProfilePath() + "user_script_recv_convert");
        }

        private void newScriptFileButtonRev_Click(object sender, RoutedEventArgs e)
        {
            var scriptName = NormalizeScriptFileName(newScriptFileNameTextBoxRev.Text);
            if (!Tools.Global.TryGetProfileScriptPath(
                    ReceiveScriptDirectory,
                    scriptName,
                    out scriptName,
                    out var scriptPath))
            {
                Tools.MessageBox.Show(string.IsNullOrWhiteSpace(scriptName)
                    ? TryFindResource("ScriptNoName") as string ?? "?!"
                    : TryFindResource("ScriptInvalidName") as string ?? "?!");
                return;
            }
            newScriptFileNameTextBoxRev.Text = scriptName;
            if (File.Exists(scriptPath))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptExist") as string ?? "?!");
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
                File.Create(scriptPath).Close();
                loadScriptFileRev(scriptName);
            }
            catch
            {
                Tools.MessageBox.Show(TryFindResource("ScriptCreateFail") as string ?? "?!");
                return;
            }
            newScriptFileWrapPanelRev.Visibility = Visibility.Collapsed;
        }

        private void newScriptFileCancelButtonRev_Click(object sender, RoutedEventArgs e)
        {
            newScriptFileWrapPanelRev.Visibility = Visibility.Collapsed;
        }

        private void scriptTestButtonRev_Click(object sender, RoutedEventArgs e)
        {
            if (scriptFileListRev.SelectedItem != null && !fileLoadingRev)
            {
                try
                {
                    saveScriptFileRev(scriptFileListRev.SelectedItem as string);
                    var testData = scriptTestHexCheckRev.IsChecked == true ?
                        Tools.Global.Hex2Byte(scriptTestTextBoxRev.Text) :
                        Tools.Global.GetEncoding().GetBytes(scriptTestTextBoxRev.Text);
                    byte[] r = ScriptEnv.JavaScriptLoader.Run(
                        $"{scriptFileListRev.SelectedItem as string}.js",
                        new System.Collections.ArrayList{
                            "uartData", testData,
                            "uartPara", scriptTestParaTextBoxRev.Text ?? "",
                            "uartSendRaw", testData,
                        },
                        "user_script_recv_convert/");
                    if (r == null)
                    {
                        Tools.MessageBox.Show($"{TryFindResource("SettingScriptRunResult") as string ?? "?!"}\r\nnull");
                        return;
                    }
                    Tools.MessageBox.Show($"{TryFindResource("SettingScriptRunResult") as string ?? "?!"}\r\nHEX：" + Tools.Global.Byte2Hex(r) +
                        $"\r\n{TryFindResource("SettingScriptRawText") as string ?? "?!"}" + Tools.Global.Byte2Readable(r));
                }
                catch (Exception ex)
                {
                    Tools.MessageBox.Show($"{TryFindResource("ErrorRecvScript") as string ?? "?!"}\r\n" + ex.ToString());
                }
            }
        }

        private void scriptTestCancelButtonRev_Click(object sender, RoutedEventArgs e)
        {
            scriptTestWrapPanelRev.Visibility = Visibility.Collapsed;
        }

        private void textEditorRev_LostFocus(object sender, RoutedEventArgs e)
        {
            //自动保存脚本
            if (lastScriptFileRev != "")
                saveScriptFileRev(lastScriptFileRev);
        }

        private static string NormalizeScriptFileName(string fileName)
        {
            return Tools.Global.NormalizeScriptFileName(fileName);
        }

        private static bool IsValidScriptFileName(string fileName)
        {
            return Tools.Global.IsValidScriptFileName(fileName);
        }
    }
}
