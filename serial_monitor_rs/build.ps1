# Builds serial_monitor_hook first and then embeds it in serial_monitor.
# This script never installs toolchains or targets. CI installs the exact
# toolchain before invoking it; local callers get an actionable failure.

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',
    [ValidateSet('x64', 'x86', 'All')]
    [string]$Arch = 'x64',
    [string]$RustToolchain = '1.82.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Split-Path -Parent $scriptDir
. (Join-Path $scriptDir 'native-source.ps1')

function Invoke-Cargo {
    param([string[]]$Arguments)
    & cargo "+$RustToolchain" @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "cargo +$RustToolchain $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Build-For {
    param(
        [string]$Target,
        [string]$DestinationArchitecture
    )

    $profile = if ($Config -eq 'Release') { 'release' } else { 'debug' }
    $releaseArgument = if ($Config -eq 'Release') { @('--release') } else { @() }
    Write-Host "`n=== Native $Target ($Config, Rust $RustToolchain) ===" -ForegroundColor Cyan

    Invoke-Cargo (@('build') + $releaseArgument + @(
        '-p', 'serial_monitor_hook', '--target', $Target))
    Invoke-Cargo (@('build') + $releaseArgument + @(
        '-p', 'serial_monitor', '--target', $Target))

    $targetDir = Join-Path $scriptDir "target\$Target\$profile"
    $hostDll = Join-Path $targetDir 'serial_monitor.dll'
    $hookDll = Join-Path $targetDir 'serial_monitor_hook.dll'
    if (-not (Test-Path -LiteralPath $hostDll -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hookDll -PathType Leaf)) {
        throw "Cargo succeeded but native DLL outputs are missing under $targetDir"
    }

    $destinationDir = Join-Path $repoRoot "llcom plus\Runtime\Native\$DestinationArchitecture"
    [void][IO.Directory]::CreateDirectory($destinationDir)
    $destination = Join-Path $destinationDir 'serial_monitor.dll'
    Copy-Item -LiteralPath $hostDll -Destination $destination -Force

    $builtHash = Get-Sha256FileDigest -Path $hostDll
    $copiedHash = Get-Sha256FileDigest -Path $destination
    if ($builtHash -ne $copiedHash) {
        throw "Copied native DLL hash mismatch for $DestinationArchitecture"
    }

    $stamp = [ordered]@{
        schemaVersion = 1
        architecture = $DestinationArchitecture
        target = $Target
        configuration = $Config
        rustToolchain = $RustToolchain
        sourceSha256 = Get-NativeSourceDigest -SourceRoot $scriptDir
        hostDllSha256 = $builtHash
        hookDllSha256 = Get-Sha256FileDigest -Path $hookDll
    }
    $stampPath = Join-Path $scriptDir "target\native-build-$DestinationArchitecture.json"
    $stamp | ConvertTo-Json | Set-Content -LiteralPath $stampPath -Encoding UTF8
    Write-Host "Verified source build -> $destination" -ForegroundColor Green
    Write-Host "Provenance stamp      -> $stampPath"
}

if ($null -eq (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "cargo was not found. Install Rust $RustToolchain and the MSVC x86/x64 targets; this script does not download tools."
}
$reportedVersion = & cargo "+$RustToolchain" --version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Rust toolchain $RustToolchain is not installed. Install it explicitly before running this script."
}
Write-Host $reportedVersion

Push-Location $scriptDir
try {
    switch ($Arch) {
        'x64' { Build-For 'x86_64-pc-windows-msvc' 'x64' }
        'x86' { Build-For 'i686-pc-windows-msvc' 'x86' }
        'All' {
            Build-For 'x86_64-pc-windows-msvc' 'x64'
            Build-For 'i686-pc-windows-msvc' 'x86'
        }
    }
}
finally {
    Pop-Location
}
