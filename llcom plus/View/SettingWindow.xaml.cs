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

        /// <summary>
        /// 加载脚本文件
        /// </summary>
        /// <param name="fileName">文件名，不带.js</param>
        private void loadScriptFile(string fileName)
        {
            //检查文件是否存在
            if (!File.Exists(Tools.Global.ProfilePath + $"user_script_send_convert/{fileName}.js"))
            {
                Tools.Global.setting.sendScript = "default";
                if (!File.Exists(Tools.Global.ProfilePath + $"user_script_send_convert/{Tools.Global.setting.sendScript}.js"))
                {
                    File.Create(Tools.Global.ProfilePath + $"user_script_send_convert/{Tools.Global.setting.sendScript}.js").Close();
                }
            }
            else
            {
                Tools.Global.setting.sendScript = fileName;
            }

            //文件内容显示出来
            textEditor.Text = File.ReadAllText(Tools.Global.ProfilePath + $"user_script_send_convert/{Tools.Global.setting.sendScript}.js");

            //刷新文件列表
            DirectoryInfo scriptFileDir = new DirectoryInfo(Tools.Global.ProfilePath + "user_script_send_convert/");
            FileSystemInfo[] scriptFiles = scriptFileDir.GetFileSystemInfos();
            fileLoading = true;
            scriptFileList.Items.Clear();
            for (int i = 0; i < scriptFiles.Length; i++)
            {
                FileInfo file = scriptFiles[i] as FileInfo;
                //是文件
                if (file != null && file.Name.EndsWith(".js"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    scriptFileList.Items.Add(name);
                    if (name == Tools.Global.setting.sendScript)
                    {
                        scriptFileList.SelectedIndex = scriptFileList.Items.Count - 1;
                    }
                }
            }
            lastScriptFile = Tools.Global.setting.sendScript;
            fileLoading = false;

            //重载脚本
            ScriptEnv.JavaScriptLoader.ClearRun();
        }
        private void loadScriptFileRev(string fileName)
        {
            //检查文件是否存在
            if (!File.Exists(Tools.Global.ProfilePath + $"user_script_recv_convert/{fileName}.js"))
            {
                Tools.Global.setting.recvScript = "default";
                if (!File.Exists(Tools.Global.ProfilePath + $"user_script_recv_convert/{Tools.Global.setting.recvScript}.js"))
                {
                    File.Create(Tools.Global.ProfilePath + $"user_script_recv_convert/{Tools.Global.setting.recvScript}.js").Close();
                }
            }
            else
            {
                Tools.Global.setting.recvScript = fileName;
            }

            //文件内容显示出来
            textEditorRev.Text = File.ReadAllText(Tools.Global.ProfilePath + $"user_script_recv_convert/{Tools.Global.setting.recvScript}.js");

            //刷新文件列表
            DirectoryInfo scriptFileDir = new DirectoryInfo(Tools.Global.ProfilePath + "user_script_recv_convert/");
            FileSystemInfo[] scriptFiles = scriptFileDir.GetFileSystemInfos();
            fileLoadingRev = true;
            scriptFileListRev.Items.Clear();
            for (int i = 0; i < scriptFiles.Length; i++)
            {
                FileInfo file = scriptFiles[i] as FileInfo;
                //是文件
                 if (file != null && file.Name.EndsWith(".js"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    scriptFileListRev.Items.Add(name);
                    if (name== Tools.Global.setting.recvScript)
                    {
                        scriptFileListRev.SelectedIndex = scriptFileListRev.Items.Count - 1;
                    }
                }
            }
            lastScriptFileRev = Tools.Global.setting.recvScript;
            fileLoadingRev = false;

            //重载脚本
            ScriptEnv.JavaScriptLoader.ClearRun();
        }

        /// <summary>
        /// 保存脚本文件
        /// </summary>
        /// <param name="fileName">文件名，不带.js</param>
        private void saveScriptFile(string fileName)
        {
            File.WriteAllText(Tools.Global.ProfilePath + $"user_script_send_convert/{fileName}.js", textEditor.Text);

            //重载脚本
            ScriptEnv.JavaScriptLoader.ClearRun();
        }
        private void saveScriptFileRev(string fileName)
        {
            File.WriteAllText(Tools.Global.ProfilePath + $"user_script_recv_convert/{fileName}.js", textEditorRev.Text);

            //重载脚本
            ScriptEnv.JavaScriptLoader.ClearRun();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.DataContext = Tools.Global.setting;

            //重写关闭响应代码
            this.Closing += SettingWindow_Closing;

            //置顶显示以免被挡住
            this.Topmost = true;

            //初始化下拉框参数
            dataBitsComboBox.SelectedIndex = Tools.Global.setting.dataBits - 5;
            stopBitComboBox.SelectedIndex = Tools.Global.setting.stopBit - 1;
            dataCheckComboBox.SelectedIndex = Tools.Global.setting.parity;

            showHexComboBox.DataContext = Tools.Global.setting;
            //scriptTestHexCheck.DataContext = Tools.Global.setting;
            //scriptTestHexCheckRev.DataContext = Tools.Global.setting;

            //快速搜索
            SearchPanel.Install(textEditor.TextArea);
            SearchPanel.Install(textEditorRev.TextArea);
            textEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
            textEditorRev.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("JavaScript");
            //加载上次打开的文件
            loadScriptFile(Tools.Global.setting.sendScript);
            if(!string.IsNullOrEmpty(MainWindow.recvScriptBackup)) loadScriptFileRev(MainWindow.recvScriptBackup);
            else loadScriptFileRev(Tools.Global.setting.recvScript);
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
            if(dataBitsComboBox.SelectedItem != null)
            {
                Tools.Global.setting.dataBits = dataBitsComboBox.SelectedIndex + 5;
            }
        }

        private void StopBitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (stopBitComboBox.SelectedItem != null)
            {
                Tools.Global.setting.stopBit = stopBitComboBox.SelectedIndex + 1;
            }
        }

        private void DataCheckComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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
            if (!IsValidScriptFileName(scriptName))
            {
                Tools.MessageBox.Show(string.IsNullOrWhiteSpace(scriptName)
                    ? TryFindResource("ScriptNoName") as string ?? "?!"
                    : TryFindResource("ScriptInvalidName") as string ?? "?!");
                return;
            }
            newScriptFileNameTextBox.Text = scriptName;
            if (File.Exists(Tools.Global.ProfilePath + $"user_script_send_convert/{scriptName}.js"))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptExist") as string ?? "?!");
                return;
            }

            try
            {
                File.Create(Tools.Global.ProfilePath + $"user_script_send_convert/{scriptName}.js").Close();
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
            ComboBox c = sender as ComboBox;
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
            if (!IsValidScriptFileName(scriptName))
            {
                Tools.MessageBox.Show(string.IsNullOrWhiteSpace(scriptName)
                    ? TryFindResource("ScriptNoName") as string ?? "?!"
                    : TryFindResource("ScriptInvalidName") as string ?? "?!");
                return;
            }
            newScriptFileNameTextBoxRev.Text = scriptName;
            if (File.Exists(Tools.Global.ProfilePath + $"user_script_recv_convert/{scriptName}.js"))
            {
                Tools.MessageBox.Show(TryFindResource("ScriptExist") as string ?? "?!");
                return;
            }

            try
            {
                File.Create(Tools.Global.ProfilePath + $"user_script_recv_convert/{scriptName}.js").Close();
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
            var name = (fileName ?? string.Empty).Trim();
            return name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 3)
                : name;
        }

        private static bool IsValidScriptFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;
            if (fileName == "." || fileName == "..")
                return false;
            if (fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                return false;
            if (fileName.Contains(System.IO.Path.DirectorySeparatorChar.ToString()) ||
                fileName.Contains(System.IO.Path.AltDirectorySeparatorChar.ToString()))
            {
                return false;
            }
            return true;
        }
    }
}
