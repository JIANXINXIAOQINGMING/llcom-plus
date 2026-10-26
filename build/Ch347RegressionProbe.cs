using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using llcom_plus.Tools;

// Compile as LlcomCh347RegressionProbe.dll against the built application.
// Every service instance receives FakeBackend. No real backend constructor,
// SDK delegate, device enumeration, USB open, Global, App or UI is invoked.
public static class Ch347RegressionProbe
{
    private const int WaitMs = 5000;

    public static string[] Run(string outputDirectory, string isolatedDirectory)
    {
        var passed = new List<string>();
        ValidateRequests();
        passed.Add("Strict HEX, seven-bit I2C addresses, reserved addresses, lengths, timeouts and SPI parameters.");
        ValidateJtagRequests();
        passed.Add("JTAG reset/IR/DR requests enforce clock, exact bit/byte counts, 32768-bit bounds and zero unused high bits.");
        TestProtocolCapabilities();
        passed.Add("JTAG/I2C and three-bus interfaces connect; unsupported protocols and unknown function types are rejected before I/O.");
        TestQueuedCopiesAndSerialExecution();
        passed.Add("Queued transactions retain independent bytes/settings and execute serially without overlapping backend calls.");
        TestQueuedJtagCopies();
        passed.Add("Queued JTAG operations retain independent TDI bytes, clock and exact bit length without overlapping backend calls.");
        TestQueueBound();
        passed.Add("A blocked transfer fills a bounded queue; excess work is rejected before reaching the backend.");
        TestCancellation();
        passed.Add("Pre-cancelled, queued and in-flight cancellations never repeat a transfer or allow backend overlap.");
        TestConnectionGeneration();
        passed.Add("Disconnect invalidates queued work; an old transaction cannot run against the replacement device.");
        TestScan();
        passed.Add("I2C scan probes exactly 08-77, reports only ACKs and remains connected through NACKs.");
        TestPresets(isolatedDirectory);
        passed.Add("Bus presets round-trip transaction settings in an isolated file and reject invalid updates without altering saved data.");
        TestNativeAbiAndLibrary(outputDirectory, isolatedDirectory);
        passed.Add("Official SDK exports, packed 20-byte SPI / 541-or-545-byte device ABI, and a library path independent of cwd.");
        TestNativeTransactions();
        passed.Add("Mock native delegates verify I2C wire addresses, NACK detection, SPI command/dummy clocks and same-CS read slicing.");
        TestNativeJtagTransactions();
        passed.Add("Mock JTAG delegates enforce explicit reset, exact LSB-first bit counts, IR/DR TAP paths, masked TDO and failure invalidation without retries.");
        return passed.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Wait(Task task)
    {
        // Wait via IAsyncResult so a completed fault is unwrapped only once.
        Assert(((IAsyncResult)task).AsyncWaitHandle.WaitOne(WaitMs), "A regression check exceeded its bounded wait.");
        task.GetAwaiter().GetResult();
    }

    private static Exception Failure(Task task)
    {
        try { Wait(task); return null; }
        catch (Exception ex) { return ex; }
    }

    private static Task<T> Capture<T>(Func<Task<T>> action)
    {
        try { return action(); }
        catch (Exception ex)
        {
            var result = new TaskCompletionSource<T>();
            result.SetException(ex);
            return result.Task;
        }
    }

    private static void Reject(Action action, string description)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid input was accepted: " + description);
    }

    private static Ch347DeviceInfo Device(string name, uint index)
    {
        return new Ch347DeviceInfo { Index = index, DevicePath = "fake://" + name, DeviceId = name,
            Product = "Hardware-free CH347 fake", Mode = 1, FunctionType = 1 };
    }

    private static Ch347TransferRequest I2c(byte marker)
    {
        return new Ch347TransferRequest { Protocol = Ch347BusProtocol.I2c,
            I2cAddress = 0x50, WriteData = new byte[] { marker }, TimeoutMs = 500 };
    }

    private static Ch347DeviceInfo JtagDevice(string name, uint index)
    {
        var device = Device(name, index);
        device.Mode = 3;
        device.FunctionType = 2; // Official JTAG + I2C interface, not a JTAG-only fictitious type.
        return device;
    }

    private static Ch347TransferRequest Jtag(int bits)
    {
        var length = (bits + 7) / 8;
        return new Ch347TransferRequest { Protocol = Ch347BusProtocol.Jtag,
            JtagOperation = Ch347JtagOperation.ShiftDr, JtagClockCode = 0,
            JtagBitLength = bits, WriteData = new byte[length], ReadLength = length, TimeoutMs = 500 };
    }

    private static void ValidateRequests()
    {
        Assert(Ch347TransferRequest.ParseHex("01 aB\tFF\r\n00").SequenceEqual(new byte[] { 1, 0xab, 0xff, 0 }),
            "Strict HEX did not preserve complete bytes and whitespace.");
        Assert(Ch347TransferRequest.ParseHex(null).Length == 0, "Empty HEX should yield an empty payload.");
        foreach (var text in new[] { "0x12", "A", "1 23", "GG", "12-34", "12,34", "12:34", "1Z", "１２" })
            Reject(delegate { Ch347TransferRequest.ParseHex(text); }, "HEX " + text);
        Assert(Ch347TransferRequest.ParseHex(new string('a', 8192)).Length == 4096, "Maximum HEX length was rejected.");
        Reject(delegate { Ch347TransferRequest.ParseHex(new string('a', 8194)); }, "HEX above 4096 bytes");
        Reject(delegate { Ch347TransferRequest.ParseHex(new string(' ', 16385)); }, "Unbounded HEX whitespace");

        foreach (var address in new[] { -1, 0, 7, 0x78, 0x7f, 0x80, 0xa0, 255 })
        {
            var request = I2c(1);
            request.I2cAddress = address;
            Reject(request.Validate, "I2C address " + address);
        }
        foreach (var address in new[] { 0x08, 0x50, 0x77 })
        {
            var request = I2c(1);
            request.I2cAddress = address;
            request.Validate();
        }
        var probe = I2c(0);
        probe.WriteData = new byte[0];
        Reject(probe.Validate, "empty ordinary I2C transaction");
        probe.ProbeOnly = true;
        probe.Validate();
        probe.ReadLength = 1;
        Reject(probe.Validate, "probe carrying a read phase");
        var maximum = I2c(0);
        maximum.WriteData = new byte[4095]; // SDK wire buffer also includes the address.
        maximum.ReadLength = 4096;
        maximum.TimeoutMs = 10000;
        maximum.Validate();
        maximum.TimeoutMs = 100;
        maximum.Validate();
        var invalidI2c = new Action<Ch347TransferRequest>[] {
            r => r.WriteData = new byte[4096], r => r.ReadLength = -1,
            r => r.ReadLength = 4097, r => r.TimeoutMs = 99,
            r => r.TimeoutMs = 10001, r => r.I2cSpeedCode = -1,
            r => r.I2cSpeedCode = 4, r => r.Protocol = (Ch347BusProtocol)42
        };
        foreach (var edit in invalidI2c)
        {
            var request = I2c(1);
            edit(request);
            Reject(request.Validate, "I2C length, timeout, speed or protocol");
        }

        var spi = new Ch347TransferRequest { Protocol = Ch347BusProtocol.Spi,
            WriteData = new byte[] { 0x9f }, ReadLength = 3, SpiOperation = Ch347SpiOperation.WriteThenRead };
        spi.Validate();
        var invalidSpi = new Action<Ch347TransferRequest>[] {
            r => r.SpiMode = -1, r => r.SpiMode = 4,
            r => r.SpiClockCode = 0, r => r.SpiClockCode = 8,
            r => r.SpiChipSelect = -1, r => r.SpiChipSelect = 2,
            r => r.SpiOperation = (Ch347SpiOperation)99, r => r.ProbeOnly = true,
            r => r.WriteData = new byte[0], r => r.ReadLength = 0,
            r => { r.WriteData = new byte[4096]; r.ReadLength = 1; },
            r => r.SpiOperation = Ch347SpiOperation.WriteOnly,
            r => r.SpiOperation = Ch347SpiOperation.ReadOnly,
            r => r.SpiOperation = Ch347SpiOperation.Exchange
        };
        foreach (var edit in invalidSpi)
        {
            var request = spi.Clone();
            edit(request);
            Reject(request.Validate, "SPI mode, clock, CS, operation or phase length");
        }
        spi.SpiOperation = Ch347SpiOperation.WriteOnly;
        spi.ReadLength = 0;
        spi.Validate();
        spi.SpiOperation = Ch347SpiOperation.ReadOnly;
        spi.WriteData = new byte[0];
        spi.ReadLength = 4096;
        spi.Validate();
        spi.SpiOperation = Ch347SpiOperation.Exchange;
        spi.WriteData = new byte[4096];
        spi.Validate();
    }

