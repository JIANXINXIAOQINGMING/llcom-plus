using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
            {
                var diagnostics = OpenSslMessageExplainer.Explain(result.Diagnostics.Trim());
                if (string.IsNullOrWhiteSpace(diagnostics))
                    diagnostics = result.Diagnostics.Trim();
                throw new InvalidOperationException("OpenSSL HTTPS 请求失败：" + diagnostics);
            }

            var response = ParseRawHttpResponse(result.Output, requestModel.Method);
            if (result.ExitCode.HasValue && result.ExitCode.Value != 0 && !IsBenignTlsCloseWithoutNotify(result.Diagnostics))
            {
                var diagnostics = OpenSslMessageExplainer.Explain(result.Diagnostics.Trim());
                if (string.IsNullOrWhiteSpace(diagnostics))
                    diagnostics = result.Diagnostics.Trim();
                throw new InvalidOperationException("OpenSSL HTTPS 请求失败：" + diagnostics);
            }
            response.ElapsedTime = stopwatch.Elapsed;
            response.RequestMethod = requestModel.Method;
            response.RequestUrl = requestModel.Url;

            var openSslInfo = OpenSslCli.BuildDiagnosticSummary(options);
            if (!string.IsNullOrWhiteSpace(result.Diagnostics))
            {
                var diagnostics = OpenSslMessageExplainer.Explain(result.Diagnostics.Trim());
                if (!string.IsNullOrWhiteSpace(diagnostics))
                    openSslInfo += "\r\n" + diagnostics;
            }
            if (!string.IsNullOrWhiteSpace(openSslInfo))
                response.Headers = response.Headers + "\r\n[OpenSSL]\r\n" + openSslInfo;

            return response;
        }

        private static bool IsBenignTlsCloseWithoutNotify(string diagnostics)
        {
            var text = diagnostics ?? string.Empty;
            return text.IndexOf("unexpected eof while reading", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   text.IndexOf("certificate verify failed", StringComparison.OrdinalIgnoreCase) < 0 &&
                   text.IndexOf("verify error:", StringComparison.OrdinalIgnoreCase) < 0;
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

        private static HttpResponseModel ParseRawHttpResponse(byte[] responseBytes, string requestMethod)
        {
            if (responseBytes == null || responseBytes.Length == 0)
                throw new InvalidDataException("OpenSSL 未返回 HTTP 响应数据。");

            SplitFinalHttpResponse(responseBytes, out var headerBytes, out var bodyBytes);
            var finalHeaderText = Encoding.ASCII.GetString(headerBytes);
            var lines = finalHeaderText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (!TryParseStatusLine(lines.FirstOrDefault(), out var code, out var reason))
                throw new InvalidDataException("OpenSSL 返回的数据不是有效的 HTTP 响应状态行。");

            var headersOnly = string.Join("\r\n", lines.Skip(1));
            var headers = ParseResponseHeaders(lines.Skip(1));
            var mustNotHaveBody = string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                                  (code >= 100 && code < 200) || code == 204 || code == 304;

            if (!mustNotHaveBody)
            {
                if (HeaderContainsToken(headers, "Transfer-Encoding", "chunked"))
                {
                    bodyBytes = DecodeChunkedBody(bodyBytes);
                }
                else if (headers.TryGetValue("Content-Length", out var contentLengthText))
                {
                    if (!long.TryParse(contentLengthText.Trim(), out var contentLength) || contentLength < 0 || contentLength > int.MaxValue)
                        throw new InvalidDataException("HTTP Content-Length 无效。");
                    if (bodyBytes.Length != (int)contentLength)
                        throw new EndOfStreamException($"HTTP 响应正文不完整：应为 {contentLength} 字节，实际为 {bodyBytes.Length} 字节。");
                }
            }

            bodyBytes = DecodeContentEncoding(bodyBytes, headers);

            return new HttpResponseModel
            {
                StatusCode = (HttpStatusCode)code,
                ReasonPhrase = reason,
                Headers = headersOnly,
                Body = GetBodyEncoding(headers).GetString(bodyBytes)
            };
        }

        private static void SplitFinalHttpResponse(byte[] responseBytes, out byte[] headerBytes, out byte[] bodyBytes)
        {
            var headerStart = 0;

            while (headerStart < responseBytes.Length)
            {
                var separator = FindHeaderSeparator(responseBytes, headerStart);
                if (separator < 0)
                    throw new InvalidDataException("HTTP 响应头不完整，缺少空行分隔符。");

                var currentHeader = SubArray(responseBytes, headerStart, separator - headerStart);
                var firstLineEnd = FindCrlf(currentHeader, 0);
                var firstLineLength = firstLineEnd < 0 ? currentHeader.Length : firstLineEnd;
                var statusLine = Encoding.ASCII.GetString(currentHeader, 0, firstLineLength);
                if (!TryParseStatusLine(statusLine, out var statusCode, out _))
                    throw new InvalidDataException("HTTP 响应状态行无效。");

                var bodyStart = separator + 4;
                if (statusCode >= 100 && statusCode < 200 && statusCode != 101)
                {
                    headerStart = bodyStart;
                    continue;
                }

                headerBytes = currentHeader;
                bodyBytes = bodyStart >= responseBytes.Length
                    ? new byte[0]
                    : SubArray(responseBytes, bodyStart, responseBytes.Length - bodyStart);
                return;
            }

            throw new InvalidDataException("HTTP 响应中没有最终状态行。");
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

        private static int FindCrlf(byte[] bytes, int startIndex)
        {
            for (var i = startIndex; i <= bytes.Length - 2; i++)
                if (bytes[i] == '\r' && bytes[i + 1] == '\n')
                    return i;
            return -1;
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
            using (var output = new MemoryStream())
            {
                var offset = 0;
                while (true)
                {
                    var line = ReadRequiredAsciiLine(body, ref offset);
                    var semicolon = line.IndexOf(';');
                    var sizeText = semicolon >= 0 ? line.Substring(0, semicolon) : line;
                    if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                        throw new InvalidDataException("HTTP 分块长度无效。");
                    if (size == 0)
                    {
                        while (ReadRequiredAsciiLine(body, ref offset).Length > 0) { }
                        if (offset != body.Length)
                            throw new InvalidDataException("HTTP 分块响应结束后包含多余数据。");
                        return output.ToArray();
                    }

                    if (size > body.Length - offset)
                        throw new EndOfStreamException("HTTP 分块响应正文被截断。");
                    output.Write(body, offset, size);
                    offset += size;
                    if (offset + 2 > body.Length || body[offset] != '\r' || body[offset + 1] != '\n')
                        throw new InvalidDataException("HTTP 分块正文后缺少 CRLF。");
                    offset += 2;
                }
            }
        }

        private static string ReadRequiredAsciiLine(byte[] bytes, ref int offset)
        {
            var end = FindCrlf(bytes, offset);
            if (end < 0)
                throw new EndOfStreamException("HTTP 分块响应缺少完整行结尾。");
            var line = Encoding.ASCII.GetString(bytes, offset, end - offset);
            offset = end + 2;
            return line;
        }

        private static bool TryParseStatusLine(string statusLine, out int statusCode, out string reasonPhrase)
        {
            statusCode = 0;
            reasonPhrase = string.Empty;
            if (string.IsNullOrWhiteSpace(statusLine) || !statusLine.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                return false;

            var parts = statusLine.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out statusCode) || statusCode < 100 || statusCode > 999)
                return false;
            if (parts.Length >= 3)
                reasonPhrase = parts[2];
            return true;
        }

        private static Dictionary<string, string> ParseResponseHeaders(IEnumerable<string> lines)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;
                var separator = line.IndexOf(':');
                if (separator <= 0)
                    throw new InvalidDataException("HTTP 响应头格式无效：" + line);

                var name = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (headers.TryGetValue(name, out var existing))
                    headers[name] = existing + ", " + value;
                else
                    headers.Add(name, value);
            }
            return headers;
        }

        private static bool HeaderContainsToken(Dictionary<string, string> headers, string name, string token)
        {
            return headers.TryGetValue(name, out var value) &&
                   value.Split(',').Any(item => item.Trim().Equals(token, StringComparison.OrdinalIgnoreCase));
        }

        private static byte[] DecodeContentEncoding(byte[] body, Dictionary<string, string> headers)
        {
            if (body.Length == 0 || !headers.TryGetValue("Content-Encoding", out var encodingHeader))
                return body;

            var encodings = encodingHeader.Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0 && !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                .ToList();
            for (var i = encodings.Count - 1; i >= 0; i--)
            {
                var encoding = encodings[i];
                if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                    body = Decompress(body, stream => new GZipStream(stream, CompressionMode.Decompress));
                else if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                    body = Decompress(body, stream => new DeflateStream(stream, CompressionMode.Decompress));
                else
                    throw new NotSupportedException("暂不支持 HTTPS Content-Encoding：" + encoding);
            }
            return body;
        }

        private static byte[] Decompress(byte[] body, Func<Stream, Stream> createDecompressionStream)
        {
            using (var input = new MemoryStream(body))
            using (var decompression = createDecompressionStream(input))
            using (var output = new MemoryStream())
            {
                decompression.CopyTo(output);
                return output.ToArray();
            }
        }

        private static Encoding GetBodyEncoding(Dictionary<string, string> headers)
        {
            if (headers.TryGetValue("Content-Type", out var contentType) &&
                MediaTypeHeaderValue.TryParse(contentType, out var mediaType) &&
                !string.IsNullOrWhiteSpace(mediaType.CharSet))
            {
                try { return Encoding.GetEncoding(mediaType.CharSet.Trim().Trim('"')); }
                catch { }
            }
            return Encoding.UTF8;
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
