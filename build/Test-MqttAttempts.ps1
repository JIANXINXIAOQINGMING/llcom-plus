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
$mqtt=[Reflection.Assembly]::LoadFrom((Join-Path $output 'MQTTnet.dll'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $output 'llcom plus.exe'))
$references=@([Windows.Controls.Page].Assembly.Location,[Windows.Threading.Dispatcher].Assembly.Location,
    [Windows.Media.Brushes].Assembly.Location,'System.dll','System.Core.dll','System.Xaml.dll')
Add-Type -Path (Join-Path $PSScriptRoot 'MqttAttemptRegressionProbe.cs') -ReferencedAssemblies $references
foreach ($message in [MqttAttemptRegressionProbe]::Run($assembly,$mqtt)) { Write-Host "PASS $message" }
$source=[IO.File]::ReadAllText((Join-Path $project 'UI\Pages\MqttTestPage.xaml.cs'))
if ($source -notmatch 'PublishCaptured\(currentClient, attemptId, topic, payload, qos\)' -or
    $source -notmatch 'CompleteSubscription\(currentClient, attemptId, topic\)' -or
    $source -notmatch '(?s)UseApplicationMessageReceivedHandler.*?RunMqttCallback\(client, attemptId,') {
    throw 'MQTT UI publication, subscription and receive callbacks must keep their captured client/attempt.'
}
Write-Host "PASS MQTT submission and receive wiring ($Platform $Configuration); no broker was contacted."
