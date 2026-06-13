using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.HttpTools
{
    public class HttpRequestService
    {
        private static readonly HashSet<string> ContentHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Allow",
            "Content-Disposition",
            "Content-Encoding",
            "Content-Language",
            "Content-Length",
            "Content-Location",
            "Content-MD5",
            "Content-Range",
            "Content-Type",
            "Expires",
            "Last-Modified"
        };

        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public async Task<HttpResponseModel> SendAsync(HttpRequestModel requestModel, CancellationToken cancellationToken = default)
        {
            ValidateUrl(requestModel.Url);

            using (var requestMessage = BuildRequestMessage(requestModel))
            {
                var stopwatch = Stopwatch.StartNew();
                using (var responseMessage = await SharedHttpClient.SendAsync(requestMessage, cancellationToken))
                {
                    stopwatch.Stop();
                    var responseBody = await responseMessage.Content.ReadAsStringAsync();

                    return new HttpResponseModel
                    {
                        StatusCode = responseMessage.StatusCode,
                        ReasonPhrase = responseMessage.ReasonPhrase ?? string.Empty,
                        Body = responseBody,
                        Headers = FormatResponseHeaders(responseMessage),
                        ElapsedTime = stopwatch.Elapsed,
                        RequestMethod = requestModel.Method,
                        RequestUrl = requestModel.Url
                    };
                }
            }
        }

        private static void ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL 不能为空。");

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new UriFormatException("URL 格式错误，请输入以 http:// 或 https:// 开头的完整地址。");
            }
        }

        private static HttpRequestMessage BuildRequestMessage(HttpRequestModel requestModel)
        {
            var method = new HttpMethod(requestModel.Method);
            var requestMessage = new HttpRequestMessage(method, requestModel.Url);

            if (CanSendBody(method))
                requestMessage.Content = BuildHttpContent(requestModel);

            foreach (var header in requestModel.Headers)
            {
                if (IsContentHeader(header.Key))
                {
                    if (requestMessage.Content == null)
                        continue;

                    if (requestMessage.Content is MultipartFormDataContent &&
                        header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    requestMessage.Content.Headers.Remove(header.Key);
                    if (!requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        throw new InvalidOperationException($"无法添加请求内容头：{header.Key}");

                    continue;
                }

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw new InvalidOperationException($"无法添加请求头：{header.Key}");
            }

            return requestMessage;
        }

        private static HttpContent BuildHttpContent(HttpRequestModel requestModel)
        {
            switch (requestModel.BodyType)
            {
                case RequestBodyType.FormUrlEncoded:
                    return BuildFormUrlEncodedContent(requestModel);
                case RequestBodyType.MultipartFormData:
                    return BuildMultipartFormDataContent(requestModel);
                default:
                    return string.IsNullOrWhiteSpace(requestModel.Body)
                        ? null
                        : new StringContent(requestModel.Body, Encoding.UTF8);
            }
        }

        private static HttpContent BuildFormUrlEncodedContent(HttpRequestModel requestModel)
        {
            var fields = requestModel.FormFields
                .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Key))
                .Select(field => new KeyValuePair<string, string>(field.Key.Trim(), field.Value ?? string.Empty))
                .ToList();

            return fields.Count == 0 ? null : new FormUrlEncodedContent(fields);
        }

        private static HttpContent BuildMultipartFormDataContent(HttpRequestModel requestModel)
        {
            var enabledFields = requestModel.FormFields
                .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Key))
                .ToList();

            var enabledFiles = requestModel.Files
                .Where(file => file.IsEnabled && !string.IsNullOrWhiteSpace(file.FieldName) && !string.IsNullOrWhiteSpace(file.FilePath))
                .ToList();

            if (enabledFields.Count == 0 && enabledFiles.Count == 0)
                return null;

            var content = new MultipartFormDataContent();

            foreach (var field in enabledFields)
                content.Add(new StringContent(field.Value ?? string.Empty, Encoding.UTF8), field.Key.Trim());

            foreach (var file in enabledFiles)
            {
                if (!File.Exists(file.FilePath))
                    throw new FileNotFoundException($"文件不存在：{file.FilePath}", file.FilePath);

                var fileContent = new StreamContent(File.OpenRead(file.FilePath));
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);

                content.Add(fileContent, file.FieldName.Trim(), Path.GetFileName(file.FilePath));
            }

            return content;
        }

        private static bool CanSendBody(HttpMethod method)
        {
            return method == HttpMethod.Post ||
                   method == HttpMethod.Put ||
                   method.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsContentHeader(string headerName)
        {
            return ContentHeaderNames.Contains(headerName);
        }

        private static string FormatResponseHeaders(HttpResponseMessage responseMessage)
        {
            var builder = new StringBuilder();

            foreach (var header in responseMessage.Headers)
                builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");

            foreach (var header in responseMessage.Content.Headers)
                builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");

            return builder.ToString();
        }
    }
}
