using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace llcom_plus.Tools
{
    /// <summary>
    /// WCH's synchronous interface only. Never resets the composite USB device
    /// or opens either UART interface. See the official CH347DLL.H (pack(1)).
    /// </summary>
    internal sealed class Ch347Native : ICh347Backend
    {
        private static readonly object NativeSync = new object();
        private static readonly Dictionary<uint, Ch347Native> Owners = new Dictionary<uint, Ch347Native>();
        // The SDK owns callback pointers. Keep them rooted for process lifetime,
        // including when a removed device prevents successful unregistration.
        private static readonly List<DeviceNotify> CallbackRoots = new List<DeviceNotify>();
        private static NativeApi sharedApi;
        private Ch347DeviceInfo openedDevice;
        private readonly JtagSession jtagSession = new JtagSession();
        private readonly DeviceNotify notification;
        private bool notificationRooted;
        private int presenceCheckNeeded;
        private int removed;
        private bool disposed;

        public Ch347Native()
        {
            notification = status =>
            {
                // Vendor notifications are filtered by VID/PID, not physical
                // instance. Another matching adapter must not retire this one.
                if (status == 0 || status == 1) Volatile.Write(ref presenceCheckNeeded, 1);
            };
        }

        public IReadOnlyList<Ch347DeviceInfo> Enumerate()
        {
            lock (NativeSync)
            {
                ThrowIfDisposed();
                var api = GetApi();
                var devices = new List<Ch347DeviceInfo>();
                // CH347DLL.H defines mCH341_MAX_NUMBER as 32. Gaps are permitted.
                for (uint index = 0; index < 32; index++)
                {
                    if (Owners.TryGetValue(index, out var owner))
                    {
                        if (owner.openedDevice != null && owner.IsPresentCore(owner.openedDevice))
                            devices.Add(owner.openedDevice.Clone());
                        continue;
                    }
                    if (api.OpenDevice(index) == new IntPtr(-1)) continue;
                    try
                    {
                        var info = ReadInformation(api, index);
                        if (info != null && info.SupportsAnyBus && info.Identity.Length != 0)
                            devices.Add(info);
                    }
                    finally { api.CloseDevice(index); }
                }
                return devices;
            }
        }

        public Ch347DeviceInfo Open(Ch347DeviceInfo expectedDevice)
        {
            if (expectedDevice == null || expectedDevice.Identity.Length == 0)
                throw new ArgumentException("Refresh and select a CH347 device before connecting.", nameof(expectedDevice));
            lock (NativeSync)
            {
                ThrowIfDisposed();
                if (openedDevice != null) throw new InvalidOperationException("A CH347 device is already connected.");
                if (Owners.ContainsKey(expectedDevice.Index))
                    throw new InvalidOperationException("This CH347 interface is already in use by another bus tool.");
                var api = GetApi();
                if (api.OpenDevice(expectedDevice.Index) == new IntPtr(-1))
                    throw new IOException("Cannot open the CH347 interface. Check its mode, driver and whether another program is using it.");
                try
                {
                    var actual = ReadInformation(api, expectedDevice.Index);
                    if (actual == null || !actual.SupportsAnyBus || actual.Identity != expectedDevice.Identity ||
                        actual.Mode != expectedDevice.Mode || actual.FunctionType != expectedDevice.FunctionType)
                        throw new IOException("The selected CH347 interface changed. Refresh the device list and connect again.");
                    if (!api.SetTimeout(actual.Index, 1000, 1000))
                        throw new IOException("CH347 could not set a finite USB timeout.");
                    openedDevice = actual;
                    jtagSession.Ready = false;
                    Volatile.Write(ref removed, 0);
                    Volatile.Write(ref presenceCheckNeeded, 0);
                    if (!notificationRooted)
                    {
                        CallbackRoots.Add(notification);
                        notificationRooted = true;
                    }
                    var filter = actual.DeviceId ?? "";
                    var vendorOffset = filter.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
                    if (vendorOffset >= 0) filter = filter.Substring(vendorOffset);
                    if (!api.SetDeviceNotify(actual.Index, filter, notification))
                        throw new IOException("CH347 hot-plug monitoring could not be enabled; the connection was closed.");
                    Owners.Add(actual.Index, this);
                    return actual.Clone();
                }
                catch
                {
                    if (openedDevice != null) api.SetDeviceNotify(expectedDevice.Index, null, null);
                    openedDevice = null;
                    api.CloseDevice(expectedDevice.Index);
                    throw;
                }
            }
        }

        public void Close()
        {
            lock (NativeSync) { CloseCore(); }
        }

        public bool IsPresent(Ch347DeviceInfo expectedDevice)
        {
            lock (NativeSync)
            {
                return !disposed && IsPresentCore(expectedDevice);
            }
        }

        public Ch347TransferResult Transfer(Ch347TransferRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            request = request.Clone();
            request.Validate();
            lock (NativeSync)
            {
                ThrowIfDisposed();
                if (openedDevice == null || !IsPresentCore(openedDevice))
                    throw new IOException("CH347 was disconnected. Reconnect explicitly before sending another transaction.");
                if (!openedDevice.SupportsProtocol(request.Protocol))
                    throw new InvalidOperationException("This CH347 interface does not support " + request.Protocol + " in its current mode.");
                var api = GetApi();
                var index = openedDevice.Index;
                if (!api.SetTimeout(index, (uint)request.TimeoutMs, (uint)request.TimeoutMs))
                {
                    jtagSession.Ready = false;
                    throw new IOException("CH347 could not configure the requested USB timeout; no transaction was sent.");
                }
                var timer = Stopwatch.StartNew();
                Ch347TransferResult result;
                if (request.Protocol == Ch347BusProtocol.Jtag)
                    result = TransferJtag(api, index, request, jtagSession);
                else
                {
                    // Other bus initializers can reconfigure shared adapter pins.
                    // Never assume the old TAP/IR state survives a protocol change.
                    jtagSession.Ready = false;
                    result = request.Protocol == Ch347BusProtocol.I2c
                        ? TransferI2c(api, index, request, openedDevice.Mode <= 3)
                        : TransferSpi(api, index, request);
                }
                timer.Stop();
                result.Duration = timer.Elapsed;
                if (!IsPresentCore(openedDevice))
                    throw new IOException("CH347 disconnected during the transaction. Its completion is unknown; it was not retried.");
                return result;
            }
        }

        public void Dispose()
        {
            lock (NativeSync)
            {
                if (disposed) return;
                CloseCore();
                disposed = true;
            }
        }

        private static Ch347TransferResult TransferI2c(NativeApi api, uint index, Ch347TransferRequest request, bool isCh347T)
        {
            if (!api.I2cSet(index, (uint)request.I2cSpeedCode))
                throw new IOException("CH347 I2C configuration failed; no transaction was sent.");
            // Do not inherit unsafe push-pull or ignore-NACK settings from a
            // previous tool. These optional controls are CH347T-specific.
            if (isCh347T && ((api.I2cDriverMode != null && !api.I2cDriverMode(index, 0)) ||
                (api.I2cIgnoreNack != null && !api.I2cIgnoreNack(index, request.ProbeOnly ? (byte)1 : (byte)0))))
                throw new IOException("CH347 could not configure open-drain / NACK handling; no transaction was sent.");
            var write = new byte[request.WriteData.Length + 1];
            // With no write payload, bit 0 starts a direct read. With a payload,
            // StreamI2C supplies the repeated START and read address itself.
            write[0] = (byte)((request.I2cAddress << 1) |
                (request.ReadLength > 0 && request.WriteData.Length == 0 ? 1 : 0));
            Buffer.BlockCopy(request.WriteData, 0, write, 1, request.WriteData.Length);
            var read = new byte[request.ReadLength];
            uint acknowledgements = 0;
            var ok = WithPinnedBuffers(write, read, (writePointer, readPointer) =>
                api.StreamI2cRetAck(index, (uint)write.Length, writePointer,
                    (uint)read.Length, readPointer, out acknowledgements));
            // The ACK total can also include the repeated address/read phase.
            // The write phase must at least have acknowledged every sent byte.
            var acknowledged = acknowledgements >= write.Length;
            var success = ok && acknowledged;
            return new Ch347TransferResult
            {
                Success = success,
                ReadData = success ? read : Array.Empty<byte>(),
                AckCount = ok ? (int?)Math.Min(acknowledgements, int.MaxValue) : null,
                Message = !ok ? "CH347 I2C transfer failed or timed out. Completion is unknown; no automatic retry." :
                    !acknowledged ? "I2C NACK: the addressed device did not acknowledge the complete write phase." :
                    request.ProbeOnly ? "I2C address acknowledged." : "I2C transaction completed."
            };
        }

        private static Ch347TransferResult TransferSpi(NativeApi api, uint index, Ch347TransferRequest request)
        {
            var cs = (uint)(0x80 | request.SpiChipSelect);
            var config = new SpiConfiguration
            {
                Mode = (byte)request.SpiMode,
                Clock = (byte)request.SpiClockCode,
                ByteOrder = (byte)(request.SpiLsbFirst ? 0 : 1),
                OutDefaultData = request.SpiDummyByte,
                ChipSelect = cs,
                AutoDeactivateCs = 1
            };
            if (!WithNativeStructure(config, pointer => api.SpiInit(index, pointer)))
                throw new IOException("CH347 SPI configuration failed; no transaction was sent.");
            var length = request.SpiOperation == Ch347SpiOperation.WriteThenRead
                ? request.WriteData.Length + request.ReadLength
                : Math.Max(request.WriteData.Length, request.ReadLength);
            var buffer = new byte[length];
            for (var i = 0; i < buffer.Length; i++) buffer[i] = request.SpiDummyByte;
            Buffer.BlockCopy(request.WriteData, 0, buffer, 0, request.WriteData.Length);
            // One native call retains CS across command and dummy clocks.
            var ok = WithPinnedBuffers(buffer, null, (pointer, _) =>
                api.SpiWriteRead(index, cs, (uint)buffer.Length, pointer));
            var read = ok ? new byte[request.ReadLength] : Array.Empty<byte>();
            if (ok && read.Length > 0)
                Buffer.BlockCopy(buffer, request.SpiOperation == Ch347SpiOperation.WriteThenRead
                    ? request.WriteData.Length : 0, read, 0, read.Length);
            return new Ch347TransferResult
            {
                Success = ok,
                ReadData = read,
                Message = ok ? "SPI transaction completed under one chip selection." :
                    "CH347 SPI transfer failed or timed out. Completion is unknown; no automatic retry."
            };
        }

        internal sealed class JtagSession
        {
            public bool Ready;
            public int ClockCode;
        }

        private static Ch347TransferResult TransferJtag(NativeApi api, uint index,
            Ch347TransferRequest request, JtagSession session)
        {
            if (api.JtagInit == null || api.JtagSwitchTapState == null || api.JtagIoScan == null)
            {
                session.Ready = false;
                throw new Ch347JtagSdkUnavailableException();
            }
            if (request.JtagOperation != Ch347JtagOperation.Reset &&
                (!session.Ready || session.ClockCode != request.JtagClockCode))
            {
                session.Ready = false;
                throw new Ch347JtagResetRequiredException();
            }

            // INIT itself clocks Test-Logic-Reset -> Idle in SDK 1.50. Calling it
            // before every shift would silently discard the IR chosen by the user.
            // Only an explicit Reset is allowed to initialize / reinitialize JTAG.
            session.Ready = false;
            if (request.JtagOperation == Ch347JtagOperation.Reset)
            {
                var reset = api.JtagInit(index, (byte)request.JtagClockCode) &&
                    api.JtagSwitchTapState(index, 0) && api.JtagSwitchTapState(index, 1);
                if (reset) { session.ClockCode = request.JtagClockCode; session.Ready = true; }
                return new Ch347TransferResult
                {
                    Success = reset,
                    Message = reset ? "JTAG TAP reset completed; Run-Test/Idle. No TRST or system reset was asserted." :
                        "JTAG reset failed. TAP state is unknown; no automatic retry."
                };
            }

            var buffer = (byte[])request.WriteData.Clone();
            // SwitchTapStateEx is indexed; the old non-Ex export relies on an
            // implicit device and must never be used when multiple adapters exist.
            var entered = api.JtagSwitchTapState(index,
                (byte)(request.JtagOperation == Ch347JtagOperation.ShiftIr ? 4 : 2));
            var shifted = entered && WithPinnedBuffers(buffer, null, (pointer, _) =>
                api.JtagIoScan(index, pointer, (uint)request.JtagBitLength, true, true));
            // IoScanT clocks the last data bit with TMS=1, reaching Exit1-IR/DR.
            // State 6 performs Update-IR/DR -> Idle without an extra shifted bit.
            var success = shifted && api.JtagSwitchTapState(index, 6);
            if (success)
            {
                var tailBits = request.JtagBitLength & 7;
                if (tailBits != 0) buffer[buffer.Length - 1] &= (byte)((1 << tailBits) - 1);
                session.Ready = true;
            }
            return new Ch347TransferResult
            {
                Success = success,
                ReadData = success ? buffer : Array.Empty<byte>(),
                Message = success ? "JTAG " + request.JtagOperation + ": " + request.JtagBitLength + " bits, LSB first; returned to Run-Test/Idle." :
                    "JTAG shift failed. TAP state and target effects are unknown; reset explicitly before continuing. No automatic retry."
            };
        }

        private bool IsPresentCore(Ch347DeviceInfo expectedDevice)
        {
            if (openedDevice == null || expectedDevice == null || Volatile.Read(ref removed) != 0 ||
                openedDevice.Index != expectedDevice.Index || openedDevice.Identity != expectedDevice.Identity)
                return false;
            // NORMAL only: no opening, resets, removal cancellation or phantom
            // nodes. Unknown path formats fall back to the SDK handle identity.
            Interlocked.Exchange(ref presenceCheckNeeded, 0);
            if (IsDevicePathPresent(openedDevice.DevicePath) == false)
            {
                Volatile.Write(ref removed, 1);
                return false;
            }
            var current = ReadInformation(GetApi(), openedDevice.Index);
            if (current != null && current.Identity == openedDevice.Identity &&
                current.Mode == openedDevice.Mode && current.FunctionType == openedDevice.FunctionType)
                return true;
            Volatile.Write(ref removed, 1);
            return false;
        }

        private void CloseCore()
        {
            jtagSession.Ready = false;
            if (openedDevice == null) return;
            var index = openedDevice.Index;
            Volatile.Write(ref removed, 1);
            try { GetApi().SetDeviceNotify(index, null, null); }
            finally
            {
                try { GetApi().CloseDevice(index); }
                finally
                {
                    Owners.Remove(index);
                    openedDevice = null;
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(Ch347Native));
        }

        private static Ch347DeviceInfo ReadInformation(NativeApi api, uint index)
        {
            // New SDKs may append fields. Give the SDK a generously sized,
            // zeroed buffer, then decode only the documented ABI prefix.
            var buffer = Marshal.AllocHGlobal(4096);
            DeviceInformation raw;
            try
            {
                Marshal.Copy(new byte[4096], 0, buffer, 4096);
                if (!api.GetDeviceInfor(index, buffer)) return null;
                raw = (DeviceInformation)Marshal.PtrToStructure(buffer, typeof(DeviceInformation));
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return new Ch347DeviceInfo
            {
                Index = index,
                DevicePath = Decode(raw.DevicePath),
                DeviceId = Decode(raw.DeviceId),
                Product = Decode(raw.ProductString),
                Mode = raw.Mode,
                FunctionType = raw.FunctionType
            };
        }

        private static string Decode(byte[] bytes)
        {
            var length = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length).Trim();
        }

        private static bool WithPinnedBuffers(byte[] first, byte[] second, Func<IntPtr, IntPtr, bool> operation)
        {
            var firstHandle = default(GCHandle);
            var secondHandle = default(GCHandle);
            try
            {
                var firstPointer = IntPtr.Zero;
                var secondPointer = IntPtr.Zero;
                if (first != null && first.Length > 0)
                {
                    firstHandle = GCHandle.Alloc(first, GCHandleType.Pinned);
                    firstPointer = firstHandle.AddrOfPinnedObject();
                }
                if (second != null && second.Length > 0)
                {
                    secondHandle = GCHandle.Alloc(second, GCHandleType.Pinned);
                    secondPointer = secondHandle.AddrOfPinnedObject();
                }
                return operation(firstPointer, secondPointer);
            }
            finally
            {
                if (secondHandle.IsAllocated) secondHandle.Free();
                if (firstHandle.IsAllocated) firstHandle.Free();
            }
        }

        private static bool WithNativeStructure<T>(T value, Func<IntPtr, bool> operation) where T : struct
        {
            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                Marshal.Copy(new byte[4096], 0, buffer, 4096);
                Marshal.StructureToPtr(value, buffer, false);
                return operation(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static string ResolveLibraryPath() => Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(typeof(Ch347Native).Assembly.Location), "CH347", "CH347DLL.dll"));

        private static bool? IsDevicePathPresent(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return null;
            var parts = path.Substring(4).Split('#');
            if (parts.Length < 4 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0 ||
                !Guid.TryParse(parts[3], out _)) return null;
            var instance = parts[0] + "\\" + parts[1] + "\\" + parts[2];
            try
            {
                var status = LocateDeviceNode(out _, instance, 0);
                if (status == 0) return true; // CR_SUCCESS
                if (status == 0x0d) return false; // CR_NO_SUCH_DEVNODE
                return null;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        private static NativeApi GetApi()
        {
            // Called only under NativeSync. A failed load can be retried after
            // installing the matching redistributable, without restarting.
            return sharedApi ?? (sharedApi = new NativeApi());
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct SpiConfiguration
        {
            public byte Mode;
            public byte Clock;
            public byte ByteOrder;
            public ushort WriteReadInterval;
            public byte OutDefaultData;
            public uint ChipSelect;
            public byte Cs1Polarity;
            public byte Cs2Polarity;
            public ushort AutoDeactivateCs;
            public ushort ActiveDelay;
            public uint DelayDeactivate;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct DeviceInformation
        {
            public byte Index;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)] public byte[] DevicePath;
            public byte UsbClass;
            public byte FunctionType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] DeviceId;
            public byte Mode;
            public IntPtr DeviceHandle;
            public ushort BulkOutEndpointSize;
            public ushort BulkInEndpointSize;
            public byte UsbSpeedType;
            public byte InterfaceNumber;
            public byte DataUpEndpoint;
            public byte DataDownEndpoint;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] ProductString;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] ManufacturerString;
            public uint WriteTimeout;
            public uint ReadTimeout;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] FunctionDescription;
            public byte FirmwareVersion;

            public static DeviceInformation Create() => new DeviceInformation
            {
                DevicePath = new byte[260], DeviceId = new byte[64], ProductString = new byte[64],
                ManufacturerString = new byte[64], FunctionDescription = new byte[64]
            };
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr OpenDevice(uint index);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool CloseDevice(uint index);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool GetDeviceInformation(uint index, IntPtr information);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SetTimeout(uint index, uint writeTimeout, uint readTimeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DeviceNotify(uint status);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SetDeviceNotify(uint index, [MarshalAs(UnmanagedType.LPStr)] string deviceId, DeviceNotify callback);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool I2cSet(uint index, uint mode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool I2cByteSetting(uint index, byte mode);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool StreamI2cRetAck(uint index, uint writeLength, IntPtr writeBuffer,
            uint readLength, IntPtr readBuffer, out uint ackCount);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SpiInit(uint index, IntPtr configuration);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SpiWriteRead(uint index, uint chipSelect, uint length, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool JtagInit(uint index, byte clock);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool JtagSwitchTapState(uint index, byte state);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool JtagIoScan(uint index, IntPtr buffer, uint bitLength,
            [MarshalAs(UnmanagedType.Bool)] bool isRead, [MarshalAs(UnmanagedType.Bool)] bool isLast);

        private sealed class NativeApi
        {
            // Keep the module loaded while SDK notification threads can run.
            private readonly IntPtr module;
            public readonly OpenDevice OpenDevice;
            public readonly CloseDevice CloseDevice;
            public readonly GetDeviceInformation GetDeviceInfor;
            public readonly SetTimeout SetTimeout;
            public readonly SetDeviceNotify SetDeviceNotify;
            public readonly I2cSet I2cSet;
            public readonly I2cByteSetting I2cDriverMode;
            public readonly I2cByteSetting I2cIgnoreNack;
            public readonly StreamI2cRetAck StreamI2cRetAck;
            public readonly SpiInit SpiInit;
            public readonly SpiWriteRead SpiWriteRead;
            public readonly JtagInit JtagInit;
            public readonly JtagSwitchTapState JtagSwitchTapState;
            public readonly JtagIoScan JtagIoScan;

            public NativeApi()
            {
                var path = ResolveLibraryPath();
                if (!File.Exists(path))
                    throw new FileNotFoundException("CH347 SDK is missing. Install the " +
                        (IntPtr.Size == 8 ? "64-bit" : "32-bit") + " CH347DLL.dll in the application's CH347 folder.", path);
                // Never search the current directory or PATH for the vendor DLL
                // or its dependencies. Only its own folder and System32 qualify.
                module = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
                if (module == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException("Cannot load the " + (IntPtr.Size == 8 ? "64-bit" : "32-bit") +
                        " CH347 SDK. Check DLL architecture and its dependencies. " + new Win32Exception(error).Message);
                }
                try
                {
                    OpenDevice = Get<OpenDevice>("CH347OpenDevice");
                    CloseDevice = Get<CloseDevice>("CH347CloseDevice");
                    GetDeviceInfor = Get<GetDeviceInformation>("CH347GetDeviceInfor");
                    SetTimeout = Get<SetTimeout>("CH347SetTimeout");
                    SetDeviceNotify = Get<SetDeviceNotify>("CH347SetDeviceNotify");
                    I2cSet = Get<I2cSet>("CH347I2C_Set");
                    I2cDriverMode = GetOptional<I2cByteSetting>("CH347I2C_SetDriverMode");
                    I2cIgnoreNack = GetOptional<I2cByteSetting>("CH347I2C_SetIgnoreNack");
                    StreamI2cRetAck = Get<StreamI2cRetAck>("CH347StreamI2C_RetACK");
                    SpiInit = Get<SpiInit>("CH347SPI_Init");
                    SpiWriteRead = Get<SpiWriteRead>("CH347SPI_WriteRead");
                    JtagInit = GetOptional<JtagInit>("CH347Jtag_INIT");
                    JtagSwitchTapState = GetOptional<JtagSwitchTapState>("CH347Jtag_SwitchTapStateEx");
                    JtagIoScan = GetOptional<JtagIoScan>("CH347Jtag_IoScanT");
                }
                catch { FreeLibrary(module); throw; }
            }

            private T GetOptional<T>(string name) where T : class
            {
                var pointer = GetProcAddress(module, name);
                return pointer == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
            }

            private T Get<T>(string name) where T : class
            {
                var pointer = GetProcAddress(module, name);
                if (pointer == IntPtr.Zero)
                    throw new EntryPointNotFoundException("CH347 SDK does not provide " + name + ". Replace it with the supported official SDK.");
                return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint LocateDeviceNode(out uint deviceInstance, string instanceId, uint flags);
    }
}
