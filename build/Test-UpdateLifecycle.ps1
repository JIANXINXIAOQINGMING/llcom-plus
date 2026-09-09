[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64'
)

# Run after building, using same-bitness Windows PowerShell 5.1. This suite never
# opens the application, downloads a package, executes an installer, or accesses
# real processes. Only the extracted wait function runs, against local mocks.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$want64Bit = $Platform -eq 'x64'
if ($PSVersionTable.PSVersion.Major -ne 5 -or
    [Environment]::Is64BitProcess -ne $want64Bit) {
    throw "Use $Platform Windows PowerShell 5.1 to test a $Platform build."
}

$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'llcom plus'
$outputDir = Join-Path $projectDir "bin\$Platform\$Configuration"
$exePath = Join-Path $outputDir 'llcom plus.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Build $Platform $Configuration first; application assembly is missing."
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Write-Pass([string]$Message) {
    $script:passed++
    Write-Host "PASS  $Message" -ForegroundColor Green
}

$script:passed = 0
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$assembly = [Reflection.Assembly]::LoadFrom($exePath)
Add-Type -Path (Join-Path $PSScriptRoot 'UpdateLifecycleRegressionProbe.cs')
foreach ($message in [UpdateLifecycleRegressionProbe]::RunCopyAndInspectionChecks($assembly)) {
    Write-Pass $message
}

$installer = [UpdateLifecycleRegressionProbe]::BuildInstallerText($assembly)
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($installer, [ref]$tokens, [ref]$parseErrors)
Assert-Check ($parseErrors.Count -eq 0) 'The generated installer contains invalid PowerShell syntax.'
Assert-Check ($installer -notmatch '(?i)Stop-Process|taskkill|\.Kill\s*\(|TerminateProcess') `
    'The generated installer still contains a forced process termination path.'
Write-Pass 'The generated installer parses successfully and has no forced process termination.'

$waitFunctions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-ForAppExit'
}, $true))
Assert-Check ($waitFunctions.Count -eq 1) 'Expected exactly one Wait-ForAppExit function.'
$waitText = $waitFunctions[0].Extent.Text
$waitCommands = @($waitFunctions[0].FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst]
}, $true))
foreach ($command in $waitCommands) {
    Assert-Check ($command.GetCommandName() -in @('Get-Date', 'Get-Process', 'Start-Sleep', 'Write-UpdateLog')) `
        "The wait function contains an unmocked command: $($command.GetCommandName())"
}

function Test-OfflineWait([string]$Mode, [string]$FunctionText) {
    $state = [pscustomobject]@{ Seconds = 0.0; Lookups = 0; Sleeps = 0; Logs = 0 }
    $epoch = [datetime]'2026-01-01T00:00:00'
    function Get-Date { return $epoch.AddSeconds($state.Seconds) }
    function Get-Process {
        param([int]$Id, [string]$ErrorAction)
        $state.Lookups++
        if ($Mode -eq 'AlreadyExited' -or ($Mode -eq 'ExitsNormally' -and $state.Lookups -ge 3)) {
            return $null
        }
        if ($Mode -eq 'ReusedPid') { return [pscustomobject]@{ ProcessName = 'unrelated-app' } }
        return [pscustomobject]@{ ProcessName = 'llcom plus' }
    }
    function Start-Sleep {
        param([int]$Milliseconds)
        $state.Sleeps++
        $state.Seconds += $Milliseconds / 1000.0
        if ($state.Sleeps -gt 10) { throw 'Offline wait exceeded the bounded mock clock.' }
    }
    function Write-UpdateLog { param([string]$Message) $state.Logs++ }

    # Only this isolated function definition is evaluated; never the installer
    # body, package extraction, file-copy helpers, or process-launch commands.
    . ([scriptblock]::Create($FunctionText))
    $failure = $null
    try { Wait-ForAppExit -processId 123456 -graceSeconds 2 }
    catch { $failure = $_.Exception.Message }

    if ($Mode -eq 'TimesOut') {
        Assert-Check ($null -ne $failure -and $failure -match 'PID=123456') `
            'A live application did not abort installation at the mock timeout.'
        Assert-Check ($state.Seconds -eq 2 -and $state.Sleeps -eq 4) `
            'Timeout did not honor the grace period or the bounded mock clock.'
    } else {
        Assert-Check ($null -eq $failure) "Normal exit handling failed: $failure"
        if ($Mode -eq 'ExitsNormally') {
            Assert-Check ($state.Lookups -eq 3 -and $state.Sleeps -eq 2) `
                'The wait function did not stop after the mocked application exit.'
        } else {
            Assert-Check ($state.Lookups -eq 1 -and $state.Sleeps -eq 0) `
                'An absent application or reused PID should not be waited on.'
        }
    }
}

foreach ($mode in @('AlreadyExited', 'ExitsNormally', 'ReusedPid', 'TimesOut')) {
    Test-OfflineWait $mode $waitText
    Write-Pass "Wait-ForAppExit handles $mode using only a mock process and clock."
}

Assert-Check ($installer -match '(?s)\$appExited\s*=\s*\$false.*?Wait-ForAppExit\s+-processId.*?\$appExited\s*=\s*\$true.*?Expand-Archive') `
    'Installation must mark appExited only after the wait succeeds and before extracting files.'
Assert-Check ($installer -match '(?s)Show-UpdateFailure\s+\$message\s+if\s*\(\$appExited\s+-and\s+\$restartAfterInstall\s+-and.*?Start-Process') `
    'Failure recovery may restart the application only after its original process has exited.'
Write-Pass 'Installation ordering and failure restart are guarded by a successful application exit.'

$controllerSource = [IO.File]::ReadAllText((Join-Path $projectDir 'UI\View\UpdateCheckController.cs'))
Assert-Check ($controllerSource -notmatch 'Environment\s*\.\s*Exit\s*\(') `
    'UpdateCheckController must allow normal application shutdown instead of Environment.Exit.'
Write-Pass 'The update controller has no forced Environment.Exit shutdown.'

# Closed controllers must be inert even without live controls or a network client.
# Bypass construction to avoid creating any real window/application.
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
$controllerType = $assembly.GetType('llcom_plus.UpdateCheckController', $true)
$closedController = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($controllerType)
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$controllerType.GetField('ownerClosed', $instanceFlags).SetValue($closedController, $true)
foreach ($methodName in @('CheckOnStartupAsync', 'CheckAsync')) {
    $task = $controllerType.GetMethod($methodName, $instanceFlags).Invoke($closedController, $null)
    Assert-Check ($task.IsCompleted -and -not $task.IsFaulted) `
        "A closed controller still attempted work in $methodName."
}
$controllerType.GetMethod('RefreshIndicatorText', $instanceFlags).Invoke($closedController, $null)
Assert-Check ($controllerSource.Contains('owner.Closed += Owner_Closed;') -and
    $controllerSource.Contains('lifetimeCts.Cancel();') -and
    $controllerSource.Contains('CheckLatestAsync(lifetimeToken)') -and
    $controllerSource.Contains('CreateLinkedTokenSource(lifetimeToken)')) `
    'Update checks and downloads must be linked to the owner window lifetime.'
Write-Pass 'Closed update controllers are inert and cancellation is wired to the owner lifetime.'
Write-Host "All $script:passed update lifecycle checks passed ($Platform $Configuration)." -ForegroundColor Green
