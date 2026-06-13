using System;
using System.Security.Cryptography;

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

    public static class DataCalcCalculator
    {
        public static DataCalcResult Calculate(byte[] data)
        {
            data = data ?? new byte[0];
            return new DataCalcResult
            {
                Length = $"{data.LongLength} bytes",
                Md5 = ComputeHash(MD5.Create(), data),
                Sha1 = ComputeHash(SHA1.Create(), data),
                Sha256 = ComputeHash(SHA256.Create(), data),
                Sha512 = ComputeHash(SHA512.Create(), data),
                Crc16Modbus = $"0x{ComputeCrc16Modbus(data):X4}",
                Crc32 = $"0x{ComputeCrc32(data):X8}"
            };
        }

        private static string ComputeHash(HashAlgorithm hashAlgorithm, byte[] data)
        {
            using (hashAlgorithm)
                return BitConverter.ToString(hashAlgorithm.ComputeHash(data)).Replace("-", "");
        }

        private static ushort ComputeCrc16Modbus(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }
    }
}
