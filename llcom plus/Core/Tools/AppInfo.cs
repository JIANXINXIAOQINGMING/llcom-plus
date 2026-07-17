using System;
using System.Reflection;

namespace llcom_plus.Tools
{
    internal static class AppInfo
    {
        internal static string DisplayVersion { get; } = GetDisplayVersion();

        private static string GetDisplayVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var metadataIndex = informational.IndexOf('+');
                return (metadataIndex >= 0 ? informational.Substring(0, metadataIndex) : informational).Trim();
            }

            var version = assembly.GetName().Version;
            if (version == null)
                return "1.2.2";

            return version.Revision <= 0 ? version.ToString(3) : version.ToString();
        }
    }
}
