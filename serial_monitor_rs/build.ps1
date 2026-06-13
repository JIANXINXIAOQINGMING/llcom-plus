# Build script for serial_monitor Rust DLL
# Run from the serial_monitor_rs directory (or from the repo root)
#
# Usage:
#   .\build.ps1                   # x64 Release  (default)
#   .\build.ps1 -Arch x86         # x86 Release
#   .\build.ps1 -Arch All         # x64 + x86 Release
#   .\build.ps1 -Config Debug     # x64 Debug
#   .\build.ps1 -Arch All -Config Debug
#
# Outputs:
#   x64  →  ..\llcom plus\costura64\serial_monitor.dll
#   x86  →  ..\llcom plus\costura32\serial_monitor.dll

param(
    [string]$Config = "Release",
    [ValidateSet("x64","x86","All")]
    [string]$Arch   = "x64"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $scriptDir

function Build-For {
    param([string]$target, [string]$dstDir)

    $profile = if ($Config -eq "Release") { "release" } else { "debug" }

    Write-Host ""
    Write-Host "=== Building for $target ($Config) ==="

    Write-Host "  [1/2] serial_monitor_hook ..."
    if ($Config -eq "Release") {
        cargo build --release -p serial_monitor_hook --target $target
    } else {
        cargo build -p serial_monitor_hook --target $target
    }
    if ($LASTEXITCODE -ne 0) { throw "serial_monitor_hook build failed for $target" }

    Write-Host "  [2/2] serial_monitor ..."
    if ($Config -eq "Release") {
        cargo build --release -p serial_monitor --target $target
    } else {
        cargo build -p serial_monitor --target $target
    }
    if ($LASTEXITCODE -ne 0) { throw "serial_monitor build failed for $target" }

    $dllSrc = Join-Path $scriptDir "target\$target\$profile\serial_monitor.dll"
    if (-not (Test-Path $dllSrc)) {
        throw "Build succeeded but DLL not found at: $dllSrc"
    }

    $dst = Join-Path $scriptDir "..\llcom plus\$dstDir\serial_monitor.dll"
    Copy-Item -Force $dllSrc $dst
    Write-Host "  Copied → $dst  ($([int]((Get-Item $dst).Length/1024)) KB)"
}

try {
    switch ($Arch) {
        "x64" { Build-For "x86_64-pc-windows-msvc" "costura64" }
        "x86" { Build-For "i686-pc-windows-msvc"   "costura32" }
        "All" {
            Build-For "x86_64-pc-windows-msvc" "costura64"
            Build-For "i686-pc-windows-msvc"   "costura32"
        }
    }
    Write-Host ""
    Write-Host "All done."
} finally {
    Pop-Location
}