    private static void ValidateJtagRequests()
    {
        Assert((int)Ch347BusProtocol.I2c == 0 && (int)Ch347BusProtocol.Spi == 1 && (int)Ch347BusProtocol.Jtag == 2,
            "Adding JTAG changed the persisted numeric I2C/SPI protocol values.");
        var defaults = new Ch347TransferRequest();
        Assert(defaults.JtagOperation == Ch347JtagOperation.ShiftDr && defaults.JtagClockCode == 0 &&
            defaults.JtagBitLength == 32, "JTAG defaults are not conservative 32-bit DR at the lowest clock.");
        foreach (var bits in new[] { 1, 7, 8, 9, 31, 32, 33, 32767, 32768 })
        foreach (var operation in new[] { Ch347JtagOperation.ShiftIr, Ch347JtagOperation.ShiftDr })
        {
            var request = Jtag(bits);
            request.JtagOperation = operation;
            request.JtagClockCode = 5;
            for (var i = 0; i < request.WriteData.Length; i++) request.WriteData[i] = 0xff;
            if ((bits & 7) != 0) request.WriteData[request.WriteData.Length - 1] = (byte)((1 << (bits & 7)) - 1);
            request.Validate();
        }
        var invalid = new Action<Ch347TransferRequest>[] {
            r => r.JtagClockCode = -1, r => r.JtagClockCode = 6,
            r => r.JtagOperation = (Ch347JtagOperation)(-1), r => r.JtagOperation = (Ch347JtagOperation)99,
            r => r.JtagBitLength = -1, r => r.JtagBitLength = 0,
            r => r.JtagBitLength = 32769,
            r => r.WriteData = new byte[3], r => r.WriteData = new byte[5],
            r => r.ReadLength = 3, r => r.ReadLength = 5, r => r.ProbeOnly = true,
            r => r.TimeoutMs = 99, r => r.TimeoutMs = 10001
        };
        foreach (var edit in invalid)
        {
            var request = Jtag(32);
            edit(request);
            Reject(request.Validate, "JTAG operation, clock, bit count, payload/read size or timeout");
        }
        foreach (var bits in new[] { 1, 7, 9, 32767 })
        {
            var request = Jtag(bits);
            request.WriteData[request.WriteData.Length - 1] = 0x80;
            Reject(request.Validate, "nonzero unused high TDI bit at " + bits + " bits");
        }
        var reset = new Ch347TransferRequest { Protocol = Ch347BusProtocol.Jtag,
            JtagOperation = Ch347JtagOperation.Reset, JtagBitLength = 0, WriteData = new byte[0], ReadLength = 0 };
        reset.Validate();
        foreach (var edit in new Action<Ch347TransferRequest>[] {
            r => r.JtagBitLength = 1, r => r.WriteData = new byte[] { 0 }, r => r.ReadLength = 1,
            r => r.JtagClockCode = 6, r => r.ProbeOnly = true })
        {
            var request = reset.Clone();
            edit(request);
            Reject(request.Validate, "JTAG reset carrying a shift phase or invalid settings");
        }
    }

    private static void TestProtocolCapabilities()
    {
        foreach (byte function in new byte[] { 0, 1, 2, 3, 4, 255 })
        {
            var device = Device("capability-" + function, function);
            device.FunctionType = function;
            var hasI2c = function == 1 || function == 2 || function == 3;
            var hasSpi = function == 1 || function == 3;
            var hasJtag = function == 2 || function == 3;
            Assert(device.SupportsI2c == hasI2c && device.SupportsSpi == hasSpi && device.SupportsJtag == hasJtag &&
                device.SupportsAnyBus == (hasI2c || hasSpi || hasJtag) &&
                device.SupportsProtocol(Ch347BusProtocol.I2c) == hasI2c &&
                device.SupportsProtocol(Ch347BusProtocol.Spi) == hasSpi &&
                device.SupportsProtocol(Ch347BusProtocol.Jtag) == hasJtag &&
                !device.SupportsProtocol((Ch347BusProtocol)42), "Incorrect capabilities for SDK FunctionType " + function);
            var fake = new FakeBackend();
            using (var service = new Ch347BusService(fake))
            {
                var connection = Capture(delegate { return ConnectResult(service, device); });
                if (!device.SupportsAnyBus)
                {
                    Assert(Failure(connection) != null && !service.IsConnected && fake.Records.Length == 0,
                        "Unknown SDK FunctionType was accepted or clocked a transaction.");
                    Assert(Failure(Capture(delegate { return service.ScanI2cAsync(1, 500, CancellationToken.None); })) != null &&
                        fake.Records.Length == 0, "A disconnected/unknown interface accepted an I2C scan.");
                    continue;
                }
                Wait(connection);
                foreach (var protocol in new[] { Ch347BusProtocol.I2c, Ch347BusProtocol.Spi, Ch347BusProtocol.Jtag })
                {
                    var request = protocol == Ch347BusProtocol.Jtag ? Jtag(1) : I2c(1);
                    if (protocol == Ch347BusProtocol.Spi)
                    {
                        request.Protocol = protocol;
                        request.SpiOperation = Ch347SpiOperation.WriteOnly;
                    }
                    var before = fake.Records.Length;
                    var result = Capture(delegate { return service.ExecuteAsync(request, CancellationToken.None); });
                    if (device.SupportsProtocol(protocol)) Assert(Wait(result).Success, "Supported bus transaction was refused.");
                    else Assert(Failure(result) is InvalidOperationException && fake.Records.Length == before,
                        "An unavailable protocol reached the backend for SDK FunctionType " + function);
                }
            }
        }
    }

