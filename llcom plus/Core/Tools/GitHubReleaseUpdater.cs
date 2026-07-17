using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
        private const int MaximumUpdateEntryCount = 10000;
        private const long MaximumExpandedUpdateBytes = 2L * 1024 * 1024 * 1024;
        private static bool installScheduled;

        public static string LocalPackageDirectory => AppDomain.CurrentDomain.BaseDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static async Task<GitHubReleaseInfo> CheckLatestAsync()
        {
            GitHubReleaseInfo info;
            try
            {
                info = await CheckLatestFromApiAsync().ConfigureAwait(false);
            }
            catch
            {
                info = await CheckLatestFromRedirectAsync().ConfigureAwait(false);
            }

            if (info != null && info.AssetSizeBytes <= 0 && !string.IsNullOrWhiteSpace(info.AssetDownloadUrl))
                info.AssetSizeBytes = await TryGetAssetSizeAsync(info.AssetDownloadUrl).ConfigureAwait(false);

            return info;
        }

        private static async Task<GitHubReleaseInfo> CheckLatestFromApiAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var json = await client.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
                var root = JObject.Parse(json);
                var tag = root["tag_name"]?.ToString() ?? "";
                var name = root["name"]?.ToString() ?? tag;
                var version = ParseVersion(tag) ?? ParseVersion(name);
                if (version == null)
                    throw new InvalidOperationException("Cannot parse release version from GitHub latest release.");

                var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
                var asset = SelectReleaseAsset(root["assets"] as JArray);
                return new GitHubReleaseInfo
                {
                    Version = version,
                    CurrentVersion = currentVersion,
                    TagName = tag,
                    ReleaseUrl = root["html_url"]?.ToString() ?? "https://github.com/" + Repository + "/releases",
                    AssetName = asset?.Name,
                    AssetDownloadUrl = asset?.DownloadUrl,
                    AssetSizeBytes = asset?.SizeBytes ?? 0,
                };
            }
        }

        private static async Task<GitHubReleaseInfo> CheckLatestFromRedirectAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");

                using (var response = await client.GetAsync(LatestReleasePage).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var releaseUri = response.RequestMessage?.RequestUri?.ToString() ?? LatestReleasePage;
                    var tag = GetTagFromReleaseUrl(releaseUri);
                    var version = ParseVersion(tag);
                    if (version == null)
                        throw new InvalidOperationException("Cannot parse release version from GitHub latest release.");

                    var expandedAssetsUrl = "https://github.com/" + Repository + "/releases/expanded_assets/" +
                                            Uri.EscapeDataString(tag);
                    var expandedAssetsHtml = await client.GetStringAsync(expandedAssetsUrl).ConfigureAwait(false);
                    var asset = SelectReleaseAssetFromHtml(expandedAssetsHtml);
                    return new GitHubReleaseInfo
                    {
                        Version = version,
                        CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0),
                        TagName = tag,
                        ReleaseUrl = releaseUri,
                        AssetName = asset?.Name,
                        AssetDownloadUrl = asset?.DownloadUrl,
                        AssetSizeBytes = asset?.SizeBytes ?? 0,
                    };
                }
            }
        }

        public static async Task<string> DownloadAndPrepareInstallAsync(GitHubReleaseInfo release, IProgress<GitHubDownloadProgress> progress = null)
        {
            var zipPath = await DownloadUpdateAsync(release, progress).ConfigureAwait(false);
            StartInstallAfterExit(zipPath);
            return zipPath;
        }

        public static async Task<string> DownloadUpdateAsync(GitHubReleaseInfo release, IProgress<GitHubDownloadProgress> progress = null)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                throw new InvalidOperationException("Release asset is missing.");

            if (TryGetCachedUpdatePackage(release, out var cachedZipPath))
            {
                progress?.Report(new GitHubDownloadProgress(new FileInfo(cachedZipPath).Length, new FileInfo(cachedZipPath).Length));
                return cachedZipPath;
            }

            var packageDirectory = LocalPackageDirectory;
            var zipFileName = SafeFileName(string.IsNullOrWhiteSpace(release.AssetName) ? "llcom-plus-update.zip" : release.AssetName);
            var zipPath = Path.Combine(packageDirectory, zipFileName);
            var downloadPath = zipPath + ".download";

            var packageInfo = TryParseLocalPackage(zipPath);
            if (packageInfo == null || NormalizeVersion(packageInfo.Version) != NormalizeVersion(release.Version))
                throw new InvalidDataException("在线更新包文件名必须包含与发布版本一致的版本号和当前架构，例如：llcom plus_1.2.2_x64.zip。");

            TryDeleteFile(downloadPath);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            try
            {
                long downloadedBytes;
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");
                    using (var response = await client.GetAsync(release.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? release.AssetSizeBytes;
                        progress?.Report(new GitHubDownloadProgress(0, totalBytes));
                        downloadedBytes = 0;
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = File.Create(downloadPath))
                        {
                            var buffer = new byte[81920];
                            int bytesRead;
                            while ((bytesRead = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                            {
                                await output.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                                downloadedBytes += bytesRead;
                                progress?.Report(new GitHubDownloadProgress(downloadedBytes, totalBytes));
                            }
                        }

                        if (totalBytes > 0 && downloadedBytes != totalBytes)
                            throw new InvalidDataException($"Update package download is incomplete. Expected {totalBytes} bytes, received {downloadedBytes} bytes.");
                    }
                }

                ValidateUpdatePackage(downloadPath, release.Version);

                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                File.Move(downloadPath, zipPath);
                return zipPath;
            }
            catch
            {
                TryDeleteFile(downloadPath);
                throw;
            }
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
                throw new InvalidDataException("本地更新包命名不正确，应类似 llcom plus_1.2.2_x64.zip。");
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (NormalizeVersion(package.Version) <= NormalizeVersion(currentVersion))
                throw new InvalidOperationException($"本地更新包版本 {package.DisplayVersion} 不高于当前版本。");
            ValidateUpdatePackage(zipPath, package.Version);

            var tempRoot = Path.Combine(UpdateRoot, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var scriptPath = Path.Combine(tempRoot, "install-update.ps1");
            File.WriteAllText(scriptPath, BuildInstallScript(zipPath, tempRoot, restartAfterInstall), Encoding.Unicode);
            StartInstaller(scriptPath);
            installScheduled = true;
        }

        private static GitHubReleaseAsset SelectReleaseAsset(JArray assets)
        {
            if (assets == null || assets.Count == 0)
                return null;

            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            var zipAssets = assets
                .OfType<JObject>()
                .Select(asset => new GitHubReleaseAsset
                {
                    Name = asset["name"]?.ToString(),
                    DownloadUrl = asset["browser_download_url"]?.ToString(),
                    SizeBytes = asset["size"]?.ToObject<long?>() ?? 0,
                })
                .Where(asset => !string.IsNullOrWhiteSpace(asset.DownloadUrl) &&
                                (asset.Name ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return zipAssets.FirstOrDefault(asset => (asset.Name ?? "").IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static GitHubReleaseAsset SelectReleaseAssetFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            var assets = Regex.Matches(html, "href=\"(?<url>[^\"]+/releases/download/[^\"]+?\\.zip(?:\\?[^\"]*)?)\"", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Select(url =>
                {
                    var cleanUrl = url.Split('?')[0];
                    if (cleanUrl.StartsWith("/", StringComparison.Ordinal))
                        cleanUrl = "https://github.com" + cleanUrl;

                    var name = Uri.UnescapeDataString(cleanUrl.Substring(cleanUrl.LastIndexOf('/') + 1));
                    return new GitHubReleaseAsset
                    {
                        Name = name,
                        DownloadUrl = cleanUrl,
                    };
                })
                .GroupBy(asset => asset.DownloadUrl, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var arch = Environment.Is64BitProcess ? "x64" : "x86";
            return assets.FirstOrDefault(asset => (asset.Name ?? "").IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static GitHubLocalUpdatePackage FindLatestLocalUpdatePackage()
        {
            var currentVersion = NormalizeVersion(
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
            return FindLocalUpdatePackages()
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

        private static string GetTagFromReleaseUrl(string releaseUrl)
        {
            if (string.IsNullOrWhiteSpace(releaseUrl))
                return "";

            var match = Regex.Match(releaseUrl, @"/releases/tag/(?<tag>[^/?#]+)", RegexOptions.IgnoreCase);
            return match.Success ? Uri.UnescapeDataString(match.Groups["tag"].Value) : "";
        }

        private static string NormalizeVersionText(Version version)
        {
            if (version == null)
                return "";

            var build = version.Build >= 0 ? version.Build : 0;
            var revision = version.Revision >= 0 ? version.Revision : 0;
            return $"{version.Major}.{version.Minor}.{build}.{revision}";
        }

        private static async Task<long> TryGetAssetSizeAsync(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var client = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                {
                    client.Timeout = TimeSpan.FromSeconds(20);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength ?? 0 : 0;
                    }
                }
            }
            catch
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
            InspectUpdatePackage(zipPath, expectedVersion);
        }

        private static bool IsValidUpdatePackage(string zipPath, Version expectedVersion = null)
        {
            try
            {
                InspectUpdatePackage(zipPath, expectedVersion);
                return true;
            }
            catch
            {
                return false;
            }
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

        private static string SafeFileName(string fileName)
        {
            fileName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "llcom-plus-update.zip" : fileName);
            foreach (var c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            return string.IsNullOrWhiteSpace(fileName) ? "llcom-plus-update.zip" : fileName;
        }

        private static string BuildInstallScript(string zipPath, string tempRoot, bool restartAfterInstall)
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exePath = Path.Combine(appDir, Global.ExpectedExeFileName);
            var pid = Process.GetCurrentProcess().Id;
            var extractDir = Path.Combine(tempRoot, "extract");

            return $@"$ErrorActionPreference = 'Stop'
$zip = '{PowerShellQuote(zipPath)}'
$appDir = '{PowerShellQuote(appDir)}'
$exe = '{PowerShellQuote(exePath)}'
$tempRoot = '{PowerShellQuote(tempRoot)}'
$extract = '{PowerShellQuote(extractDir)}'
$log = Join-Path $tempRoot 'update-error.log'
$pidToWait = {pid}
$restartAfterInstall = ${restartAfterInstall.ToString().ToLowerInvariant()}
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
    Wait-ForAppExit -processId $pidToWait -graceSeconds 8
    if (!(Test-Path -LiteralPath $zip)) {{ throw ""更新包不存在：$zip"" }}
    if (Test-Path -LiteralPath $extract) {{ Remove-Item -LiteralPath $extract -Recurse -Force }}
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Write-UpdateLog ""开始解压：$zip""
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $source = $extract
    $children = @(Get-ChildItem -LiteralPath $extract -Force)
    if ($children.Count -eq 1 -and $children[0].PSIsContainer -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'llcom plus.exe'))) {{
        $source = $children[0].FullName
    }}
    if (!(Test-Path -LiteralPath (Join-Path $source 'llcom plus.exe'))) {{
        throw ""更新包内容不正确，未找到 llcom plus.exe""
    }}
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
        public bool HasUpdate => Version != null && CurrentVersion != null && Version > CurrentVersion;
    }

    internal sealed class GitHubReleaseAsset
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public long SizeBytes { get; set; }
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
