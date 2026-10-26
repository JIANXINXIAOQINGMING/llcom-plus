[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','x86')][string]$Platform = 'x64',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) {
    throw 'Use matching-bitness Windows PowerShell 5.1.'
}
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "llcom plus\bin\$Platform\$Configuration" }
$exe = Join-Path $OutputDirectory 'llcom plus.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build the application first.' }
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($exe))
Add-Type -Path (Join-Path $PSScriptRoot 'SerialPortAccessRegressionProbe.cs')
[SerialPortAccessRegressionProbe]::Run($assembly) | ForEach-Object { Write-Host "PASS $_" }

# Guard the two production entry points as well as the standalone coordinator:
# a future refactor must not leave the helper tested but bypass it at Open().
function Assert-Source([bool]$Condition,[string]$Message) {
    if (-not $Condition) { throw "FAIL $Message" }
    Write-Host "PASS $Message"
}
function Get-SourcePart([string]$Source,[string]$Start,[string]$End) {
    $begin=$Source.IndexOf($Start,[StringComparison]::Ordinal)
    if ($begin -lt 0) { throw "Missing source method: $Start" }
    $finish=$Source.IndexOf($End,$begin+$Start.Length,[StringComparison]::Ordinal)
    if ($finish -lt 0) { throw "Missing source method boundary: $End" }
    return $Source.Substring($begin,$finish-$begin)
}
$uartSource=[IO.File]::ReadAllText((Join-Path $root 'llcom plus\Core\Model\Uart.cs'))
$splitSource=[IO.File]::ReadAllText((Join-Path $root 'llcom plus\UI\Pages\MultiPortPage.xaml.cs'))
$uartOpen=Get-SourcePart $uartSource 'public void Open()' 'public void Close('
Assert-Source ($uartOpen.IndexOf('SerialPortAccessCoordinator.Acquire(') -ge 0 -and
    $uartOpen.IndexOf('SerialPortAccessCoordinator.Acquire(') -lt $uartOpen.IndexOf('serial.Open();')) `
    'Main UART acquires the shared reservation before opening the driver.'
$splitOpen=Get-SourcePart $splitSource 'private bool Open()' 'private void ClearButton_Click('
$reserveAt=$splitOpen.IndexOf('SerialPortAccessCoordinator.Acquire(')
$driverAt=$splitOpen.IndexOf('openingPort.Open();')
Assert-Source ($reserveAt -ge 0 -and $driverAt -gt $reserveAt -and
    $splitOpen.IndexOf('openingPort.PortName = portName;') -gt $reserveAt) `
    'Split UART acquires the shared reservation before configuring or opening its driver.'
$uartRetire=Get-SourcePart $uartSource 'private void RefreshSerialDeviceCore(' 'private void AttachPinMonitor('
$splitRetire=Get-SourcePart $splitSource 'private Task RetireDirectConnection(' 'private static void WaitForDirectRelease('
Assert-Source ($uartRetire.Contains('oldReservation?.BeginRelease();') -and
    $uartRetire.IndexOf('oldReservation?.BeginRelease();') -lt $uartRetire.IndexOf('serial = new SerialPort') -and
    $uartSource.Contains('SerialPortAccessCoordinator.ReleaseAsync(reservation, dispose)')) `
    'Main UART retains its reservation across publication of a closed replacement until asynchronous cleanup completes.'
Assert-Source ($splitRetire.Contains('oldReservation?.BeginRelease();') -and
    $splitRetire.IndexOf('oldReservation?.BeginRelease();') -lt $splitRetire.IndexOf('serial = new SerialPort') -and
    $splitRetire.Contains('SerialPortAccessCoordinator.ReleaseAsync(oldReservation,') -and
    $splitRetire.Contains('SerialPortAccessCoordinator.DisposePort(oldPort, oldStream,')) `
    'Split UART marks the retired reservation before replacing the visible port and releases it only through actual cleanup.'
Write-Host "PASS All serial port access coordinator checks ($Platform); no COM was opened."
