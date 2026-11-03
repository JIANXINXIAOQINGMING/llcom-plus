using System;
using System.IO;
using System.Text;

namespace Llcom.SerialMonitoring
{
    // Inherited anonymous standard streams bind this protocol to one child.
    // No native pointer or process-local structure layout crosses this boundary.
    internal static class MonitorBrokerProtocol
    {
        internal const uint Magic = 0x31424D4C; // LMB1
        internal const ushort Version = 1;
        internal const int PacketSize = 8256;
        internal const uint NativeAbi = 3;
        internal const int MaximumErrorBytes = 2048;
        internal enum Kind : ushort { Hello = 1, Status = 2, Packet = 3, Stop = 4, Stopped = 5, Error = 6, Start = 7 }
        internal sealed class Frame
        {
            internal Kind Type;
            internal byte[] Payload;
        }
        internal static bool IsLengthValid(Kind kind, int length)
        {
            switch (kind)
            {
                case Kind.Hello: return length == 12;
                case Kind.Status:
                case Kind.Stopped: return length == 4;
                case Kind.Packet: return length == PacketSize;
                case Kind.Start: return length == 16;
                case Kind.Stop: return length == 0;
                case Kind.Error: return length >= 0 && length <= MaximumErrorBytes;
                default: return false;
            }
        }
        internal static Frame Read(Stream stream)
        {
            int first = stream.ReadByte();
            if (first < 0) return null;
            var header = new byte[12];
            header[0] = (byte)first;
            ReadExactly(stream, header, 1, 11);
            var kind = (Kind)BitConverter.ToUInt16(header, 6);
            int length = BitConverter.ToInt32(header, 8);
            if (BitConverter.ToUInt32(header, 0) != Magic || BitConverter.ToUInt16(header, 4) != Version ||
                !IsLengthValid(kind, length)) throw new InvalidDataException("Invalid serial monitor helper frame.");
            var payload = new byte[length];
            ReadExactly(stream, payload, 0, length);
            return new Frame { Type = kind, Payload = payload };
        }
        private static void ReadExactly(Stream stream, byte[] data, int offset, int length)
        {
            while (length > 0)
            {
                int read = stream.Read(data, offset, length);
                if (read == 0) throw new EndOfStreamException("The serial monitor helper frame was truncated.");
                offset += read; length -= read;
            }
        }
        internal static void Write(Stream stream, Kind kind, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            if (!IsLengthValid(kind, payload.Length)) throw new InvalidDataException("Invalid outgoing monitor frame.");
            var header = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(Magic), 0, header, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(Version), 0, header, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((ushort)kind), 0, header, 6, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 8, 4);
            stream.Write(header, 0, header.Length);
            if (payload.Length != 0) stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
        internal static byte[] Hello(int pid, int bits)
        {
            var data = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(pid), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(bits), 0, data, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(NativeAbi), 0, data, 8, 4);
            return data;
        }
        internal static byte[] Start(uint pid, uint com, long startedTicks)
        {
            var data = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(pid), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(com), 0, data, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(startedTicks), 0, data, 8, 8);
            return data;
        }
        internal static byte[] Error(string message)
        {
            var text = message ?? "Monitor helper failed.";
            if (text.Length > 600) text = text.Substring(0, 600);
            return Encoding.UTF8.GetBytes(text);
        }
        internal static bool IsPacketValid(byte[] data, uint expectedCom)
        {
            if (data == null || data.Length != PacketSize || BitConverter.ToUInt32(data, 0) != 0x334D534C ||
                BitConverter.ToUInt16(data, 4) != NativeAbi || BitConverter.ToUInt16(data, 6) != 64 ||
                BitConverter.ToUInt64(data, 8) == 0 || BitConverter.ToUInt32(data, 24) != expectedCom) return false;
            uint state = BitConverter.ToUInt32(data, 28), total = BitConverter.ToUInt32(data, 40),
                offset = BitConverter.ToUInt32(data, 44), length = BitConverter.ToUInt32(data, 48),
                flags = BitConverter.ToUInt32(data, 52);
            return state >= 2 && state <= 4 && total <= 1024 * 1024 && length <= 8192 &&
                offset <= total && length <= total - offset && (flags & ~7U) == 0 &&
                ((flags & 1) == 0 || offset == 0) && ((flags & 2) == 0 || offset + length == total);
        }
        internal static ushort PeMachine(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid helper PE image.");
                stream.Position = 0x3C;
                int pe = reader.ReadInt32();
                if (pe < 64 || pe > stream.Length - 24) throw new InvalidDataException("Invalid helper PE offset.");
                stream.Position = pe;
                if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Invalid helper PE signature.");
                return reader.ReadUInt16();
            }
        }
        internal static ulong SaturatingAdd(ulong left, ulong right) { return ulong.MaxValue - left < right ? ulong.MaxValue : left + right; }
    }
}
