using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace llcom_plus.Tools
{
    internal sealed class Ch347BusPreset
    {
        public string Name { get; set; } = "";
        public Ch347TransferRequest Request { get; set; } = new Ch347TransferRequest();
        public Ch347BusPreset Clone() => new Ch347BusPreset { Name = Name, Request = Request?.Clone() };
        public override string ToString() => Name;
    }

    // Bus presets never modify or migrate the user's serial quick-send collection.
    internal sealed class Ch347PresetStore
    {
        internal const int MaximumPresets = 100;
        private const int MaximumFileBytes = 2 * 1024 * 1024;
        private readonly string path;
        private readonly object gate = new object();
        public Ch347PresetStore() : this(Path.Combine(Global.ProfilePath, "ch347-presets.json")) { }
        internal Ch347PresetStore(string path) { this.path = Path.GetFullPath(path); }

        public IReadOnlyList<Ch347BusPreset> Load()
        {
            lock (gate)
            {
                if (!File.Exists(path)) return Array.Empty<Ch347BusPreset>();
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Bus preset file is too large.");
                    json = reader.ReadToEnd();
                }
                var document = JsonConvert.DeserializeObject<PresetDocument>(json,
                    new JsonSerializerSettings { MaxDepth = 16, TypeNameHandling = TypeNameHandling.None });
                if (document == null || document.Version != 1) throw new InvalidDataException("Unsupported bus preset file.");
                return Validate(document.Presets).Select(p => p.Clone()).ToArray();
            }
        }

        public void Save(IEnumerable<Ch347BusPreset> presets)
        {
            var snapshot = Validate(presets).Select(p => p.Clone()).ToArray();
            var json = JsonConvert.SerializeObject(new PresetDocument { Presets = snapshot }, Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes) throw new InvalidDataException("Bus preset file is too large.");
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static Ch347BusPreset[] Validate(IEnumerable<Ch347BusPreset> source)
        {
            var presets = (source ?? Enumerable.Empty<Ch347BusPreset>()).Take(MaximumPresets + 1).ToArray();
            if (presets.Length > MaximumPresets) throw new InvalidDataException("At most 100 bus presets are allowed.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in presets)
            {
                if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80 ||
                    !names.Add(preset.Name.Trim()) || preset.Request == null || preset.Request.ProbeOnly)
                    throw new InvalidDataException("Invalid or duplicate bus preset name or request.");
                preset.Request.Validate();
            }
            return presets;
        }

        private sealed class PresetDocument
        {
            public int Version { get; set; } = 1;
            public Ch347BusPreset[] Presets { get; set; } = Array.Empty<Ch347BusPreset>();
        }
    }
}
