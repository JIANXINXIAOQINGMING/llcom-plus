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

        private const int FileCopyBufferSize = 81920;
        private const long MaxBufferedRawRequestBodyBytes = 64L * 1024L * 1024L;
        internal const int MaxRedirectCount = 5;
        internal const long MaxResponseHeaderBytes = 64L * 1024L;
        internal const long MaxCompressedResponseBodyBytes = 16L * 1024L * 1024L;
        internal const long MaxDecompressedResponseBodyBytes = 64L * 1024L * 1024L;
        internal const int MaxCompressionRatio = 100;
        private const long CompressionRatioSlackBytes = 64L * 1024L;
        private static readonly byte[] CrlfBytes = Encoding.ASCII.GetBytes("\r\n");
        private static readonly byte[] FinalChunkBytes = Encoding.ASCII.GetBytes("0\r\n\r\n");

        private static readonly HttpClient SharedHttpClient = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = (int)(MaxResponseHeaderBytes / 1024L)
            })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private sealed class RawHttpRequestPlan
        {
            public byte[] HeaderBytes { get; set; } = new byte[0];
            public RawHttpBodyPlan Body { get; set; } = new RawHttpBodyPlan();
            public bool UseChunkedTransferEncoding { get; set; }
        }

        private sealed class RawHttpBodyPlan
        {
            public string ContentType { get; set; } = string.Empty;
            public string TextBody { get; set; }
            public long ContentLength { get; set; }
            public List<RawMultipartFieldPart> Fields { get; } = new List<RawMultipartFieldPart>();
            public List<RawMultipartFilePart> Files { get; } = new List<RawMultipartFilePart>();
            public byte[] MultipartClosingBytes { get; set; }
            public bool IsMultipart { get; set; }
        }

        private sealed class RawMultipartFieldPart
        {
            public byte[] HeaderBytes { get; set; } = new byte[0];
            public string Value { get; set; } = string.Empty;
        }

        private sealed class RawMultipartFilePart
        {
            public byte[] HeaderBytes { get; set; } = new byte[0];
            public string FilePath { get; set; } = string.Empty;
            public long ExpectedLength { get; set; }
        }

        public async Task<HttpResponseModel> SendAsync(HttpRequestModel requestModel, CancellationToken cancellationToken = default)
        {
            if (requestModel == null)
                throw new ArgumentNullException(nameof(requestModel));
            ValidateUrl(requestModel.Url);

            var stopwatch = Stopwatch.StartNew();
            var currentRequest = CloneRequest(requestModel, requestModel.Url, requestModel.Method, includeBody: true);
            var redirectTrace = new List<string>();

            for (var redirectCount = 0; ; redirectCount++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentUri = new Uri(currentRequest.Url);
                var response = currentUri.Scheme == Uri.UriSchemeHttps
                    ? await SendWithOpenSslAsync(currentRequest, currentUri, cancellationToken).ConfigureAwait(false)
                    : await SendWithHttpClientAsync(currentRequest, cancellationToken).ConfigureAwait(false);

                response.RequestUrl = currentUri.AbsoluteUri;
                response.RequestMethod = currentRequest.Method;
                if (!IsRedirectStatus(response.StatusCode) ||
                    !TryGetResponseHeader(response.Headers, "Location", out var location))
                {
                    stopwatch.Stop();
                    response.ElapsedTime = stopwatch.Elapsed;
                    if (redirectTrace.Count > 0)
                    {
                        response.Headers += "\r\n[Redirects]\r\n" +
                            string.Join("\r\n", redirectTrace) +
                            "\r\nFinal URL: " + response.RequestUrl;
                    }
                    return response;
                }

                if (redirectCount >= MaxRedirectCount)
                    throw new InvalidOperationException($"HTTP redirect limit ({MaxRedirectCount}) exceeded.");

                Uri nextUri;
                try { nextUri = new Uri(currentUri, location); }
                catch (Exception ex)
                {
                    throw new InvalidDataException("HTTP redirect Location is invalid: " + location, ex);
                }
                if (nextUri.Scheme != Uri.UriSchemeHttp && nextUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("HTTP redirect uses an unsupported scheme: " + nextUri.Scheme);
                if (currentUri.Scheme == Uri.UriSchemeHttps && nextUri.Scheme == Uri.UriSchemeHttp)
                    throw new InvalidOperationException("Refused insecure HTTPS-to-HTTP redirect: " + nextUri.AbsoluteUri);
                if (!IsSameOrigin(currentUri, nextUri))
                {
                    throw new InvalidOperationException(
                        "Refused cross-origin redirect to protect Authorization, Cookie, Proxy-Authorization " +
                        "and custom sensitive headers. Send the final URL explicitly if this is intended: " +
                        nextUri.AbsoluteUri);
                }

                var nextMethod = GetRedirectMethod(currentRequest.Method, response.StatusCode);
                var includeBody = string.Equals(nextMethod, currentRequest.Method, StringComparison.OrdinalIgnoreCase);
                redirectTrace.Add($"{(int)response.StatusCode}: {currentUri.AbsoluteUri} -> {nextUri.AbsoluteUri}");
                currentRequest = CloneRequest(currentRequest, nextUri.AbsoluteUri, nextMethod, includeBody);
            }
        }

        private static async Task<HttpResponseModel> SendWithHttpClientAsync(
            HttpRequestModel requestModel,
            CancellationToken cancellationToken)
        {
            using (var requestMessage = BuildRequestMessage(requestModel))
            using (var responseMessage = await SharedHttpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                EnsureResponseHeadersWithinLimit(responseMessage);
                var contentLength = responseMessage.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > MaxCompressedResponseBodyBytes)
                {
                    throw new InvalidDataException(
                        $"HTTP response body exceeds the {MaxCompressedResponseBodyBytes} byte compressed-body limit.");
                }

                byte[] bodyBytes;
                using (var stream = await responseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    bodyBytes = await ReadBoundedResponseBodyAsync(
                        stream,
                        MaxCompressedResponseBodyBytes,
                        cancellationToken).ConfigureAwait(false);
                }
                if (contentLength.HasValue && bodyBytes.LongLength != contentLength.Value)
                    throw new EndOfStreamException(
                        $"HTTP response body is incomplete: expected {contentLength.Value}, received {bodyBytes.LongLength} bytes.");

                var headers = ToResponseHeaderDictionary(responseMessage);
                bodyBytes = DecodeContentEncoding(bodyBytes, headers, cancellationToken);
                return new HttpResponseModel
                {
                    StatusCode = responseMessage.StatusCode,
                    ReasonPhrase = responseMessage.ReasonPhrase ?? string.Empty,
                    Body = GetBodyEncoding(headers).GetString(bodyBytes),
                    Headers = FormatResponseHeaders(responseMessage),
                    RequestMethod = requestModel.Method,
                    RequestUrl = requestModel.Url
                };
            }
        }

        private static async Task<byte[]> ReadBoundedResponseBodyAsync(
            Stream stream,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    total = checked(total + read);
                    ValidateResponseBodyLength(total, maximumBytes, "HTTP response body");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        internal static void ValidateResponseBodyLength(long length, long maximumBytes, string phase)
        {
            if (maximumBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (length < 0 || length > maximumBytes)
                throw new InvalidDataException($"{phase ?? "HTTP response body"} exceeds the {maximumBytes} byte safety limit.");
        }

        internal static void ValidateDecompressedBodyLength(long compressedBytes, long decompressedBytes)
        {
            if (compressedBytes < 0 || compressedBytes > MaxCompressedResponseBodyBytes)
                throw new InvalidDataException("HTTP compressed response length is outside the safety limit.");
            var ratioLimit = Math.Min(
                MaxDecompressedResponseBodyBytes,
                checked(compressedBytes * MaxCompressionRatio + CompressionRatioSlackBytes));
            if (decompressedBytes < 0 || decompressedBytes > ratioLimit ||
                decompressedBytes > MaxDecompressedResponseBodyBytes)
            {
                throw new InvalidDataException(
                    $"HTTP decompressed response exceeds the {MaxDecompressedResponseBodyBytes} byte or {MaxCompressionRatio}:1 ratio safety limit.");
            }
        }

        private static void EnsureResponseHeadersWithinLimit(HttpResponseMessage response)
        {
            long total = 32;
            foreach (var header in response.Headers)
                total = AddHeaderBytes(total, header.Key, header.Value);
            foreach (var header in response.Content.Headers)
                total = AddHeaderBytes(total, header.Key, header.Value);
            if (total > MaxResponseHeaderBytes)
                throw new InvalidDataException($"HTTP response headers exceed the {MaxResponseHeaderBytes} byte safety limit.");
        }

        private static long AddHeaderBytes(long total, string name, IEnumerable<string> values)
        {
            total = checked(total + Encoding.UTF8.GetByteCount(name ?? string.Empty) + 4);
            foreach (var value in values ?? Enumerable.Empty<string>())
                total = checked(total + Encoding.UTF8.GetByteCount(value ?? string.Empty) + 2);
            return total;
        }

        private static Dictionary<string, string> ToResponseHeaderDictionary(HttpResponseMessage response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
            foreach (var header in response.Content.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
            return headers;
        }

        private static bool IsRedirectStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 301 || code == 302 || code == 303 || code == 307 || code == 308;
        }

        private static bool IsSameOrigin(Uri first, Uri second)
        {
            return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
                   first.Port == second.Port;
        }

        private static string GetRedirectMethod(string method, HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            if (code == 303 && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
                return "GET";
            if ((code == 301 || code == 302) && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                return "GET";
            return method;
        }

        private static bool TryGetResponseHeader(string formattedHeaders, string headerName, out string value)
        {
            value = null;
            foreach (var line in (formattedHeaders ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0 || !line.Substring(0, separator).Trim().Equals(headerName, StringComparison.OrdinalIgnoreCase))
                    continue;
                value = line.Substring(separator + 1).Trim();
                return value.Length > 0;
            }
            return false;
        }

        private static HttpRequestModel CloneRequest(
            HttpRequestModel source,
            string url,
            string method,
            bool includeBody)
        {
            var headers = new Dictionary<string, string>(
                source.Headers ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            if (!includeBody)
            {
                foreach (var headerName in headers.Keys
                    .Where(name => IsContentHeader(name) ||
                                   name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    .ToList())
                {
                    headers.Remove(headerName);
                }
            }

            return new HttpRequestModel
            {
                Method = method,
                Url = url,
                Headers = headers,
                Body = includeBody ? source.Body : string.Empty,
                BodyType = includeBody ? source.BodyType : RequestBodyType.Raw,
                FormFields = includeBody ? new List<FormFieldModel>(source.FormFields ?? new List<FormFieldModel>()) : new List<FormFieldModel>(),
                Files = includeBody ? new List<FileFieldModel>(source.Files ?? new List<FileFieldModel>()) : new List<FileFieldModel>()
            };
        }

        private static async Task<HttpResponseModel> SendWithOpenSslAsync(
            HttpRequestModel requestModel,
            Uri uri,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestPlan = await Task.Run(
                () => BuildRawHttpRequestPlan(requestModel, uri, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var options = OpenSslCli.FromGlobalSettings(uri.Host, uri.Port > 0 ? uri.Port : 443, useDtls: false);
            if (string.IsNullOrWhiteSpace(options.TargetHost))
                options.TargetHost = uri.Host;

            var result = await Task.Run(
                () => OpenSslCli.SendAsync(
                    options,
                    (stream, token) => WriteRawHttpRequestAsync(requestPlan, stream, token),
                    30000,
                    cancellationToken),
                cancellationToken);
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

            var response = ParseRawHttpResponse(result.Output, requestModel.Method, cancellationToken);
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
            var requestHeaders = new Dictionary<string, string>(
                requestModel.Headers ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            var hasTransferEncoding = requestHeaders.TryGetValue("Transfer-Encoding", out var transferEncoding);
            if (hasTransferEncoding && !IsSupportedRequestTransferEncoding(transferEncoding))
                throw new NotSupportedException("请求 Transfer-Encoding 仅支持单一且无参数的 chunked 编码。");

            if (CanSendBody(method))
                requestMessage.Content = BuildHttpContent(requestModel);

            if (hasTransferEncoding && requestMessage.Content == null)
                requestMessage.Content = new ByteArrayContent(new byte[0]);

            foreach (var header in requestHeaders)
            {
                if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (hasTransferEncoding && header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;

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

            if (hasTransferEncoding)
            {
                requestMessage.Content.Headers.ContentLength = null;
                requestMessage.Headers.TransferEncodingChunked = true;
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
            var plan = BuildRawHttpRequestPlan(requestModel, uri, CancellationToken.None);
            ThrowIfBufferedBodyTooLarge(plan.Body.ContentLength);

            using (var memory = new MemoryStream())
            {
                WriteRawHttpRequestAsync(plan, memory, CancellationToken.None).GetAwaiter().GetResult();
                return memory.ToArray();
            }
        }

        private static RawHttpRequestPlan BuildRawHttpRequestPlan(
            HttpRequestModel requestModel,
            Uri uri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = BuildRawHttpBodyPlan(requestModel, cancellationToken);
            var requestHeaders = requestModel.Headers ?? new Dictionary<string, string>();
            var headers = new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase);
            headers["Host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            headers["Connection"] = "close";

            var hasTransferEncoding = headers.TryGetValue("Transfer-Encoding", out var transferEncoding);
            if (hasTransferEncoding)
            {
                if (!IsSupportedRequestTransferEncoding(transferEncoding))
                    throw new NotSupportedException("请求 Transfer-Encoding 仅支持单一且无参数的 chunked 编码。");
                headers.Remove("Content-Length");
            }
            else if (body.ContentLength > 0)
            {
                headers["Content-Length"] = body.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (body.ContentLength > 0 &&
                !string.IsNullOrWhiteSpace(body.ContentType) &&
                !headers.ContainsKey("Content-Type"))
            {
                headers["Content-Type"] = body.ContentType;
            }

            var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            var builder = new StringBuilder();
            builder.Append(requestModel.Method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
            foreach (var header in headers)
                builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            builder.Append("\r\n");

            return new RawHttpRequestPlan
            {
                HeaderBytes = Encoding.ASCII.GetBytes(builder.ToString()),
                Body = body,
                UseChunkedTransferEncoding = hasTransferEncoding
            };
        }

        private static RawHttpBodyPlan BuildRawHttpBodyPlan(
            HttpRequestModel requestModel,
            CancellationToken cancellationToken)
        {
            if (!CanSendBody(new HttpMethod(requestModel.Method)))
                return new RawHttpBodyPlan();

            switch (requestModel.BodyType)
            {
                case RequestBodyType.FormUrlEncoded:
                {
                    var text = BuildFormUrlEncodedBody(requestModel);
                    return new RawHttpBodyPlan
                    {
                        ContentType = "application/x-www-form-urlencoded",
                        TextBody = text,
                        ContentLength = Encoding.UTF8.GetByteCount(text)
                    };
                }
                case RequestBodyType.MultipartFormData:
                    return BuildMultipartBodyPlan(requestModel, cancellationToken);
                default:
                {
                    var text = requestModel.Body ?? string.Empty;
                    return new RawHttpBodyPlan
                    {
                        TextBody = text,
                        ContentLength = Encoding.UTF8.GetByteCount(text)
                    };
                }
            }
        }

        private static RawHttpBodyPlan BuildMultipartBodyPlan(
            HttpRequestModel requestModel,
            CancellationToken cancellationToken)
        {
            var fields = (requestModel.FormFields ?? new List<FormFieldModel>())
                .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Key))
                .ToList();
            var files = (requestModel.Files ?? new List<FileFieldModel>())
                .Where(file => file.IsEnabled &&
                               !string.IsNullOrWhiteSpace(file.FieldName) &&
                               !string.IsNullOrWhiteSpace(file.FilePath))
                .ToList();

            if (fields.Count == 0 && files.Count == 0)
                return new RawHttpBodyPlan();

            var boundary = "----llcomplus-" + Guid.NewGuid().ToString("N");
            var plan = new RawHttpBodyPlan
            {
                ContentType = "multipart/form-data; boundary=" + boundary,
                IsMultipart = true
            };
            long contentLength = 0;

            foreach (var field in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var headerBytes = Encoding.ASCII.GetBytes(
                    "--" + boundary + "\r\n" +
                    $"Content-Disposition: form-data; name=\"{EscapeQuoted(field.Key.Trim())}\"\r\n\r\n");
                var value = field.Value ?? string.Empty;
                contentLength = AddBodyLength(contentLength, headerBytes.Length);
                contentLength = AddBodyLength(contentLength, Encoding.UTF8.GetByteCount(value));
                contentLength = AddBodyLength(contentLength, CrlfBytes.Length);
                plan.Fields.Add(new RawMultipartFieldPart
                {
                    HeaderBytes = headerBytes,
                    Value = value
                });
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(file.FilePath);
                if (!fileInfo.Exists)
                    throw new FileNotFoundException($"文件不存在：{file.FilePath}", file.FilePath);

                var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType;
                var headerBytes = Encoding.ASCII.GetBytes(
                    "--" + boundary + "\r\n" +
                    $"Content-Disposition: form-data; name=\"{EscapeQuoted(file.FieldName.Trim())}\"; filename=\"{EscapeQuoted(Path.GetFileName(file.FilePath))}\"\r\n" +
                    "Content-Type: " + contentType + "\r\n\r\n");
                contentLength = AddBodyLength(contentLength, headerBytes.Length);
                contentLength = AddBodyLength(contentLength, fileInfo.Length);
                contentLength = AddBodyLength(contentLength, CrlfBytes.Length);
                plan.Files.Add(new RawMultipartFilePart
                {
                    HeaderBytes = headerBytes,
                    FilePath = file.FilePath,
                    ExpectedLength = fileInfo.Length
                });
            }

            plan.MultipartClosingBytes = Encoding.ASCII.GetBytes("--" + boundary + "--\r\n");
            contentLength = AddBodyLength(contentLength, plan.MultipartClosingBytes.Length);
            plan.ContentLength = contentLength;
            return plan;
        }

        private static long AddBodyLength(long current, long value)
        {
            try
            {
                return checked(current + value);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("HTTPS 请求正文长度超过支持范围。", ex);
            }
        }

        private static async Task WriteRawHttpRequestAsync(
            RawHttpRequestPlan plan,
            Stream output,
            CancellationToken cancellationToken)
        {
            ValidateMultipartFiles(plan.Body, cancellationToken);
            await WriteBytesAsync(output, plan.HeaderBytes, 0, plan.HeaderBytes.Length, cancellationToken).ConfigureAwait(false);
            await WriteRawHttpBodyAsync(
                plan.Body,
                output,
                plan.UseChunkedTransferEncoding,
                cancellationToken).ConfigureAwait(false);

            if (plan.UseChunkedTransferEncoding)
                await WriteBytesAsync(output, FinalChunkBytes, 0, FinalChunkBytes.Length, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WriteRawHttpBodyAsync(
            RawHttpBodyPlan body,
            Stream output,
            bool useChunkedTransferEncoding,
            CancellationToken cancellationToken)
        {
            if (!body.IsMultipart)
            {
                await WriteUtf8TextAsync(
                    output,
                    body.TextBody ?? string.Empty,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var field in body.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteBodyBytesAsync(
                    output,
                    field.HeaderBytes,
                    0,
                    field.HeaderBytes.Length,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
                await WriteUtf8TextAsync(
                    output,
                    field.Value,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
                await WriteBodyBytesAsync(
                    output,
                    CrlfBytes,
                    0,
                    CrlfBytes.Length,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in body.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteBodyBytesAsync(
                    output,
                    file.HeaderBytes,
                    0,
                    file.HeaderBytes.Length,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
                await WriteMultipartFileAsync(
                    output,
                    file,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
                await WriteBodyBytesAsync(
                    output,
                    CrlfBytes,
                    0,
                    CrlfBytes.Length,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
            }

            if (body.MultipartClosingBytes != null)
            {
                await WriteBodyBytesAsync(
                    output,
                    body.MultipartClosingBytes,
                    0,
                    body.MultipartClosingBytes.Length,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private static void ValidateMultipartFiles(RawHttpBodyPlan body, CancellationToken cancellationToken)
        {
            foreach (var file in body.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(file.FilePath);
                if (!fileInfo.Exists)
                    throw new FileNotFoundException($"文件不存在：{file.FilePath}", file.FilePath);
                if (fileInfo.Length != file.ExpectedLength)
                    throw new IOException($"文件大小在请求构建后发生变化：{file.FilePath}");
            }
        }

        private static async Task WriteMultipartFileAsync(
            Stream output,
            RawMultipartFilePart file,
            bool useChunkedTransferEncoding,
            CancellationToken cancellationToken)
        {
            using (var input = new FileStream(
                file.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileCopyBufferSize,
                useAsync: true))
            {
                if (input.Length != file.ExpectedLength)
                    throw new IOException($"文件大小在请求构建后发生变化：{file.FilePath}");

                var buffer = new byte[FileCopyBufferSize];
                var remaining = file.ExpectedLength;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = await input.ReadAsync(buffer, 0, requested, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        throw new EndOfStreamException($"读取上传文件时提前到达结尾：{file.FilePath}");

                    await WriteBodyBytesAsync(
                        output,
                        buffer,
                        0,
                        read,
                        useChunkedTransferEncoding,
                        cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
            }
        }

        private static async Task WriteUtf8TextAsync(
            Stream output,
            string text,
            bool useChunkedTransferEncoding,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(text))
                return;

            const int characterBlockSize = 4096;
            var buffer = new byte[Encoding.UTF8.GetMaxByteCount(characterBlockSize)];
            var characterOffset = 0;
            while (characterOffset < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var characterCount = Math.Min(characterBlockSize, text.Length - characterOffset);
                if (characterOffset + characterCount < text.Length &&
                    char.IsHighSurrogate(text[characterOffset + characterCount - 1]))
                {
                    characterCount--;
                }
                if (characterCount <= 0)
                    characterCount = 1;

                var byteCount = Encoding.UTF8.GetBytes(text, characterOffset, characterCount, buffer, 0);
                await WriteBodyBytesAsync(
                    output,
                    buffer,
                    0,
                    byteCount,
                    useChunkedTransferEncoding,
                    cancellationToken).ConfigureAwait(false);
                characterOffset += characterCount;
            }
        }

        private static async Task WriteBodyBytesAsync(
            Stream output,
            byte[] bytes,
            int offset,
            int count,
            bool useChunkedTransferEncoding,
            CancellationToken cancellationToken)
        {
            if (count <= 0)
                return;

            if (!useChunkedTransferEncoding)
            {
                await WriteBytesAsync(output, bytes, offset, count, cancellationToken).ConfigureAwait(false);
                return;
            }

            var chunkHeader = Encoding.ASCII.GetBytes(
                count.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
            await WriteBytesAsync(output, chunkHeader, 0, chunkHeader.Length, cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(output, bytes, offset, count, cancellationToken).ConfigureAwait(false);
            await WriteBytesAsync(output, CrlfBytes, 0, CrlfBytes.Length, cancellationToken).ConfigureAwait(false);
        }

        private static Task WriteBytesAsync(
            Stream output,
            byte[] bytes,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return output.WriteAsync(bytes, offset, count, cancellationToken);
        }

        private static byte[] BuildRawBody(HttpRequestModel requestModel, out string contentType)
        {
            var body = BuildRawHttpBodyPlan(requestModel, CancellationToken.None);
            contentType = body.ContentType;
            ThrowIfBufferedBodyTooLarge(body.ContentLength);
            using (var memory = new MemoryStream())
            {
                WriteRawHttpBodyAsync(body, memory, false, CancellationToken.None).GetAwaiter().GetResult();
                return memory.ToArray();
            }
        }

        private static byte[] BuildMultipartBody(HttpRequestModel requestModel, out string contentType)
        {
            var body = BuildMultipartBodyPlan(requestModel, CancellationToken.None);
            contentType = body.ContentType;
            ThrowIfBufferedBodyTooLarge(body.ContentLength);
            using (var memory = new MemoryStream())
            {
                WriteRawHttpBodyAsync(body, memory, false, CancellationToken.None).GetAwaiter().GetResult();
                return memory.ToArray();
            }
        }

        private static void ThrowIfBufferedBodyTooLarge(long bodyLength)
        {
            if (bodyLength <= MaxBufferedRawRequestBodyBytes)
                return;

            throw new InvalidOperationException(
                $"HTTPS 聚合请求正文不能超过 {MaxBufferedRawRequestBodyBytes / (1024 * 1024)} MiB；请使用流式发送路径。");
        }

        private static byte[] EncodeChunkedBody(byte[] body)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));

            using (var memory = new MemoryStream())
            {
                if (body.Length > 0)
                {
                    WriteAscii(memory, body.Length.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                    WriteAscii(memory, "\r\n");
                    memory.Write(body, 0, body.Length);
                    WriteAscii(memory, "\r\n");
                }
                memory.Write(FinalChunkBytes, 0, FinalChunkBytes.Length);
                return memory.ToArray();
            }
        }

        private static string BuildFormUrlEncodedBody(HttpRequestModel requestModel)
        {
            return string.Join("&", (requestModel.FormFields ?? new List<FormFieldModel>())
                .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Key))
                .Select(field => $"{HttpUtility.UrlEncode(field.Key.Trim())}={HttpUtility.UrlEncode(field.Value ?? string.Empty)}"));
        }

        private static bool IsSupportedRequestTransferEncoding(string transferEncoding)
        {
            return TryParseTransferEncodingCodings(transferEncoding, out var codings, out var hasParameters) &&
                   codings.Length == 1 &&
                   !hasParameters[0] &&
                   codings[0].Equals("chunked", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasFinalChunkedTransferEncoding(string transferEncoding)
        {
            return TryParseTransferEncodingCodings(transferEncoding, out var codings, out var hasParameters) &&
                   codings.Length > 0 &&
                   !hasParameters[codings.Length - 1] &&
                   codings[codings.Length - 1].Equals("chunked", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedResponseTransferEncoding(string transferEncoding)
        {
            return IsSupportedRequestTransferEncoding(transferEncoding);
        }

        private static bool TryParseTransferEncodingCodings(
            string transferEncoding,
            out string[] codings,
            out bool[] hasParameters)
        {
            codings = new string[0];
            hasParameters = new bool[0];
            if (string.IsNullOrWhiteSpace(transferEncoding))
                return false;

            var codingList = new List<string>();
            var parameterList = new List<bool>();
            foreach (var rawItem in transferEncoding.Split(','))
            {
                var item = rawItem.Trim();
                if (item.Length == 0)
                    return false;

                var semicolon = item.IndexOf(';');
                var coding = (semicolon < 0 ? item : item.Substring(0, semicolon)).Trim();
                if (!IsHttpToken(coding))
                    return false;
                if (semicolon >= 0 && string.IsNullOrWhiteSpace(item.Substring(semicolon + 1)))
                    return false;

                codingList.Add(coding);
                parameterList.Add(semicolon >= 0);
            }

            codings = codingList.ToArray();
            hasParameters = parameterList.ToArray();
            return codings.Length > 0;
        }

        private static bool IsHttpToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (var ch in value)
            {
                if ((ch >= 'a' && ch <= 'z') ||
                    (ch >= 'A' && ch <= 'Z') ||
                    (ch >= '0' && ch <= '9'))
                {
                    continue;
                }
                if ("!#$%&'*+-.^_`|~".IndexOf(ch) >= 0)
                    continue;
                return false;
            }
            return true;
        }

        private static HttpResponseModel ParseRawHttpResponse(byte[] responseBytes, string requestMethod)
        {
            return ParseRawHttpResponse(responseBytes, requestMethod, CancellationToken.None);
        }

        private static HttpResponseModel ParseRawHttpResponse(
            byte[] responseBytes,
            string requestMethod,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (responseBytes == null || responseBytes.Length == 0)
                throw new InvalidDataException("OpenSSL 未返回 HTTP 响应数据。");

            SplitFinalHttpResponse(responseBytes, out var headerBytes, out var bodyBytes);
            if (headerBytes.LongLength > MaxResponseHeaderBytes)
                throw new InvalidDataException($"HTTP response headers exceed the {MaxResponseHeaderBytes} byte safety limit.");
            if (bodyBytes.LongLength > MaxCompressedResponseBodyBytes)
                throw new InvalidDataException($"HTTP response body exceeds the {MaxCompressedResponseBodyBytes} byte compressed-body limit.");
            var finalHeaderText = Encoding.ASCII.GetString(headerBytes);
            var lines = finalHeaderText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (!TryParseStatusLine(lines.FirstOrDefault(), out var code, out var reason))
                throw new InvalidDataException("OpenSSL 返回的数据不是有效的 HTTP 响应状态行。");

            var headersOnly = string.Join("\r\n", lines.Skip(1));
            var headers = ParseResponseHeaders(lines.Skip(1));
            var mustNotHaveBody = string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                                  (code >= 100 && code < 200) || code == 204 || code == 304;
            var hasTransferEncoding = headers.TryGetValue("Transfer-Encoding", out var transferEncoding);

            if (hasTransferEncoding)
            {
                if (!HasFinalChunkedTransferEncoding(transferEncoding))
                    throw new InvalidDataException("HTTP 响应 Transfer-Encoding 的最终编码必须是 chunked。");
                if (!IsSupportedResponseTransferEncoding(transferEncoding))
                    throw new NotSupportedException("暂不支持 chunked 之前的 HTTP 响应 Transfer-Encoding 编码。");

                if (!mustNotHaveBody)
                    bodyBytes = DecodeChunkedBody(bodyBytes, cancellationToken);
            }
            else if (!mustNotHaveBody && headers.TryGetValue("Content-Length", out var contentLengthText))
            {
                if (!long.TryParse(contentLengthText.Trim(), out var contentLength) || contentLength < 0)
                    throw new InvalidDataException("HTTP Content-Length 无效。");
                if (contentLength > MaxCompressedResponseBodyBytes)
                    throw new InvalidDataException($"HTTP response body exceeds the {MaxCompressedResponseBodyBytes} byte compressed-body limit.");
                if (bodyBytes.LongLength != contentLength)
                    throw new EndOfStreamException($"HTTP 响应正文不完整：应为 {contentLength} 字节，实际为 {bodyBytes.LongLength} 字节。");
            }

            bodyBytes = DecodeContentEncoding(bodyBytes, headers, cancellationToken);

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

                var currentHeaderLength = separator - headerStart;
                if (currentHeaderLength < 0 || currentHeaderLength > MaxResponseHeaderBytes)
                    throw new InvalidDataException($"HTTP response headers exceed the {MaxResponseHeaderBytes} byte safety limit.");
                var currentHeader = SubArray(responseBytes, headerStart, currentHeaderLength);
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
            return DecodeChunkedBody(body, CancellationToken.None);
        }

        private static byte[] DecodeChunkedBody(byte[] body, CancellationToken cancellationToken)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (body.LongLength > MaxCompressedResponseBodyBytes)
                throw new InvalidDataException($"HTTP chunked body exceeds the {MaxCompressedResponseBodyBytes} byte safety limit.");

            using (var output = new MemoryStream())
            {
                var offset = 0;
                long decodedBytes = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = ReadRequiredAsciiLine(body, ref offset);
                    var semicolon = line.IndexOf(';');
                    var sizeText = semicolon >= 0 ? line.Substring(0, semicolon) : line;
                    if (!int.TryParse(sizeText.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
                        throw new InvalidDataException("HTTP 分块长度无效。");
                    if (size == 0)
                    {
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (ReadRequiredAsciiLine(body, ref offset).Length == 0)
                                break;
                        }
                        if (offset != body.Length)
                            throw new InvalidDataException("HTTP 分块响应结束后包含多余数据。");
                        return output.ToArray();
                    }

                    decodedBytes = checked(decodedBytes + size);
                    if (decodedBytes > MaxCompressedResponseBodyBytes)
                        throw new InvalidDataException($"HTTP decoded chunked body exceeds the {MaxCompressedResponseBodyBytes} byte safety limit.");
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
            return DecodeContentEncoding(body, headers, CancellationToken.None);
        }

        private static byte[] DecodeContentEncoding(
            byte[] body,
            Dictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (body.LongLength > MaxCompressedResponseBodyBytes)
                throw new InvalidDataException($"HTTP response body exceeds the {MaxCompressedResponseBodyBytes} byte compressed-body limit.");
            if (body.Length == 0 || !headers.TryGetValue("Content-Encoding", out var encodingHeader))
                return body;

            var originalCompressedLength = body.LongLength;
            var ratioLimit = Math.Min(
                MaxDecompressedResponseBodyBytes,
                checked(originalCompressedLength * MaxCompressionRatio + CompressionRatioSlackBytes));
            var encodings = encodingHeader.Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0 && !value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                .ToList();
            for (var i = encodings.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var encoding = encodings[i];
                if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                    body = Decompress(body, stream => new GZipStream(stream, CompressionMode.Decompress), originalCompressedLength, ratioLimit, cancellationToken);
                else if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
                    body = Decompress(body, stream => new DeflateStream(stream, CompressionMode.Decompress), originalCompressedLength, ratioLimit, cancellationToken);
                else
                    throw new NotSupportedException("暂不支持 HTTP Content-Encoding：" + encoding);
            }
            return body;
        }

        private static byte[] Decompress(
            byte[] body,
            Func<Stream, Stream> createDecompressionStream,
            long originalCompressedBytes,
            long maximumOutputBytes,
            CancellationToken cancellationToken)
        {
            using (var input = new MemoryStream(body, writable: false))
            using (var decompression = createDecompressionStream(input))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = decompression.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    total = checked(total + read);
                    ValidateDecompressedBodyLength(originalCompressedBytes, total);
                    ValidateResponseBodyLength(total, maximumOutputBytes, "HTTP decompressed response");
                    output.Write(buffer, 0, read);
                }
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
            try
            {
                foreach (var field in enabledFields)
                    content.Add(new StringContent(field.Value ?? string.Empty, Encoding.UTF8), field.Key.Trim());

                foreach (var file in enabledFiles)
                {
                    if (!File.Exists(file.FilePath))
                        throw new FileNotFoundException($"文件不存在：{file.FilePath}", file.FilePath);

                    var fileContent = new StreamContent(File.OpenRead(file.FilePath));
                    try
                    {
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
                        content.Add(fileContent, file.FieldName.Trim(), Path.GetFileName(file.FilePath));
                        fileContent = null; // MultipartFormDataContent now owns it.
                    }
                    finally
                    {
                        fileContent?.Dispose();
                    }
                }

                return content;
            }
            catch
            {
                content.Dispose();
                throw;
            }
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
