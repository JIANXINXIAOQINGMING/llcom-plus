using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace llcom_plus.Tools
{
    internal static class StartupProfiler
    {
        private const long MaxLogBytes = 2 * 1024 * 1024;
        private static readonly object SyncRoot = new object();
        private static readonly Stopwatch TotalWatch = new Stopwatch();
        private static long lastElapsedMilliseconds;
        private static string logPath;

        public static string LogPath
        {
            get
            {
                lock (SyncRoot)
                    return EnsureLogPath();
            }
        }

        public static void Begin()
        {
            lock (SyncRoot)
            {
                if (TotalWatch.IsRunning)
                    return;

                TotalWatch.Restart();
                lastElapsedMilliseconds = 0;
                var path = EnsureLogPath();

                try
                {
                    var file = new FileInfo(path);
                    if (file.Exists && file.Length > MaxLogBytes)
                        file.Delete();
                }
                catch
                {
                    // Startup profiling must never block normal startup.
                }

                WriteLineUnsafe("");
                WriteLineUnsafe("============================================================");
                WriteLineUnsafe($"Startup begin: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteLineUnsafe($"Log path: {path}");
                WriteLineUnsafe($"Process start: {GetProcessStartTime()}");
                WriteLineUnsafe($"Profiler begin delay: {GetDelayFromProcessStart()}ms");
                WriteLineUnsafe($"Command line: {Environment.CommandLine}");
            }
        }

        public static void Mark(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return;

            lock (SyncRoot)
                WriteLineUnsafe(FormatLine(label));
        }

        public static void Measure(string label, Action action)
        {
            Mark(label + " begin");
            var watch = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                watch.Stop();
                Mark($"{label} end, duration={watch.ElapsedMilliseconds}ms");
            }
        }

        public static T Measure<T>(string label, Func<T> action)
        {
            Mark(label + " begin");
            var watch = Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                watch.Stop();
                Mark($"{label} end, duration={watch.ElapsedMilliseconds}ms");
            }
        }

        private static string FormatLine(string label)
        {
            var elapsed = TotalWatch.IsRunning ? TotalWatch.ElapsedMilliseconds : 0;
            var delta = elapsed - lastElapsedMilliseconds;
            lastElapsedMilliseconds = elapsed;
            return string.Format(
                CultureInfo.InvariantCulture,
                "+{0,8}ms (+{1,6}ms) [T{2}] {3}",
                elapsed,
                delta,
                Thread.CurrentThread.ManagedThreadId,
                label);
        }

        private static string EnsureLogPath()
        {
            if (!string.IsNullOrEmpty(logPath))
                return logPath;

            logPath = Path.Combine(GetBaseDirectory(), "startup.log");
            return logPath;
        }

        private static string GetBaseDirectory()
        {
            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                    return baseDirectory;
            }
            catch
            {
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "llcom plus");
        }

        private static string GetProcessStartTime()
        {
            try
            {
                return GetCurrentProcessStartTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }

        private static long GetDelayFromProcessStart()
        {
            try
            {
                return (long)(DateTime.Now - GetCurrentProcessStartTime()).TotalMilliseconds;
            }
            catch
            {
                return -1;
            }
        }

        private static DateTime GetCurrentProcessStartTime()
        {
            return Process.GetCurrentProcess().StartTime;
        }

        private static void WriteLineUnsafe(string line)
        {
            try
            {
                var path = EnsureLogPath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                try
                {
                    var fallback = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "llcom plus",
                        "startup.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(fallback));
                    logPath = fallback;
                    File.AppendAllText(fallback, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                }
            }
        }
    }
}
