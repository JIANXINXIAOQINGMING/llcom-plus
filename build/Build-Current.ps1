[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root 'llcom plus\llcom plus.csproj'

function Resolve-MSBuildPath {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramFiles')
    $vswhereCandidates = @(
        $(if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
            Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        }),
        $(if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
            Join-Path $programFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
        })
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }

    foreach ($vswhere in $vswhereCandidates) {
        $found = & $vswhere `
            -latest `
            -products '*' `
            -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null |
            Select-Object -First 1
        if (-not [string]::IsNullOrWhiteSpace($found) -and
            (Test-Path -LiteralPath $found -PathType Leaf)) {
            return [string]$found
        }
    }

    throw 'MSBuild.exe was not found. Install Visual Studio 2022 with the .NET desktop build tools.'
}

$msbuildPath = Resolve-MSBuildPath
Write-Host "BUILD Current source ($Configuration|$Platform)" -ForegroundColor Cyan
& $msbuildPath `
    $projectPath `
    /t:Rebuild `
    "/p:Configuration=$Configuration" `
    "/p:Platform=$Platform" `
    /m `
    /v:minimal `
    /nologo
if ($LASTEXITCODE -ne 0) {
    throw "Current-source build failed ($Configuration|$Platform) with exit code $LASTEXITCODE."
}
