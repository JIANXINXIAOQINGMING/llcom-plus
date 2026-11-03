[CmdletBinding()]
param([ValidateSet('x64','x86','All')][string]$Arch='All')
$ErrorActionPreference='Stop'
if ($Arch -eq 'All') {
    foreach ($architecture in @('x64','x86')) {
        $directory=if ($architecture -eq 'x64') { 'System32' } else { 'SysWOW64' }
        & (Join-Path $env:WINDIR "$directory\WindowsPowerShell\v1.0\powershell.exe") -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File $PSCommandPath -Arch $architecture
        if ($LASTEXITCODE -ne 0) { throw "Native export probe failed: $architecture" }
    }
    exit 0
}
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Arch -eq 'x64')) {
    throw 'Use matching-bitness Windows PowerShell 5.1.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeExportProbe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true, SetLastError=true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint Probe();
}
'@
$root=Split-Path -Parent $PSScriptRoot
$target=if ($Arch -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'i686-pc-windows-msvc' }
$hostPath=Join-Path $root "llcom plus\Runtime\Native\$Arch\serial_monitor.dll"
$hookPath=Join-Path $PSScriptRoot "target\$target\release\serial_monitor_hook.dll"
foreach ($entry in @(
    @{ Path=$hostPath; Exports=@('MonitorComm','UnMonitorComm','SerialMonitorGetAbiVersion','SerialMonitorGetPendingCleanupCount') },
    @{ Path=$hookPath; Exports=@('SerialMonitorInitialize','SerialMonitorDeactivate') }
)) {
    $module=[NativeExportProbe]::LoadLibrary($entry.Path)
    if ($module -eq [IntPtr]::Zero) { throw "LoadLibrary failed: $($entry.Path), error=$([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    try {
        foreach ($name in $entry.Exports) {
            $address=[NativeExportProbe]::GetProcAddress($module,$name)
            if ($address -eq [IntPtr]::Zero) { throw "Missing exact undecorated $Arch export: $name" }
            if ($name -in @('SerialMonitorGetAbiVersion','SerialMonitorGetPendingCleanupCount')) {
                $probe=[Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($address,[NativeExportProbe+Probe])
                $actual=$probe.Invoke()
                $expected=if ($name -eq 'SerialMonitorGetAbiVersion') { 3 } else { 0 }
                if ($actual -ne $expected) { throw "Unexpected $name return: $actual" }
            }
            Write-Output "PASS $Arch exact PE export $name"
        }
    } finally { [void][NativeExportProbe]::FreeLibrary($module) }
}
Write-Output "PASS $Arch load/export/ABI probe; no monitor, hook initialization, injection or COM operation was invoked."
