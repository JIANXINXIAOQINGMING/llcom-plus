using System;
using System.Collections.Generic;
using System.Linq;

namespace llcom_plus.Tools
{
    internal enum SerialTraceKind { Tx, Rx, Pin, Wake, Info, Error }

    // One immutable host-observed event, shared by analysis, charts and test flows.
    // Timestamp is a software observation, not a hardware sample timestamp.
    internal sealed class SerialTraceEntry : EventArgs
    {
        private readonly byte[] bytes;
        internal SerialTraceEntry(long id, DateTime timestamp, string portName,
            string connectionId, SerialTraceKind kind, byte[] data, string text, int encoding)
        {
            Id = id; Timestamp = timestamp; PortName = portName ?? "";
            ConnectionId = connectionId ?? ""; Kind = kind;
            bytes = data == null ? Array.Empty<byte>() : (byte[])data.Clone();
            Text = text ?? ""; EncodingCodePage = encoding;
        }
        public long Id { get; }
        public DateTime Timestamp { get; }
        public string PortName { get; }
        public string ConnectionId { get; }
        public SerialTraceKind Kind { get; }
        public int EncodingCodePage { get; }
        public string Text { get; }
        public int ByteCount => bytes.Length;
        public byte[] GetData() => (byte[])bytes.Clone();
    }

    internal static class SerialTraceHub
    {
        internal const int MaximumEntries = 10000;
        internal const int MaximumBytes = 16 * 1024 * 1024;
        private static readonly object gate = new object();
        private static readonly Queue<SerialTraceEntry> entries = new Queue<SerialTraceEntry>();
        private static long nextId, dropped;
        private static int byteCount;

        // Subscribers run on the publishing thread: only queue/process bounded data;
        // never synchronously invoke the UI or wait for another serial operation.
        internal static event EventHandler<SerialTraceEntry> Recorded;
        internal static long DroppedCount { get { lock (gate) return dropped; } }
        internal static long LastId { get { lock (gate) return nextId; } }

        internal static void RecordBuffer(string portName, string connectionId, bool sent,
            byte[] data, int offset, int count, int encoding = 65001)
        {
            if (data == null || offset < 0 || count < 0 || offset > data.Length - count) return;
            var timestamp = DateTime.Now;
            for (var position = offset; position < offset + count; position += 65536)
            {
                var block = new byte[Math.Min(65536, offset + count - position)];
                Buffer.BlockCopy(data, position, block, 0, block.Length);
                RecordData(portName, connectionId, sent, block, encoding, timestamp);
            }
        }

        internal static void RecordData(string portName, string connectionId, bool sent,
            byte[] data, int encoding = 65001, DateTime? timestamp = null)
        {
            if (data == null || data.Length == 0) return;
            Record(portName, connectionId, sent ? SerialTraceKind.Tx : SerialTraceKind.Rx,
                data, "", encoding, timestamp ?? DateTime.Now);
        }

        internal static void RecordEvent(string portName, string connectionId,
            SerialTraceKind kind, string text, DateTime? timestamp = null)
        {
            Record(portName, connectionId, kind, null, text, 65001, timestamp ?? DateTime.Now);
        }

        private static void Record(string portName, string connectionId, SerialTraceKind kind,
            byte[] data, string text, int encoding, DateTime timestamp)
        {
            // A single oversized event must not allocate unbounded analysis memory.
            // Real device writes/reads are published in their existing bounded blocks.
            if (data != null && data.Length > MaximumBytes)
            {
                lock (gate) dropped++;
                return;
            }
            if (text != null && text.Length > 8192) text = text.Substring(0, 8192);
            SerialTraceEntry entry;
            lock (gate)
            {
                entry = new SerialTraceEntry(++nextId, timestamp, portName, connectionId,
                    kind, data, text, encoding);
                entries.Enqueue(entry);
                byteCount += entry.ByteCount + entry.Text.Length * 2;
                while (entries.Count > MaximumEntries || byteCount > MaximumBytes)
                {
                    var removed = entries.Dequeue();
                    byteCount -= removed.ByteCount + removed.Text.Length * 2;
                    dropped++;
                }
            }
            var handlers = Recorded;
            if (handlers == null) return;
            foreach (EventHandler<SerialTraceEntry> handler in handlers.GetInvocationList())
            {
                try { handler(null, entry); }
                catch { /* An optional observer cannot interrupt serial IO. */ }
            }
        }

        internal static IReadOnlyList<SerialTraceEntry> Snapshot()
        {
            lock (gate) return entries.ToArray();
        }

        internal static string[] GetKnownPorts()
        {
            lock (gate) return entries.Select(item => item.PortName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray();
        }

        internal static bool MatchesConnection(SerialTraceEntry entry, string targetIdentity)
        {
            return entry != null && !string.IsNullOrEmpty(entry.ConnectionId) &&
                !string.IsNullOrEmpty(targetIdentity) &&
                (string.Equals(entry.ConnectionId, targetIdentity, StringComparison.Ordinal) ||
                 targetIdentity.EndsWith(":" + entry.ConnectionId, StringComparison.Ordinal));
        }
    }
}
