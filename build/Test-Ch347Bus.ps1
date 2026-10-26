[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'x86')][string]$Platform = 'x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($PSVersionTable.PSVersion.Major -ne 5 -or
    [Environment]::Is64BitProcess -ne ($Platform -eq 'x64') -or
    [Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    throw "Use $Platform Windows PowerShell 5.1 with -STA to test a $Platform build."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "llcom plus\bin\$Platform\$Configuration"
} else { [IO.Path]::GetFullPath($OutputDirectory) }
$exePath = Join-Path $outputDir 'llcom plus.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the application first.' }
$jsonPath = Join-Path $outputDir 'Newtonsoft.Json.dll'
if (-not (Test-Path -LiteralPath $jsonPath -PathType Leaf)) { throw 'The built application is missing Newtonsoft.Json.dll.' }

# The probe never constructs Global or App. Its preset store receives an explicit
# temporary path. Also isolate process-local profile variables and cwd so an
# accidental relative write cannot touch a real user configuration.
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$testName = 'llcom-ch347-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $tempBase $testName
[void](New-Item -ItemType Directory -Path $testRoot)
$previousCwd = [Environment]::CurrentDirectory
$previousAppData = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
$previousLocalAppData = [Environment]::GetEnvironmentVariable('LOCALAPPDATA', 'Process')
$provider = $null
try {
    [Environment]::CurrentDirectory = $testRoot
    [Environment]::SetEnvironmentVariable('APPDATA', (Join-Path $testRoot 'AppData\Roaming'), 'Process')
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', (Join-Path $testRoot 'AppData\Local'), 'Process')
    [void][Reflection.Assembly]::LoadFrom($jsonPath)
    [void][Reflection.Assembly]::LoadFrom($exePath)

    # A fixed assembly name matches the application's narrow friend-assembly
    # grant; all implementation types remain internal in the shipped application.
    Add-Type -AssemblyName Microsoft.CSharp
    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
    $parameters = New-Object CodeDom.Compiler.CompilerParameters
    $parameters.GenerateExecutable = $false
    $parameters.GenerateInMemory = $false
    $parameters.IncludeDebugInformation = $false
    $parameters.OutputAssembly = Join-Path $testRoot 'LlcomCh347RegressionProbe.dll'
    $parameters.CompilerOptions = "/optimize+ /platform:$Platform"
    foreach ($reference in @('System.dll', 'System.Core.dll', $exePath, $jsonPath)) {
        [void]$parameters.ReferencedAssemblies.Add($reference)
    }
    $probeSource = Join-Path $PSScriptRoot 'Ch347RegressionProbe.cs'
    $compiled = $provider.CompileAssemblyFromFile($parameters, $probeSource)
    $compileErrors = @($compiled.Errors | Where-Object { -not $_.IsWarning })
    if ($compileErrors.Count -gt 0) {
        throw ('CH347 probe compilation failed:' + [Environment]::NewLine +
            (($compileErrors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine))
    }
    $probeAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($parameters.OutputAssembly))
    $probe = $probeAssembly.GetType('Ch347RegressionProbe', $true)
    try {
        $messages = $probe.GetMethod('Run').Invoke($null, [object[]]@([string]$outputDir, [string]$testRoot))
    } catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
    foreach ($message in $messages) { Write-Host "PASS  $message" -ForegroundColor Green }
    Write-Host "All $($messages.Count) CH347 hardware-free regression groups passed ($Platform $Configuration)." -ForegroundColor Green
}
finally {
    if ($null -ne $provider) { $provider.Dispose() }
    [Environment]::CurrentDirectory = $previousCwd
    [Environment]::SetEnvironmentVariable('APPDATA', $previousAppData, 'Process')
    [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $previousLocalAppData, 'Process')
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $tempBase -or
        [IO.Path]::GetFileName($resolvedRoot) -ne $testName -or
        $testName -notmatch '^llcom-ch347-[0-9a-f]{32}$') {
        throw 'Refusing cleanup outside the isolated CH347 test directory.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
