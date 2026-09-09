using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

// Pure in-memory tests. These probes do not start the application, contact a
// server, open a serial port, or install an update.
public static class UpdateLifecycleRegressionProbe
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Invoke(MethodInfo method, params object[] arguments)
    {
        Assert(method != null, "The required updater method was not found. Rebuild the application first.");
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static void ExpectCancellation(Action action, CancellationToken token)
    {
        try { action(); }
        catch (OperationCanceledException ex)
        {
            Assert(ex.CancellationToken == token, "Cancellation must retain the caller's token.");
            return;
        }
        throw new InvalidOperationException("A canceled update operation continued instead of throwing.");
    }

    private sealed class CountingInput : MemoryStream
    {
        internal int Reads;
        internal int CancelOnRead;
        internal CancellationTokenSource Cancellation;

        internal CountingInput(byte[] bytes) : base(bytes, false) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            int read = base.Read(buffer, offset, count);
            if (Reads == CancelOnRead) Cancellation.Cancel();
            return read;
        }
    }

    public static string[] RunCopyAndInspectionChecks(Assembly assembly)
    {
        var updater = assembly.GetType("llcom_plus.Tools.GitHubReleaseUpdater", true);
        var copy = updater.GetMethod("CopyUpdateEntry", Static);
        var inspect = updater.GetMethod("InspectUpdatePackage", Static);
        var passed = new List<string>();
        var bytes = new byte[81920 * 3 + 13];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);

        using (var input = new CountingInput(bytes))
        using (var output = new MemoryStream())
        {
            Invoke(copy, input, output, CancellationToken.None);
            var actual = output.ToArray();
            Assert(actual.Length == bytes.Length, "Normal copy lost or added bytes.");
            for (int i = 0; i < bytes.Length; i++)
                Assert(actual[i] == bytes[i], "Normal copy changed the package contents.");
            Assert(input.Reads >= 4, "The fixture must exercise multiple copy blocks.");
            passed.Add("CopyUpdateEntry copies every byte across multiple blocks.");
        }

        using (var cancellation = new CancellationTokenSource())
        using (var input = new CountingInput(bytes))
        using (var output = new MemoryStream())
        {
            cancellation.Cancel();
            ExpectCancellation(() => Invoke(copy, input, output, cancellation.Token), cancellation.Token);
            Assert(input.Reads == 0 && output.Length == 0, "Pre-canceled copy performed I/O.");
            passed.Add("A pre-canceled copy does not read or write any block.");
        }

        using (var cancellation = new CancellationTokenSource())
        using (var input = new CountingInput(bytes) { Cancellation = cancellation, CancelOnRead = 2 })
        using (var output = new MemoryStream())
        {
            ExpectCancellation(() => Invoke(copy, input, output, cancellation.Token), cancellation.Token);
            Assert(input.Reads == 2, "The copy read another block after cancellation.");
            Assert(output.Length == 81920, "The block whose read canceled the operation was written out.");
            var actual = output.ToArray();
            for (int i = 0; i < actual.Length; i++)
                Assert(actual[i] == bytes[i], "Cancellation changed an already copied block.");
            passed.Add("Cancellation during a read preserves earlier blocks and prevents all subsequent writes.");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            // This deliberately invalid path distinguishes cancellation from file
            // validation, without creating any ZIP or touching an update directory.
            ExpectCancellation(() => Invoke(inspect, "\0invalid-update-path", new Version(1, 2, 3),
                cancellation.Token), cancellation.Token);
            passed.Add("InspectUpdatePackage honors pre-cancellation before validating or opening a path.");
        }

        return passed.ToArray();
    }

    public static string BuildInstallerText(Assembly assembly)
    {
        var updater = assembly.GetType("llcom_plus.Tools.GitHubReleaseUpdater", true);
        var trustedType = assembly.GetType("llcom_plus.Tools.TrustedUpdatePackage", true);
        var trusted = Activator.CreateInstance(trustedType, true);
        var values = new Dictionary<string, object>
        {
            { "Version", new Version(99, 1, 2) },
            { "Architecture", Environment.Is64BitProcess ? "x64" : "x86" },
            { "Sha256", new string('A', 64) },
            { "SignatureBase64", "AA==" },
            { "CanonicalPayload", "offline-test-payload" },
            { "PublicKeyXml", "<offline-test-key />" }
        };
        foreach (var entry in values)
            trustedType.GetProperty(entry.Key).SetValue(trusted, entry.Value, null);
        // Only generate the text. The dummy trust data cannot authenticate a real
        // package, and no part of the installer's top-level body is executed.
        return (string)Invoke(updater.GetMethod("BuildInstallScript", Static),
            @"C:\offline-probe\package.zip", @"C:\offline-probe\install", true, trusted);
    }
}
