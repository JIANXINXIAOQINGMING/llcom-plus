[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86')][string]$Platform = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw "Use $Platform Windows PowerShell 5.1 for this test."
}
$projectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'llcom plus'
$outputPath = Join-Path $projectPath "bin\$Platform\$Configuration"
$exePath = Join-Path $outputPath 'llcom plus.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the application first.' }
# Pure test services only. Never construct Application, Global, Uart, or a Page.
Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework
[void][Reflection.Assembly]::LoadFrom((Join-Path $outputPath 'Newtonsoft.Json.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($exePath)
Add-Type -Path (Join-Path $PSScriptRoot 'AtWorkflowRegressionProbe.cs')
$passed = 0
foreach ($message in [AtWorkflowRegressionProbe]::Run($assembly)) {
    $passed++
    Write-Host "PASS  $message" -ForegroundColor Green
}
$globalSource = [IO.File]::ReadAllText((Join-Path $projectPath 'Core\Tools\Global.cs'))
$windowSource = [IO.File]::ReadAllText((Join-Path $projectPath 'UI\View\MainWindow.xaml.cs'))
$start = $windowSource.IndexOf('private async Task<bool> Global_SendDataRequestAsync(', [StringComparison]::Ordinal)
$end = $windowSource.IndexOf('private void Global_MainSendTargetChangedEvent(', $start, [StringComparison]::Ordinal)
$sendSource = $windowSource.Substring($start, $end - $start)
if ($globalSource -notmatch 'ExpectedTargetIdentity = request.ExpectedTargetIdentity' -or
    $sendSource -notmatch '(?s)Dispatcher.Invoke.*?CaptureActiveSerialTarget\(\).*?target.Identity, request.ExpectedTargetIdentity.*?SetReceiveScriptContext.*?autoOpen: false') {
    throw 'Whole-job identity must be copied and checked on the UI thread before sending, with no automatic open.'
}
$passed++
Write-Host 'PASS  Request cloning and UI target validation preserve the whole-job connection.' -ForegroundColor Green
Write-Host "All $passed AT workflow checks passed ($Platform $Configuration)." -ForegroundColor Green
