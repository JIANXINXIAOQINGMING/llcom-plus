[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64',
      [string]$OutputDirectory='')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use matching Windows PowerShell 5.1 with -STA.' }
$project = Join-Path (Split-Path -Parent $PSScriptRoot) 'llcom plus'
$outputDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $project "bin\$Platform\$Configuration"
} else { [IO.Path]::GetFullPath($OutputDirectory) }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'llcom plus.exe'))
$references = @([Windows.Controls.Page].Assembly.Location, [Windows.Threading.Dispatcher].Assembly.Location,
    [Windows.Media.Brushes].Assembly.Location, 'System.dll', 'System.Core.dll', 'System.Xaml.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'WorkflowToolGateRegressionProbe.cs') -ReferencedAssemblies $references
foreach ($message in [WorkflowToolGateRegressionProbe]::Run($assembly)) { Write-Host "PASS $message" }
$source = [IO.File]::ReadAllText((Join-Path $project 'UI\View\MainWindow.xaml.cs'))
$captureStart = $source.IndexOf('private ActiveSerialTarget CaptureActiveSerialTargetForTools()', [StringComparison]::Ordinal)
$captureEnd = $source.IndexOf('private ActiveSerialTarget GuardToolSerialTarget(', $captureStart, [StringComparison]::Ordinal)
$capture = $source.Substring($captureStart, $captureEnd - $captureStart)
if ([regex]::Matches($capture, 'return GuardToolSerialTarget\(').Count -ne 3) {
    throw 'Main, split and broadcast tool leases must all use the write guard.'
}
Write-Host "PASS Captured main, split and broadcast tool lease wiring ($Platform $Configuration); no COM was opened."
