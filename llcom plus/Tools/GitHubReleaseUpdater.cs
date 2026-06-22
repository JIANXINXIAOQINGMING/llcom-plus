using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
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

        public static async Task<GitHubReleaseInfo> CheckLatestAsync()
        {
            try
            {
                return await CheckLatestFromApiAsync().ConfigureAwait(false);
            }
            catch
            {
                return await CheckLatestFromRedirectAsync().ConfigureAwait(false);
            }
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

                    var arch = Environment.Is64BitProcess ? "x64" : "x86";
                    var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var asset = SelectReleaseAssetFromHtml(html, arch);
                    var assetName = asset?.Name ?? $"llcom.plus_{NormalizeVersionText(version)}_{arch}.zip";
                    return new GitHubReleaseInfo
                    {
                        Version = version,
                        CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0),
                        TagName = tag,
                        ReleaseUrl = releaseUri,
                        AssetName = assetName,
                        AssetDownloadUrl = asset?.DownloadUrl ?? ("https://github.com/" + Repository + "/releases/download/" +
                                           Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(assetName)),
                    };
                }
            }
        }

        public static async Task<string> DownloadAndPrepareInstallAsync(GitHubReleaseInfo release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                throw new InvalidOperationException("Release asset is missing.");

            var tempRoot = Path.Combine(Path.GetTempPath(), "llcom_plus_update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, string.IsNullOrWhiteSpace(release.AssetName) ? "llcom-plus-update.zip" : release.AssetName);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(5);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("llcom-plus-updater");
                    using (var response = await client.GetAsync(release.AssetDownloadUrl).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = File.Create(zipPath))
                        {
                            await input.CopyToAsync(output).ConfigureAwait(false);
                        }
                    }
                }

                var scriptPath = Path.Combine(tempRoot, "install-update.ps1");
                File.WriteAllText(scriptPath, BuildInstallScript(zipPath), new UTF8Encoding(false));
                StartInstaller(scriptPath);
                return zipPath;
            }
            catch
            {
                TryDeleteDirectory(tempRoot);
                throw;
            }
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
                    DownloadUrl = asset["browser_download_url"]?.ToString()
                })
                .Where(asset => !string.IsNullOrWhiteSpace(asset.DownloadUrl) &&
                                (asset.Name ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return zipAssets.FirstOrDefault(asset => (asset.Name ?? "").IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? zipAssets.FirstOrDefault();
        }

        private static GitHubReleaseAsset SelectReleaseAssetFromHtml(string html, string arch)
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
                        DownloadUrl = cleanUrl
                    };
                })
                .GroupBy(asset => asset.DownloadUrl, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            return assets.FirstOrDefault(asset => (asset.Name ?? "").IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? assets.FirstOrDefault();
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

        private static string BuildInstallScript(string zipPath)
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exePath = Path.Combine(appDir, Global.ExpectedExeFileName);
            var pid = Process.GetCurrentProcess().Id;
            var tempRoot = Path.GetDirectoryName(zipPath);
            var extractDir = Path.Combine(tempRoot, "extract");

            return $@"$ErrorActionPreference = 'Stop'
$zip = '{PowerShellQuote(zipPath)}'
$appDir = '{PowerShellQuote(appDir)}'
$exe = '{PowerShellQuote(exePath)}'
$tempRoot = '{PowerShellQuote(tempRoot)}'
$extract = '{PowerShellQuote(extractDir)}'
$log = Join-Path $tempRoot 'update-error.log'
$pidToWait = {pid}
function Show-UpdateFailure([string]$message) {{
    try {{ $message | Out-File -LiteralPath $log -Encoding UTF8 -Force }} catch {{ }}
    try {{
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show($message, 'llcom plus 自动更新失败', 'OK', 'Error') | Out-Null
    }} catch {{
        try {{ Start-Process -FilePath 'notepad.exe' -ArgumentList $log }} catch {{ }}
    }}
}}
$success = $false
try {{
    Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
    if (!(Test-Path -LiteralPath $zip)) {{ throw ""更新包不存在：$zip"" }}
    if (Test-Path -LiteralPath $extract) {{ Remove-Item -LiteralPath $extract -Recurse -Force }}
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $source = $extract
    $children = @(Get-ChildItem -LiteralPath $extract -Force)
    if ($children.Count -eq 1 -and $children[0].PSIsContainer -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'llcom plus.exe'))) {{
        $source = $children[0].FullName
    }}
    if (!(Test-Path -LiteralPath (Join-Path $source 'llcom plus.exe'))) {{
        throw ""更新包内容不正确，未找到 llcom plus.exe""
    }}
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {{
        Copy-Item -LiteralPath $_.FullName -Destination $appDir -Recurse -Force
    }}
    $success = $true
    Start-Process -FilePath $exe -WorkingDirectory $appDir
}} catch {{
    $message = ""自动更新失败。`r`n临时目录：$tempRoot`r`n下载包：$zip`r`n解压目录：$extract`r`n安装目录：$appDir`r`n错误信息：$($_.Exception.Message)`r`n`r`n可以手动关闭 llcom plus 后，将解压目录中的文件复制到安装目录覆盖。""
    Show-UpdateFailure $message
    if (Test-Path -LiteralPath $exe) {{
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
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(startInfo);
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
    }

    internal sealed class GitHubReleaseInfo
    {
        public Version Version { get; set; }
        public Version CurrentVersion { get; set; }
        public string TagName { get; set; }
        public string ReleaseUrl { get; set; }
        public string AssetName { get; set; }
        public string AssetDownloadUrl { get; set; }
        public bool HasUpdate => Version != null && CurrentVersion != null && Version > CurrentVersion;
    }

    internal sealed class GitHubReleaseAsset
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
    }
}
