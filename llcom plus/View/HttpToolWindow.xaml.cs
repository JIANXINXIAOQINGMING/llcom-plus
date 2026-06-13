using llcom_plus.HttpTools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace llcom_plus
{
    public partial class HttpToolWindow : UserControl
    {
        private readonly HttpRequestService _httpRequestService = new HttpRequestService();
        private readonly ObservableCollection<FormFieldModel> _formFields = new ObservableCollection<FormFieldModel>();
        private readonly ObservableCollection<FormFieldModel> _multipartFields = new ObservableCollection<FormFieldModel>();
        private readonly ObservableCollection<FileFieldModel> _files = new ObservableCollection<FileFieldModel>();
        private readonly List<HeaderPreset> _headerPresets = new List<HeaderPreset>
        {
            new HeaderPreset("Accept", "application/json"),
            new HeaderPreset("Accept-Charset", "utf-8"),
            new HeaderPreset("Accept-Encoding", "gzip, deflate, br"),
            new HeaderPreset("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8"),
            new HeaderPreset("Authorization", "Bearer your-token"),
            new HeaderPreset("Cache-Control", "no-cache"),
            new HeaderPreset("Connection", "keep-alive"),
            new HeaderPreset("Content-Type", "application/json"),
            new HeaderPreset("Cookie", "sessionId=your-session-id"),
            new HeaderPreset("Origin", "https://example.com"),
            new HeaderPreset("Referer", "https://example.com/"),
            new HeaderPreset("User-Agent", "llcom-plus-HttpTool/1.0"),
            new HeaderPreset("X-Request-Id", "demo-request-id"),
            new HeaderPreset("If-Modified-Since", "Sat, 25 Apr 2026 00:00:00 GMT"),
            new HeaderPreset("If-None-Match", "\"etag-value\""),
            new HeaderPreset("Range", "bytes=0-1023"),
            new HeaderPreset("Access-Control-Allow-Origin", "*"),
            new HeaderPreset("Access-Control-Allow-Headers", "Content-Type, Authorization"),
            new HeaderPreset("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS"),
            new HeaderPreset("Sec-WebSocket-Key", "base64-key"),
            new HeaderPreset("Sec-WebSocket-Version", "13")
        };

        public HttpToolWindow()
        {
            InitializeComponent();
            InitializeHeaderPresets();
            InitializeBodyEditors();
            ApplySelectedHeaderPreset();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SetSendingState(true);
            ClearResponse();

            try
            {
                var requestModel = BuildRequestModelFromUi();
                var responseModel = await _httpRequestService.SendAsync(requestModel);
                ShowResponse(responseModel);
            }
            catch (TaskCanceledException)
            {
                ShowError("请求超时，请检查网络或稍后重试。");
            }
            catch (HttpRequestException ex)
            {
                ShowError($"网络连接失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                SetSendingState(false);
            }
        }

        private void HeaderPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySelectedHeaderPreset();
        }

        private void AddHeaderPresetButton_Click(object sender, RoutedEventArgs e)
        {
            var headerName = GetSelectedHeaderName();
            var headerValue = HeaderDefaultValueTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(headerName))
                return;

            UpsertHeaderLine(headerName, headerValue);
        }

        private void OpenFormFieldsDialogButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFormFieldsDialog("编辑表单字段", _formFields);
        }

        private void OpenMultipartEditorDialogButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = CreateEditorDialog("编辑 Multipart/Form-Data");
            var tabControl = new TabControl
            {
                ItemContainerStyle = CreateDialogTabItemStyle()
            };
            tabControl.Items.Add(new TabItem
            {
                Header = "Form Fields",
                Content = CreateFormFieldsEditor(_multipartFields)
            });
            tabControl.Items.Add(new TabItem
            {
                Header = "Files",
                Content = CreateFilesEditor()
            });

            dialog.Content = tabControl;
            dialog.ShowDialog();
            UpdateBodySummaries();
        }

        private Style CreateDialogTabItemStyle()
        {
            var baseStyle = TryFindResource(typeof(TabItem)) as Style;
            var style = baseStyle == null
                ? new Style(typeof(TabItem))
                : new Style(typeof(TabItem), baseStyle);

            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 116.0));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 46.0));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(22, 10, 22, 10)));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0)));
            return style;
        }

        private HttpRequestModel BuildRequestModelFromUi()
        {
            var selectedMethod = ((ComboBoxItem)MethodComboBox.SelectedItem).Content?.ToString() ?? "GET";

            return new HttpRequestModel
            {
                Method = selectedMethod,
                Url = BuildFullUrlFromUi(),
                Headers = HeaderParser.Parse(RequestHeadersTextBox.Text),
                Body = RequestBodyTextBox.Text,
                BodyType = GetSelectedBodyType(),
                FormFields = GetSelectedFormFields(),
                Files = _files.ToList()
            };
        }

        private RequestBodyType GetSelectedBodyType()
        {
            switch (BodyTypeTabControl.SelectedIndex)
            {
                case 1:
                    return RequestBodyType.FormUrlEncoded;
                case 2:
                    return RequestBodyType.MultipartFormData;
                default:
                    return RequestBodyType.Raw;
            }
        }

        private List<FormFieldModel> GetSelectedFormFields()
        {
            return BodyTypeTabControl.SelectedIndex == 2
                ? _multipartFields.ToList()
                : _formFields.ToList();
        }

        private string BuildFullUrlFromUi()
        {
            var selectedScheme = ((ComboBoxItem)UrlSchemeComboBox.SelectedItem).Content?.ToString() ?? "https://";
            var urlPart = UrlTextBox.Text.Trim();

            if (urlPart.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                urlPart = urlPart.Substring("http://".Length);
            else if (urlPart.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                urlPart = urlPart.Substring("https://".Length);

            return selectedScheme + urlPart;
        }

        private void ApplySelectedHeaderPreset()
        {
            var selectedItem = HeaderPresetComboBox?.SelectedItem as HeaderPreset;
            if (selectedItem == null || HeaderDefaultValueTextBox == null)
                return;

            HeaderDefaultValueTextBox.Text = selectedItem.DefaultValue;
        }

        private string GetSelectedHeaderName()
        {
            var selectedItem = HeaderPresetComboBox.SelectedItem as HeaderPreset;
            return selectedItem == null ? string.Empty : selectedItem.Name;
        }

        private void InitializeHeaderPresets()
        {
            HeaderPresetComboBox.ItemsSource = _headerPresets.OrderBy(header => header.Name).ToList();
            HeaderPresetComboBox.SelectedIndex = 0;
        }

        private void InitializeBodyEditors()
        {
            _formFields.Add(new FormFieldModel { Key = "username", Value = "demo" });
            _formFields.Add(new FormFieldModel { Key = "password", Value = "123456" });
            _multipartFields.Add(new FormFieldModel { Key = "description", Value = "upload demo" });
            UpdateBodySummaries();
        }

        private void UpsertHeaderLine(string headerName, string headerValue)
        {
            var newLine = $"{headerName}: {headerValue}";
            var lines = RequestHeadersTextBox.Text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            for (var i = 0; i < lines.Count; i++)
            {
                var separatorIndex = lines[i].IndexOf(':');
                if (separatorIndex <= 0)
                    continue;

                var existingHeaderName = lines[i].Substring(0, separatorIndex).Trim();
                if (existingHeaderName.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = newLine;
                    RequestHeadersTextBox.Text = string.Join(Environment.NewLine, lines);
                    return;
                }
            }

            lines.Add(newLine);
            RequestHeadersTextBox.Text = string.Join(Environment.NewLine, lines);
        }

        private void ShowResponse(HttpResponseModel responseModel)
        {
            ResponseBodyTextBox.Text = responseModel.Body;
            ResponseHeadersTextBox.Text = responseModel.Headers;
            StatusCodeTextBlock.Text = $"{(int)responseModel.StatusCode} {responseModel.StatusCode}";
            ReasonPhraseTextBlock.Text = responseModel.ReasonPhrase;
            ElapsedTimeTextBlock.Text = $"{responseModel.ElapsedTime.TotalMilliseconds:N0} ms";
            RequestMethodTextBlock.Text = responseModel.RequestMethod;
            RequestUrlTextBlock.Text = responseModel.RequestUrl;
        }

        private void ShowError(string message)
        {
            ResponseBodyTextBox.Text = message;
            StatusCodeTextBlock.Text = "Error";
            ReasonPhraseTextBlock.Text = message;
            ElapsedTimeTextBlock.Text = string.Empty;
            RequestMethodTextBlock.Text = string.Empty;
            RequestUrlTextBlock.Text = BuildFullUrlFromUi();
        }

        private void ClearResponse()
        {
            ResponseBodyTextBox.Clear();
            ResponseHeadersTextBox.Clear();
            StatusCodeTextBlock.Text = string.Empty;
            ReasonPhraseTextBlock.Text = string.Empty;
            ElapsedTimeTextBlock.Text = string.Empty;
            RequestMethodTextBlock.Text = string.Empty;
            RequestUrlTextBlock.Text = string.Empty;
        }

        private void SetSendingState(bool isSending)
        {
            SendButton.IsEnabled = !isSending;
            SendButton.Content = isSending ? "发送中..." : "发送";
        }

        private void OpenFormFieldsDialog(string title, ObservableCollection<FormFieldModel> fields)
        {
            var dialog = CreateEditorDialog(title);
            dialog.Content = CreateFormFieldsEditor(fields);
            dialog.ShowDialog();
            UpdateBodySummaries();
        }

        private Window CreateEditorDialog(string title)
        {
            var owner = Window.GetWindow(this);
            var width = 920.0;
            var height = 620.0;

            if (owner != null)
            {
                width = Math.Max(860, owner.ActualWidth * 0.68);
                height = Math.Max(540, owner.ActualHeight * 0.72);
            }

            return new Window
            {
                Title = title,
                Owner = owner,
                Width = width,
                Height = height,
                MinWidth = 760,
                MinHeight = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
        }

        private FrameworkElement CreateFormFieldsEditor(ObservableCollection<FormFieldModel> fields)
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Orientation = Orientation.Horizontal
            };

            var dataGrid = CreateFormFieldsGrid(fields);
            var addButton = new Button { Width = 110, Content = "添加字段" };
            addButton.Click += (sender, e) =>
            {
                var field = new FormFieldModel { Key = "key", Value = "value" };
                fields.Add(field);
                dataGrid.SelectedItem = field;
                dataGrid.ScrollIntoView(field);
                UpdateBodySummaries();
            };

            var removeButton = new Button { Width = 110, Margin = new Thickness(8, 0, 0, 0), Content = "删除字段" };
            removeButton.Click += (sender, e) =>
            {
                var selectedField = dataGrid.SelectedItem as FormFieldModel;
                if (selectedField != null)
                    fields.Remove(selectedField);
                UpdateBodySummaries();
            };

            toolbar.Children.Add(addButton);
            toolbar.Children.Add(removeButton);

            Grid.SetRow(toolbar, 0);
            Grid.SetRow(dataGrid, 1);
            root.Children.Add(toolbar);
            root.Children.Add(dataGrid);
            return root;
        }

        private DataGrid CreateFormFieldsGrid(ObservableCollection<FormFieldModel> fields)
        {
            var dataGrid = CreateEditorDataGrid(fields);
            dataGrid.CanUserAddRows = true;
            dataGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Width = 70,
                Header = "启用",
                Binding = new Binding(nameof(FormFieldModel.IsEnabled))
            });
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Width = 220,
                Header = "Key",
                Binding = new Binding(nameof(FormFieldModel.Key))
            });
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Header = "Value",
                Binding = new Binding(nameof(FormFieldModel.Value))
            });
            return dataGrid;
        }

        private FrameworkElement CreateFilesEditor()
        {
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                Orientation = Orientation.Horizontal
            };

            var dataGrid = CreateFilesGrid();
            var chooseButton = new Button { Width = 110, Content = "选择文件" };
            chooseButton.Click += (sender, e) =>
            {
                AddFilesFromDialog(Window.GetWindow(root));
                if (_files.Count > 0)
                    dataGrid.ScrollIntoView(_files[_files.Count - 1]);
            };

            var removeButton = new Button { Width = 110, Margin = new Thickness(8, 0, 0, 0), Content = "删除文件" };
            removeButton.Click += (sender, e) =>
            {
                var selectedFile = dataGrid.SelectedItem as FileFieldModel;
                if (selectedFile != null)
                    _files.Remove(selectedFile);
                UpdateBodySummaries();
            };

            toolbar.Children.Add(chooseButton);
            toolbar.Children.Add(removeButton);

            Grid.SetRow(toolbar, 0);
            Grid.SetRow(dataGrid, 1);
            root.Children.Add(toolbar);
            root.Children.Add(dataGrid);
            return root;
        }

        private DataGrid CreateFilesGrid()
        {
            var dataGrid = CreateEditorDataGrid(_files);
            dataGrid.CanUserAddRows = false;
            dataGrid.Columns.Add(new DataGridCheckBoxColumn
            {
                Width = 70,
                Header = "启用",
                Binding = new Binding(nameof(FileFieldModel.IsEnabled))
            });
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Width = 150,
                Header = "字段名",
                Binding = new Binding(nameof(FileFieldModel.FieldName))
            });
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Header = "文件路径",
                IsReadOnly = true,
                Binding = new Binding(nameof(FileFieldModel.FilePath))
            });
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Width = 220,
                Header = "Content-Type",
                Binding = new Binding(nameof(FileFieldModel.ContentType))
            });
            return dataGrid;
        }

        private static DataGrid CreateEditorDataGrid<T>(ObservableCollection<T> items)
        {
            return new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserDeleteRows = true,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                ItemsSource = items,
                Margin = new Thickness(0)
            };
        }

        private void AddFilesFromDialog(Window owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的文件",
                Multiselect = true,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(owner) != true)
                return;

            foreach (var fileName in dialog.FileNames)
            {
                _files.Add(new FileFieldModel
                {
                    FieldName = "file",
                    FilePath = fileName,
                    ContentType = GuessContentType(fileName)
                });
            }

            UpdateBodySummaries();
        }

        private void UpdateBodySummaries()
        {
            if (FormFieldsSummaryTextBlock != null)
                FormFieldsSummaryTextBlock.Text = $"已配置 {_formFields.Count} 个表单字段";

            if (MultipartSummaryTextBlock != null)
                MultipartSummaryTextBlock.Text = $"已配置 {_multipartFields.Count} 个表单字段，{_files.Count} 个文件";
        }

        private static string GuessContentType(string filePath)
        {
            switch (Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".txt":
                    return "text/plain";
                case ".csv":
                    return "text/csv";
                case ".json":
                    return "application/json";
                case ".xml":
                    return "application/xml";
                case ".pdf":
                    return "application/pdf";
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
                case ".gif":
                    return "image/gif";
                case ".webp":
                    return "image/webp";
                case ".zip":
                    return "application/zip";
                case ".doc":
                    return "application/msword";
                case ".docx":
                    return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls":
                    return "application/vnd.ms-excel";
                case ".xlsx":
                    return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                default:
                    return "application/octet-stream";
            }
        }
    }
}
