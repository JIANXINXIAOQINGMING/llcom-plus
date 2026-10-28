[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',
      [ValidateSet('x64','x86')][string]$Platform='x64',
      [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use matching Windows PowerShell 5.1 -STA.' }
$project=Join-Path (Split-Path -Parent $PSScriptRoot) 'llcom plus'
$output=if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $project "bin\$Platform\$Configuration"
} else { [IO.Path]::GetFullPath($OutputDirectory) }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
Get-ChildItem -LiteralPath $output -Filter '*.dll' | ForEach-Object {
    try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
}
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
$references=@([Windows.Threading.Dispatcher].Assembly.Location,'System.dll','System.Core.dll','System.Numerics.dll',
    (Join-Path $output 'Jint.dll'),(Join-Path $output 'Acornima.dll'))
Add-Type -Path (Join-Path $PSScriptRoot 'ScriptSessionSendRegressionProbe.cs') -ReferencedAssemblies $references
[ScriptSessionSendRegressionProbe]::InstallDependencyResolution($output)
try {
    foreach ($message in [ScriptSessionSendRegressionProbe]::Run($assembly)) { Write-Host "PASS $message" }
} catch {
    $errorDetail = $_.Exception
    while ($null -ne $errorDetail.InnerException) { $errorDetail = $errorDetail.InnerException }
    throw $errorDetail
}
$uartSource=[IO.File]::ReadAllText((Join-Path $project 'Core\Model\Uart.cs'))
$runtimeSource=[IO.File]::ReadAllText((Join-Path $project 'Core\ScriptEnv\JavaScriptRunEnv.cs'))
$mqttSource=[IO.File]::ReadAllText((Join-Path $project 'UI\Pages\MqttTestPage.xaml.cs'))
if ($uartSource -notmatch '(?s)SendChannelsRegisterCancelable\("uart".*?SendUartFromSession\(data, token, current,' -or
    $runtimeSource -notmatch 'SendFromSession\(channel, data, options, session.Token, \(\) => IsCurrentSession\(session\)\)' -or
    $mqttSource -notmatch 'SendChannelsReceived\("mqtt", new ScriptApis.MqttChannelData\(') {
    throw 'Script runtime identity, cancellable UART registration, or MQTT owned payload wiring is missing.'
}
Write-Host "PASS script session send and MQTT queue wiring ($Platform $Configuration); no hardware or network was used."
