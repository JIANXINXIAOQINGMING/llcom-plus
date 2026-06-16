using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using llcom_plus.Tools;

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

            var uri = new Uri(requestModel.Url);
            if (uri.Scheme == Uri.UriSchemeHttps)
                return await SendWithOpenSslAsync(requestModel, uri, cancellationToken);

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

        private static async Task<HttpResponseModel> SendWithOpenSslAsync(
            HttpRequestModel requestModel,
            Uri uri,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestBytes = BuildRawHttpRequest(requestModel, uri);
            var options = OpenSslCli.FromGlobalSettings(uri.Host, uri.Port > 0 ? uri.Port : 443, useDtls: false);
            if (string.IsNullOrWhiteSpace(options.TargetHost))
                options.TargetHost = uri.Host;

            var result = await OpenSslCli.SendAsync(options, requestBytes, 30000, cancellationToken);
            stopwatch.Stop();

            if (result.TimedOut)
                throw new TimeoutException("OpenSSL HTTPS 请求超时。");
            if (result.Output.Length == 0 && result.ExitCode.HasValue && result.ExitCode.Value != 0)
                throw new InvalidOperationException("OpenSSL HTTPS 请求失败：" + result.Diagnostics.Trim());

            var response = ParseRawHttpResponse(result.Output);
            response.ElapsedTime = stopwatch.Elapsed;
            response.RequestMethod = requestModel.Method;
            response.RequestUrl = requestModel.Url;

            var openSslInfo = OpenSslCli.BuildDiagnosticSummary(options);
            if (!string.IsNullOrWhiteSpace(result.Diagnostics))
                openSslInfo += "\r\n" + result.Diagnostics.Trim();
            if (!string.IsNullOrWhiteSpace(openSslInfo))
                response.Headers = response.Headers + "\r\n[OpenSSL]\r\n" + openSslInfo;

            return response;
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

        private static byte[] BuildRawHttpRequest(HttpRequestModel requestModel, Uri uri)
        {
            byte[] body = BuildRawBody(requestModel, out string generatedContentType);
            var headers = new Dictionary<string, string>(requestModel.Headers, StringComparer.OrdinalIgnoreCase);
            headers["Host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            headers["Connection"] = "close";

            if (body.Length > 0)
            {
                headers["Content-Length"] = body.Length.ToString();
                if (!string.IsNullOrWhiteSpace(generatedContentType) && !headers.ContainsKey("Content-Type"))
                    headers["Content-Type"] = generatedContentType;
            }

            var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            var builder = new StringBuilder();
            builder.Append(requestModel.Method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            foreach (var header in headers)
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            builder.Append("\r\n");

            var head = Encoding.ASCII.GetBytes(builder.ToString());
            var request = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, request, 0, head.Length);
            Buffer.BlockCopy(body, 0, request, head.Length, body.Length);
            return request;
        }

        private static byte[] BuildRawBody(HttpRequestModel requestModel, out string contentType)
        {
            contentType = string.Empty;
            if (!CanSendBody(new HttpMethod(requestModel.Method)))
                return new byte[0];

            switch (requestModel.BodyType)
            {
                case RequestBodyType.FormUrlEncoded:
                    contentType = "application/x-www-form-urlencoded";
                    return Encoding.UTF8.GetBytes(BuildFormUrlEncodedBody(requestModel));
                case RequestBodyType.MultipartFormData:
                    return BuildMultipartBody(requestModel, out contentType);
                default:
                    return string.IsNullOrEmpty(requestModel.Body)
                        ? new byte[0]
                        : Encoding.UTF8.GetBytes(requestModel.Body);
            }
        }

        private static string BuildFormUrlEncodedBody(HttpRequestModel requestModel)
        {
            return string.Join("&", requestModel.FormFields
                .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Key))
                .Select(field => $"{HttpUtility.UrlEncode(field.Key.Trim())}={HttpUtility.UrlEncode(field.Value ?? string.Empty)}"));
        }

        private static byte[] BuildMultipartBody(HttpRequestModel requestModel, out string contentType)
        {
            var boundary = "----llcomplus-" + Guid.NewGuid().ToString("N");
            contentType = "multipart/form-data; boundary=" + boundary;

            using (var memory = new MemoryStream())
            {
                foreach (var field in requestModel.FormFields.Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Key)))
                {
                    WriteAscii(memory, "--" + boundary + "\r\n");
                    WriteAscii(memory, $"Content-Disposition: form-data; name=\"{EscapeQuoted(field.Key.Trim())}\"\r\n\r\n");
                    WriteUtf8(memory, field.Value ?? string.Empty);
                    WriteAscii(memory, "\r\n");
                }

                foreach (var file in requestModel.Files.Where(file => file.IsEnabled && !string.IsNullOrWhiteSpace(file.FieldName) && !string.IsNullOrWhiteSpace(file.FilePath)))
                {
                    if (!File.Exists(file.FilePath))
                        throw new FileNotFoundException($"文件不存在：{file.FilePath}", file.FilePath);

                    WriteAscii(memory, "--" + boundary + "\r\n");
                    WriteAscii(memory, $"Content-Disposition: form-data; name=\"{EscapeQuoted(file.FieldName.Trim())}\"; filename=\"{EscapeQuoted(Path.GetFileName(file.FilePath))}\"\r\n");
                    WriteAscii(memory, "Content-Type: " + (string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType) + "\r\n\r\n");
                    var bytes = File.ReadAllBytes(file.FilePath);
                    memory.Write(bytes, 0, bytes.Length);
                    WriteAscii(memory, "\r\n");
                }

                WriteAscii(memory, "--" + boundary + "--\r\n");
                return memory.ToArray();
            }
        }

        private static HttpResponseModel ParseRawHttpResponse(byte[] responseBytes)
        {
            SplitFinalHttpResponse(responseBytes, out var headerBytes, out var bodyBytes);
            var finalHeaderText = Encoding.UTF8.GetString(headerBytes);
            var lines = finalHeaderText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var statusCode = HttpStatusCode.OK;
            var reason = string.Empty;

            if (lines.Length > 0)
            {
                var statusParts = lines[0].Split(new[] { ' ' }, 3);
                int code;
                if (statusParts.Length >= 2 && int.TryParse(statusParts[1], out code))
                    statusCode = (HttpStatusCode)code;
                if (statusParts.Length >= 3)
                    reason = statusParts[2];
            }

            var headersOnly = string.Join("\r\n", lines.Skip(1));
            if (headersOnly.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                bodyBytes = DecodeChunkedBody(bodyBytes);

            return new HttpResponseModel
            {
                StatusCode = statusCode,
                ReasonPhrase = reason,
                Headers = headersOnly,
                Body = Encoding.UTF8.GetString(bodyBytes)
            };
        }

        private static void SplitFinalHttpResponse(byte[] responseBytes, out byte[] headerBytes, out byte[] bodyBytes)
        {
            var headerStart = 0;
            var finalHeaderStart = 0;
            var finalHeaderEnd = responseBytes.Length;
            var bodyStart = responseBytes.Length;

            while (headerStart < responseBytes.Length)
            {
                var separator = FindHeaderSeparator(responseBytes, headerStart);
                if (separator < 0)
                {
                    finalHeaderStart = headerStart;
                    finalHeaderEnd = responseBytes.Length;
                    bodyStart = responseBytes.Length;
                    break;
                }

                finalHeaderStart = headerStart;
                finalHeaderEnd = separator;
                bodyStart = separator + 4;

                if (!StartsWithAscii(responseBytes, bodyStart, "HTTP/"))
                    break;

                headerStart = bodyStart;
            }

            var headerLength = Math.Max(0, finalHeaderEnd - finalHeaderStart);
            headerBytes = SubArray(responseBytes, finalHeaderStart, headerLength);
            bodyBytes = bodyStart >= responseBytes.Length
                ? new byte[0]
                : SubArray(responseBytes, bodyStart, responseBytes.Length - bodyStart);
        }

        private static int FindHeaderSeparator(byte[] bytes, int startIndex)
        {
            for (var i = startIndex; i <= bytes.Length - 4; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static bool StartsWithAscii(byte[] bytes, int offset, string value)
        {
            if (offset < 0 || offset + value.Length > bytes.Length)
                return false;

            for (var i = 0; i < value.Length; i++)
            {
                if (bytes[offset + i] != (byte)value[i])
                    return false;
            }

            return true;
        }

        private static byte[] SubArray(byte[] bytes, int offset, int length)
        {
            if (length <= 0)
                return new byte[0];

            var result = new byte[length];
            Buffer.BlockCopy(bytes, offset, result, 0, length);
            return result;
        }

        private static byte[] DecodeChunkedBody(byte[] body)
        {
            using (var input = new MemoryStream(body))
            using (var output = new MemoryStream())
            {
                while (true)
                {
                    var line = ReadAsciiLine(input);
                    if (line == null)
                        break;
                    var semicolon = line.IndexOf(';');
                    var sizeText = semicolon >= 0 ? line.Substring(0, semicolon) : line;
                    int size;
                    if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out size))
                        break;
                    if (size == 0)
                        break;

                    var buffer = new byte[size];
                    var read = input.Read(buffer, 0, buffer.Length);
                    output.Write(buffer, 0, read);
                    input.ReadByte();
                    input.ReadByte();
                }
                return output.ToArray();
            }
        }

        private static string ReadAsciiLine(Stream stream)
        {
            var bytes = new List<byte>();
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0)
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                if (value == '\n')
                    break;
                if (value != '\r')
                    bytes.Add((byte)value);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void WriteAscii(Stream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteUtf8(Stream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string EscapeQuoted(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
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
