[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64',
      [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use matching Windows PowerShell 5.1 -STA.' }
$root=Split-Path -Parent $PSScriptRoot
$output=if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $root "llcom plus\bin\$Platform\$Configuration"
} else { [IO.Path]::GetFullPath($OutputDirectory) }
$tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName='llcom-monitor-ui-' + [Guid]::NewGuid().ToString('N')
$testRoot=Join-Path $tempBase $testName
[void][IO.Directory]::CreateDirectory($testRoot)
$app=$null
$global=$null
$assembly=$null
try {
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
    Get-ChildItem -LiteralPath $output -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
    $global=$assembly.GetType('llcom_plus.Tools.Global',$true)
    $global.GetField('ProfilePath').SetValue($null,$testRoot+'\')
    $settings=[Activator]::CreateInstance($assembly.GetType('llcom_plus.Model.Settings',$true),$true)
    $global.GetField('setting').SetValue($null,$settings)
    [Windows.Application]::ResourceAssembly=$assembly
    $app=[Activator]::CreateInstance($assembly.GetType('llcom_plus.App',$true))
    $app.InitializeComponent()
    [Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
    $app.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
    $references=@([Windows.Controls.Page].Assembly.Location,[Windows.Threading.Dispatcher].Assembly.Location,
        [Windows.Media.Brushes].Assembly.Location,'System.dll','System.Core.dll','System.Xaml.dll')
    Add-Type -Path (Join-Path $PSScriptRoot 'SerialMonitorUiRegressionProbe.cs') -ReferencedAssemblies $references
    foreach ($message in [SerialMonitorUiRegressionProbe]::Run($assembly,$output)) { Write-Host "PASS $message" }
    Write-Host "PASS monitor UI ($Platform $Configuration); no helper was launched, process injected or serial port opened."
} catch {
    $failure=$_.Exception
    while ($null -ne $failure.InnerException) { $failure=$failure.InnerException }
    throw $failure
} finally {
    if ($null -ne $global) {
        $global.GetProperty('isMainWindowsClosed').SetValue($null,$true,$null)
        [void]$assembly.GetType('llcom_plus.Tools.QuickSendBackupService',$true).GetMethod('Shutdown',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,$null)
        [void]$assembly.GetType('llcom_plus.Tools.Logger',$true).GetMethod('CloseUartLog').Invoke($null,$null)
    }
    if ($null -ne $app) { $app.Shutdown() }
    $resolvedRoot=[IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or
        [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or
        $testName -notmatch '^llcom-monitor-ui-[0-9a-f]{32}$') { throw 'Refusing to remove an unowned test profile.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
