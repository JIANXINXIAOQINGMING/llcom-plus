using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace llcom_plus.Tools
{
    /// <summary>
    /// A self-contained, best-effort snapshot store for quick-send data.
    /// Snapshot failures are deliberately reported to the caller instead of being allowed
    /// to break the settings save path.
    /// </summary>
    internal static class QuickSendBackupService
    {
        internal const int CurrentSchemaVersion = 1;
        internal const int MaximumSnapshotCount = 15;
        internal const long MaximumSnapshotBytes = 64L * 1024L * 1024L;
        internal const int AutoSnapshotDebounceMilliseconds = 1000;
        internal const int AutoSnapshotRetryMilliseconds = 3000;

        private const int MutexWaitMilliseconds = 150;
        private const int MaximumReasonLength = 120;
        private const string BackupDirectoryName = "quick-send-backups";
        private const string SnapshotFilePrefix = "quick-send_";
        private const string SnapshotFileExtension = ".json";

        private static readonly object timerLock = new object();
        private static readonly object autoSnapshotWriteLock = new object();
        private static Timer debounceTimer;
        private static Model.Settings pendingSettings;
        private static string pendingReason;
        private static bool autoSnapshotPending;
        private static bool shuttingDown;
        private static long pendingVersion;
        private static long nextAutoSnapshotTimestamp;

        internal static string BackupDirectory => BackupDirectoryPath;

        internal static string BackupDirectoryPath =>
            Path.Combine(Global.ProfilePath ?? string.Empty, BackupDirectoryName);

        /// <summary>
        /// Creates the snapshot directory and applies retention to snapshots that can be
        /// validated. This method never throws and never waits on another process for long.
        /// </summary>
        internal static bool Initialize(Model.Settings settings = null)
        {
            lock (timerLock)
            {
                shuttingDown = false;
                if (settings != null)
                    pendingSettings = settings;
            }

            try
            {
                Directory.CreateDirectory(BackupDirectoryPath);
                using (var lease = TryAcquireStoreLease())
                {
                    if (lease == null)
                        return false;
                    ApplyRetentionUnsafe();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Schedules a snapshot for one second after the last observed change.
        /// Repeated calls replace the pending reason and reset the debounce interval.
        /// </summary>
        internal static void ScheduleAutoSnapshot(Model.Settings settings, string reason = "change")
        {
            if (settings == null)
                return;

            lock (timerLock)
            {
                if (shuttingDown)
                    return;

                pendingSettings = settings;
                pendingReason = NormalizeReason(reason, "change");
                autoSnapshotPending = true;
                pendingVersion++;
                ArmAutoSnapshotTimerUnsafe(AutoSnapshotDebounceMilliseconds);
            }
        }

        internal static void Schedule(Model.Settings settings, string reason = "auto")
        {
            ScheduleAutoSnapshot(settings, reason);
        }

        /// <summary>
        /// Writes a snapshot synchronously. A content-identical snapshot is not written.
        /// The result records whether a file was created, deduplicated, skipped, or failed.
        /// </summary>
        internal static QuickSendBackupWriteResult CreateSnapshotNow(
            Model.Settings settings,
            string reason = "manual")
        {
            if (settings == null)
                return QuickSendBackupWriteResult.Failure("快捷发送设置为空。");

            try
            {
                return CreateSnapshotNow(CaptureState(settings), reason);
            }
            catch (Exception ex)
            {
                return QuickSendBackupWriteResult.Failure(ex.Message);
            }
        }

        internal static QuickSendBackupWriteResult CreateNow(
            Model.Settings settings,
            string reason = "manual")
        {
            return CreateSnapshotNow(settings, reason);
        }

        internal static QuickSendBackupWriteResult FlushPending()
        {
            return WritePendingSnapshot(false);
        }

        /// <summary>
        /// Writes an already captured restore DTO. This overload is useful when the caller
        /// can take an application-level lock and capture all three state components atomically.
        /// </summary>
        internal static QuickSendBackupWriteResult CreateSnapshotNow(
            QuickSendBackupState state,
            string reason = "manual")
        {
            if (state == null)
                return QuickSendBackupWriteResult.Failure("快捷发送快照内容为空。");

            try
            {
                var normalizedState = NormalizeState(state);
                var contentHash = ComputeContentHash(normalizedState);

                Directory.CreateDirectory(BackupDirectoryPath);
                using (var lease = TryAcquireStoreLease())
                {
                    if (lease == null)
                        return QuickSendBackupWriteResult.Skipped("另一个进程正在维护快捷发送快照。");

                    var existing = EnumerateValidSnapshotsUnsafe();
                    var duplicate = existing.FirstOrDefault(item => FixedTimeEquals(item.ContentSha256, contentHash));
                    if (duplicate != null)
                    {
                        if (IsPreUpgradeReason(reason) && !duplicate.IsPreUpgrade)
                        {
                            PromoteDuplicateToPreUpgradeUnsafe(duplicate, reason);
                            existing = EnumerateValidSnapshotsUnsafe();
                        }
                        ApplyRetentionUnsafe(existing);
                        return QuickSendBackupWriteResult.Deduplicated(duplicate.FilePath);
                    }

                    var createdAtUtc = DateTime.UtcNow;
                    var envelope = new QuickSendSnapshotEnvelope
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        CreatedAtUtc = createdAtUtc,
                        AppVersion = AppInfo.DisplayVersion ?? string.Empty,
                        Reason = NormalizeReason(reason, "manual"),
                        PageCount = normalizedState.QuickSendList.Count,
                        NonEmptyItemCount = CountNonEmptyItems(normalizedState),
                        ContentSha256 = contentHash,
                        Selected = normalizedState.Selected,
                        QuickSendList = ClonePages(normalizedState.QuickSendList),
                        QuickListNames = new List<string>(normalizedState.QuickListNames)
                    };

                    var serialized = SerializeEnvelope(envelope);
                    var byteCount = new UTF8Encoding(false).GetByteCount(serialized);
                    if (byteCount <= 0 || byteCount > MaximumSnapshotBytes)
                        return QuickSendBackupWriteResult.Failure("快捷发送快照超过允许的大小。");

                    var fileName = BuildSnapshotFileName(createdAtUtc, contentHash);
                    var destinationPath = Path.Combine(BackupDirectoryPath, fileName);
                    WriteAtomicUtf8(destinationPath, serialized);
                    ApplyRetentionUnsafe();
                    return QuickSendBackupWriteResult.CreatedFile(destinationPath);
                }
            }
            catch (Exception ex)
            {
                return QuickSendBackupWriteResult.Failure(ex.Message);
            }
        }

        /// <summary>
        /// Returns valid snapshots newest first. Truncated, malformed, tampered, or
        /// unsupported files are ignored without aborting the enumeration.
        /// </summary>
        internal static IReadOnlyList<QuickSendBackupInfo> EnumerateValidSnapshots()
        {
            try
            {
                return EnumerateValidSnapshotsUnsafe().AsReadOnly();
            }
            catch
            {
                return new List<QuickSendBackupInfo>().AsReadOnly();
            }
        }

        internal static List<QuickSendBackupInfo> GetSnapshots()
        {
            return new List<QuickSendBackupInfo>(EnumerateValidSnapshots());
        }

        /// <summary>
        /// Reads and validates a snapshot, returning a deep restore DTO.
        /// </summary>
        internal static bool TryReadSnapshot(
            string snapshotPath,
            out QuickSendBackupState state,
            out string error)
        {
            state = null;
            error = string.Empty;
            try
            {
                string canonicalPath;
                if (!TryResolveManagedSnapshotPath(snapshotPath, false, out canonicalPath))
                {
                    error = "快照路径不属于快捷发送备份目录。";
                    return false;
                }

                QuickSendSnapshotEnvelope envelope;
                QuickSendBackupInfo ignored;
                if (!TryReadEnvelope(canonicalPath, out envelope, out ignored, out error))
                    return false;

                state = new QuickSendBackupState
                {
                    Selected = envelope.Selected,
                    QuickSendList = ClonePages(envelope.QuickSendList),
                    QuickListNames = new List<string>(envelope.QuickListNames)
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryReadSnapshot(string snapshotPath, out QuickSendBackupState state)
        {
            string ignored;
            return TryReadSnapshot(snapshotPath, out state, out ignored);
        }

        /// <summary>
        /// Returns a validated, deep restore DTO. The caller owns applying the DTO so UI
        /// event suppression and any pre-restore snapshot can be coordinated atomically.
        /// </summary>
        internal static bool TryCreateRestoreData(
            string snapshotPath,
            out QuickSendBackupState restoreData,
            out string error)
        {
            return TryReadSnapshot(snapshotPath, out restoreData, out error);
        }

        internal static bool TryLoad(
            QuickSendBackupInfo snapshot,
            out QuickSendBackupState state,
            out string error)
        {
            state = null;
            error = string.Empty;
            if (snapshot == null)
            {
                error = "未选择快捷发送快照。";
                return false;
            }
            return TryReadSnapshot(snapshot.FilePath, out state, out error);
        }

        /// <summary>
        /// Deletes one managed snapshot. Arbitrary paths and directories are rejected.
        /// </summary>
        internal static bool DeleteSnapshot(string snapshotPath)
        {
            try
            {
                string canonicalPath;
                if (!TryResolveManagedSnapshotPath(snapshotPath, true, out canonicalPath))
                    return false;

                using (var lease = TryAcquireStoreLease())
                {
                    if (lease == null)
                        return false;
                    if (!File.Exists(canonicalPath))
                        return true;
                    File.Delete(canonicalPath);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool Delete(QuickSendBackupInfo snapshot, out string error)
        {
            error = string.Empty;
            if (snapshot == null)
            {
                error = "未选择快捷发送快照。";
                return false;
            }
            if (DeleteSnapshot(snapshot.FilePath))
                return true;
            error = "快捷发送快照删除失败，文件可能正由其他进程使用。";
            return false;
        }

        /// <summary>
        /// Supplies a validated source path and a safe filename for an export/save dialog.
        /// The snapshot is revalidated before its path is returned.
        /// </summary>
        internal static bool TryGetExportSource(
            string snapshotPath,
            out string sourcePath,
            out string suggestedFileName)
        {
            sourcePath = string.Empty;
            suggestedFileName = string.Empty;

            QuickSendBackupState ignoredState;
            string ignoredError;
            if (!TryReadSnapshot(snapshotPath, out ignoredState, out ignoredError))
                return false;

            string canonicalPath;
            if (!TryResolveManagedSnapshotPath(snapshotPath, false, out canonicalPath))
                return false;

            sourcePath = canonicalPath;
            suggestedFileName = Path.GetFileName(canonicalPath);
            return true;
        }

        internal static void Shutdown()
        {
            // An automatic writer that already started must finish before shutdown
            // returns; queued callbacks must not start writing afterwards.
            lock (autoSnapshotWriteLock)
            {
                lock (timerLock)
                {
                    shuttingDown = true;
                    autoSnapshotPending = false;
                    pendingSettings = null;
                    pendingReason = null;
                    pendingVersion++;
                    if (debounceTimer != null)
                    {
                        debounceTimer.Dispose();
                        debounceTimer = null;
                    }
                }
            }
        }

        private static void AutoSnapshotTimerCallback(object ignored)
        {
            WritePendingSnapshot(true);
        }

        private static void ArmAutoSnapshotTimerUnsafe(int delayMilliseconds)
        {
            nextAutoSnapshotTimestamp = Stopwatch.GetTimestamp() +
                (long)Math.Ceiling(delayMilliseconds * (double)Stopwatch.Frequency / 1000);
            if (debounceTimer == null)
                debounceTimer = new Timer(AutoSnapshotTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
            debounceTimer.Change(delayMilliseconds, Timeout.Infinite);
        }

        private static QuickSendBackupWriteResult WritePendingSnapshot(bool respectDebounce)
        {
            // Serialize timer/flush attempts without holding timerLock during file IO.
            // Edits may schedule newer work while a snapshot is being captured/written.
            lock (autoSnapshotWriteLock)
            {
                Model.Settings settings;
                string reason;
                long version;
                lock (timerLock)
                {
                    if (!autoSnapshotPending || shuttingDown)
                        return QuickSendBackupWriteResult.Skipped("没有等待写入的快捷发送快照。");

                    var remainingTicks = nextAutoSnapshotTimestamp - Stopwatch.GetTimestamp();
                    if (respectDebounce && remainingTicks > 0)
                    {
                        // A callback queued before a new edit/retry must respect the
                        // new deadline instead of defeating debounce or retry throttling.
                        var remainingMilliseconds = (int)Math.Min(int.MaxValue,
                            Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
                        debounceTimer.Change(Math.Max(1, remainingMilliseconds), Timeout.Infinite);
                        return QuickSendBackupWriteResult.Skipped("快捷发送快照仍在等待写入。");
                    }

                    settings = pendingSettings;
                    reason = pendingReason;
                    version = pendingVersion;
                    autoSnapshotPending = false;
                    pendingReason = null;
                    debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }

                var result = CreateSnapshotNow(settings, reason ?? "change");
                lock (timerLock)
                {
                    // A busy store or transient IO failure is not a completed backup.
                    // Never replace a newer pending edit with this older attempt.
                    if (!result.Succeeded && !shuttingDown && pendingVersion == version)
                    {
                        pendingSettings = settings;
                        pendingReason = reason;
                        autoSnapshotPending = true;
                        ArmAutoSnapshotTimerUnsafe(AutoSnapshotRetryMilliseconds);
                    }
                }
                return result;
            }
        }

        private static QuickSendBackupState CaptureState(Model.Settings settings)
        {
            settings.GetQuickSendStateSnapshot(out var sourcePages, out var sourceNames, out var selected);
            var result = new QuickSendBackupState
            {
                Selected = selected,
                QuickSendList = new List<List<QuickSendBackupItem>>(),
                QuickListNames = sourceNames == null
                    ? new List<string>()
                    : sourceNames.Select(name => name ?? string.Empty).ToList()
            };

            if (sourcePages != null)
            {
                foreach (var sourcePage in sourcePages)
                {
                    var targetPage = new List<QuickSendBackupItem>();
                    if (sourcePage != null)
                    {
                        foreach (var item in sourcePage)
                            targetPage.Add(QuickSendBackupItem.FromModel(item));
                    }
                    result.QuickSendList.Add(targetPage);
                }
            }
            return NormalizeState(result);
        }

        private static QuickSendBackupState NormalizeState(QuickSendBackupState state)
        {
            var pages = ClonePages(state.QuickSendList);
            if (pages.Count == 0)
                pages.Add(new List<QuickSendBackupItem>());

            var names = state.QuickListNames == null
                ? new List<string>()
                : state.QuickListNames.Select(name => name ?? string.Empty).ToList();
            while (names.Count < pages.Count)
                names.Add("未命名" + names.Count);
            if (names.Count > pages.Count)
                names.RemoveRange(pages.Count, names.Count - pages.Count);

            return new QuickSendBackupState
            {
                Selected = state.Selected < 0 || state.Selected >= pages.Count ? 0 : state.Selected,
                QuickSendList = pages,
                QuickListNames = names
            };
        }

        private static List<List<QuickSendBackupItem>> ClonePages(
            IEnumerable<IEnumerable<QuickSendBackupItem>> source)
        {
            var pages = new List<List<QuickSendBackupItem>>();
            if (source == null)
                return pages;

            foreach (var page in source)
            {
                var clonedPage = new List<QuickSendBackupItem>();
                if (page != null)
                {
                    foreach (var item in page)
                        clonedPage.Add((item ?? new QuickSendBackupItem()).Clone());
                }
                pages.Add(clonedPage);
            }
            return pages;
        }

        private static int CountNonEmptyItems(QuickSendBackupState state)
        {
            var count = 0;
            foreach (var page in state.QuickSendList)
            {
                foreach (var item in page)
                {
                    if (item != null && item.HasUserContent())
                        count++;
                }
            }
            return count;
        }

        private static string SerializeEnvelope(QuickSendSnapshotEnvelope envelope)
        {
            return JsonConvert.SerializeObject(
                envelope,
                Formatting.Indented,
                new JsonSerializerSettings
                {
                    Culture = System.Globalization.CultureInfo.InvariantCulture,
                    DateFormatHandling = DateFormatHandling.IsoDateFormat,
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                    NullValueHandling = NullValueHandling.Include
                });
        }

        private static string ComputeContentHash(QuickSendBackupState state)
        {
            var canonical = new QuickSendSnapshotContent
            {
                QuickSendList = ClonePages(state.QuickSendList),
                QuickListNames = new List<string>(state.QuickListNames)
            };
            var json = JsonConvert.SerializeObject(
                canonical,
                Formatting.None,
                new JsonSerializerSettings
                {
                    Culture = System.Globalization.CultureInfo.InvariantCulture,
                    NullValueHandling = NullValueHandling.Include
                });
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(json)))
                    .Replace("-", string.Empty);
            }
        }

        private static List<QuickSendBackupInfo> EnumerateValidSnapshotsUnsafe()
        {
            var snapshots = new List<QuickSendBackupInfo>();
            var directory = BackupDirectoryPath;
            if (!Directory.Exists(directory))
                return snapshots;

            foreach (var path in Directory.EnumerateFiles(
                directory,
                SnapshotFilePrefix + "*" + SnapshotFileExtension,
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    QuickSendSnapshotEnvelope envelope;
                    QuickSendBackupInfo info;
                    string ignored;
                    if (TryReadEnvelope(path, out envelope, out info, out ignored))
                        snapshots.Add(info);
                }
                catch
                {
                    // One damaged snapshot must not hide the remaining history.
                }
            }

            return snapshots
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool TryReadEnvelope(
            string path,
            out QuickSendSnapshotEnvelope envelope,
            out QuickSendBackupInfo info,
            out string error)
        {
            envelope = null;
            info = null;
            error = string.Empty;
            try
            {
                var file = new FileInfo(path);
                file.Refresh();
                if (!file.Exists || file.Length <= 0 || file.Length > MaximumSnapshotBytes)
                    throw new InvalidDataException("快照文件不存在、为空或过大。");

                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    Culture = System.Globalization.CultureInfo.InvariantCulture,
                    DateParseHandling = DateParseHandling.DateTime,
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                    MaxDepth = 64
                });
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                using (var jsonReader = new JsonTextReader(reader) { MaxDepth = 64 })
                    envelope = serializer.Deserialize<QuickSendSnapshotEnvelope>(jsonReader);

                if (envelope == null || envelope.SchemaVersion != CurrentSchemaVersion)
                    throw new InvalidDataException("快照版本不受支持。");
                if (envelope.CreatedAtUtc == default(DateTime))
                    throw new InvalidDataException("快照时间无效。");
                envelope.CreatedAtUtc = envelope.CreatedAtUtc.ToUniversalTime();
                if (envelope.QuickSendList == null || envelope.QuickSendList.Count == 0 ||
                    envelope.QuickSendList.Any(page => page == null || page.Any(item => item == null)))
                    throw new InvalidDataException("快照页面结构无效。");
                if (envelope.QuickListNames == null ||
                    envelope.QuickListNames.Count != envelope.QuickSendList.Count)
                    throw new InvalidDataException("快照页面名称无效。");
                if (envelope.Selected < 0 || envelope.Selected >= envelope.QuickSendList.Count)
                    throw new InvalidDataException("快照选中页无效。");

                var state = new QuickSendBackupState
                {
                    Selected = envelope.Selected,
                    QuickSendList = ClonePages(envelope.QuickSendList),
                    QuickListNames = envelope.QuickListNames.Select(name => name ?? string.Empty).ToList()
                };
                var expectedHash = ComputeContentHash(state);
                if (string.IsNullOrWhiteSpace(envelope.ContentSha256) ||
                    !FixedTimeEquals(expectedHash, envelope.ContentSha256))
                    throw new InvalidDataException("快照内容校验失败。");
                if (envelope.PageCount != state.QuickSendList.Count ||
                    envelope.NonEmptyItemCount != CountNonEmptyItems(state))
                    throw new InvalidDataException("快照元数据与内容不一致。");

                info = new QuickSendBackupInfo
                {
                    FilePath = Path.GetFullPath(path),
                    FileName = Path.GetFileName(path),
                    FileSize = file.Length,
                    SchemaVersion = envelope.SchemaVersion,
                    CreatedAtUtc = envelope.CreatedAtUtc,
                    AppVersion = envelope.AppVersion ?? string.Empty,
                    Reason = envelope.Reason ?? string.Empty,
                    PageCount = envelope.PageCount,
                    NonEmptyItemCount = envelope.NonEmptyItemCount,
                    ContentSha256 = expectedHash,
                    Selected = envelope.Selected,
                    IsPreUpgrade = IsPreUpgradeReason(envelope.Reason)
                };
                return true;
            }
            catch (Exception ex)
            {
                envelope = null;
                info = null;
                error = ex.Message;
                return false;
            }
        }

        private static void ApplyRetentionUnsafe()
        {
            ApplyRetentionUnsafe(EnumerateValidSnapshotsUnsafe());
        }

        private static void PromoteDuplicateToPreUpgradeUnsafe(
            QuickSendBackupInfo duplicate,
            string reason)
        {
            if (duplicate == null || string.IsNullOrWhiteSpace(duplicate.FilePath))
                return;

            QuickSendSnapshotEnvelope envelope;
            QuickSendBackupInfo ignoredInfo;
            string ignoredError;
            if (!TryReadEnvelope(duplicate.FilePath, out envelope, out ignoredInfo, out ignoredError))
                return;

            envelope.CreatedAtUtc = DateTime.UtcNow;
            envelope.AppVersion = AppInfo.DisplayVersion ?? envelope.AppVersion ?? string.Empty;
            envelope.Reason = NormalizeReason(reason, "pre-upgrade");
            WriteAtomicUtf8(duplicate.FilePath, SerializeEnvelope(envelope));
        }

        private static void ApplyRetentionUnsafe(List<QuickSendBackupInfo> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
                return;

            var newestFirst = snapshots
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var priority = new List<QuickSendBackupInfo>();

            AddUnique(priority, newestFirst[0]);
            AddUnique(priority, newestFirst.FirstOrDefault(item => item.IsPreUpgrade));
            foreach (var recent in newestFirst.Take(7))
                AddUnique(priority, recent);

            var representedDays = new HashSet<DateTime>();
            foreach (var item in priority)
                representedDays.Add(item.CreatedAtUtc.Date);
            foreach (var item in newestFirst)
            {
                if (representedDays.Add(item.CreatedAtUtc.Date))
                    AddUnique(priority, item);
            }
            foreach (var item in newestFirst)
                AddUnique(priority, item);

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long keptBytes = 0;
            foreach (var item in priority)
            {
                if (keep.Count >= MaximumSnapshotCount)
                    break;
                if (item.FileSize <= 0 || item.FileSize > MaximumSnapshotBytes)
                    continue;
                if (keptBytes + item.FileSize > MaximumSnapshotBytes)
                    continue;
                keep.Add(item.FilePath);
                keptBytes += item.FileSize;
            }

            foreach (var item in newestFirst.OrderBy(item => item.CreatedAtUtc))
            {
                if (keep.Contains(item.FilePath))
                    continue;
                try
                {
                    File.Delete(item.FilePath);
                }
                catch
                {
                    // Retention is best effort and must not turn a successful snapshot
                    // into a failed application settings save.
                }
            }
        }

        private static void AddUnique(
            ICollection<QuickSendBackupInfo> items,
            QuickSendBackupInfo candidate)
        {
            if (candidate == null || items.Any(item =>
                string.Equals(item.FilePath, candidate.FilePath, StringComparison.OrdinalIgnoreCase)))
                return;
            items.Add(candidate);
        }

        private static void WriteAtomicUtf8(string destinationPath, string content)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("快照目录无效。");
            Directory.CreateDirectory(directory);

            var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    {
                        writer.Write(content);
                        writer.Flush();
                    }
                    stream.Flush(true);
                }

                if (File.Exists(destinationPath))
                    File.Replace(tempPath, destinationPath, null);
                else
                    File.Move(tempPath, destinationPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private static StoreLease TryAcquireStoreLease()
        {
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, BuildMutexName());
                var acquired = false;
                try
                {
                    acquired = mutex.WaitOne(MutexWaitMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }
                return new StoreLease(mutex);
            }
            catch
            {
                if (mutex != null)
                    mutex.Dispose();
                return null;
            }
        }

        private static string BuildMutexName()
        {
            var canonicalDirectory = Path.GetFullPath(BackupDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            using (var sha256 = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalDirectory)))
                    .Replace("-", string.Empty);
                return @"Local\llcom_plus_quick_send_backups_" + hash;
            }
        }

        private static string BuildSnapshotFileName(DateTime createdAtUtc, string hash)
        {
            return SnapshotFilePrefix +
                createdAtUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture) +
                "_" + hash.Substring(0, Math.Min(12, hash.Length)).ToLowerInvariant() +
                "_" + Guid.NewGuid().ToString("N").Substring(0, 8) +
                SnapshotFileExtension;
        }

        private static string NormalizeReason(string reason, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
            if (value.Length > MaximumReasonLength)
                value = value.Substring(0, MaximumReasonLength);
            return value;
        }

        private static bool IsPreUpgradeReason(string reason)
        {
            return !string.IsNullOrWhiteSpace(reason) &&
                (reason.IndexOf("pre-upgrade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 reason.IndexOf("before-upgrade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 reason.IndexOf("升级前", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool FixedTimeEquals(string first, string second)
        {
            if (first == null || second == null || first.Length != second.Length)
                return false;
            var difference = 0;
            for (var index = 0; index < first.Length; index++)
                difference |= char.ToUpperInvariant(first[index]) ^ char.ToUpperInvariant(second[index]);
            return difference == 0;
        }

        private static bool TryResolveManagedSnapshotPath(
            string path,
            bool allowMissing,
            out string canonicalPath)
        {
            canonicalPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var directory = Path.GetFullPath(BackupDirectoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                canonicalPath = Path.GetFullPath(path);
                if (!string.Equals(
                    Path.GetDirectoryName(canonicalPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    directory,
                    StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!Path.GetFileName(canonicalPath).StartsWith(SnapshotFilePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetExtension(canonicalPath), SnapshotFileExtension, StringComparison.OrdinalIgnoreCase))
                    return false;
                return allowMissing || File.Exists(canonicalPath);
            }
            catch
            {
                canonicalPath = string.Empty;
                return false;
            }
        }

        private sealed class StoreLease : IDisposable
        {
            private Mutex mutex;

            internal StoreLease(Mutex mutex)
            {
                this.mutex = mutex;
            }

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref mutex, null);
                if (current == null)
                    return;
                try
                {
                    current.ReleaseMutex();
                }
                catch
                {
                }
                current.Dispose();
            }
        }
    }

    internal enum QuickSendBackupWriteStatus
    {
        Created,
        Deduplicated,
        Skipped,
        Failed
    }

    internal sealed class QuickSendBackupWriteResult
    {
        internal QuickSendBackupWriteStatus Status { get; private set; }
        internal string FilePath { get; private set; }
        internal string Error { get; private set; }

        internal bool Succeeded =>
            Status == QuickSendBackupWriteStatus.Created ||
            Status == QuickSendBackupWriteStatus.Deduplicated;

        internal static QuickSendBackupWriteResult CreatedFile(string path)
        {
            return Build(QuickSendBackupWriteStatus.Created, path, string.Empty);
        }

        internal static QuickSendBackupWriteResult Deduplicated(string path)
        {
            return Build(QuickSendBackupWriteStatus.Deduplicated, path, string.Empty);
        }

        internal static QuickSendBackupWriteResult Skipped(string message)
        {
            return Build(QuickSendBackupWriteStatus.Skipped, string.Empty, message);
        }

        internal static QuickSendBackupWriteResult Failure(string message)
        {
            return Build(QuickSendBackupWriteStatus.Failed, string.Empty, message);
        }

        private static QuickSendBackupWriteResult Build(
            QuickSendBackupWriteStatus status,
            string path,
            string error)
        {
            return new QuickSendBackupWriteResult
            {
                Status = status,
                FilePath = path ?? string.Empty,
                Error = error ?? string.Empty
            };
        }
    }

    internal sealed class QuickSendBackupInfo
    {
        internal string FilePath { get; set; }
        internal string FileName { get; set; }
        internal long FileSize { get; set; }
        internal int SchemaVersion { get; set; }
        internal DateTime CreatedAtUtc { get; set; }
        internal string AppVersion { get; set; }
        internal string Reason { get; set; }
        internal int PageCount { get; set; }
        internal int NonEmptyItemCount { get; set; }
        internal string ContentSha256 { get; set; }
        internal int Selected { get; set; }
        internal bool IsPreUpgrade { get; set; }
    }

    /// <summary>
    /// Deep, framework-neutral data returned to restore/import callers. Applying it is left
    /// to the owner so it can suppress ToSendData.DataChanged and snapshot before replacement.
    /// </summary>
    internal sealed class QuickSendBackupState
    {
        internal int Selected { get; set; }
        internal List<List<QuickSendBackupItem>> QuickSendList { get; set; } =
            new List<List<QuickSendBackupItem>>();
        internal List<string> QuickListNames { get; set; } = new List<string>();

        internal int SelectedIndex
        {
            get { return Selected; }
            set { Selected = value; }
        }

        internal List<List<QuickSendBackupItem>> Lists
        {
            get { return QuickSendList; }
            set { QuickSendList = value ?? new List<List<QuickSendBackupItem>>(); }
        }

        internal List<string> Names
        {
            get { return QuickListNames; }
            set { QuickListNames = value ?? new List<string>(); }
        }

        /// <summary>
        /// Materializes fresh model objects for Settings.SetAllQuickSendState. The caller
        /// should suppress ToSendData.DataChanged while applying these objects.
        /// </summary>
        internal List<List<Model.ToSendData>> CreateModelLists()
        {
            var result = new List<List<Model.ToSendData>>();
            foreach (var page in QuickSendList ?? new List<List<QuickSendBackupItem>>())
            {
                var targetPage = new List<Model.ToSendData>();
                if (page != null)
                {
                    foreach (var item in page)
                        targetPage.Add((item ?? new QuickSendBackupItem()).ToModel());
                }
                result.Add(targetPage);
            }
            return result;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class QuickSendBackupItem
    {
        [JsonProperty("id", Order = 1)]
        internal int Id { get; set; }

        [JsonProperty("text", Order = 2)]
        internal string Text { get; set; } = string.Empty;

        [JsonProperty("hex", Order = 3)]
        internal bool Hex { get; set; }

        [JsonProperty("commit", Order = 4)]
        internal string Commit { get; set; } = string.Empty;

        [JsonProperty("recvScriptPath", Order = 5)]
        internal string ReceiveScriptPath { get; set; } = string.Empty;

        [JsonProperty("recvScriptPara", Order = 6)]
        internal string ReceiveScriptParameter { get; set; } = string.Empty;

        [JsonProperty("appendCrlf", Order = 7)]
        internal bool AppendCrlf { get; set; }

        [JsonProperty("disableSuggestion", Order = 8)]
        internal bool DisableSuggestion { get; set; }

        internal static QuickSendBackupItem FromModel(Model.ToSendData source)
        {
            source = source ?? new Model.ToSendData();
            return new QuickSendBackupItem
            {
                Id = source.id,
                Text = source.text ?? string.Empty,
                Hex = source.hex,
                Commit = source.commit ?? string.Empty,
                ReceiveScriptPath = source.recvScriptPath ?? string.Empty,
                ReceiveScriptParameter = source.recvScriptPara ?? string.Empty,
                AppendCrlf = source.appendCrlf,
                DisableSuggestion = source.disableSuggestion
            };
        }

        internal QuickSendBackupItem Clone()
        {
            return new QuickSendBackupItem
            {
                Id = Id,
                Text = Text ?? string.Empty,
                Hex = Hex,
                Commit = Commit ?? string.Empty,
                ReceiveScriptPath = ReceiveScriptPath ?? string.Empty,
                ReceiveScriptParameter = ReceiveScriptParameter ?? string.Empty,
                AppendCrlf = AppendCrlf,
                DisableSuggestion = DisableSuggestion
            };
        }

        internal Model.ToSendData ToModel()
        {
            return new Model.ToSendData
            {
                id = Id,
                text = Text ?? string.Empty,
                hex = Hex,
                commit = Commit ?? string.Empty,
                recvScriptPath = ReceiveScriptPath ?? string.Empty,
                recvScriptPara = ReceiveScriptParameter ?? string.Empty,
                appendCrlf = AppendCrlf,
                disableSuggestion = DisableSuggestion
            };
        }

        internal bool HasUserContent()
        {
            var button = (Commit ?? string.Empty).Trim();
            var hasCustomButton = button.Length > 0 &&
                !string.Equals(button, "发送", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "Send", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(button, "?!", StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrWhiteSpace(Text) ||
                Hex ||
                hasCustomButton ||
                !string.IsNullOrWhiteSpace(ReceiveScriptPath) ||
                !string.IsNullOrWhiteSpace(ReceiveScriptParameter) ||
                !AppendCrlf ||
                DisableSuggestion;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class QuickSendSnapshotContent
    {
        // The selected tab is UI state, not user content. It remains in the envelope for
        // restore convenience but is excluded from de-duplication so tab switching cannot
        // crowd real edits out of the bounded history.
        [JsonProperty("quickSendList", Order = 1)]
        internal List<List<QuickSendBackupItem>> QuickSendList { get; set; }

        [JsonProperty("quickListNames", Order = 2)]
        internal List<string> QuickListNames { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class QuickSendSnapshotEnvelope
    {
        [JsonProperty("schemaVersion", Order = 1)]
        internal int SchemaVersion { get; set; }

        [JsonProperty("createdAtUtc", Order = 2)]
        internal DateTime CreatedAtUtc { get; set; }

        [JsonProperty("appVersion", Order = 3)]
        internal string AppVersion { get; set; }

        [JsonProperty("reason", Order = 4)]
        internal string Reason { get; set; }

        [JsonProperty("pageCount", Order = 5)]
        internal int PageCount { get; set; }

        [JsonProperty("nonEmptyItemCount", Order = 6)]
        internal int NonEmptyItemCount { get; set; }

        [JsonProperty("contentSha256", Order = 7)]
        internal string ContentSha256 { get; set; }

        [JsonProperty("selected", Order = 8)]
        internal int Selected { get; set; }

        [JsonProperty("quickSendList", Order = 9)]
        internal List<List<QuickSendBackupItem>> QuickSendList { get; set; }

        [JsonProperty("quickListNames", Order = 10)]
        internal List<string> QuickListNames { get; set; }
    }
}
