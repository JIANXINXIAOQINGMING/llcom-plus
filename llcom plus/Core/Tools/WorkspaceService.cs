using llcom_plus.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace llcom_plus.Tools
{
    // A workspace is an allowlisted configuration, never a copy of settings.json.
    // In particular it does not contain account credentials, connection secrets,
    // an executable script body, or an instruction to connect/send/run anything.
    internal sealed class WorkspaceSnapshot
    {
        public string Format { get; set; } = "llcom-plus.workspace";
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";
        public DateTime SavedAtUtc { get; set; }
        public WorkspaceLayout Layout { get; set; } = new WorkspaceLayout();
        public Dictionary<string, JObject> SerialProfiles { get; set; } =
            new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        public List<List<QuickSendBackupItem>> QuickSendPages { get; set; } = new List<List<QuickSendBackupItem>>();
        public List<string> PageNames { get; set; } = new List<string>();
        public int SelectedPage { get; set; }

        internal UartPortProfile GetPortProfile(string name)
        {
            return SerialProfiles.TryGetValue(name, out var value)
                ? Settings.CreateNormalizedUartProfileSnapshot(value.ToObject<UartPortProfile>()) : null;
        }

        internal QuickSendBackupState GetQuickSendState()
        {
            return new QuickSendBackupState
            {
                Selected = SelectedPage,
                QuickListNames = PageNames.ToList(),
                QuickSendList = QuickSendPages.Select(p => p.Select(i => i.Clone()).ToList()).ToList()
            };
        }
    }

    internal sealed class WorkspaceLayout
    {
        public int SplitCount { get; set; } = 1;
        public int ActiveSlot { get; set; } = 1;
        public List<WorkspacePortRole> Ports { get; set; } = new List<WorkspacePortRole>();
    }

    internal sealed class WorkspacePortRole
    {
        public int Slot { get; set; } = 1;
        public string PortName { get; set; } = "";
        public string Role { get; set; } = "";
    }

    internal sealed class WorkspaceInfo
    {
        public string Name { get; set; }
        public DateTime SavedAt { get; set; }
        public int PortCount { get; set; }
        public int PageCount { get; set; }
        public string Error { get; set; }
        public string Summary => Error ?? (PortCount + " COM · " + PageCount + " pages");
    }

    internal static class WorkspaceService
    {
        internal const int MaximumFileBytes = 2 * 1024 * 1024;
        internal const int MaximumWorkspaces = 100;
        private static readonly object storeGate = new object();
        private static readonly string[] profileFields =
        {
            "baudRate", "autoReconnect", "showHexFormat", "hexSend", "showSend", "showSendRaw",
            "parity", "timeout", "dataBits", "stopBit", "flowControl", "sendThrottlePacketSize",
            "sendThrottleDelayMs", "dtrWakeBeforeSend", "dtrWakeDelayMs", "dtrWakeIdleMs", "bitDelay",
            "maxLength", "sendScript", "recvScript", "terminal", "encoding", "extraEnter", "enterSend",
            "enableSymbol", "showLineEndings", "logShowDate", "logShowTime", "logShowMilliseconds",
            "logShowPort", "logTxLabel", "logRxLabel", "rts", "dtr"
        };
        private static readonly HashSet<string> allowedProfileFields = new HashSet<string>(profileFields, StringComparer.Ordinal);
        private static readonly JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Error,
            MaxDepth = 24,
            DateParseHandling = DateParseHandling.DateTime
        };

        internal static string DirectoryPath(string profilePath) => Path.Combine(Path.GetFullPath(profilePath), "workspaces");

        internal static WorkspaceSnapshot Capture(Settings settings, WorkspaceLayout layout, string name = "Current")
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.GetQuickSendStateSnapshot(out var pages, out var names, out var selected);
            var snapshot = new WorkspaceSnapshot
            {
                Name = name,
                SavedAtUtc = DateTime.UtcNow,
                Layout = layout ?? new WorkspaceLayout(),
                QuickSendPages = pages.Select(p => p.Select(QuickSendBackupItem.FromModel).ToList()).ToList(),
                PageNames = names.ToList(),
                SelectedPage = selected
            };
            // Keep only the ports belonging to this workspace, not every historical COM.
            foreach (var port in snapshot.Layout.Ports.Select(p => p.PortName)
                .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var profile = string.Equals(port, settings.ActiveUartProfileName, StringComparison.OrdinalIgnoreCase)
                    ? settings.GetCurrentUartProfileSnapshot() : settings.GetUartProfileSnapshot(port);
                if (profile == null) continue;
                var source = JObject.FromObject(profile);
                var target = new JObject();
                foreach (var field in profileFields) target[field] = source[field]?.DeepClone();
                snapshot.SerialProfiles[port.ToUpperInvariant()] = target;
            }
            return Validate(snapshot);
        }

        internal static WorkspaceSnapshot Validate(WorkspaceSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Format != "llcom-plus.workspace" || snapshot.Version != 1)
                throw new InvalidDataException("不支持的工作区格式或版本 / Unsupported workspace format or version.");
            ValidateName(snapshot.Name);
            if (snapshot.SavedAtUtc == default(DateTime)) throw new InvalidDataException("工作区缺少保存时间 / Missing saved time.");
            var layout = snapshot.Layout;
            if (layout == null || layout.SplitCount < 1 || layout.SplitCount > 4 ||
                layout.ActiveSlot < 1 || layout.ActiveSlot > layout.SplitCount || layout.Ports == null ||
                layout.Ports.Count != layout.SplitCount)
                throw new InvalidDataException("无效的分屏布局 / Invalid split layout.");
            var slots = new HashSet<int>();
            var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in layout.Ports)
            {
                if (role == null || role.Slot < 1 || role.Slot > layout.SplitCount || !slots.Add(role.Slot))
                    throw new InvalidDataException("分屏编号重复或无效 / Invalid or duplicate slot.");
                ValidatePortName(role.PortName, true);
                if (!string.IsNullOrEmpty(role.PortName) && !ports.Add(role.PortName))
                    throw new InvalidDataException("同一串口不能分配到两个分屏 / A COM port cannot occupy two slots.");
                CheckText(role.Role, 80, "串口角色 / Port role");
            }
            if (snapshot.SerialProfiles == null || snapshot.SerialProfiles.Count > 4)
                throw new InvalidDataException("串口配置数量超限 / Too many port configurations.");
            foreach (var entry in snapshot.SerialProfiles)
            {
                ValidatePortName(entry.Key, false);
                if (!ports.Contains(entry.Key)) throw new InvalidDataException("串口配置不属于当前布局 / Profile is not in this layout.");
                ValidateProfile(entry.Value);
            }
            if (ports.Any(p => !snapshot.SerialProfiles.Keys.Contains(p, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("布局缺少串口配置 / Missing port configuration.");
            if (snapshot.QuickSendPages == null || snapshot.QuickSendPages.Count < 1 || snapshot.QuickSendPages.Count > 100 ||
                snapshot.PageNames == null || snapshot.PageNames.Count != snapshot.QuickSendPages.Count ||
                snapshot.SelectedPage < 0 || snapshot.SelectedPage >= snapshot.QuickSendPages.Count)
                throw new InvalidDataException("无效的快捷发送页面 / Invalid quick-send pages.");
            var count = 0;
            foreach (var page in snapshot.QuickSendPages)
            {
                if (page == null || page.Count < 1 || page.Count > 1000 || (count += page.Count) > 10000)
                    throw new InvalidDataException("快捷指令数量超限 / Too many quick commands.");
                foreach (var item in page)
                {
                    if (item == null) throw new InvalidDataException("空指令对象 / Invalid command entry.");
                    CheckText(item.Text, 65536, "指令 / Command");
                    CheckText(item.Commit, 256, "按钮名称 / Button name");
                    CheckText(item.ReceiveScriptParameter, 16384, "脚本参数 / Script parameters");
                    ValidateRunScriptReference(item.ReceiveScriptPath);
                    QuickSendWorkflow.Validate(item);
                }
            }
            foreach (var name in snapshot.PageNames) CheckText(name, 256, "页面名称 / Page name");
            return snapshot;
        }

        internal static WorkspaceSnapshot Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("工作区文件超过 2 MiB / Workspace file exceeds 2 MiB.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                using (var json = new JsonTextReader(reader) { MaxDepth = 24 })
                {
                    var token = JToken.ReadFrom(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (json.Read()) throw new InvalidDataException("工作区包含多份 JSON / Trailing JSON content.");
                    return Validate(token.ToObject<WorkspaceSnapshot>(JsonSerializer.Create(jsonSettings)));
                }
            }
        }

        internal static WorkspaceSnapshot Clone(WorkspaceSnapshot snapshot, bool includeScriptReferences = true)
        {
            Validate(snapshot);
            var json = Serialize(snapshot);
            var copy = JsonConvert.DeserializeObject<WorkspaceSnapshot>(json, jsonSettings);
            if (!includeScriptReferences)
            {
                foreach (var profile in copy.SerialProfiles.Values)
                {
                    profile["sendScript"] = "default";
                    profile["recvScript"] = "default";
                }
                foreach (var item in copy.QuickSendPages.SelectMany(p => p))
                    item.ReceiveScriptPath = "";
            }
            return Validate(copy);
        }

        internal static string Save(string profilePath, WorkspaceSnapshot snapshot, bool overwrite)
        {
            var content = Serialize(Validate(snapshot));
            lock (storeGate)
            {
                var directory = DirectoryPath(profilePath);
                Directory.CreateDirectory(directory);
                var path = ManagedPath(profilePath, snapshot.Name);
                if (File.Exists(path) && !overwrite) throw new IOException("同名工作区已存在 / Workspace already exists.");
                if (!File.Exists(path) && Directory.EnumerateFiles(directory, "*.workspace.json").Take(MaximumWorkspaces).Count() >= MaximumWorkspaces)
                    throw new IOException("最多保存 100 个工作区 / Maximum 100 workspaces.");
                WriteAtomic(path, content, overwrite);
                return path;
            }
        }

        internal static WorkspaceSnapshot Load(string profilePath, string name) => Read(ManagedPath(profilePath, name));

        internal static IReadOnlyList<WorkspaceInfo> List(string profilePath)
        {
            var directory = DirectoryPath(profilePath);
            if (!Directory.Exists(directory)) return Array.Empty<WorkspaceInfo>();
            var result = new List<WorkspaceInfo>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.workspace.json").Take(MaximumWorkspaces + 1))
            {
                var name = Path.GetFileName(file);
                name = name.Substring(0, name.Length - ".workspace.json".Length);
                try
                {
                    var snapshot = Read(file);
                    result.Add(new WorkspaceInfo { Name = name, SavedAt = snapshot.SavedAtUtc.ToLocalTime(),
                        PortCount = snapshot.SerialProfiles.Count, PageCount = snapshot.QuickSendPages.Count });
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException || ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    result.Add(new WorkspaceInfo { Name = name, Error = "无法读取 / Unreadable: " + ex.Message });
                }
            }
            return result.OrderByDescending(i => i.SavedAt).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        internal static void Export(string path, WorkspaceSnapshot snapshot)
        {
            WriteAtomic(Path.GetFullPath(path), Serialize(Validate(snapshot)), true);
        }

        internal static string Delete(string profilePath, string name)
        {
            lock (storeGate)
            {
                var source = ManagedPath(profilePath, name);
                var trash = Path.Combine(DirectoryPath(profilePath), ".deleted");
                Directory.CreateDirectory(trash);
                var target = Path.Combine(trash, Guid.NewGuid().ToString("N") + ".json");
                File.Move(source, target);
                // Keep a bounded recovery area. Never touch the quick-send snapshots.
                foreach (var old in new DirectoryInfo(trash).EnumerateFiles("*.json")
                    .Where(f => Regex.IsMatch(f.Name, "^[a-f0-9]{32}\\.json$"))
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(20)) old.Delete();
                return target;
            }
        }

        internal static void Restore(string profilePath, Settings settings, WorkspaceSnapshot desired,
            Func<WorkspaceSnapshot> captureCurrent, Func<string> getBlockReason, Action<WorkspaceSnapshot> apply,
            bool includeScriptReferences)
        {
            if (captureCurrent == null || getBlockReason == null || apply == null)
                throw new InvalidOperationException("工作区未连接到主窗口 / Workspace host is not ready.");
            var incoming = Clone(desired, includeScriptReferences);
            EnsureUnblocked(getBlockReason);
            var previous = Clone(captureCurrent());
            var backup = QuickSendBackupService.CreateNow(settings, "pre-workspace-restore");
            if (!backup.Succeeded)
                throw new IOException("旧快捷发送备份未成功，未加载工作区 / Backup failed; workspace was not loaded. " + backup.Error);
            var recoveryPath = Path.Combine(DirectoryPath(profilePath), ".before-restore.json");
            WriteAtomic(recoveryPath, Serialize(previous), true);
            EnsureUnblocked(getBlockReason);
            try { apply(incoming); }
            catch (Exception failure)
            {
                try { apply(previous); }
                catch (Exception rollbackFailure)
                {
                    throw new IOException("工作区加载与回滚失败。旧配置保存在 / Restore and rollback failed; recovery: " +
                        recoveryPath + "\n" + failure.Message + "\n" + rollbackFailure.Message, failure);
                }
                throw new IOException("工作区加载失败，已恢复原配置 / Restore failed; previous configuration restored. " + failure.Message, failure);
            }
        }

        private static void EnsureUnblocked(Func<string> getBlockReason)
        {
            var reason = getBlockReason();
            if (!string.IsNullOrEmpty(reason)) throw new InvalidOperationException(reason);
        }

        private static void ValidateProfile(JObject profile)
        {
            if (profile == null || profile.Properties().Any(p => !allowedProfileFields.Contains(p.Name)))
                throw new InvalidDataException("串口配置包含未知字段 / Unknown serial setting.");
            foreach (var property in profile.Properties())
            {
                var name = property.Name;
                if (name == "sendScript" || name == "recvScript")
                {
                    if (property.Value.Type != JTokenType.String) throw new InvalidDataException("无效脚本引用 / Invalid script reference.");
                    ValidateScriptName(property.Value.Value<string>());
                }
                else if (name == "logTxLabel" || name == "logRxLabel")
                {
                    if (property.Value.Type != JTokenType.String)
                        throw new InvalidDataException("日志方向标识必须为文本 / Log direction must be text.");
                    var label = property.Value.Value<string>();
                    if (label.Length > 16 || Settings.NormalizeLogDirectionLabel(label, "") != label)
                        throw new InvalidDataException("日志方向标识超长或包含控制字符 / Invalid log direction label.");
                }
                else if (name == "baudRate" || name == "showHexFormat" || name == "parity" || name == "timeout" ||
                    name == "dataBits" || name == "stopBit" || name == "flowControl" || name == "sendThrottlePacketSize" ||
                    name == "sendThrottleDelayMs" || name == "dtrWakeDelayMs" || name == "dtrWakeIdleMs" || name == "maxLength" || name == "encoding")
                {
                    if (property.Value.Type != JTokenType.Integer) throw new InvalidDataException("串口参数必须为整数 / Serial value must be an integer.");
                }
                else if (property.Value.Type != JTokenType.Boolean) throw new InvalidDataException("无效串口开关 / Invalid serial switch.");
            }
            var p = profile.ToObject<UartPortProfile>();
            if (p.baudRate < 1 || p.baudRate > 12000000 || p.parity < 0 || p.parity > 4 || p.showHexFormat < 0 || p.showHexFormat > 2 ||
                p.dataBits < 5 || p.dataBits > 8 || p.stopBit < 1 || p.stopBit > 3 || p.flowControl < 0 || p.flowControl > 2 ||
                p.timeout < -60000 || p.timeout > 60000 || p.maxLength < 1 || p.maxLength > 16 * 1024 * 1024 ||
                p.sendThrottlePacketSize < 0 || p.sendThrottlePacketSize > 16 * 1024 * 1024 ||
                p.sendThrottleDelayMs < 0 || p.sendThrottleDelayMs > 10000 || p.dtrWakeDelayMs < 0 || p.dtrWakeDelayMs > 10000 ||
                p.dtrWakeIdleMs < 0 || p.dtrWakeIdleMs > 60000)
                throw new InvalidDataException("串口参数超出范围 / Serial setting is out of range.");
            try { Encoding.GetEncoding(p.encoding); }
            catch (ArgumentException ex) { throw new InvalidDataException("无效编码 / Invalid encoding.", ex); }
        }

        private static void ValidatePortName(string value, bool allowEmpty)
        {
            if (allowEmpty && value == "") return;
            if (value == null || !Regex.IsMatch(value, "^COM[1-9][0-9]{0,5}$", RegexOptions.IgnoreCase))
                throw new InvalidDataException("无效 COM 名称 / Invalid COM name.");
        }

        private static void ValidateScriptName(string value)
        {
            CheckText(value, 200, "脚本引用 / Script reference");
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                value.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || value.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidDataException("脚本引用只能是本地脚本名 / Script reference must be a local script name.");
        }

        private static void ValidateRunScriptReference(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            // Quick commands store a receive-converter NAME, not a run-script path.
            // Its fixed directory is resolved by Global.TryGetProfileScriptPath.
            ValidateScriptName(value);
        }

        internal static void ValidateName(string name)
        {
            CheckText(name, 64, "工作区名称 / Workspace name");
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.StartsWith(".", StringComparison.Ordinal) ||
                name.EndsWith(".", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\\.)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("请输入不含路径符号的工作区名称 / Enter a workspace name without path characters.");
        }

        private static void CheckText(string value, int maximum, string label)
        {
            if (value == null || value.Length > maximum || value.IndexOf('\0') >= 0)
                throw new InvalidDataException(label + " 为空或过长 / is missing or too long.");
        }

        private static string ManagedPath(string profilePath, string name)
        {
            ValidateName(name);
            return Path.Combine(DirectoryPath(profilePath), name + ".workspace.json");
        }

        private static string Serialize(WorkspaceSnapshot snapshot)
        {
            var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented, jsonSettings);
            if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
                throw new InvalidDataException("工作区文件超过 2 MiB / Workspace file exceeds 2 MiB.");
            return json;
        }

        private static void WriteAtomic(string path, string content, bool overwrite)
        {
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, ".workspace-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)) writer.Write(content);
                    stream.Flush(true);
                }
                if (overwrite && File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
