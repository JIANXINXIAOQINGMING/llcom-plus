param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) { throw 'Use matching Windows PowerShell 5.1.' }
$output=Join-Path (Split-Path -Parent $PSScriptRoot) "llcom plus\bin\$Platform\$Configuration"
$testBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName='llcom-plot-'+[Guid]::NewGuid().ToString('N')
$testRoot=Join-Path $testBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
try {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $output 'Newtonsoft.Json.dll'))
    $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
    Add-Type -Path (Join-Path $PSScriptRoot 'PlotWorkflowProbe.cs')
    foreach ($message in [PlotWorkflowProbe]::Run($assembly,$testRoot)) { Write-Host "PASS $message" }
    Write-Host "All plot workflow checks passed ($Platform $Configuration)."
}
finally {
    $resolved=[IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolved) -ne $testBase -or [IO.Path]::GetFileName($resolved) -ne $testName) { throw 'Unsafe temporary path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
