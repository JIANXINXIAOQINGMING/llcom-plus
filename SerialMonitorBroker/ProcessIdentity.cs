using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Llcom.SerialMonitoring
{
    internal sealed class ProcessIdentity : IDisposable
    {
        private readonly SafeWaitHandle handle;
        internal readonly uint Pid;
        internal readonly long StartedTicks;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeWaitHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(SafeWaitHandle process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(SafeWaitHandle process, out bool wow64);
        [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out SystemInfo info);
        [StructLayout(LayoutKind.Sequential)] private struct SystemInfo
        {
            internal ushort Architecture, Reserved;
            internal uint PageSize;
            internal IntPtr MinimumAddress, MaximumAddress, ProcessorMask;
            internal uint NumberOfProcessors, ProcessorType, Granularity;
            internal ushort Level, Revision;
        }
        internal ProcessIdentity(uint pid, long expectedTicks)
        {
            Pid = pid;
            var raw = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (raw == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the selected process.");
            handle = new SafeWaitHandle(raw, true);
            try
            {
                long created, exited, kernel, user;
                if (!GetProcessTimes(handle, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
                StartedTicks = DateTime.FromFileTimeUtc(created).Ticks;
                if (expectedTicks <= 0 || StartedTicks != expectedTicks || HasExited)
                    throw new InvalidOperationException("The selected process exited or its PID was reused. Refresh the process list.");
            }
            catch { handle.Dispose(); throw; }
        }
        internal bool HasExited { get { return WaitForSingleObject(handle, 0) != 0x102; } }
        internal string Architecture
        {
            get
            {
                try
                {
                    ushort machine, native;
                    if (IsWow64Process2(handle, out machine, out native))
                        return ArchitectureName(machine == 0 ? native : machine);
                }
                catch (EntryPointNotFoundException) { }
                SystemInfo info;
                GetNativeSystemInfo(out info);
                if (info.Architecture != 0 && info.Architecture != 9) return "unknown";
                bool wow;
                if (!IsWow64Process(handle, out wow)) return "unknown";
                return wow || info.Architecture == 0 ? "x86" : "x64";
            }
        }
        private static string ArchitectureName(ushort machine) { return machine == 0x14C ? "x86" : machine == 0x8664 ? "x64" : "unknown"; }
        public void Dispose() { handle.Dispose(); }
    }
}
