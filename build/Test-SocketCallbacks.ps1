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
    'System.dll', 'System.Core.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'SocketCallbackRegressionProbe.cs') -ReferencedAssemblies $references
foreach ($message in [SocketCallbackRegressionProbe]::Run($assembly)) { Write-Host "PASS $message" }
$source = [IO.File]::ReadAllText((Join-Path $project 'UI\Pages\SocketClientPage.xaml.cs'))
if ([regex]::Matches($source, 'data => PublishInteractiveData\(epoch, data\)').Count -ne 2 -or
    $source -notmatch 'text => RunInteractiveCallback\(epoch,') { throw 'Both interactive transports must use the captured callback guard.' }
$readStart = $source.IndexOf('public void Read_Callback(IAsyncResult ar)', [StringComparison]::Ordinal)
$readEnd = $source.IndexOf('private void Reconnect_TextInputCheck(', $readStart, [StringComparison]::Ordinal)
$readSource = $source.Substring($readStart, $readEnd - $readStart)
if ($readSource -notmatch 'PublishNativeData\(state.owner, state.epoch, buffer\)' -or
    $readSource -match 'DataRecived\?\.Invoke') { throw 'Native TCP/UDP receives must use their captured owner/epoch publication guard.' }
Write-Host "PASS TCP, UDP, SSH, TLS/DTLS receive and diagnostic source wiring ($Platform $Configuration)."
