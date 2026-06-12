using llcom.HttpTools;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace llcom
{
    public partial class HttpToolWindow : Window
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
            new HeaderPreset("User-Agent", "LLCOM-HttpTool/1.0"),
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

        private void AddFormFieldButton_Click(object sender, RoutedEventArgs e)
        {
            if (BodyTypeTabControl.SelectedIndex == 1)
            {
                _formFields.Add(new FormFieldModel { Key = "key", Value = "value" });
                return;
            }

            _multipartFields.Add(new FormFieldModel { Key = "key", Value = "value" });
        }

        private void RemoveFormFieldButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedField = FormFieldsDataGrid.SelectedItem as FormFieldModel;
            if (selectedField != null)
                _formFields.Remove(selectedField);
        }

        private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要上传的文件",
                Multiselect = true,
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true)
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
        }

        private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedFile = FilesDataGrid.SelectedItem as FileFieldModel;
            if (selectedFile != null)
                _files.Remove(selectedFile);
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

            FormFieldsDataGrid.ItemsSource = _formFields;
            MultipartFieldsDataGrid.ItemsSource = _multipartFields;
            FilesDataGrid.ItemsSource = _files;
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

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (Tools.Global.isMainWindowsClosed)
                return;

            e.Cancel = true;
            Hide();
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
