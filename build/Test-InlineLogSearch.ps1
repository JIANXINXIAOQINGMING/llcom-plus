param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64')) { throw 'Use matching Windows PowerShell 5.1.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this isolated WPF probe with -STA.' }
$output=Join-Path (Split-Path -Parent $PSScriptRoot) "llcom plus\bin\$Platform\$Configuration"
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
$references=@([System.Windows.Controls.RichTextBox].Assembly.Location,
    [System.Windows.Media.Brushes].Assembly.Location,
    [System.Windows.Threading.Dispatcher].Assembly.Location,
    'System.dll','System.Core.dll','System.Xaml.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'InlineLogSearchProbe.cs') -ReferencedAssemblies $references
foreach ($message in [InlineLogSearchProbe]::Run($assembly)) { Write-Host "PASS $message" }
Write-Host "All inline log search checks passed ($Platform $Configuration). No application window or serial port was opened."
