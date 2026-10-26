using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace llcom_plus.Tools
{
    internal enum Ch347BusProtocol { I2c, Spi, Jtag }
    internal enum Ch347SpiOperation { WriteOnly, ReadOnly, WriteThenRead, Exchange }
    internal enum Ch347JtagOperation { Reset, ShiftIr, ShiftDr }

    internal sealed class Ch347JtagResetRequiredException : InvalidOperationException
    {
        public Ch347JtagResetRequiredException() : base("JTAG requires an explicit TAP reset after connecting, changing clock/protocol or an error. Reset, then load the target IR again before shifting DR.") { }
    }

    internal sealed class Ch347JtagSdkUnavailableException : NotSupportedException
    {
        public Ch347JtagSdkUnavailableException() : base("The CH347 SDK lacks the indexed JTAG interfaces. I2C/SPI are unaffected; install the supported SDK to use JTAG.") { }
    }

    internal sealed class Ch347DeviceInfo
    {
        public uint Index { get; set; }
        public string DevicePath { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string Product { get; set; } = "";
        public byte Mode { get; set; }
        public byte FunctionType { get; set; }
        public string Identity => (DevicePath ?? "").Trim().ToUpperInvariant();
        public bool SupportsI2c => FunctionType == 1 || FunctionType == 2 || FunctionType == 3;
        public bool SupportsSpi => FunctionType == 1 || FunctionType == 3;
        public bool SupportsJtag => FunctionType == 2 || FunctionType == 3;
        public bool SupportsAnyBus => SupportsI2c || SupportsSpi || SupportsJtag;
        public bool SupportsProtocol(Ch347BusProtocol protocol)
        {
            switch (protocol)
            {
                case Ch347BusProtocol.I2c: return SupportsI2c;
                case Ch347BusProtocol.Spi: return SupportsSpi;
                case Ch347BusProtocol.Jtag: return SupportsJtag;
                default: return false;
            }
        }
        public override string ToString() => $"CH347 #{Index} · M{Mode} · {Product}";
        internal Ch347DeviceInfo Clone() => (Ch347DeviceInfo)MemberwiseClone();
    }

    // A transaction is not a serial byte stream: its addressing, read phase and
    // chip-select boundaries must survive saving, queueing and scripting intact.
    internal sealed class Ch347TransferRequest
    {
        internal const int MaximumTransferBytes = 4096;
        public Ch347BusProtocol Protocol { get; set; }
        public int I2cAddress { get; set; } = 0x50; // Seven-bit address, never an eight-bit wire address.
        public int I2cSpeedCode { get; set; } = 1;
        public bool ProbeOnly { get; set; }
        public byte[] WriteData { get; set; } = Array.Empty<byte>();
        public int ReadLength { get; set; }
        public int TimeoutMs { get; set; } = 1000;
        public int SpiMode { get; set; }
        public int SpiClockCode { get; set; } = 7;
        public bool SpiLsbFirst { get; set; }
        public int SpiChipSelect { get; set; }
        public byte SpiDummyByte { get; set; } = 0xff;
        public Ch347SpiOperation SpiOperation { get; set; } = Ch347SpiOperation.WriteThenRead;
        public Ch347JtagOperation JtagOperation { get; set; } = Ch347JtagOperation.ShiftDr;
        public int JtagClockCode { get; set; }
        public int JtagBitLength { get; set; } = 32;

        public Ch347TransferRequest Clone()
        {
            var copy = (Ch347TransferRequest)MemberwiseClone();
            copy.WriteData = WriteData == null ? Array.Empty<byte>() : (byte[])WriteData.Clone();
            return copy;
        }

        public void Validate()
        {
            var length = WriteData?.Length ?? 0;
            if (!Enum.IsDefined(typeof(Ch347BusProtocol), Protocol) ||
                length > MaximumTransferBytes || ReadLength < 0 || ReadLength > MaximumTransferBytes ||
                TimeoutMs < 100 || TimeoutMs > 10000)
                throw new ArgumentException("Invalid bus transaction: lengths must be 0–4096 bytes and timeout 100–10000 ms.");
            if (Protocol == Ch347BusProtocol.I2c)
            {
                if (I2cAddress < 0x08 || I2cAddress > 0x77 || I2cSpeedCode < 0 || I2cSpeedCode > 3 ||
                    length >= MaximumTransferBytes ||
                    (ProbeOnly ? length != 0 || ReadLength != 0 : length == 0 && ReadLength == 0))
                    throw new ArgumentException("I2C requires a seven-bit address 08–77, a supported speed and data or a read length.");
            }
            else if (Protocol == Ch347BusProtocol.Spi)
            {
                if (ProbeOnly || SpiMode < 0 || SpiMode > 3 || SpiClockCode < 1 || SpiClockCode > 7 ||
                    SpiChipSelect < 0 || SpiChipSelect > 1 ||
                    !Enum.IsDefined(typeof(Ch347SpiOperation), SpiOperation))
                    throw new ArgumentException("Invalid SPI mode, clock, chip select or operation.");
                if ((SpiOperation == Ch347SpiOperation.WriteOnly && (length == 0 || ReadLength != 0)) ||
                    (SpiOperation == Ch347SpiOperation.ReadOnly && (length != 0 || ReadLength == 0)) ||
                    (SpiOperation == Ch347SpiOperation.WriteThenRead &&
                        (length == 0 || ReadLength == 0 || length + ReadLength > MaximumTransferBytes)) ||
                    (SpiOperation == Ch347SpiOperation.Exchange && (length == 0 || ReadLength != length)))
                    throw new ArgumentException("SPI write/read phases are inconsistent with the selected operation.");
            }
            else
            {
                if (ProbeOnly || JtagClockCode < 0 || JtagClockCode > 5 ||
                    !Enum.IsDefined(typeof(Ch347JtagOperation), JtagOperation))
                    throw new ArgumentException("Invalid JTAG clock or operation.");
                if (JtagOperation == Ch347JtagOperation.Reset)
                {
                    if (JtagBitLength != 0 || length != 0 || ReadLength != 0)
                        throw new ArgumentException("JTAG TAP reset does not accept shift data or a bit length.");
                }
                else
                {
                    if (JtagBitLength < 1 || JtagBitLength > MaximumTransferBytes * 8 ||
                        length != (JtagBitLength + 7) / 8 || ReadLength != length)
                        throw new ArgumentException("JTAG shift requires 1–32768 bits and equally sized TX/RX buffers of ceil(bits / 8) bytes.");
                    // Shifts are LSB-first: the unused high bits of a final
                    // partial byte must not silently become extra target data.
                    var finalBits = JtagBitLength & 7;
                    if (finalBits != 0 && (WriteData[length - 1] & ~((1 << finalBits) - 1)) != 0)
                        throw new ArgumentException("Unused high bits in the final JTAG byte must be zero.");
                }
            }
        }

        internal static byte[] ParseHex(string text)
        {
            text = text ?? "";
            if (text.Length > MaximumTransferBytes * 4)
                throw new ArgumentException("HEX input is too long.");
            var hex = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if ((hex.Length & 1) != 0 || hex.Length > MaximumTransferBytes * 2 || hex.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("Enter complete HEX bytes, for example 01 A0 FF.");
            var data = new byte[hex.Length / 2];
            for (var i = 0; i < data.Length; i++) data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return data;
        }

        internal static string ToHex(byte[] data) => BitConverter.ToString(data ?? Array.Empty<byte>()).Replace('-', ' ');
    }

    internal sealed class Ch347TransferResult
    {
        public bool Success { get; set; }
        public byte[] ReadData { get; set; } = Array.Empty<byte>();
        public int? AckCount { get; set; }
        public TimeSpan Duration { get; set; }
        public string Message { get; set; } = "";
    }

    internal sealed class Ch347BusLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string DeviceIdentity { get; set; } = "";
        public string Text { get; set; } = "";
    }

    internal interface ICh347Backend : IDisposable
    {
        IReadOnlyList<Ch347DeviceInfo> Enumerate();
        Ch347DeviceInfo Open(Ch347DeviceInfo expectedDevice);
        void Close();
        bool IsPresent(Ch347DeviceInfo expectedDevice);
        Ch347TransferResult Transfer(Ch347TransferRequest request);
    }
}
