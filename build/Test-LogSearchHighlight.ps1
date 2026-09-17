param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) { throw 'Use matching Windows PowerShell 5.1.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use -STA.' }
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root "llcom plus\bin\$Platform\$Configuration"
$artifacts=Join-Path $root 'artifacts\log-search-highlight'
[void](New-Item -ItemType Directory -Force -Path $artifacts)
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
$references=@([System.Windows.Controls.RichTextBox].Assembly.Location,
    [System.Windows.Media.Brushes].Assembly.Location,
    [System.Windows.Threading.Dispatcher].Assembly.Location,
    'System.dll','System.Core.dll','System.Xaml.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'LogSearchHighlightProbe.cs') -ReferencedAssemblies $references
foreach ($message in [LogSearchHighlightProbe]::Run($assembly, $artifacts)) { Write-Host "PASS $message" }
Write-Host "All log-search highlight checks passed ($Platform $Configuration). No application window or serial port was opened."