    private static async Task<bool> ConnectResult(Ch347BusService service, Ch347DeviceInfo device)
    {
        await service.ConnectAsync(device, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static void TestQueuedJtagCopies()
    {
        var fake = new FakeBackend();
        using (var service = new Ch347BusService(fake))
        {
            Wait(service.ConnectAsync(JtagDevice("JTAG", 0), CancellationToken.None));
            var gate = fake.BlockNextTransfer();
            var first = service.ExecuteAsync(Jtag(1), CancellationToken.None);
            gate.WaitEntered();
            var request = Jtag(33);
            request.JtagOperation = Ch347JtagOperation.ShiftIr;
            request.JtagClockCode = 5;
            request.TimeoutMs = 750;
            request.WriteData = new byte[] { 0x12, 0x34, 0x56, 0x78, 1 };
            var queued = service.ExecuteAsync(request, CancellationToken.None);
            request.WriteData[0] = 0xff;
            request.WriteData = new byte[1];
            request.JtagOperation = Ch347JtagOperation.Reset;
            request.JtagClockCode = 0;
            request.JtagBitLength = 0;
            request.ReadLength = 0;
            request.TimeoutMs = 9000;
            Assert(fake.Records.Length == 1, "Queued JTAG work overtook an active transfer.");
            gate.Release();
            Assert(Wait(first).Success && Wait(queued).Success, "A queued JTAG transaction failed.");
            var actual = fake.Records[1].Request;
            Assert(fake.Records.Length == 2 && actual.Protocol == Ch347BusProtocol.Jtag &&
                actual.JtagOperation == Ch347JtagOperation.ShiftIr && actual.JtagClockCode == 5 &&
                actual.JtagBitLength == 33 && actual.ReadLength == 5 && actual.TimeoutMs == 750 &&
                actual.WriteData.SequenceEqual(new byte[] { 0x12, 0x34, 0x56, 0x78, 1 }) &&
                fake.MaximumConcurrentCalls == 1, "Queued JTAG work retained mutable caller fields or overlapped the backend.");
        }
    }

    private static void TestQueuedCopiesAndSerialExecution()
    {
        var fake = new FakeBackend();
        using (var service = new Ch347BusService(fake))
        {
            var stateEvents = 0;
            var logEvents = 0;
            service.StateChanged += delegate { Interlocked.Increment(ref stateEvents); };
            service.Logged += delegate { Interlocked.Increment(ref logEvents); };
            var enumerated = Wait(service.EnumerateAsync(CancellationToken.None));
            Assert(enumerated.Count == 2 && fake.Enumerations == 1, "Enumeration did not use the injected backend.");
            Wait(service.ConnectAsync(Device("A", 0), CancellationToken.None));
            Assert(service.IsConnected && service.ConnectedDevice.Identity == Device("A", 0).Identity,
                "The fake connection did not become observable.");
            var gate = fake.BlockNextTransfer();
            var first = service.ExecuteAsync(I2c(0x11), CancellationToken.None);
            gate.WaitEntered();
            var request = I2c(0x22);
            request.I2cAddress = 0x51;
            request.I2cSpeedCode = 3;
            request.ReadLength = 2;
            var queued = service.ExecuteAsync(request, CancellationToken.None);
            request.WriteData[0] = 0xee;
            request.WriteData = new byte[] { 0xff, 0xff };
            request.I2cAddress = 0x70;
            request.I2cSpeedCode = 0;
            request.ReadLength = 9;
            request.TimeoutMs = 9000;
            var following = new List<Task<Ch347TransferResult>>();
            for (byte marker = 0x30; marker < 0x38; marker++)
                following.Add(service.ExecuteAsync(I2c(marker), CancellationToken.None));
            Assert(fake.Records.Length == 1, "Queued work ran while a transfer was blocked.");
            gate.Release();
            Assert(Wait(first).Success && Wait(queued).Success, "Fake transactions failed.");
            foreach (var task in following) Assert(Wait(task).Success, "A serialized transaction failed.");
            var records = fake.Records;
            Assert(records.Length == 10 && records[1].Request.WriteData.SequenceEqual(new byte[] { 0x22 }) &&
                records[1].Request.I2cAddress == 0x51 && records[1].Request.I2cSpeedCode == 3 &&
                records[1].Request.ReadLength == 2 && records[1].Request.TimeoutMs == 500,
                "An enqueued transaction retained mutable caller bytes/settings.");
            Assert(records.Skip(2).Select(r => r.Request.WriteData[0]).SequenceEqual(Enumerable.Range(0x30, 8).Select(i => (byte)i)),
                "The queue reordered complete transactions.");
            Assert(fake.MaximumConcurrentCalls == 1, "Backend operations overlapped.");
            Assert(stateEvents > 0 && logEvents > 0, "Connection/transaction events were not published.");
            Wait(service.DisconnectAsync());
            Assert(!service.IsConnected && service.ConnectedDevice == null, "Disconnect retained an active device.");
        }
    }

    private static void TestQueueBound()
    {
        var fake = new FakeBackend();
        using (var service = new Ch347BusService(fake))
        {
            Wait(service.ConnectAsync(Device("A", 0), CancellationToken.None));
            var gate = fake.BlockNextTransfer();
            var first = service.ExecuteAsync(I2c(1), CancellationToken.None);
            gate.WaitEntered();
            var tasks = new List<Task<Ch347TransferResult>>();
            var rejected = false;
            for (var i = 0; i < 257; i++)
            {
                var task = Capture(delegate { return service.ExecuteAsync(I2c(2), CancellationToken.None); });
                tasks.Add(task);
                if (task.IsFaulted || task.IsCanceled) { rejected = true; break; }
            }
            Assert(rejected, "More than 256 waiting transactions were accepted; queue limit is missing.");
            Assert(tasks.Count > 1 && fake.Records.Length == 1, "Queue admission sent work while the backend was blocked.");
            gate.Release();
            Wait(first);
            var accepted = 0;
            foreach (var task in tasks)
            {
                var failure = Failure(task);
                if (failure == null) accepted++;
                else Assert(failure is InvalidOperationException, "Queue overflow produced an unexpected failure: " + failure);
            }
            Assert(fake.Records.Length == accepted + 1, "Rejected queue work reached the backend or accepted work was duplicated.");
            Assert(fake.MaximumConcurrentCalls == 1, "Queue saturation caused backend overlap.");
        }
    }

    private static void TestCancellation()
    {
        var fake = new FakeBackend();
        using (var service = new Ch347BusService(fake))
        using (var cancelled = new CancellationTokenSource())
        using (var queuedCancellation = new CancellationTokenSource())
        using (var inFlightCancellation = new CancellationTokenSource())
        {
            Wait(service.ConnectAsync(Device("A", 0), CancellationToken.None));
            cancelled.Cancel();
            var preCancelled = Capture(delegate { return service.ExecuteAsync(I2c(1), cancelled.Token); });
            Assert(Failure(preCancelled) is OperationCanceledException && fake.Records.Length == 0,
                "A pre-cancelled transaction reached the backend.");

            var firstGate = fake.BlockNextTransfer();
            var first = service.ExecuteAsync(I2c(2), CancellationToken.None);
            firstGate.WaitEntered();
            var queued = service.ExecuteAsync(I2c(3), queuedCancellation.Token);
            queuedCancellation.Cancel();
            firstGate.Release();
            Wait(first);
            Assert(Failure(queued) is OperationCanceledException, "Queued cancellation did not cancel its task.");
            Assert(fake.Records.Length == 1, "A transaction cancelled while queued was sent.");

            var inFlightGate = fake.BlockNextTransfer();
            var inFlight = service.ExecuteAsync(I2c(4), inFlightCancellation.Token);
            inFlightGate.WaitEntered();
            inFlightCancellation.Cancel();
            Assert(!inFlight.IsCompleted, "An in-flight cancellation reported completion before the native call returned.");
            var after = service.ExecuteAsync(I2c(5), CancellationToken.None);
            Assert(fake.Records.Length == 2, "Cancellation allowed a later transfer to overtake a blocked native call.");
            inFlightGate.Release();
            var failure = Failure(inFlight);
            Assert(failure == null || failure is OperationCanceledException,
                "In-flight cancellation produced an unexpected failure.");
            Wait(after);
            Assert(fake.Records.Select(r => r.Request.WriteData[0]).SequenceEqual(new byte[] { 2, 4, 5 }),
                "Cancellation repeated, reordered or sent a cancelled transaction.");
            Assert(fake.MaximumConcurrentCalls == 1, "Cancellation released serialization while a call was in flight.");
        }
    }

    private static void TestConnectionGeneration()
    {
        var fake = new FakeBackend();
        using (var service = new Ch347BusService(fake))
        {
            Wait(service.ConnectAsync(Device("A", 0), CancellationToken.None));
            var gate = fake.BlockNextTransfer();
            var old = service.ExecuteAsync(I2c(0x61), CancellationToken.None);
            gate.WaitEntered();
            var queuedOld = service.ExecuteAsync(I2c(0x62), CancellationToken.None);
            var disconnect = service.DisconnectAsync();
            var reconnect = service.ConnectAsync(Device("B", 1), CancellationToken.None);
            Assert(fake.Records.Length == 1 && fake.MaximumConcurrentCalls == 1,
                "A lifecycle operation overlapped the blocked transfer.");
            gate.Release();
            Failure(old); // A completed physical call may be discarded after generation change.
            Assert(Failure(queuedOld) != null, "Old-generation queued work survived disconnect.");
            Wait(disconnect);
            Wait(reconnect);
            Assert(service.IsConnected && service.ConnectedDevice.Identity == Device("B", 1).Identity,
                "Replacement device connection did not complete.");
            Wait(service.ExecuteAsync(I2c(0x63), CancellationToken.None));
            var records = fake.Records;
            Assert(records.Length == 2 && records[0].Identity == Device("A", 0).Identity &&
                records[1].Identity == Device("B", 1).Identity && records[1].Request.WriteData[0] == 0x63,
                "A queued old transaction was sent to a replacement device.");
            Assert(fake.MaximumConcurrentCalls == 1, "Disconnect/reconnect overlapped an active backend call.");
        }
    }

    private static void TestScan()
    {
        var fake = new FakeBackend();
        fake.Responder = delegate(Ch347TransferRequest request)
        {
            var acknowledged = request.I2cAddress == 0x50 || request.I2cAddress == 0x68;
            return new Ch347TransferResult { Success = acknowledged, AckCount = acknowledged ? 1 : 0,
                Message = acknowledged ? "Fake ACK" : "Fake I2C NACK" };
        };
        using (var service = new Ch347BusService(fake))
        {
            Wait(service.ConnectAsync(Device("A", 0), CancellationToken.None));
            var result = Wait(service.ScanI2cAsync(3, 500, CancellationToken.None));
            var hits = ((IEnumerable)result).Cast<object>().Select(Convert.ToInt32).ToArray();
            Assert(hits.SequenceEqual(new[] { 0x50, 0x68 }), "Scan did not report exactly the acknowledged addresses.");
            var records = fake.Records;
            Assert(records.Length == 112 && records.Select(r => r.Request.I2cAddress).SequenceEqual(Enumerable.Range(8, 112)),
                "Scan probed reserved addresses, skipped an address or retried a NACK.");
            Assert(records.All(r => r.Request.Protocol == Ch347BusProtocol.I2c && r.Request.ProbeOnly &&
                r.Request.WriteData.Length == 0 && r.Request.ReadLength == 0 && r.Request.I2cSpeedCode == 3 && r.Request.TimeoutMs == 500),
                "Scan changed bus settings or sent data/read phases.");
            Assert(service.IsConnected && fake.CloseCalls == 0, "An I2C NACK was misreported as device removal.");
            Assert(fake.MaximumConcurrentCalls == 1, "I2C scan overlapped backend operations.");
        }
    }

    private static void TestPresets(string directory)
    {
        var path = Path.Combine(directory, "profile", "ch347-presets.json");
        var store = new Ch347PresetStore(path);
        Assert(store.Load().Count == 0, "A fresh isolated preset store was not empty.");
        var request = new Ch347TransferRequest { Protocol = Ch347BusProtocol.Spi,
            WriteData = new byte[] { 0x9f }, ReadLength = 3, SpiOperation = Ch347SpiOperation.WriteThenRead,
            SpiMode = 3, SpiClockCode = 6, SpiChipSelect = 1, SpiLsbFirst = true, SpiDummyByte = 0xa5, TimeoutMs = 750 };
        store.Save(new[] { new Ch347BusPreset { Name = "Read ID", Request = request } });
        var savedBytes = File.ReadAllBytes(path);
        request.WriteData[0] = 0;
        var loaded = store.Load();
        Assert(loaded.Count == 1 && loaded[0].Name == "Read ID" && loaded[0].Request.WriteData[0] == 0x9f &&
            loaded[0].Request.ReadLength == 3 && loaded[0].Request.SpiMode == 3 &&
            loaded[0].Request.SpiClockCode == 6 && loaded[0].Request.SpiChipSelect == 1 &&
            loaded[0].Request.SpiLsbFirst && loaded[0].Request.SpiDummyByte == 0xa5 && loaded[0].Request.TimeoutMs == 750,
            "Preset persistence lost the bus transaction's settings.");
        Reject(delegate { store.Save(new[] {
            new Ch347BusPreset { Name = "Same", Request = I2c(1) },
            new Ch347BusPreset { Name = " same ", Request = I2c(2) } }); }, "duplicate presets");
        Reject(delegate { store.Save(new[] { new Ch347BusPreset { Name = "Invalid", Request = new Ch347TransferRequest {
            Protocol = Ch347BusProtocol.I2c, I2cAddress = 0xa0, WriteData = new byte[] { 1 } } } }); }, "eight-bit saved address");
        Reject(delegate { store.Save(new[] { new Ch347BusPreset { Name = "Probe", Request = new Ch347TransferRequest {
            Protocol = Ch347BusProtocol.I2c, ProbeOnly = true } } }); }, "saved scan probe");
        Assert(File.ReadAllBytes(path).SequenceEqual(savedBytes), "Invalid preset updates altered the previous file.");
        var all = new List<Ch347BusPreset> { new Ch347BusPreset { Name = "I2C", Request = I2c(1) } };
        foreach (Ch347SpiOperation operation in Enum.GetValues(typeof(Ch347SpiOperation)))
        {
            var spi = new Ch347TransferRequest { Protocol = Ch347BusProtocol.Spi, SpiOperation = operation,
                WriteData = operation == Ch347SpiOperation.ReadOnly ? new byte[0] : new byte[] { 0x9f },
                ReadLength = operation == Ch347SpiOperation.WriteOnly ? 0 : 1, SpiDummyByte = 0xa5 };
            all.Add(new Ch347BusPreset { Name = operation.ToString(), Request = spi });
        }
        foreach (var operation in new[] { Ch347JtagOperation.Reset, Ch347JtagOperation.ShiftIr, Ch347JtagOperation.ShiftDr })
        {
            var jtag = Jtag(operation == Ch347JtagOperation.Reset ? 0 : 9);
            jtag.JtagOperation = operation;
            jtag.JtagClockCode = 5;
            if (jtag.WriteData.Length != 0) jtag.WriteData = new byte[] { 0xa5, 1 };
            all.Add(new Ch347BusPreset { Name = "JTAG " + operation, Request = jtag });
        }
        store.Save(all); store.Save(all);
        loaded = store.Load();
        Assert(loaded.Count == all.Count && Directory.GetFiles(Path.GetDirectoryName(path), "*.bak").Length == 1 &&
            Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "Preset updates lost operations or accumulated backup/temp files.");
        foreach (var expected in all)
        {
            var actual = loaded.Single(p => p.Name == expected.Name).Request;
            Assert(actual.Protocol == expected.Request.Protocol && actual.ReadLength == expected.Request.ReadLength &&
                actual.WriteData.SequenceEqual(expected.Request.WriteData) && actual.JtagOperation == expected.Request.JtagOperation &&
                actual.JtagClockCode == expected.Request.JtagClockCode && actual.JtagBitLength == expected.Request.JtagBitLength,
                "A preset lost its JTAG operation, clock, exact bit count, bytes or prior bus identity.");
        }
        var healthy = File.ReadAllBytes(path);
        var invalidJtag = Jtag(9);
        invalidJtag.WriteData[1] = 0x80;
        Reject(delegate { store.Save(new[] { new Ch347BusPreset { Name = "Invalid JTAG", Request = invalidJtag } }); },
            "saved JTAG payload with nonzero unused high bits");
        Assert(File.ReadAllBytes(path).SequenceEqual(healthy), "Rejecting an invalid JTAG preset altered the saved presets.");

        // Handwritten old-version documents intentionally omit every new JTAG field.
        // Legacy I2C/SPI requests must still deserialize and save as their original protocol.
        var legacyPath = Path.Combine(directory, "legacy-profile", "ch347-presets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath));
        File.WriteAllText(legacyPath, "{\"Version\":1,\"Presets\":[" +
            "{\"Name\":\"Legacy I2C\",\"Request\":{\"Protocol\":0,\"I2cAddress\":80,\"WriteData\":\"AQ==\"}}," +
            "{\"Name\":\"Legacy SPI\",\"Request\":{\"Protocol\":1,\"WriteData\":\"nw==\",\"ReadLength\":3,\"SpiOperation\":2}}]}");
        var legacyStore = new Ch347PresetStore(legacyPath);
        var legacy = legacyStore.Load();
        Assert(legacy.Count == 2 && legacy[0].Request.Protocol == Ch347BusProtocol.I2c &&
            legacy[0].Request.WriteData.SequenceEqual(new byte[] { 1 }) &&
            legacy[1].Request.Protocol == Ch347BusProtocol.Spi && legacy[1].Request.ReadLength == 3 &&
            legacy[1].Request.WriteData.SequenceEqual(new byte[] { 0x9f }) &&
            legacy.All(p => p.Request.JtagOperation == Ch347JtagOperation.ShiftDr && p.Request.JtagBitLength == 32),
            "Old I2C/SPI presets without JTAG properties no longer load with their original semantics.");
        legacyStore.Save(legacy);
        Assert(legacyStore.Load().Count == 2, "Saving an old I2C/SPI preset document dropped its operations.");
        File.WriteAllText(path, "{invalid-json");
        try { store.Load(); throw new InvalidOperationException("Corrupt presets were silently accepted."); }
        catch (Newtonsoft.Json.JsonException) { }
        Assert(File.ReadAllText(path) == "{invalid-json", "Loading corrupt presets overwrote them.");
        File.WriteAllBytes(path, new byte[2 * 1024 * 1024 + 1]);
        Reject(delegate { store.Load(); }, "oversized preset input");
        File.WriteAllBytes(path, healthy);
        Assert(!File.Exists(Path.Combine(directory, "settings.json")), "Bus tests wrote serial/user settings.");
    }

    private static byte[] nativeWrite;
    private static Ch347Native.SpiConfiguration nativeConfiguration;
    private static uint nativeCs, nativeAck;
    private static bool SpySpiInit(uint index, IntPtr configuration)
    {
        nativeConfiguration = (Ch347Native.SpiConfiguration)Marshal.PtrToStructure(configuration, typeof(Ch347Native.SpiConfiguration));
        return true;
    }
    private static bool SpySpiTransfer(uint index, uint cs, uint length, IntPtr buffer)
    {
        nativeCs = cs;
        nativeWrite = new byte[length]; Marshal.Copy(buffer, nativeWrite, 0, (int)length);
        var read = Enumerable.Range(0, (int)length).Select(i => (byte)(0xc0 + i)).ToArray();
        Marshal.Copy(read, 0, buffer, read.Length); return true;
    }
    private static bool SpyI2cSet(uint index, uint speed) { return true; }
    private static bool SpyI2cTransfer(uint index, uint writeLength, IntPtr write, uint readLength, IntPtr read, out uint acknowledgements)
    {
        nativeWrite = new byte[writeLength]; Marshal.Copy(write, nativeWrite, 0, (int)writeLength);
        if (readLength > 0) Marshal.Copy(Enumerable.Repeat((byte)0x42, (int)readLength).ToArray(), 0, read, (int)readLength);
        acknowledgements = nativeAck; return true;
    }
    private static void TestNativeTransactions()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var apiType = typeof(Ch347Native).GetNestedType("NativeApi", BindingFlags.NonPublic);
        var api = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(apiType);
        foreach (var pair in new[] { new[] { "SpiInit", "SpySpiInit" }, new[] { "SpiWriteRead", "SpySpiTransfer" },
            new[] { "I2cSet", "SpyI2cSet" }, new[] { "StreamI2cRetAck", "SpyI2cTransfer" } })
        {
            var field = apiType.GetField(pair[0], BindingFlags.Instance | BindingFlags.Public);
            field.SetValue(api, Delegate.CreateDelegate(field.FieldType, typeof(Ch347RegressionProbe).GetMethod(pair[1], flags)));
        }
        var spiMethod = typeof(Ch347Native).GetMethod("TransferSpi", flags);
        var request = new Ch347TransferRequest { Protocol = Ch347BusProtocol.Spi, WriteData = new byte[] { 0x9f },
            ReadLength = 3, SpiOperation = Ch347SpiOperation.WriteThenRead, SpiChipSelect = 1, SpiMode = 3, SpiLsbFirst = true };
        var result = (Ch347TransferResult)spiMethod.Invoke(null, new object[] { api, 0u, request });
        Assert(nativeWrite.SequenceEqual(new byte[] { 0x9f, 0xff, 0xff, 0xff }) && nativeCs == 0x81 &&
            result.ReadData.SequenceEqual(new byte[] { 0xc1, 0xc2, 0xc3 }) && nativeConfiguration.Mode == 3 &&
            nativeConfiguration.ByteOrder == 0 && nativeConfiguration.AutoDeactivateCs == 1,
            "SPI command/dummy phases, CS mapping or readback slicing are incorrect.");
        request.SpiOperation = Ch347SpiOperation.Exchange; request.ReadLength = 1;
        result = (Ch347TransferResult)spiMethod.Invoke(null, new object[] { api, 0u, request });
        Assert(nativeWrite.SequenceEqual(new byte[] { 0x9f }) && result.ReadData.SequenceEqual(new byte[] { 0xc0 }), "Full-duplex SPI shifted readback.");
        request.SpiOperation = Ch347SpiOperation.ReadOnly; request.WriteData = new byte[0]; request.ReadLength = 2; request.SpiDummyByte = 0;
        result = (Ch347TransferResult)spiMethod.Invoke(null, new object[] { api, 0u, request });
        Assert(nativeWrite.SequenceEqual(new byte[] { 0, 0 }) && result.ReadData.Length == 2, "SPI reads ignored dummy settings.");
        var i2cMethod = typeof(Ch347Native).GetMethod("TransferI2c", flags);
        request = new Ch347TransferRequest { I2cAddress = 0x50, ReadLength = 2 };
        nativeAck = 1;
        result = (Ch347TransferResult)i2cMethod.Invoke(null, new object[] { api, 0u, request, false });
        Assert(nativeWrite.SequenceEqual(new byte[] { 0xa1 }) && result.Success && result.ReadData.SequenceEqual(new byte[] { 0x42, 0x42 }), "Direct I2C reads used the wrong wire address.");
        request.WriteData = new byte[] { 0x10 }; nativeAck = 3;
        result = (Ch347TransferResult)i2cMethod.Invoke(null, new object[] { api, 0u, request, false });
        Assert(nativeWrite.SequenceEqual(new byte[] { 0xa0, 0x10 }) && result.Success, "I2C register reads lost the write prefix.");
        request.ProbeOnly = true; request.WriteData = new byte[0]; request.ReadLength = 0; nativeAck = 0;
        result = (Ch347TransferResult)i2cMethod.Invoke(null, new object[] { api, 0u, request, false });
        Assert(nativeWrite.SequenceEqual(new byte[] { 0xa0 }) && !result.Success && result.AckCount == 0, "An I2C NACK probe was reported as present.");
    }

    private static readonly List<string> jtagCalls = new List<string>();
    private const uint JtagMockIndex = 17u;
    private static string jtagFailAt;
    private static uint jtagScanBits;
    private static bool jtagScanRead, jtagScanLast;
    private static bool JtagCall(string value)
    {
        jtagCalls.Add(value);
        return !string.Equals(jtagFailAt, value, StringComparison.Ordinal);
    }
    private static bool SpyJtagInit(uint index, byte clock)
    {
        Assert(index == JtagMockIndex, "JTAG initialization targeted an implicit or incorrect adapter index.");
        return JtagCall("init:" + clock);
    }
    private static bool SpyJtagState(uint index, byte state)
    {
        Assert(index == JtagMockIndex, "JTAG TAP transition targeted an implicit or incorrect adapter index.");
        return JtagCall("tap:" + state);
    }
    private static bool SpyJtagScan(uint index, IntPtr buffer, uint bitCount, bool read, bool last)
    {
        Assert(index == JtagMockIndex, "JTAG scan targeted an implicit or incorrect adapter index.");
        jtagScanBits = bitCount;
        jtagScanRead = read;
        jtagScanLast = last;
        var length = checked((int)((bitCount + 7) / 8));
        nativeWrite = new byte[length];
        Marshal.Copy(buffer, nativeWrite, 0, length);
        // A nonzero tail checks that the returned unused TDO bits are masked,
        // rather than accidentally exposed as clocks outside the request.
        Marshal.Copy(Enumerable.Repeat((byte)0xff, length).ToArray(), 0, buffer, length);
        return JtagCall("scan:" + bitCount);
    }

    private static object CreateJtagApi(string missingField)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var type = typeof(Ch347Native).GetNestedType("NativeApi", BindingFlags.NonPublic);
        var api = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        foreach (var pair in new[] { new[] { "JtagInit", "SpyJtagInit" },
            new[] { "JtagSwitchTapState", "SpyJtagState" }, new[] { "JtagIoScan", "SpyJtagScan" } })
        {
            if (pair[0] == missingField) continue;
            var field = type.GetField(pair[0], BindingFlags.Instance | BindingFlags.Public);
            Assert(field != null, "JTAG mock contract field is missing: " + pair[0]);
            field.SetValue(api, Delegate.CreateDelegate(field.FieldType, typeof(Ch347RegressionProbe).GetMethod(pair[1], flags)));
        }
        return api;
    }

    private static Ch347TransferResult InvokeJtag(object api, Ch347TransferRequest request, Ch347Native.JtagSession session)
    {
        try
        {
            return (Ch347TransferResult)typeof(Ch347Native).GetMethod("TransferJtag", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { api, JtagMockIndex, request, session });
        }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    private static Exception JtagFailure(object api, Ch347TransferRequest request, Ch347Native.JtagSession session)
    {
        try { InvokeJtag(api, request, session); return null; }
        catch (Exception error) { return error; }
    }

    private static Ch347TransferRequest JtagReset(int clock)
    {
        return new Ch347TransferRequest { Protocol = Ch347BusProtocol.Jtag, JtagOperation = Ch347JtagOperation.Reset,
            JtagBitLength = 0, JtagClockCode = clock, WriteData = new byte[0], ReadLength = 0 };
    }

    private static void ResetJtagSpy()
    {
        jtagCalls.Clear();
        jtagFailAt = null;
        nativeWrite = null;
        jtagScanBits = 0;
        jtagScanRead = jtagScanLast = false;
    }

    private static void TestNativeJtagTransactions()
    {
        var api = CreateJtagApi(null);
        var session = new Ch347Native.JtagSession();
        ResetJtagSpy();
        Assert(JtagFailure(api, Jtag(1), session) is InvalidOperationException && jtagCalls.Count == 0,
            "The first JTAG shift performed an implicit TAP reset or clocked an uninitialized session.");
        var reset = InvokeJtag(api, JtagReset(0), session);
        Assert(reset.Success && reset.ReadData.Length == 0 && session.Ready && session.ClockCode == 0 &&
            jtagCalls.SequenceEqual(new[] { "init:0", "tap:0", "tap:1" }),
            "Explicit JTAG reset did not initialize the requested clock and establish TAP idle exactly once.");

        foreach (var bits in new[] { 1, 7, 8, 9, 32, 32768 })
        foreach (var operation in new[] { Ch347JtagOperation.ShiftIr, Ch347JtagOperation.ShiftDr })
        {
            ResetJtagSpy();
            var request = Jtag(bits);
            request.JtagOperation = operation;
            request.WriteData[0] = 1; // Bit 0 must remain the first bit, with no host-side reversal.
            var before = (byte[])request.WriteData.Clone();
            var result = InvokeJtag(api, request, session);
            var state = operation == Ch347JtagOperation.ShiftIr ? 4 : 2;
            var expectedRead = Enumerable.Repeat((byte)0xff, request.ReadLength).ToArray();
            if ((bits & 7) != 0) expectedRead[expectedRead.Length - 1] = (byte)((1 << (bits & 7)) - 1);
            Assert(result.Success && result.ReadData.SequenceEqual(expectedRead) && nativeWrite.SequenceEqual(before) &&
                request.WriteData.SequenceEqual(before) && jtagScanBits == bits && jtagScanRead && jtagScanLast &&
                jtagCalls.SequenceEqual(new[] { "tap:" + state, "scan:" + bits, "tap:6" }) && session.Ready,
                "JTAG " + operation + " at " + bits + " bits lost packed order/length, mutated TDI, failed to mask TDO or followed the wrong TAP path.");
        }

        ResetJtagSpy();
        var changedClock = Jtag(8);
        changedClock.JtagClockCode = 1;
        Assert(JtagFailure(api, changedClock, session) is InvalidOperationException && !session.Ready && jtagCalls.Count == 0,
            "Changing JTAG clock silently reinitialized or shifted instead of requiring explicit reset.");
        Assert(JtagFailure(api, Jtag(8), session) is InvalidOperationException && jtagCalls.Count == 0,
            "A clock-invalidated JTAG session allowed a later shift without explicit reset.");

        foreach (var missing in new[] { "JtagInit", "JtagSwitchTapState", "JtagIoScan" })
        {
            ResetJtagSpy();
            var missingApi = CreateJtagApi(missing);
            var missingSession = new Ch347Native.JtagSession { Ready = true, ClockCode = 0 };
            Assert(JtagFailure(missingApi, JtagReset(0), missingSession) != null &&
                !missingSession.Ready && jtagCalls.Count == 0,
                "A partially supported SDK started JTAG I/O before rejecting its missing " + missing + " export.");
        }

        foreach (var failedAt in new[] { "init:0", "tap:0", "tap:1" })
        {
            ResetJtagSpy();
            session = new Ch347Native.JtagSession();
            jtagFailAt = failedAt;
            var result = InvokeJtag(api, JtagReset(0), session);
            Assert(!result.Success && result.ReadData.Length == 0 && !session.Ready && jtagCalls.Count(c => c == failedAt) == 1 &&
                jtagCalls[jtagCalls.Count - 1] == failedAt,
                "A failed explicit JTAG reset was retried, continued clocking or left the TAP marked ready.");
        }
        foreach (var failedAt in new[] { "tap:2", "scan:9", "tap:6" })
        {
            ResetJtagSpy();
            session = new Ch347Native.JtagSession();
            InvokeJtag(api, JtagReset(0), session);
            ResetJtagSpy();
            jtagFailAt = failedAt;
            var result = InvokeJtag(api, Jtag(9), session);
            Assert(!result.Success && result.ReadData.Length == 0 && !session.Ready && jtagCalls.Count(c => c == failedAt) == 1 &&
                jtagCalls[jtagCalls.Count - 1] == failedAt,
                "A failed JTAG shift was retried, continued moving TAP or left its state trusted.");
            jtagCalls.Clear();
            Assert(JtagFailure(api, Jtag(9), session) is InvalidOperationException && jtagCalls.Count == 0,
                "A failed JTAG operation allowed further shifts without explicit recovery.");
        }
        ResetJtagSpy();
    }

    private static void TestNativeAbiAndLibrary(string outputDirectory, string isolatedDirectory)
    {
        Assert(Marshal.SizeOf(typeof(Ch347Native.SpiConfiguration)) == 20,
            "WCH SPI configuration must follow pack(1), size 20.");
        Assert(Marshal.OffsetOf(typeof(Ch347Native.SpiConfiguration), "WriteReadInterval").ToInt32() == 3 &&
            Marshal.OffsetOf(typeof(Ch347Native.SpiConfiguration), "ChipSelect").ToInt32() == 6 &&
            Marshal.OffsetOf(typeof(Ch347Native.SpiConfiguration), "DelayDeactivate").ToInt32() == 16,
            "WCH SPI packed field offsets changed.");
        Assert(Marshal.SizeOf(typeof(Ch347Native.DeviceInformation)) == (IntPtr.Size == 8 ? 545 : 541),
            "WCH device information has an incorrect packed size for this process architecture.");
        Assert(Marshal.OffsetOf(typeof(Ch347Native.DeviceInformation), "DeviceHandle").ToInt32() == 328 &&
            Marshal.OffsetOf(typeof(Ch347Native.DeviceInformation), "FirmwareVersion").ToInt32() == (IntPtr.Size == 8 ? 544 : 540),
            "WCH device information packed field offsets changed.");

        var pathMethod = typeof(Ch347Native).GetMethod("ResolveLibraryPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert(pathMethod != null, "Native library path resolution must be independently testable without constructing an SDK backend.");
        var previousDirectory = Environment.CurrentDirectory;
        string library;
        try
        {
            Environment.CurrentDirectory = isolatedDirectory;
            File.WriteAllText(Path.Combine(isolatedDirectory, "CH347DLL.dll"), "Deliberately invalid cwd DLL; never load this file.");
            library = (string)pathMethod.Invoke(null, null);
            var expected = Path.GetFullPath(Path.Combine(outputDirectory, "CH347", "CH347DLL.dll"));
            Assert(Path.IsPathRooted(library) && string.Equals(Path.GetFullPath(library), expected, StringComparison.OrdinalIgnoreCase),
                "CH347 SDK path must be the application assembly's CH347 directory, independent of cwd.");
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
        Assert(File.Exists(library), "The built output is missing its matching-architecture CH347 SDK.");

        // Loading an official module and resolving symbols performs no SDK call.
        var module = LoadLibraryEx(library, IntPtr.Zero, 0x00000100 | 0x00000800);
        Assert(module != IntPtr.Zero, "Official CH347 SDK could not be loaded for export inspection; Win32=" + Marshal.GetLastWin32Error());
        try
        {
            foreach (var export in new[] { "CH347OpenDevice", "CH347CloseDevice", "CH347GetDeviceInfor", "CH347SetTimeout",
                "CH347SetDeviceNotify", "CH347I2C_Set", "CH347StreamI2C_RetACK", "CH347SPI_Init", "CH347SPI_WriteRead",
                "CH347Jtag_INIT", "CH347Jtag_SwitchTapStateEx", "CH347Jtag_IoScanT" })
                Assert(GetProcAddress(module, export) != IntPtr.Zero, "Official SDK export missing: " + export);
        }
        finally { FreeLibrary(module); }
    }

    private sealed class Record
    {
        public string Identity;
        public Ch347TransferRequest Request;
    }

    private sealed class TransferGate
    {
        private readonly ManualResetEventSlim entered = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim released = new ManualResetEventSlim(false);
        public void WaitEntered() { Assert(entered.Wait(WaitMs), "Fake transfer never entered its synchronous gate."); }
        public void Release() { released.Set(); }
        public void Block()
        {
            entered.Set();
            Assert(released.Wait(WaitMs), "The fake's blocked transfer was not released.");
        }
    }

    private sealed class FakeBackend : ICh347Backend
    {
        private readonly object sync = new object();
        private readonly List<Record> records = new List<Record>();
        private readonly List<TransferGate> allGates = new List<TransferGate>();
        private TransferGate nextGate;
        private Ch347DeviceInfo opened;
        private int concurrent;
        private int maximumConcurrent;
        private int enumerations;
        private int closeCalls;
        public Func<Ch347TransferRequest, Ch347TransferResult> Responder;
        public int MaximumConcurrentCalls { get { return Volatile.Read(ref maximumConcurrent); } }
        public int Enumerations { get { return Volatile.Read(ref enumerations); } }
        public int CloseCalls { get { return Volatile.Read(ref closeCalls); } }
        public Record[] Records { get { lock (sync) return records.ToArray(); } }

        public TransferGate BlockNextTransfer()
        {
            lock (sync)
            {
                Assert(nextGate == null, "A transfer gate was already pending.");
                nextGate = new TransferGate();
                allGates.Add(nextGate);
                return nextGate;
            }
        }

        private void Enter()
        {
            var count = Interlocked.Increment(ref concurrent);
            int prior;
            do { prior = maximumConcurrent; if (prior >= count) break; }
            while (Interlocked.CompareExchange(ref maximumConcurrent, count, prior) != prior);
        }
        private void Exit() { Interlocked.Decrement(ref concurrent); }

        public IReadOnlyList<Ch347DeviceInfo> Enumerate()
        {
            Enter();
            try { Interlocked.Increment(ref enumerations); return new[] { Device("A", 0), Device("B", 1) }; }
            finally { Exit(); }
        }

        public Ch347DeviceInfo Open(Ch347DeviceInfo expectedDevice)
        {
            Enter();
            try
            {
                lock (sync)
                {
                    Assert(opened == null, "Fake backend opened before its old device was closed.");
                    opened = expectedDevice.Clone();
                    return opened.Clone();
                }
            }
            finally { Exit(); }
        }

        public void Close()
        {
            Enter();
            try { lock (sync) { if (opened != null) Interlocked.Increment(ref closeCalls); opened = null; } }
            finally { Exit(); }
        }

        public bool IsPresent(Ch347DeviceInfo expectedDevice)
        {
            Enter();
            try { lock (sync) return opened != null && opened.Identity == expectedDevice.Identity && opened.Index == expectedDevice.Index; }
            finally { Exit(); }
        }

        public Ch347TransferResult Transfer(Ch347TransferRequest request)
        {
            Enter();
            try
            {
                TransferGate gate;
                lock (sync)
                {
                    Assert(opened != null, "Fake transfer was attempted with no open device.");
                    records.Add(new Record { Identity = opened.Identity, Request = request.Clone() });
                    gate = nextGate;
                    nextGate = null;
                }
                if (gate != null) gate.Block();
                if (Responder != null) return Responder(request);
                return new Ch347TransferResult { Success = true, AckCount = request.WriteData.Length + 1,
                    ReadData = new byte[request.ReadLength], Message = "Fake transaction completed" };
            }
            finally { Exit(); }
        }

        public void Dispose()
        {
            // Release gates even after an assertion so regression failures never
            // leave a background worker waiting indefinitely during cleanup.
            lock (sync) { foreach (var gate in allGates) gate.Release(); }
            Close();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
