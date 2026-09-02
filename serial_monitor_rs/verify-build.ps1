[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'x86')]
    [string]$Architecture,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration,
    [Parameter(Mandatory = $true)]
    [string]$RuntimeDll,
    [Parameter(Mandatory = $true)]
    [string]$StampFile,
    [string]$RustToolchain = '1.82.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $scriptDir 'native-source.ps1')

if (-not (Test-Path -LiteralPath $RuntimeDll -PathType Leaf)) {
    throw "Native runtime DLL is missing: $RuntimeDll"
}
if (-not (Test-Path -LiteralPath $StampFile -PathType Leaf)) {
    throw "Native source-build provenance stamp is missing: $StampFile"
}

$stamp = Get-Content -LiteralPath $StampFile -Raw | ConvertFrom-Json
$expectedTarget = if ($Architecture -eq 'x64') {
    'x86_64-pc-windows-msvc'
} else {
    'i686-pc-windows-msvc'
}
$sourceHash = Get-NativeSourceDigest -SourceRoot $scriptDir
$runtimeHash = Get-Sha256FileDigest -Path $RuntimeDll

if ([int]$stamp.schemaVersion -ne 1 -or
    [string]$stamp.architecture -cne $Architecture -or
    [string]$stamp.target -cne $expectedTarget -or
    [string]$stamp.configuration -cne $Configuration -or
    [string]$stamp.rustToolchain -cne $RustToolchain -or
    [string]$stamp.sourceSha256 -cne $sourceHash -or
    [string]$stamp.hostDllSha256 -cne $runtimeHash) {
    throw ("Native provenance mismatch for {0}. Rebuild with " +
        "serial_monitor_rs\build.ps1 -Config {1} -Arch {0} -RustToolchain {2}.") -f `
        $Architecture, $Configuration, $RustToolchain
}

Write-Host "Verified native provenance: $Architecture $Configuration $runtimeHash" -ForegroundColor Green
