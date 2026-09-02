using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace llcom_plus.Tools
{
    public sealed class DataCalcResult
    {
        public string Length { get; set; }
        public string Md5 { get; set; }
        public string Sha1 { get; set; }
        public string Sha256 { get; set; }
        public string Sha512 { get; set; }
        public string Crc16Modbus { get; set; }
        public string Crc32 { get; set; }
    }

    internal sealed class DataCalcFileFingerprint
    {
        private const int HashBufferSize = 81920;
        private static readonly byte[] EmptyBytes = new byte[0];

        private DataCalcFileFingerprint(
            string fullPath,
            long length,
            long lastWriteTimeUtcTicks,
            string contentSha256)
        {
            FullPath = fullPath;
            Length = length;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            ContentSha256 = contentSha256 ?? string.Empty;
        }

        internal string FullPath { get; }
        internal long Length { get; }
        internal long LastWriteTimeUtcTicks { get; }
        internal string ContentSha256 { get; }
        internal bool HasContentIdentity => !string.IsNullOrEmpty(ContentSha256);

        internal static DataCalcFileFingerprint Capture(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new FileNotFoundException("No file was selected.");

            var fullPath = Path.GetFullPath(filePath);
            var fileInfo = new FileInfo(fullPath);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
                throw new FileNotFoundException("The selected file does not exist.", fullPath);

            return new DataCalcFileFingerprint(
                fileInfo.FullName,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks,
                string.Empty);
        }

        internal static DataCalcFileFingerprint CaptureWithContentIdentity(
            string filePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = Capture(filePath);
            using (var stream = metadata.OpenRead(HashBufferSize, cancellationToken))
            {
                var contentSha256 = ComputeSha256(stream, cancellationToken);
                metadata.VerifyUnchanged(cancellationToken);
                return new DataCalcFileFingerprint(
                    metadata.FullPath,
                    metadata.Length,
                    metadata.LastWriteTimeUtcTicks,
                    contentSha256);
            }
        }

        internal bool Matches(DataCalcFileFingerprint other)
        {
            if (HasContentIdentity && other?.HasContentIdentity == true)
                return MatchesContentIdentity(other);
            return MatchesMetadata(other);
        }

        internal bool MatchesMetadata(DataCalcFileFingerprint other)
        {
            return other != null &&
                   string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase) &&
                   Length == other.Length &&
                   LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
        }

        internal bool MatchesContentIdentity(DataCalcFileFingerprint other)
        {
            return other != null &&
                   HasContentIdentity &&
                   other.HasContentIdentity &&
                   string.Equals(FullPath, other.FullPath, StringComparison.OrdinalIgnoreCase) &&
                   Length == other.Length &&
                   string.Equals(ContentSha256, other.ContentSha256, StringComparison.Ordinal);
        }

        internal void VerifyUnchanged()
        {
            VerifyUnchanged(CancellationToken.None);
        }

        internal void VerifyUnchanged(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DataCalcFileFingerprint current;
            try
            {
                current = Capture(FullPath);
            }
            catch (Exception ex)
            {
                throw new IOException("The selected file is no longer available.", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesMetadata(current))
                throw new IOException("The selected file changed after the operation started.");
        }

        internal void VerifyContentIdentity(CancellationToken cancellationToken)
        {
            if (!HasContentIdentity)
                throw new InvalidOperationException("A strong file identity is required.");

            var current = CaptureWithContentIdentity(FullPath, cancellationToken);
            if (!MatchesContentIdentity(current))
                throw new IOException("The selected file content changed after the operation started.");
        }

        internal FileStream OpenRead(int bufferSize)
        {
            return OpenRead(bufferSize, CancellationToken.None);
        }

        internal FileStream OpenRead(int bufferSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyUnchanged(cancellationToken);
            var stream = new FileStream(
                FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                Math.Max(1, bufferSize),
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Length != Length)
                    throw new IOException("The selected file length changed after the operation started.");
                VerifyUnchanged(cancellationToken);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal FileStream OpenVerifiedRead(int bufferSize, CancellationToken cancellationToken)
        {
            if (!HasContentIdentity)
                throw new InvalidOperationException("A strong file identity is required.");

            var stream = OpenRead(bufferSize, cancellationToken);
            try
            {
                var actualSha256 = ComputeSha256(stream, cancellationToken);
                if (!string.Equals(ContentSha256, actualSha256, StringComparison.Ordinal))
                    throw new IOException("The selected file content changed after the operation started.");

                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = 0;
                VerifyUnchanged(cancellationToken);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal static string ComputeSha256(byte[] data)
        {
            using (var sha256 = SHA256.Create())
                return FormatHash(sha256.ComputeHash(data ?? EmptyBytes));
        }

        private static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
        {
            using (var sha256 = SHA256.Create())
            {
                var buffer = new byte[HashBufferSize];
                while (true)
                {
                    var read = ReadWithCancellation(stream, buffer, cancellationToken);
                    if (read <= 0)
                        break;
                    sha256.TransformBlock(buffer, 0, read, buffer, 0);
                }

                cancellationToken.ThrowIfCancellationRequested();
                sha256.TransformFinalBlock(EmptyBytes, 0, 0);
                return FormatHash(sha256.Hash);
            }
        }

        private static int ReadWithCancellation(
            Stream stream,
            byte[] buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }

        private static string FormatHash(byte[] hash)
        {
            return BitConverter.ToString(hash ?? EmptyBytes).Replace("-", "");
        }
    }

    public static class DataCalcCalculator
    {
        private const int StreamBufferSize = 81920;
        private static readonly byte[] EmptyBytes = new byte[0];

        public static DataCalcResult Calculate(byte[] data)
        {
            data = data ?? EmptyBytes;
            using (var stream = new MemoryStream(data, false))
                return Calculate(stream, CancellationToken.None);
        }

        public static DataCalcResult Calculate(Stream stream)
        {
            return Calculate(stream, CancellationToken.None);
        }

        public static DataCalcResult Calculate(Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead)
                throw new ArgumentException("The stream must be readable.", nameof(stream));

            using (var md5 = MD5.Create())
            using (var sha1 = SHA1.Create())
            using (var sha256 = SHA256.Create())
            using (var sha512 = SHA512.Create())
            {
                var buffer = new byte[StreamBufferSize];
                long length = 0;
                ushort crc16 = 0xFFFF;
                uint crc32 = 0xFFFFFFFF;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    if (read <= 0)
                        break;

                    length = checked(length + read);
                    md5.TransformBlock(buffer, 0, read, buffer, 0);
                    sha1.TransformBlock(buffer, 0, read, buffer, 0);
                    sha256.TransformBlock(buffer, 0, read, buffer, 0);
                    sha512.TransformBlock(buffer, 0, read, buffer, 0);
                    crc16 = UpdateCrc16Modbus(crc16, buffer, read);
                    crc32 = UpdateCrc32(crc32, buffer, read);
                }

                cancellationToken.ThrowIfCancellationRequested();
                md5.TransformFinalBlock(EmptyBytes, 0, 0);
                sha1.TransformFinalBlock(EmptyBytes, 0, 0);
                sha256.TransformFinalBlock(EmptyBytes, 0, 0);
                sha512.TransformFinalBlock(EmptyBytes, 0, 0);

                return new DataCalcResult
                {
                    Length = $"{length} bytes",
                    Md5 = FormatHash(md5.Hash),
                    Sha1 = FormatHash(sha1.Hash),
                    Sha256 = FormatHash(sha256.Hash),
                    Sha512 = FormatHash(sha512.Hash),
                    Crc16Modbus = $"0x{crc16:X4}",
                    Crc32 = $"0x{~crc32:X8}"
                };
            }
        }

        private static string FormatHash(byte[] hash)
        {
            return BitConverter.ToString(hash ?? EmptyBytes).Replace("-", "");
        }

        private static ushort UpdateCrc16Modbus(ushort crc, byte[] buffer, int count)
        {
            for (var index = 0; index < count; index++)
            {
                crc ^= buffer[index];
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        private static uint UpdateCrc32(uint crc, byte[] buffer, int count)
        {
            for (var index = 0; index < count; index++)
            {
                crc ^= buffer[index];
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc;
        }
    }
}
