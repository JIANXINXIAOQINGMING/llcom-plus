[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',
      [ValidateSet('x64','x86')][string]$Platform='x64',
      [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
$repo=Split-Path -Parent $PSScriptRoot
$product=if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repo "llcom plus\bin\$Platform\$Configuration"
} else { [IO.Path]::GetFullPath($OutputDirectory) }
$actualHelper=Join-Path $product "Monitor\$Platform\SerialMonitorBroker.exe"
if (-not (Test-Path -LiteralPath $actualHelper -PathType Leaf)) { throw 'Build the application and packaged monitor helpers first.' }
$command=Get-Command msbuild.exe -ErrorAction SilentlyContinue
$msbuild=if ($null -ne $command) { $command.Source } else { $null }
if ([string]::IsNullOrWhiteSpace($msbuild)) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $msbuild=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if ([string]::IsNullOrWhiteSpace($msbuild)) { throw 'MSBuild is required to compile the isolated monitor broker probe.' }
$testProject=Join-Path $PSScriptRoot 'MonitorBrokerRegression\MonitorBrokerRegression.csproj'
foreach ($architecture in @('x64','x86')) {
    & $msbuild $testProject /t:Build /p:Configuration=Release "/p:Platform=$architecture" /m /v:quiet /nologo
    if ($LASTEXITCODE -ne 0) { throw "Monitor broker probe compilation failed: $architecture" }
}
$opposite=if ($Platform -eq 'x64') { 'x86' } else { 'x64' }
$testExe=Join-Path $PSScriptRoot "MonitorBrokerRegression\bin\$Platform\Release\MonitorBrokerRegression.exe"
$otherExe=Join-Path $PSScriptRoot "MonitorBrokerRegression\bin\$opposite\Release\MonitorBrokerRegression.exe"
$tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName='llcom-monitor-broker-' + [Guid]::NewGuid().ToString('N')
$testRoot=Join-Path $tempBase $testName
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    & $testExe $testRoot $actualHelper $otherExe
    if ($LASTEXITCODE -ne 0) { throw "Monitor broker regression failed: $Platform" }
    Write-Host "PASS monitor broker protocol/lifecycle ($Platform); no external process was injected or serial port opened."
} finally {
    $fullRoot=[IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($fullRoot) -ne $tempBase -or [IO.Path]::GetFileName($fullRoot) -ne $testName -or
        $testName -notmatch '^llcom-monitor-broker-[0-9a-f]{32}$') { throw 'Refusing to remove an unowned probe profile.' }
    for ($attempt=1; $attempt -le 5; $attempt++) {
        try {
            if (Test-Path -LiteralPath $fullRoot) { Remove-Item -LiteralPath $fullRoot -Recurse -Force -ErrorAction Stop }
            break
        } catch {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Milliseconds (100 * $attempt)
        }
    }
}
