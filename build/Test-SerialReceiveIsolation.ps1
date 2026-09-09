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
    throw "Use $Platform Windows PowerShell 5.1 to test a $Platform build."
}
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'llcom plus'
$outputDir = Join-Path $projectDir "bin\$Platform\$Configuration"
$exePath = Join-Path $outputDir 'llcom plus.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the application first.' }

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Write-Pass([string]$Message) {
    $script:passed++
    Write-Host "PASS  $Message" -ForegroundColor Green
}
function Get-SourceRegion([string]$Source, [string]$Start, [string]$End) {
    $startAt = $Source.IndexOf($Start, [StringComparison]::Ordinal)
    Assert-Check ($startAt -ge 0) "Source region was not found: $Start"
    $endAt = $Source.IndexOf($End, $startAt + $Start.Length, [StringComparison]::Ordinal)
    Assert-Check ($endAt -gt $startAt) "Source region terminator was not found: $End"
    return $Source.Substring($startAt, $endAt - $startAt)
}

$script:passed = 0
# Framework type loading only; no Application, Page, or control is instantiated.
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
[void][Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'Newtonsoft.Json.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($exePath)
Add-Type -Path (Join-Path $PSScriptRoot 'SerialReceiveIsolationRegressionProbe.cs')
foreach ($message in [SerialReceiveIsolationRegressionProbe]::Run($assembly)) {
    Write-Pass $message
}

# The normal publication/UI-dispatch methods access Global, whose static
# initializer constructs Uart and starts its receive thread. Do not invoke them
# in this hardware-free suite; verify their dispatch wiring explicitly instead.
$uartSource = [IO.File]::ReadAllText((Join-Path $projectDir 'Core\Model\Uart.cs'))
$splitSource = [IO.File]::ReadAllText((Join-Path $projectDir 'UI\Pages\MultiPortPage.xaml.cs'))
$publish = Get-SourceRegion $uartSource 'private void PublishReceivedData(' 'private void RenewDtrWakeAfterReceive('
Assert-Check ($publish -match '(?s)!IsConnectionCurrent\(connection\).*?return;.*?ReceivedCount') `
    'Publication must reject stale connections before updating receive counters.'
Assert-Check ($publish -match '(?s)new UartReceiveEventArgs\(connection\).*?foreach.*?if\s*\(!eventArgs.IsCurrent\)\s*break;\s*handler\(data, eventArgs\)') `
    'Every receive subscriber must be checked again against the captured connection.'
Write-Pass 'Source wiring: main UART rejects stale publication and rechecks before every subscriber.'

$read = $uartSource.Substring($uartSource.IndexOf('private void ReadData()', [StringComparison]::Ordinal))
Assert-Check ($read -match '(?s)var connection = CaptureConnectionLease\(\);\s*var readPort = connection.Port;\s*var profile = connection.Profile;') `
    'The packet aggregation loop must capture one port and profile before reading.'
Assert-Check ($read -match 'readPort.Read\(' -and
    $read -notmatch '\bserial.Read\(' -and
    $read -match 'connection.Generation != Interlocked.Read\(ref connectionGeneration\)' -and
    $read -match 'PublishReceivedData\(result, connection\)') `
    'The main receive loop must not reselect a different port within one packet.'
Write-Pass 'Source wiring: main receive aggregation reads and publishes one fixed port/generation/profile.'

$flush = Get-SourceRegion $splitSource 'private void FlushReceivedData(' 'private void ResetReceiveBuffer('
Assert-Check ($flush -match '(?s)generation = pendingReceiveGeneration;\s*profile = pendingReceiveProfile;') `
    'A split flush must capture the buffered generation and profile before clearing it.'
Assert-Check ($flush -match '(?s)owner.RunOnUi\(\(\) =>.*?lock \(serialLock\).*?if \(serialDisposed \|\| serialTransition \|\| generation != connectionGeneration\)\s*return;.*?WriteDataLog\(data, false, profile\)') `
    'A split UI callback must recheck generation/transition/disposal after dispatch, before logging.'
Write-Pass 'Source wiring: delayed split UI callbacks reject stale generations and retain captured decoding.'

$mainSubscriber = Get-SourceRegion $splitSource 'private void MainUart_UartDataRecived(' 'private void WriteDataLog('
Assert-Check ($mainSubscriber -match '(?s)var received = e as UartReceiveEventArgs;.*?received\?\.Profile.*?owner.RunOnUi.*?!received.IsCurrent.*?return;.*?WriteDataLog\(data, false, profile') `
    'The main-port split subscriber must preserve receive metadata and reject a stale UI callback.'
Write-Pass 'Source wiring: the main-port split subscriber carries its packet profile through UI dispatch.'
Write-Host "All $script:passed serial receive isolation checks passed ($Platform $Configuration)." -ForegroundColor Green
