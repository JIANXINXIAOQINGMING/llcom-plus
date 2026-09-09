[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or
    [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw 'Run with matching-bitness Windows PowerShell 5.1. This test does not build the application.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot "llcom plus\bin\$Platform\$Configuration"
$exePath = Join-Path $outputDir 'llcom plus.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Build the application before running this test: $exePath"
}
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-backup-retry-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
try {
    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName PresentationFramework
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom($exePath)
    Add-Type -Path (Join-Path $PSScriptRoot 'QuickSendBackupRetryProbe.cs')
    [QuickSendBackupRetryProbe]::Run($assembly, $testRoot)
    Write-Host 'PASS  All 6 quick-send backup retry/atomic-state checks.'
}
finally {
    # Only this invocation's GUID-named directory may be removed.
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or
        [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or
        $testName -notmatch '^llcom-backup-retry-[0-9a-f]{32}$') {
        throw "Refusing cleanup outside the isolated test directory: $resolvedRoot"
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
