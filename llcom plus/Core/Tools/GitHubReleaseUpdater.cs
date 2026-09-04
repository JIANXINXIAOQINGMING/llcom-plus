using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace llcom_plus.Tools
{
    internal static class GitHubReleaseUpdater
    {
        private const string Repository = "JIANXINXIAOQINGMING/llcom-plus";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const string LatestReleasePage = "https://github.com/" + Repository + "/releases/latest";
        private const string UpdateRootDirectoryName = "llcom_plus_update";
        private const string PendingDirectoryName = "pending";
        private const string PendingMetadataFileName = "pending-update.json";
        private const string SignatureSchema = "llcom-plus-update-signature-v1";
        private const string TrustedPublicKeyFileName = "UpdateSigningPublicKey.xml";
        internal const long MaximumUpdateDownloadBytes = 512L * 1024L * 1024L;
        internal const long MaximumSignatureBytes = 64L * 1024L;
        internal const long MaximumReleaseMetadataBytes = 2L * 1024L * 1024L;
        internal const int MaximumUpdateResponseHeaderKilobytes = 64;
        internal const int MaximumDownloadRedirects = 5;
        private const long MinimumFreeDiskReserveBytes = 64L * 1024L * 1024L;
        private static readonly TimeSpan DownloadTotalTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReleaseMetadataTotalTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ReleaseMetadataIdleTimeout = TimeSpan.FromSeconds(10);
        private const int MaximumUpdateEntryCount = 10000;
        private const long MaximumExpandedUpdateBytes = 2L * 1024 * 1024 * 1024;
        private static bool installScheduled;

        internal static string TrustedPublicKeyPath => Path.Combine(Global.AppPath, TrustedPublicKeyFileName);

        public static string LocalPackageDirectory => AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static Task<GitHubReleaseInfo> CheckLatestAsync()
        {
            return CheckLatestAsync(CancellationToken.None);
        }

        public static async Task<GitHubReleaseInfo> CheckLatestAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitHubReleaseInfo info;
            try
            {
                info = await CheckLatestFromApiAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                info = await CheckLatestFromRedirectAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (info != null && info.AssetSizeBytes <= 0 && !string.IsNullOrWhiteSpace(info.AssetDownloadUrl))
                info.AssetSizeBytes = await TryGetAssetSizeAsync(info.AssetDownloadUrl, cancellationToken).ConfigureAwait(false);
            if (info != null)
                info.AutomaticUpdateTrustError = GetAutomaticUpdateTrustError(info);

            return info;
        }

        private static async Task<GitHubReleaseInfo> CheckLatestFromApiAsync(CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                string json;
                using (var response = await client.GetAsync(
                    LatestReleaseApi,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    json = await ReadMetadataContentAsync(response, cancellationToken).ConfigureAwait(false);
                }
                var root = JObject.Parse(json);
                var tag = root["tag_name"]?.ToString() ?? "";
                if (!TryParseReleaseTag(tag, out var version))
                    throw new InvalidOperationException("GitHub latest release has an invalid version tag.");

                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
                cancellationToken.ThrowIfCancellationRequested();
                var assets = root["assets"] as JArray;
                var asset = SelectReleaseAsset(assets, version);
                var signatureAsset = SelectSignatureAsset(assets, asset?.Name);
                return new GitHubReleaseInfo
                {
                    Version = version,
                    CurrentVersion = currentVersion,
                    TagName = tag,
                    ReleaseUrl = root["html_url"]?.ToString() ?? "https://github.com/" + Repository + "/releases",
                    AssetName = asset?.Name,
                    AssetDownloadUrl = asset?.DownloadUrl,
                    AssetSizeBytes = asset?.SizeBytes ?? 0,
                    AssetDigest = asset?.Digest,
                    SignatureAssetName = signatureAsset?.Name,
                    SignatureDownloadUrl = signatureAsset?.DownloadUrl,
                };
            }
        }

        private static async Task<GitHubReleaseInfo> CheckLatestFromRedirectAsync(CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");

                using (var response = await client.GetAsync(
                    LatestReleasePage,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var releaseUri = response.RequestMessage?.RequestUri;
                    if (!TryGetReleaseTagFromUri(releaseUri, out var tag, out var version))
                        throw new InvalidOperationException("GitHub latest release redirected to an invalid release URL.");

                    var expandedAssetsUrl = "https://github.com/" + Repository + "/releases/expanded_assets/" +
                                            Uri.EscapeDataString(tag);
                    string expandedAssetsHtml;
                    using (var assetsResponse = await client.GetAsync(
                        expandedAssetsUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false))
                    {
                        assetsResponse.EnsureSuccessStatusCode();
                        if (!IsExpectedExpandedAssetsUri(assetsResponse.RequestMessage?.RequestUri, tag))
                            throw new InvalidOperationException("GitHub expanded-assets request redirected outside the expected release.");
                        expandedAssetsHtml = await ReadMetadataContentAsync(assetsResponse, cancellationToken).ConfigureAwait(false);
                    }

                    var asset = SelectReleaseAssetFromHtml(expandedAssetsHtml, tag, version);
                    var signatureAsset = SelectSignatureAssetFromHtml(expandedAssetsHtml, tag, asset?.Name);
                    return new GitHubReleaseInfo
                    {
                        Version = version,
                        CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0),
                        TagName = tag,
                        ReleaseUrl = releaseUri.AbsoluteUri,
                        AssetName = asset?.Name,
                        AssetDownloadUrl = asset?.DownloadUrl,
                        AssetSizeBytes = asset?.SizeBytes ?? 0,
                        SignatureAssetName = signatureAsset?.Name,
                        SignatureDownloadUrl = signatureAsset?.DownloadUrl,
                    };
                }
            }
        }

        internal static string GetAutomaticUpdateTrustError(GitHubReleaseInfo release)
        {
            if (release == null)
                return "Automatic update is disabled because release metadata is missing. Download manually from the release page.";
            if (!File.Exists(TrustedPublicKeyPath))
            {
                return "Automatic update is disabled: no trusted update-signing public key is deployed. " +
                       "Download the package manually from the release page; it will not be installed automatically.";
            }
            try
            {
                LoadTrustedPublicKeyXml();
            }
            catch (Exception ex)
            {
                return "Automatic update is disabled: the deployed update-signing public key is invalid (" +
                       ex.GetBaseException().Message + "). Download manually from the release page.";
            }
            if (string.IsNullOrWhiteSpace(release.SignatureDownloadUrl) ||
                string.IsNullOrWhiteSpace(release.SignatureAssetName))
            {
                return "Automatic update is disabled: this release has no independent detached signature (.zip.sig). " +
                       "The GitHub digest alone is not an independent trust source; download manually from the release page.";
            }
            if (!string.Equals(release.SignatureAssetName, (release.AssetName ?? string.Empty) + ".sig", StringComparison.Ordinal))
                return "Automatic update is disabled: the detached signature asset does not match the ZIP name.";
            return null;
        }

        public static Task<string> DownloadAndPrepareInstallAsync(
            GitHubReleaseInfo release,
            IProgress<GitHubDownloadProgress> progress = null)
        {
            return DownloadAndPrepareInstallAsync(release, progress, CancellationToken.None);
        }

        public static async Task<string> DownloadAndPrepareInstallAsync(
            GitHubReleaseInfo release,
            IProgress<GitHubDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            var zipPath = await DownloadUpdateAsync(release, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            StartInstallAfterExit(zipPath);
            return zipPath;
        }

        public static Task<string> DownloadUpdateAsync(
            GitHubReleaseInfo release,
            IProgress<GitHubDownloadProgress> progress = null)
        {
            return DownloadUpdateAsync(release, progress, CancellationToken.None);
        }

        public static async Task<string> DownloadUpdateAsync(
            GitHubReleaseInfo release,
            IProgress<GitHubDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                throw new InvalidOperationException("Release asset is missing.");

            var trustError = GetAutomaticUpdateTrustError(release);
            if (!string.IsNullOrWhiteSpace(trustError))
                throw new InvalidOperationException(trustError);
            if (release.AssetSizeBytes > MaximumUpdateDownloadBytes)
                throw new InvalidDataException($"Update package exceeds the {MaximumUpdateDownloadBytes} byte download limit.");

            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetCachedUpdatePackage(release, out var cachedZipPath))
            {
                var cachedLength = new FileInfo(cachedZipPath).Length;
                progress?.Report(new GitHubDownloadProgress(cachedLength, cachedLength));
                return cachedZipPath;
            }

            var packageDirectory = LocalPackageDirectory;
            var zipFileName = SafeFileName(string.IsNullOrWhiteSpace(release.AssetName) ? "llcom-plus-update.zip" : release.AssetName);
            var zipPath = Path.Combine(packageDirectory, zipFileName);
            var signaturePath = zipPath + ".sig";
            var downloadPath = zipPath + ".download";
            var signatureDownloadPath = signaturePath + ".download";

            var packageInfo = TryParseLocalPackage(zipPath);
            if (packageInfo == null || NormalizeVersion(packageInfo.Version) != NormalizeVersion(release.Version))
                throw new InvalidDataException("在线更新包文件名必须包含与发布版本一致的版本号和当前架构，例如：llcom.plus_1.2.12_x64.zip。");

            TryDeleteFile(downloadPath);
            TryDeleteFile(signatureDownloadPath);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                totalCts.CancelAfter(DownloadTotalTimeout);
                try
                {
                    using (var client = CreateUpdateHttpClient())
                    {
                        await DownloadFileAsync(
                            client,
                            release.SignatureDownloadUrl,
                            signatureDownloadPath,
                            MaximumSignatureBytes,
                            0,
                            null,
                            totalCts.Token).ConfigureAwait(false);

                        await DownloadFileAsync(
                            client,
                            release.AssetDownloadUrl,
                            downloadPath,
                            MaximumUpdateDownloadBytes,
                            release.AssetSizeBytes,
                            progress,
                            totalCts.Token).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateTrustedUpdatePackage(
                        downloadPath,
                        signatureDownloadPath,
                        release.Version,
                        release.AssetName,
                        release.AssetDigest,
                        cancellationToken);

                    TryDeleteFile(zipPath);
                    TryDeleteFile(signaturePath);
                    File.Move(downloadPath, zipPath);
                    File.Move(signatureDownloadPath, signaturePath);
                    return zipPath;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Update download exceeded the total timeout of {DownloadTotalTimeout.TotalMinutes:0} minutes.");
                }
                finally
                {
                    TryDeleteFile(downloadPath);
                    TryDeleteFile(signatureDownloadPath);
                }
            }
        }

        private static HttpClient CreateUpdateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = MaximumDownloadRedirects,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = MaximumUpdateResponseHeaderKilobytes
            };
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");
            return client;
        }

        private static async Task<long> DownloadFileAsync(
            HttpClient client,
            string url,
            string destinationPath,
            long maximumBytes,
            long expectedBytes,
            IProgress<GitHubDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri) || requestUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Update downloads require an HTTPS URL.");
            ValidateDownloadLength(expectedBytes, maximumBytes, "Declared update size");

            using (var response = await client.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri == null || finalUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Refused an HTTPS-to-HTTP update download redirect.");

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue)
                    ValidateDownloadLength(contentLength.Value, maximumBytes, "Update response Content-Length");
                if (expectedBytes > 0 && contentLength.HasValue && expectedBytes != contentLength.Value)
                    throw new InvalidDataException(
                        $"Update Content-Length differs from release metadata ({contentLength.Value} vs {expectedBytes}).");

                var totalBytes = contentLength ?? expectedBytes;
                EnsureDownloadDiskSpace(destinationPath, totalBytes > 0 ? totalBytes : 1);
                progress?.Report(new GitHubDownloadProgress(0, totalBytes));

                long downloadedBytes = 0;
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int bytesRead;
                        using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            idleCts.CancelAfter(DownloadIdleTimeout);
                            try
                            {
                                bytesRead = await input.ReadAsync(
                                    buffer,
                                    0,
                                    buffer.Length,
                                    idleCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new TimeoutException(
                                    $"Update download was idle for more than {DownloadIdleTimeout.TotalSeconds:0} seconds.");
                            }
                        }

                        if (bytesRead <= 0)
                            break;
                        downloadedBytes = checked(downloadedBytes + bytesRead);
                        ValidateDownloadLength(downloadedBytes, maximumBytes, "Update download");
                        if (totalBytes > 0 && downloadedBytes > totalBytes)
                            throw new InvalidDataException("Update response exceeded its declared Content-Length.");

                        await output.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        if ((downloadedBytes & ((8L * 1024L * 1024L) - 1)) < bytesRead)
                            EnsureDownloadDiskSpace(destinationPath, Math.Max(1, totalBytes - downloadedBytes));
                        progress?.Report(new GitHubDownloadProgress(downloadedBytes, totalBytes));
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (totalBytes > 0 && downloadedBytes != totalBytes)
                    throw new InvalidDataException(
                        $"Update package download is incomplete. Expected {totalBytes} bytes, received {downloadedBytes} bytes.");
                return downloadedBytes;
            }
        }

        internal static void ValidateDownloadLength(long value, long maximumBytes, string source)
        {
            if (maximumBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (value < 0 || value > maximumBytes)
                throw new InvalidDataException($"{source ?? "Update data"} is outside the 0..{maximumBytes} byte limit.");
        }

        private static void EnsureDownloadDiskSpace(string destinationPath, long remainingBytes)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("Cannot determine the update download volume.");
            var drive = new DriveInfo(root);
            var required = checked(Math.Max(0, remainingBytes) + MinimumFreeDiskReserveBytes);
            if (drive.AvailableFreeSpace < required)
                throw new IOException(
                    $"Not enough disk space for update download. Required {required} bytes, available {drive.AvailableFreeSpace} bytes.");
        }

        public static bool TryGetCachedUpdatePackage(GitHubReleaseInfo release, out string zipPath)
        {
            zipPath = null;
            if (release == null || release.Version == null)
                return false;

            var candidates = FindLocalUpdatePackages()
                .Where(package => NormalizeVersion(package.Version) == NormalizeVersion(release.Version));
            foreach (var package in candidates)
            {
                if (!string.IsNullOrWhiteSpace(release.AssetName) &&
                    !string.Equals(Path.GetFileName(package.Path), release.AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsValidUpdatePackage(package.Path, release.Version))
                    continue;

                zipPath = package.Path;
                return true;
            }

            return false;
        }

        public static bool TryGetPendingUpdatePackage(out string zipPath)
        {
            zipPath = null;
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            foreach (var package in FindLocalUpdatePackages())
            {
                if (NormalizeVersion(package.Version) <= NormalizeVersion(currentVersion) ||
                    !IsValidUpdatePackage(package.Path, package.Version))
                    continue;

                zipPath = package.Path;
                return true;
            }

            return false;
        }

        public static bool TryStartPendingInstallOnExit()
        {
            if (installScheduled)
                return false;

            if (!TryGetPendingUpdatePackage(out var zipPath))
                return false;

            try
            {
                StartInstallAfterExit(zipPath, restartAfterInstall: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void StartInstallAfterExit(string zipPath, bool restartAfterInstall = true)
        {
            // Best effort and fail-open for the updater itself: the snapshot service
            // validates and atomically stores the user's current quick-send data.
            QuickSendBackupService.CreateNow(Global.setting, "pre-upgrade");
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                throw new FileNotFoundException("Update package is missing.", zipPath);

            zipPath = Path.GetFullPath(zipPath);
            if (!string.Equals(
                Path.GetDirectoryName(zipPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                LocalPackageDirectory,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("本地更新包必须位于程序安装目录中。");

            var package = TryParseLocalPackage(zipPath);
            if (package == null)
                throw new InvalidDataException("本地更新包命名不正确，应类似 llcom.plus_1.2.12_x64.zip。");
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (NormalizeVersion(package.Version) <= NormalizeVersion(currentVersion))
                throw new InvalidOperationException($"本地更新包版本 {package.DisplayVersion} 不高于当前版本。");
            var trustedPackage = ValidateTrustedUpdatePackage(
                zipPath,
                zipPath + ".sig",
                package.Version,
                Path.GetFileName(zipPath),
                null,
                CancellationToken.None);

            var tempRoot = Path.Combine(UpdateRoot, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var stagedZipPath = Path.Combine(tempRoot, Path.GetFileName(zipPath));
                var stagedSignaturePath = stagedZipPath + ".sig";
                File.Copy(zipPath, stagedZipPath, false);
                File.Copy(zipPath + ".sig", stagedSignaturePath, false);

                // Re-open and revalidate the staged bytes immediately before creating the child.
                trustedPackage = ValidateTrustedUpdatePackage(
                    stagedZipPath,
                    stagedSignaturePath,
                    package.Version,
                    Path.GetFileName(zipPath),
                    null,
                    CancellationToken.None);

                var scriptPath = Path.Combine(tempRoot, "install-update.ps1");
                File.WriteAllText(
                    scriptPath,
                    BuildInstallScript(stagedZipPath, tempRoot, restartAfterInstall, trustedPackage),
                    Encoding.Unicode);
                StartInstaller(scriptPath);
                installScheduled = true;
            }
            catch
            {
                TryDeleteDirectory(tempRoot);
                throw;
            }
        }

        private static GitHubReleaseAsset SelectReleaseAsset(JArray assets, Version version)
        {
            if (assets == null || assets.Count == 0 || version == null)
                return null;

            var expectedName = GetExpectedReleaseAssetName(version);
            var matches = assets
                .OfType<JObject>()
                .Select(asset => new GitHubReleaseAsset
                {
                    Name = asset["name"]?.ToString(),
                    DownloadUrl = asset["browser_download_url"]?.ToString(),
                    SizeBytes = asset["size"]?.ToObject<long?>() ?? 0,
                    Digest = asset["digest"]?.ToString(),
                })
                .Where(asset =>
                    string.Equals(asset.Name, expectedName, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(asset.DownloadUrl) &&
                    asset.SizeBytes >= 0 &&
                    asset.SizeBytes <= MaximumUpdateDownloadBytes)
                .Take(2)
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private static GitHubReleaseAsset SelectSignatureAsset(JArray assets, string zipAssetName)
        {
            if (assets == null || string.IsNullOrWhiteSpace(zipAssetName))
                return null;

            var expectedName = zipAssetName + ".sig";
            var matches = assets
                .OfType<JObject>()
                .Select(asset => new GitHubReleaseAsset
                {
                    Name = asset["name"]?.ToString(),
                    DownloadUrl = asset["browser_download_url"]?.ToString(),
                    SizeBytes = asset["size"]?.ToObject<long?>() ?? 0,
                    Digest = asset["digest"]?.ToString(),
                })
                .Where(asset =>
                    string.Equals(asset.Name, expectedName, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(asset.DownloadUrl) &&
                    asset.SizeBytes >= 0 &&
                    asset.SizeBytes <= MaximumSignatureBytes)
                .Take(2)
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private static GitHubReleaseAsset SelectReleaseAssetFromHtml(
            string html,
            string expectedTag,
            Version version)
        {
            if (version == null)
                return null;

            var expectedName = GetExpectedReleaseAssetName(version);
            var matches = ParseReleaseAssetsFromHtml(html, expectedTag)
                .Where(asset => string.Equals(asset.Name, expectedName, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static GitHubReleaseAsset SelectSignatureAssetFromHtml(
            string html,
            string expectedTag,
            string zipAssetName)
        {
            if (string.IsNullOrWhiteSpace(zipAssetName))
                return null;

            var expectedName = zipAssetName + ".sig";
            var matches = ParseReleaseAssetsFromHtml(html, expectedTag)
                .Where(asset => string.Equals(asset.Name, expectedName, StringComparison.Ordinal))
                .Take(2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static List<GitHubReleaseAsset> ParseReleaseAssetsFromHtml(string html, string expectedTag)
        {
            var assets = new List<GitHubReleaseAsset>();
            if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(expectedTag))
                return assets;

            foreach (Match match in Regex.Matches(
                html,
                "href\\s*=\\s*\"(?<url>[^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var rawUrl = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!TryNormalizeGitHubReleaseAssetUrl(rawUrl, expectedTag, out var name, out var downloadUrl))
                    continue;
                if (!name.EndsWith(".zip", StringComparison.Ordinal) &&
                    !name.EndsWith(".zip.sig", StringComparison.Ordinal))
                {
                    continue;
                }
                if (assets.Any(asset => string.Equals(asset.DownloadUrl, downloadUrl, StringComparison.Ordinal)))
                    continue;

                assets.Add(new GitHubReleaseAsset
                {
                    Name = name,
                    DownloadUrl = downloadUrl,
                });
            }

            return assets;
        }

        private static bool TryNormalizeGitHubReleaseAssetUrl(
            string rawUrl,
            string expectedTag,
            out string assetName,
            out string downloadUrl)
        {
            assetName = null;
            downloadUrl = null;
            if (string.IsNullOrWhiteSpace(rawUrl) || string.IsNullOrWhiteSpace(expectedTag))
                return false;

            var candidate = rawUrl.Trim();
            if (candidate.StartsWith("/", StringComparison.Ordinal) &&
                !candidate.StartsWith("//", StringComparison.Ordinal))
            {
                candidate = "https://github.com" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
                !IsTrustedGitHubMetadataUri(uri) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split('/');
            var repositoryParts = Repository.Split('/');
            if (repositoryParts.Length != 2 ||
                segments.Length != 7 ||
                !string.Equals(segments[1], repositoryParts[0], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(segments[2], repositoryParts[1], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(segments[3], "releases", StringComparison.Ordinal) ||
                !string.Equals(segments[4], "download", StringComparison.Ordinal) ||
                !TryDecodeSinglePathSegment(segments[5], out var assetTag) ||
                !string.Equals(assetTag, expectedTag, StringComparison.Ordinal) ||
                !TryDecodeSinglePathSegment(segments[6], out assetName) ||
                !Regex.IsMatch(assetName, @"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant) ||
                assetName == "." ||
                assetName == "..")
            {
                assetName = null;
                return false;
            }

            downloadUrl = "https://github.com/" + Repository + "/releases/download/" +
                          Uri.EscapeDataString(expectedTag) + "/" + Uri.EscapeDataString(assetName);
            return true;
        }

        private static bool TryGetReleaseTagFromUri(Uri releaseUri, out string tag, out Version version)
        {
            tag = null;
            version = null;
            if (!IsTrustedGitHubMetadataUri(releaseUri) ||
                !string.IsNullOrEmpty(releaseUri.Query) ||
                !string.IsNullOrEmpty(releaseUri.Fragment))
            {
                return false;
            }

            var segments = releaseUri.AbsolutePath.Split('/');
            var repositoryParts = Repository.Split('/');
            if (repositoryParts.Length != 2 ||
                segments.Length != 6 ||
                !string.Equals(segments[1], repositoryParts[0], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(segments[2], repositoryParts[1], StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(segments[3], "releases", StringComparison.Ordinal) ||
                !string.Equals(segments[4], "tag", StringComparison.Ordinal) ||
                !TryDecodeSinglePathSegment(segments[5], out tag))
            {
                tag = null;
                return false;
            }

            if (!TryParseReleaseTag(tag, out version))
            {
                tag = null;
                version = null;
                return false;
            }

            return true;
        }

        private static bool TryParseReleaseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            var match = Regex.Match(
                tag,
                @"^v?(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$",
                RegexOptions.CultureInvariant);
            return match.Success && Version.TryParse(match.Groups["version"].Value, out version);
        }

        private static bool IsExpectedExpandedAssetsUri(Uri uri, string expectedTag)
        {
            if (!IsTrustedGitHubMetadataUri(uri) ||
                string.IsNullOrWhiteSpace(expectedTag) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split('/');
            var repositoryParts = Repository.Split('/');
            return repositoryParts.Length == 2 &&
                   segments.Length == 6 &&
                   string.Equals(segments[1], repositoryParts[0], StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(segments[2], repositoryParts[1], StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(segments[3], "releases", StringComparison.Ordinal) &&
                   string.Equals(segments[4], "expanded_assets", StringComparison.Ordinal) &&
                   TryDecodeSinglePathSegment(segments[5], out var tag) &&
                   string.Equals(tag, expectedTag, StringComparison.Ordinal);
        }

        private static bool IsTrustedGitHubMetadataUri(Uri uri)
        {
            return uri != null &&
                   uri.IsAbsoluteUri &&
                   string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                   uri.IsDefaultPort &&
                   string.IsNullOrEmpty(uri.UserInfo);
        }

        private static bool TryDecodeSinglePathSegment(string escapedSegment, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(escapedSegment))
                return false;

            try
            {
                value = Uri.UnescapeDataString(escapedSegment);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value) || value == "." || value == "..")
                return false;
            return value.All(character =>
                character != '/' &&
                character != '\\' &&
                !char.IsControl(character));
        }

        private static string GetExpectedReleaseAssetName(Version version)
        {
            if (version == null)
                return null;

            var build = Math.Max(0, version.Build);
            var versionText = $"{version.Major}.{version.Minor}.{build}";
            if (version.Revision >= 0)
                versionText += "." + version.Revision;
            var architecture = Environment.Is64BitProcess ? "x64" : "x86";
            return $"llcom.plus_{versionText}_{architecture}.zip";
        }

        private static async Task<string> ReadMetadataContentAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            if (response == null || response.Content == null)
                throw new InvalidDataException("GitHub release metadata response is empty.");

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue)
                ValidateDownloadLength(contentLength.Value, MaximumReleaseMetadataBytes, "GitHub release metadata");

            using (var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                totalCts.CancelAfter(ReleaseMetadataTotalTimeout);
                try
                {
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        var buffer = new byte[16 * 1024];
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int read;
                            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token))
                            {
                                idleCts.CancelAfter(ReleaseMetadataIdleTimeout);
                                try
                                {
                                    read = await input.ReadAsync(
                                        buffer,
                                        0,
                                        buffer.Length,
                                        idleCts.Token).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                                {
                                    if (totalCts.IsCancellationRequested)
                                        throw new TimeoutException("GitHub release metadata exceeded the total timeout.");
                                    throw new TimeoutException("GitHub release metadata stopped responding.");
                                }
                            }

                            if (read <= 0)
                                break;
                            if (output.Length + read > MaximumReleaseMetadataBytes)
                                throw new InvalidDataException("GitHub release metadata exceeded the allowed size.");
                            output.Write(buffer, 0, read);
                        }
                        return Encoding.UTF8.GetString(output.ToArray());
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("GitHub release metadata exceeded the total timeout.");
                }
            }
        }

        public static GitHubLocalUpdatePackage FindLatestLocalUpdatePackage()
        {
            var currentVersion = NormalizeVersion(
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
            return FindLocalUpdatePackages()
                .Where(package => IsValidUpdatePackage(package.Path, package.Version))
                .FirstOrDefault(package => NormalizeVersion(package.Version) > currentVersion);
        }

        private static GitHubLocalUpdatePackage[] FindLocalUpdatePackages()
        {
            try
            {
                return Directory.EnumerateFiles(LocalPackageDirectory, "*.zip", SearchOption.TopDirectoryOnly)
                    .Select(TryParseLocalPackage)
                    .Where(package => package != null)
                    .OrderByDescending(package => package.Version)
                    .ThenByDescending(package => File.GetLastWriteTimeUtc(package.Path))
                    .ToArray();
            }
            catch
            {
                return new GitHubLocalUpdatePackage[0];
            }
        }

        private static GitHubLocalUpdatePackage TryParseLocalPackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var fileName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var match = Regex.Match(
                fileName,
                @"^llcom[ _.-]+plus[ _.-]+v?(?<version>\d+\.\d+\.\d+(?:\.\d+)?)(?:[ _.-]+(?<arch>x64|x86))?(?:[ _.-].*)?$",
                RegexOptions.IgnoreCase);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var version))
                return null;

            var packageArchitecture = match.Groups["arch"].Value;
            var currentArchitecture = Environment.Is64BitProcess ? "x64" : "x86";
            if (!string.IsNullOrWhiteSpace(packageArchitecture) &&
                !string.Equals(packageArchitecture, currentArchitecture, StringComparison.OrdinalIgnoreCase))
                return null;

            return new GitHubLocalUpdatePackage
            {
                Path = Path.GetFullPath(path),
                Version = version,
                DisplayVersion = $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"
            };
        }

        private static Version NormalizeVersion(Version version)
        {
            if (version == null)
                return new Version(0, 0, 0, 0);
            return new Version(
                version.Major,
                version.Minor,
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var match = Regex.Match(value, @"(?<version>\d+\.\d+\.\d+(?:\.\d+)?)");
            return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
        }

        private static string NormalizeVersionText(Version version)
        {
            if (version == null)
                return "";

            var build = version.Build >= 0 ? version.Build : 0;
            var revision = version.Revision >= 0 ? version.Revision : 0;
            return $"{version.Major}.{version.Minor}.{build}.{revision}";
        }

        private static async Task<long> TryGetAssetSizeAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var client = CreateUpdateHttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeoutCts.CancelAfter(ReleaseMetadataTotalTimeout);
                    using (var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token).ConfigureAwait(false))
                    {
                        return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : 0;
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
        }

        private static string UpdateRoot => Path.Combine(Path.GetTempPath(), UpdateRootDirectoryName);

        private static string PendingUpdateRoot => Path.Combine(UpdateRoot, PendingDirectoryName);

        private static string PendingMetadataPath => Path.Combine(PendingUpdateRoot, PendingMetadataFileName);

        private static JObject ReadPendingMetadata()
        {
            try
            {
                if (!File.Exists(PendingMetadataPath))
                    return null;

                return JObject.Parse(File.ReadAllText(PendingMetadataPath, Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static void WritePendingMetadata(GitHubReleaseInfo release, string zipFileName, long downloadedSizeBytes)
        {
            var metadata = new JObject
            {
                ["version"] = NormalizeVersionText(release.Version),
                ["assetName"] = release.AssetName ?? "",
                ["assetDownloadUrl"] = release.AssetDownloadUrl ?? "",
                ["assetSizeBytes"] = release.AssetSizeBytes,
                ["downloadedSizeBytes"] = downloadedSizeBytes,
                ["zipFileName"] = zipFileName ?? "",
                ["downloadedAtUtc"] = DateTime.UtcNow.ToString("O"),
            };

            File.WriteAllText(PendingMetadataPath, metadata.ToString(), new UTF8Encoding(false));
        }

        private static bool TryGetPendingPackageFromMetadata(JObject metadata, out string zipPath)
        {
            zipPath = null;
            var zipFileName = Path.GetFileName(metadata["zipFileName"]?.ToString() ?? "");
            if (string.IsNullOrWhiteSpace(zipFileName))
                return false;

            var candidate = Path.Combine(PendingUpdateRoot, zipFileName);
            if (!File.Exists(candidate))
                return false;

            var expectedSize = metadata["downloadedSizeBytes"]?.ToObject<long?>()
                               ?? metadata["assetSizeBytes"]?.ToObject<long?>()
                               ?? 0;
            try
            {
                var actualSize = new FileInfo(candidate).Length;
                if ((expectedSize > 0 && actualSize != expectedSize) || !IsValidUpdatePackage(candidate))
                {
                    TryDeleteDirectory(PendingUpdateRoot);
                    return false;
                }
            }
            catch
            {
                TryDeleteDirectory(PendingUpdateRoot);
                return false;
            }

            zipPath = candidate;
            return true;
        }

        private static void ValidateUpdatePackage(string zipPath, Version expectedVersion = null)
        {
            ValidateTrustedUpdatePackage(
                zipPath,
                zipPath + ".sig",
                expectedVersion,
                Path.GetFileName(zipPath),
                null,
                CancellationToken.None);
        }

        private static bool IsValidUpdatePackage(string zipPath, Version expectedVersion = null)
        {
            try
            {
                ValidateUpdatePackage(zipPath, expectedVersion);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static TrustedUpdatePackage ValidateTrustedUpdatePackage(
            string zipPath,
            string signaturePath,
            Version expectedVersion,
            string expectedAssetName,
            string githubDigest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                throw new FileNotFoundException("Update ZIP is missing.", zipPath);
            var zipInfo = new FileInfo(zipPath);
            if (zipInfo.Length <= 0 || zipInfo.Length > MaximumUpdateDownloadBytes)
                throw new InvalidDataException($"Update ZIP size is outside the 1..{MaximumUpdateDownloadBytes} byte limit.");
            if (!HasDetachedSignature(zipPath, signaturePath))
            {
                throw new InvalidDataException(
                    "Automatic installation requires an independent detached signature file next to the ZIP (.zip.sig). " +
                    "Unsigned local ZIP files are never installed automatically.");
            }

            var envelope = ReadSignatureEnvelope(signaturePath);
            var assetName = Path.GetFileName(expectedAssetName ?? zipPath);
            if (!string.Equals(envelope.AssetName, assetName, StringComparison.Ordinal))
                throw new InvalidDataException("Detached signature asset name does not match the update ZIP.");
            if (envelope.AssetName.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                !string.Equals(Path.GetFileName(envelope.AssetName), envelope.AssetName, StringComparison.Ordinal))
                throw new InvalidDataException("Detached signature contains an invalid asset name.");
            if (!Version.TryParse(envelope.Version, out var signedVersion))
                throw new InvalidDataException("Detached signature contains an invalid version.");
            if (expectedVersion != null && NormalizeVersion(signedVersion) != NormalizeVersion(expectedVersion))
                throw new InvalidDataException("Detached signature version does not match release metadata.");

            var currentArchitecture = Environment.Is64BitProcess ? "x64" : "x86";
            if (!string.Equals(envelope.Architecture, currentArchitecture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Detached signature architecture {envelope.Architecture} does not match {currentArchitecture}.");
            if (!Regex.IsMatch(envelope.Sha256 ?? string.Empty, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Detached signature contains an invalid SHA-256 digest.");

            var actualHash = ComputeSha256(zipPath, cancellationToken);
            if (!FixedTimeHexEquals(actualHash, envelope.Sha256))
                throw new InvalidDataException("Update ZIP SHA-256 does not match the detached signature envelope.");
            if (!string.IsNullOrWhiteSpace(githubDigest))
            {
                var githubHash = githubDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? githubDigest.Substring("sha256:".Length)
                    : null;
                if (githubHash != null && !FixedTimeHexEquals(actualHash, githubHash))
                    throw new InvalidDataException("Update ZIP SHA-256 does not match GitHub release metadata.");
            }

            var canonicalPayload = BuildSignaturePayload(
                envelope.AssetName,
                signedVersion,
                envelope.Architecture,
                actualHash);
            byte[] signature;
            try { signature = Convert.FromBase64String(envelope.SignatureBase64 ?? string.Empty); }
            catch (FormatException ex) { throw new InvalidDataException("Detached signature is not valid base64.", ex); }
            if (signature.Length < 128 || signature.Length > 1024)
                throw new InvalidDataException("Detached signature length is invalid.");

            var publicKeyXml = LoadTrustedPublicKeyXml();
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(publicKeyXml);
                if (!rsa.VerifyData(
                    Encoding.UTF8.GetBytes(canonicalPayload),
                    CryptoConfig.MapNameToOID("SHA256"),
                    signature))
                {
                    throw new CryptographicException("Detached update signature verification failed.");
                }
            }

            var actualVersion = InspectUpdatePackage(zipPath, signedVersion);
            return new TrustedUpdatePackage
            {
                Version = actualVersion,
                Architecture = currentArchitecture,
                Sha256 = actualHash.ToUpperInvariant(),
                SignatureBase64 = envelope.SignatureBase64,
                CanonicalPayload = canonicalPayload,
                PublicKeyXml = publicKeyXml,
                SignaturePath = signaturePath
            };
        }

        internal static bool HasDetachedSignature(string zipPath, string signaturePath)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath) ||
                string.IsNullOrWhiteSpace(signaturePath) || !File.Exists(signaturePath))
            {
                return false;
            }
            try
            {
                var signatureLength = new FileInfo(signaturePath).Length;
                return signatureLength > 0 && signatureLength <= MaximumSignatureBytes;
            }
            catch
            {
                return false;
            }
        }

        private static UpdateSignatureEnvelope ReadSignatureEnvelope(string signaturePath)
        {
            var info = new FileInfo(signaturePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumSignatureBytes)
                throw new InvalidDataException($"Detached signature must be within 1..{MaximumSignatureBytes} bytes.");

            var root = JObject.Parse(File.ReadAllText(signaturePath, Encoding.UTF8));
            var schema = root["schema"]?.ToString();
            if (!string.Equals(schema, SignatureSchema, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported detached signature schema.");
            return new UpdateSignatureEnvelope
            {
                AssetName = root["asset"]?.ToString(),
                Version = root["version"]?.ToString(),
                Architecture = root["architecture"]?.ToString(),
                Sha256 = root["sha256"]?.ToString(),
                SignatureBase64 = root["signature"]?.ToString()
            };
        }

        private static string LoadTrustedPublicKeyXml()
        {
            if (!File.Exists(TrustedPublicKeyPath))
                throw new FileNotFoundException("Trusted update-signing public key is not deployed.", TrustedPublicKeyPath);
            var info = new FileInfo(TrustedPublicKeyPath);
            if (info.Length <= 0 || info.Length > MaximumSignatureBytes)
                throw new InvalidDataException("Trusted update-signing public key has an invalid size.");
            var xml = File.ReadAllText(TrustedPublicKeyPath, Encoding.UTF8).Trim();
            if (!xml.StartsWith("<RSAKeyValue>", StringComparison.Ordinal) ||
                !xml.EndsWith("</RSAKeyValue>", StringComparison.Ordinal))
                throw new InvalidDataException("Trusted update-signing public key must be RSA XML public-key material.");

            // Parse once here so trust availability never reports success for malformed material.
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(xml);
                if (rsa.KeySize < 2048)
                    throw new CryptographicException("Update-signing RSA key must be at least 2048 bits.");
                var parameters = rsa.ExportParameters(false);
                if (parameters.Modulus == null || parameters.Exponent == null)
                    throw new CryptographicException("Update-signing public key is incomplete.");
            }
            return xml;
        }

        private static string BuildSignaturePayload(
            string assetName,
            Version version,
            string architecture,
            string sha256)
        {
            return SignatureSchema + "\n" +
                   "asset=" + assetName + "\n" +
                   "version=" + NormalizeVersionText(version) + "\n" +
                   "architecture=" + architecture.ToLowerInvariant() + "\n" +
                   "sha256=" + sha256.ToUpperInvariant() + "\n";
        }

        private static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            using (var sha256 = SHA256.Create())
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: false))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha256.Hash).Replace("-", string.Empty);
            }
        }

        private static bool FixedTimeHexEquals(string first, string second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            var difference = 0;
            for (var i = 0; i < first.Length; i++)
                difference |= char.ToUpperInvariant(first[i]) ^ char.ToUpperInvariant(second[i]);
            return difference == 0;
        }

        private static Version InspectUpdatePackage(string zipPath, Version expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath) || new FileInfo(zipPath).Length <= 0)
                throw new FileNotFoundException("本地更新包不存在或为空。", zipPath);

            var validationRoot = Path.Combine(UpdateRoot, "validate-" + Guid.NewGuid().ToString("N"));
            var validationRootPrefix = Path.GetFullPath(validationRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(validationRoot);
            try
            {
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    if (archive.Entries.Count == 0)
                        throw new InvalidDataException("本地更新 ZIP 是空包。");
                    if (archive.Entries.Count > MaximumUpdateEntryCount)
                        throw new InvalidDataException("本地更新 ZIP 文件数量异常。");

                    long expandedBytes = 0;
                    var executableEntries = new System.Collections.Generic.List<ZipArchiveEntry>();
                    foreach (var entry in archive.Entries)
                    {
                        expandedBytes = checked(expandedBytes + entry.Length);
                        if (expandedBytes > MaximumExpandedUpdateBytes)
                            throw new InvalidDataException("本地更新 ZIP 解压后超过 2 GB，已拒绝安装。");

                        var relativePath = (entry.FullName ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
                        if (Path.IsPathRooted(relativePath))
                            throw new InvalidDataException($"本地更新 ZIP 包含非法路径：{entry.FullName}");
                        var destinationPath = Path.GetFullPath(Path.Combine(validationRoot, relativePath));
                        if (!destinationPath.StartsWith(validationRootPrefix, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(destinationPath.TrimEnd(Path.DirectorySeparatorChar), validationRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"本地更新 ZIP 包含非法路径：{entry.FullName}");

                        if (string.Equals(Path.GetFileName(entry.FullName), Global.ExpectedExeFileName, StringComparison.OrdinalIgnoreCase))
                            executableEntries.Add(entry);
                    }

                    if (executableEntries.Count != 1)
                        throw new InvalidDataException($"本地更新 ZIP 必须包含且只能包含一个 {Global.ExpectedExeFileName}。");

                    var executablePath = Path.Combine(validationRoot, Global.ExpectedExeFileName);
                    using (var input = executableEntries[0].Open())
                    using (var output = File.Create(executablePath))
                        input.CopyTo(output);

                    var actualArchitecture = GetPortableExecutableArchitecture(executablePath);
                    var expectedArchitecture = Environment.Is64BitProcess ? "x64" : "x86";
                    if (!string.Equals(actualArchitecture, expectedArchitecture, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"更新包内主程序架构 {actualArchitecture} 与当前进程 {expectedArchitecture} 不一致。");

                    var fileVersionText = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
                    var actualVersion = ParseVersion(fileVersionText);
                    if (actualVersion == null)
                        throw new InvalidDataException("无法读取更新包内主程序的真实版本号。");
                    if (expectedVersion != null && NormalizeVersion(actualVersion) != NormalizeVersion(expectedVersion))
                        throw new InvalidDataException(
                            $"更新包标注版本 {NormalizeVersionText(expectedVersion)} 与内部主程序版本 {NormalizeVersionText(actualVersion)} 不一致。");
                    return actualVersion;
                }
            }
            finally
            {
                TryDeleteDirectory(validationRoot);
            }
        }

        private static string GetPortableExecutableArchitecture(string executablePath)
        {
            using (var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
                    throw new InvalidDataException("更新包内主程序不是有效的 PE 文件。");
                stream.Position = 0x3C;
                var peOffset = reader.ReadInt32();
                if (peOffset < 0 || peOffset > stream.Length - 6)
                    throw new InvalidDataException("更新包内主程序 PE 头偏移无效。");
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550)
                    throw new InvalidDataException("更新包内主程序 PE 签名无效。");
                var machine = reader.ReadUInt16();
                switch (machine)
                {
                    case 0x014c:
                        return "x86";
                    case 0x8664:
                        return "x64";
                    default:
                        throw new InvalidDataException($"不支持的更新主程序 PE 架构：0x{machine:X4}。");
                }
            }
        }

        private static string SafeFileName(string fileName)
        {
            fileName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "llcom-plus-update.zip" : fileName);
            foreach (var c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            return string.IsNullOrWhiteSpace(fileName) ? "llcom-plus-update.zip" : fileName;
        }

        private static string BuildInstallScript(
            string zipPath,
            string tempRoot,
            bool restartAfterInstall,
            TrustedUpdatePackage trustedPackage)
        {
            if (trustedPackage == null)
                throw new ArgumentNullException(nameof(trustedPackage));

            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exePath = Path.Combine(appDir, Global.ExpectedExeFileName);
            var pid = Process.GetCurrentProcess().Id;
            var extractDir = Path.Combine(tempRoot, "extract");
            var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(trustedPackage.CanonicalPayload));
            var publicKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(trustedPackage.PublicKeyXml));

            return $@"$ErrorActionPreference = 'Stop'
$zip = '{PowerShellQuote(zipPath)}'
$appDir = '{PowerShellQuote(appDir)}'
$exe = '{PowerShellQuote(exePath)}'
$tempRoot = '{PowerShellQuote(tempRoot)}'
$extract = '{PowerShellQuote(extractDir)}'
$log = Join-Path $tempRoot 'update-error.log'
$pidToWait = {pid}
$restartAfterInstall = ${restartAfterInstall.ToString().ToLowerInvariant()}
$expectedHash = '{PowerShellQuote(trustedPackage.Sha256)}'
$signatureBase64 = '{PowerShellQuote(trustedPackage.SignatureBase64)}'
$payloadBase64 = '{PowerShellQuote(payloadBase64)}'
$publicKeyBase64 = '{PowerShellQuote(publicKeyBase64)}'
$expectedVersion = '{PowerShellQuote(NormalizeVersionText(trustedPackage.Version))}'
$expectedArchitecture = '{PowerShellQuote(trustedPackage.Architecture)}'
function Write-UpdateLog([string]$message) {{
    try {{
        $line = ""$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') $message""
        Add-Content -LiteralPath $log -Value $line -Encoding UTF8
    }} catch {{ }}
}}
function Show-UpdateFailure([string]$message) {{
    try {{ $message | Out-File -LiteralPath $log -Encoding UTF8 -Append }} catch {{ }}
    try {{
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show($message, 'llcom plus 自动更新失败', 'OK', 'Error') | Out-Null
    }} catch {{
        try {{ Start-Process -FilePath 'notepad.exe' -ArgumentList $log }} catch {{ }}
    }}
}}
function Wait-ForAppExit([int]$processId, [int]$graceSeconds) {{
    if ($processId -le 0) {{ return }}
    $deadline = (Get-Date).AddSeconds($graceSeconds)
    while ($true) {{
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) {{
            Write-UpdateLog ""待更新进程已退出：$processId""
            return
        }}
        if ($process.ProcessName -notlike 'llcom*') {{
            Write-UpdateLog ""PID 已被其他进程占用，跳过等待：$processId $($process.ProcessName)""
            return
        }}

        if ((Get-Date) -ge $deadline) {{
            Write-UpdateLog ""待更新进程未按时退出，强制结束：$processId $($process.ProcessName)""
            Stop-Process -Id $processId -Force -ErrorAction Stop
            Start-Sleep -Seconds 2
            return
        }}

        Start-Sleep -Milliseconds 500
    }}
}}
function Assert-PackageTrust {{
    if (!(Test-Path -LiteralPath $zip -PathType Leaf)) {{ throw ""更新包不存在：$zip"" }}
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedHash.ToUpperInvariant()) {{
        throw ""更新包 SHA-256 在安装前发生变化。Expected=$expectedHash Actual=$actualHash""
    }}
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    try {{
        $rsa.PersistKeyInCsp = $false
        $publicKeyXml = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($publicKeyBase64))
        $rsa.FromXmlString($publicKeyXml)
        $payload = [Convert]::FromBase64String($payloadBase64)
        $signature = [Convert]::FromBase64String($signatureBase64)
        $sha256Oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
        if (!$rsa.VerifyData($payload, $sha256Oid, $signature)) {{
            throw ""更新包独立签名复验失败。""
        }}
    }} finally {{
        $rsa.Dispose()
    }}
}}
function Get-PeArchitecture([string]$path) {{
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {{
        if ($reader.ReadUInt16() -ne 0x5A4D) {{ throw ""更新主程序不是有效 PE。"" }}
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {{ throw ""PE 头偏移无效。"" }}
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {{ throw ""PE 签名无效。"" }}
        $machine = $reader.ReadUInt16()
        if ($machine -eq 0x014c) {{ return 'x86' }}
        if ($machine -eq 0x8664) {{ return 'x64' }}
        throw ""不支持的 PE 架构：$machine""
    }} finally {{
        $reader.Dispose()
        $stream.Dispose()
    }}
}}
function Assert-ExtractedIdentity([string]$programPath) {{
    if (!(Test-Path -LiteralPath $programPath -PathType Leaf)) {{ throw ""更新包内容不正确，未找到 llcom plus.exe"" }}
    $actualVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($programPath).FileVersion
    if ($actualVersion -ne [Version]$expectedVersion) {{
        throw ""更新主程序版本不匹配。Expected=$expectedVersion Actual=$actualVersion""
    }}
    $actualArchitecture = Get-PeArchitecture $programPath
    if ($actualArchitecture -ne $expectedArchitecture) {{
        throw ""更新主程序架构不匹配。Expected=$expectedArchitecture Actual=$actualArchitecture""
    }}
}}
function Copy-WithRetry([string]$sourcePath, [string]$destinationPath, [int]$attempts) {{
    for ($i = 1; $i -le $attempts; $i++) {{
        try {{
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Recurse -Force -ErrorAction Stop
            return
        }} catch {{
            Write-UpdateLog ""复制失败，第 $i 次：$sourcePath -> $destinationPath，$($_.Exception.Message)""
            if ($i -ge $attempts) {{ throw }}
            Start-Sleep -Seconds 1
        }}
    }}
}}
$success = $false
try {{
    Write-UpdateLog ""安装脚本启动。PID=$pidToWait, Zip=$zip, AppDir=$appDir""
    Assert-PackageTrust
    Wait-ForAppExit -processId $pidToWait -graceSeconds 8
    Assert-PackageTrust
    if (Test-Path -LiteralPath $extract) {{ Remove-Item -LiteralPath $extract -Recurse -Force }}
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Write-UpdateLog ""开始解压：$zip""
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $source = $extract
    $children = @(Get-ChildItem -LiteralPath $extract -Force)
    if ($children.Count -eq 1 -and $children[0].PSIsContainer -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'llcom plus.exe'))) {{
        $source = $children[0].FullName
    }}
    $programPath = Join-Path $source 'llcom plus.exe'
    Assert-PackageTrust
    Assert-ExtractedIdentity $programPath
    Write-UpdateLog ""开始覆盖安装：$source -> $appDir""
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {{
        Copy-WithRetry -sourcePath $_.FullName -destinationPath $appDir -attempts 60
    }}
    $success = $true
    if ($restartAfterInstall) {{
        Write-UpdateLog ""安装完成，启动程序：$exe""
        Start-Process -FilePath $exe -WorkingDirectory $appDir
    }} else {{
        Write-UpdateLog ""安装完成，用户关闭程序，不自动启动。""
    }}
}} catch {{
    $message = ""自动更新失败。`r`n临时目录：$tempRoot`r`n下载包：$zip`r`n解压目录：$extract`r`n安装目录：$appDir`r`n错误信息：$($_.Exception.Message)`r`n`r`n可以手动关闭 llcom plus 后，将解压目录中的文件复制到安装目录覆盖。""
    Write-UpdateLog $message
    Show-UpdateFailure $message
    if ($restartAfterInstall -and (Test-Path -LiteralPath $exe)) {{
        try {{ Start-Process -FilePath $exe -WorkingDirectory $appDir }} catch {{ }}
    }}
}} finally {{
    if ($success) {{
        try {{ Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue }} catch {{ }}
        try {{ Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue }} catch {{ }}
        try {{ Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }} catch {{ }}
    }}
}}
";
        }

        private static string PowerShellQuote(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        private static void StartInstaller(string scriptPath)
        {
            var tempRoot = Path.GetDirectoryName(scriptPath);
            var launchLogPath = Path.Combine(tempRoot, "installer-launch.log");
            var stdoutLogPath = Path.Combine(tempRoot, "installer-powershell.log");
            var launcherPath = Path.Combine(tempRoot, "install-update.cmd");
            var powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershellPath))
                powershellPath = "powershell.exe";
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            if (!File.Exists(cmdPath))
                cmdPath = "cmd.exe";

            File.WriteAllText(
                launchLogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine +
                "launcher=" + launcherPath + Environment.NewLine +
                "powershell=" + powershellPath + Environment.NewLine +
                "script=" + scriptPath + Environment.NewLine,
                new UTF8Encoding(false));
            File.WriteAllText(
                launcherPath,
                BuildCommandLauncher(powershellPath, scriptPath, launchLogPath, stdoutLogPath),
                Encoding.Default);

            var startInfo = new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = "/d /c \"\"" + launcherPath + "\"\"",
                WorkingDirectory = tempRoot,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start update installer.");
            File.AppendAllText(
                launchLogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine +
                "cmd pid=" + (process?.Id.ToString() ?? "unknown") + Environment.NewLine,
                new UTF8Encoding(false));
        }

        private static string BuildCommandLauncher(string powershellPath, string scriptPath, string launchLogPath, string stdoutLogPath)
        {
            return "@echo off\r\n" +
                   "setlocal\r\n" +
                   ">> \"" + launchLogPath + "\" echo %date% %time% cmd launcher started\r\n" +
                   "\"" + powershellPath + "\" -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\" >> \"" + stdoutLogPath + "\" 2>&1\r\n" +
                   "set EXITCODE=%ERRORLEVEL%\r\n" +
                   ">> \"" + launchLogPath + "\" echo %date% %time% powershell exited %EXITCODE%\r\n" +
                   "exit /b %EXITCODE%\r\n";
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

    internal sealed class GitHubLocalUpdatePackage
    {
        public string Path { get; set; }
        public Version Version { get; set; }
        public string DisplayVersion { get; set; }
    }

    internal sealed class GitHubReleaseInfo
    {
        public Version Version { get; set; }
        public Version CurrentVersion { get; set; }
        public string TagName { get; set; }
        public string ReleaseUrl { get; set; }
        public string AssetName { get; set; }
        public string AssetDownloadUrl { get; set; }
        public long AssetSizeBytes { get; set; }
        public string AssetDigest { get; set; }
        public string SignatureAssetName { get; set; }
        public string SignatureDownloadUrl { get; set; }
        public string AutomaticUpdateTrustError { get; set; }
        public bool CanAutoInstall => string.IsNullOrWhiteSpace(AutomaticUpdateTrustError);
        public bool HasUpdate => Version != null && CurrentVersion != null && Version > CurrentVersion;
    }

    internal sealed class GitHubReleaseAsset
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public long SizeBytes { get; set; }
        public string Digest { get; set; }
    }

    internal sealed class UpdateSignatureEnvelope
    {
        public string AssetName { get; set; }
        public string Version { get; set; }
        public string Architecture { get; set; }
        public string Sha256 { get; set; }
        public string SignatureBase64 { get; set; }
    }

    internal sealed class TrustedUpdatePackage
    {
        public Version Version { get; set; }
        public string Architecture { get; set; }
        public string Sha256 { get; set; }
        public string SignatureBase64 { get; set; }
        public string CanonicalPayload { get; set; }
        public string PublicKeyXml { get; set; }
        public string SignaturePath { get; set; }
    }

    internal sealed class GitHubDownloadProgress
    {
        public GitHubDownloadProgress(long bytesReceived, long totalBytes)
        {
            BytesReceived = bytesReceived;
            TotalBytes = totalBytes;
        }

        public long BytesReceived { get; }
        public long TotalBytes { get; }
        public double Percent => TotalBytes > 0 ? Math.Min(100, BytesReceived * 100d / TotalBytes) : 0;
    }
}
