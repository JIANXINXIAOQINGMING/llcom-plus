[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64')
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw 'Run using matching Windows PowerShell 5.1 architecture.'
}
$projectDir=Join-Path (Split-Path -Parent $PSScriptRoot) 'llcom plus'
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $projectDir "bin\$Platform\$Configuration\llcom plus.exe"))
Add-Type -Path (Join-Path $PSScriptRoot 'SerialTraceRegressionProbe.cs')
foreach ($result in [SerialTraceRegressionProbe]::Run($assembly)) { Write-Host "PASS $result" }
$uart=[IO.File]::ReadAllText((Join-Path $projectDir 'Core\Model\Uart.cs'))
$split=[IO.File]::ReadAllText((Join-Path $projectDir 'UI\Pages\MultiPortPage.xaml.cs'))
if (($uart.Split(@('SerialTraceHub.RecordBuffer'), [StringSplitOptions]::None).Length - 1) -ne 3) {
    throw 'Main UART must trace physical TX and both RX paths.'
}
if (($split.Split(@('SerialTraceHub.RecordBuffer'), [StringSplitOptions]::None).Length - 1) -ne 2) {
    throw 'Split UART must trace physical TX and RX independently of log visibility.'
}
Write-Host 'PASS Physical TX/RX trace producers are wired for main and split COM paths.'
Write-Host "All serial trace checks passed ($Platform $Configuration)."
