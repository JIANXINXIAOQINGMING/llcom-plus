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
        private const string Repository = "JIANXINXIAOQINGMING/llcom-lawrence";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

        public static async Task<GitHubReleaseInfo> CheckLatestAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
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

        public static async Task<string> DownloadAndPrepareInstallAsync(GitHubReleaseInfo release)
        {
            if (release == null || string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                throw new InvalidOperationException("Release asset is missing.");

            var tempRoot = Path.Combine(Path.GetTempPath(), "llcom_plus_update", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, string.IsNullOrWhiteSpace(release.AssetName) ? "llcom-plus-update.zip" : release.AssetName);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var client = new HttpClient())
            {
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

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var match = Regex.Match(value, @"(?<version>\d+\.\d+\.\d+(?:\.\d+)?)");
            return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
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
$extract = '{PowerShellQuote(extractDir)}'
$pidToWait = {pid}
Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $extract) {{ Remove-Item -LiteralPath $extract -Recurse -Force }}
New-Item -ItemType Directory -Path $extract -Force | Out-Null
Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
$source = $extract
$children = @(Get-ChildItem -LiteralPath $extract -Force)
if ($children.Count -eq 1 -and $children[0].PSIsContainer -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'llcom plus.exe'))) {{
    $source = $children[0].FullName
}}
Get-ChildItem -LiteralPath $source -Force | ForEach-Object {{
    Copy-Item -LiteralPath $_.FullName -Destination $appDir -Recurse -Force
}}
Start-Process -FilePath $exe -WorkingDirectory $appDir
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
