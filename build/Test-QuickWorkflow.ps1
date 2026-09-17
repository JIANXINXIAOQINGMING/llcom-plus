param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) { throw 'Use matching Windows PowerShell 5.1.' }
$output=Join-Path (Split-Path -Parent $PSScriptRoot) "llcom plus\bin\$Platform\$Configuration"
[void][Reflection.Assembly]::LoadFrom((Join-Path $output 'Newtonsoft.Json.dll'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
Add-Type -Path (Join-Path $PSScriptRoot 'QuickWorkflowRegressionProbe.cs')
foreach ($message in [QuickWorkflowRegressionProbe]::Run($assembly)) { Write-Host "PASS $message" }
Write-Host "All quick workflow checks passed ($Platform $Configuration)."
