[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64',
    [switch]$SkipBuild,
    [ValidateRange(1, 26)]
    [int]$CheckFrom = 1,
    [ValidateRange(1, 26)]
    [int]$CheckThrough = 26
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($CheckFrom -gt $CheckThrough) {
    throw 'CheckFrom must be less than or equal to CheckThrough.'
}

# A platform-targeted .NET Framework/WPF executable must run in a same-bitness,
# STA Windows PowerShell 5.1 process. Relaunch from pwsh, ISE, MTA, or the wrong
# architecture before loading the application assembly.
$want64BitProcess = $Platform -eq 'x64'
if ($want64BitProcess -and -not [Environment]::Is64BitOperatingSystem) {
    Write-Host 'FAIL  Setup - an x64 build cannot be tested on 32-bit Windows.' -ForegroundColor Red
    exit 1
}

$powerShellEdition = if ($PSVersionTable.ContainsKey('PSEdition')) {
    [string]$PSVersionTable['PSEdition']
} else {
    'Desktop'
}
$currentProcess = [Diagnostics.Process]::GetCurrentProcess()
try {
    $isConsoleWindowsPowerShell = $currentProcess.ProcessName -ieq 'powershell'
}
finally {
    $currentProcess.Dispose()
}
$isWindowsPowerShell51 =
    $powerShellEdition -eq 'Desktop' -and
    $PSVersionTable.PSVersion.Major -eq 5 -and
    $PSVersionTable.PSVersion.Minor -eq 1 -and
    $isConsoleWindowsPowerShell
$isMatchingBitness = [Environment]::Is64BitProcess -eq $want64BitProcess
$isStaThread = [Threading.Thread]::CurrentThread.GetApartmentState() -eq [Threading.ApartmentState]::STA

if (-not $isWindowsPowerShell51 -or -not $isMatchingBitness -or -not $isStaThread) {
    if ($want64BitProcess) {
        $powerShellCandidates = @(
            (Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'),
            (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe')
        )
    } elseif ([Environment]::Is64BitOperatingSystem) {
        $powerShellCandidates = @(
            (Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe')
        )
    } else {
        $powerShellCandidates = @(
            (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe')
        )
    }

    $matchingPowerShell = $powerShellCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($matchingPowerShell)) {
        Write-Host "FAIL  Setup - could not find $Platform Windows PowerShell 5.1." -ForegroundColor Red
        exit 1
    }

    $versionOutput = & $matchingPowerShell `
        -NoLogo `
        -NoProfile `
        -NonInteractive `
        -Command '$PSVersionTable.PSVersion.ToString()'
    $versionExitCode = $LASTEXITCODE
    $versionText = ([string]($versionOutput | Select-Object -Last 1)).Trim()
    $parsedVersion = $null
    $versionIsValid = [Version]::TryParse($versionText, [ref]$parsedVersion)
    if ($versionExitCode -ne 0 -or
        -not $versionIsValid -or
        $parsedVersion.Major -ne 5 -or
        $parsedVersion.Minor -ne 1) {
        Write-Host "FAIL  Setup - matching Windows PowerShell 5.1 is unavailable (reported '$versionText')." -ForegroundColor Red
        exit 1
    }

    $relaunchArguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-STA',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $MyInvocation.MyCommand.Path,
        '-Configuration',
        $Configuration,
        '-Platform',
        $Platform,
        '-CheckFrom',
        [string]$CheckFrom,
        '-CheckThrough',
        [string]$CheckThrough)
    if ($SkipBuild) {
        $relaunchArguments += '-SkipBuild'
    }
    & $matchingPowerShell @relaunchArguments
    exit $LASTEXITCODE
}
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'llcom plus'
$outputDir = Join-Path $projectDir "bin\$Platform\$Configuration"
$exePath = Join-Path $outputDir 'llcom plus.exe'
$failures = New-Object System.Collections.Generic.List[string]
$executedCheckCount = 0
$ownedWriters = New-Object System.Collections.Generic.List[System.IDisposable]
$tempRoot = $null
$locationPushed = $false
$assembly = $null
$globalType = $null
$loggerType = $null
$globalSettingField = $null
$globalProfilePathField = $null
$originalSetting = $null
$originalProfilePath = $null
$globalStateCaptured = $false
$createdApplication = $null

$staticFlags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$instanceFlags = [Reflection.BindingFlags]'Public,NonPublic,Instance'

function Assert-CriticalCondition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-RequiredType {
    param(
        [Reflection.Assembly]$SourceAssembly,
        [string]$Name
    )

    $type = $SourceAssembly.GetType($Name, $false)
    if ($null -eq $type) {
        throw "Required type was not found: $Name"
    }
    return $type
}

function Get-RequiredMethod {
    param(
        [Type]$Type,
        [string]$Name,
        [Reflection.BindingFlags]$Flags,
        [Type[]]$ParameterTypes
    )

    $method = $Type.GetMethod($Name, $Flags, $null, $ParameterTypes, $null)
    if ($null -eq $method) {
        $signature = ($ParameterTypes | ForEach-Object { $_.FullName }) -join ', '
        throw "Required method was not found: $($Type.FullName).$Name($signature)"
    }
    return $method
}

function Get-ErrorDetail {
    param([object]$ErrorRecord)

    $exception = $ErrorRecord.Exception
    $parts = New-Object System.Collections.Generic.List[string]
    while ($null -ne $exception) {
        $text = $exception.GetType().Name + ': ' + $exception.Message
        if (-not $parts.Contains($text)) {
            [void]$parts.Add($text)
        }
        $exception = $exception.InnerException
    }
    return $parts -join ' -> '
}

function Add-CleanupFailure {
    param(
        [string]$Name,
        [object]$ErrorRecord
    )

    $detail = Get-ErrorDetail $ErrorRecord
    $failure = 'Cleanup {0}: {1}' -f $Name, $detail
    [void]$script:failures.Add($failure)
    Write-Host ("FAIL  {0}" -f $failure) -ForegroundColor Red
}

function Invoke-CriticalCheck {
    param(
        [int]$Number,
        [string]$Name,
        [scriptblock]$Body
    )

    if ($Number -lt $CheckFrom -or $Number -gt $CheckThrough) {
        return
    }
    $script:executedCheckCount++

    try {
        & $Body
        Write-Host ("PASS  {0}) {1}" -f $Number, $Name) -ForegroundColor Green
    }
    catch {
        $detail = Get-ErrorDetail $_
        $failure = ("{0}) {1}: {2}" -f $Number, $Name, $detail)
        [void]$failures.Add($failure)
        Write-Host ("FAIL  {0}" -f $failure) -ForegroundColor Red
    }
}

function Remove-CriticalTempDirectory {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $separatorChars = [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd($separatorChars)
    $fullTempPath = [IO.Path]::GetFullPath($Path).TrimEnd($separatorChars)
    $parentPath = [IO.Path]::GetDirectoryName($fullTempPath)
    $leafName = [IO.Path]::GetFileName($fullTempPath)
    $ownedPrefix = 'llcom-critical-fixes-'
    $ownedGuid = [Guid]::Empty
    $hasOwnedName =
        $leafName.StartsWith($ownedPrefix, [StringComparison]::Ordinal) -and
        [Guid]::TryParseExact(
            $leafName.Substring($ownedPrefix.Length),
            'N',
            [ref]$ownedGuid)
    Assert-CriticalCondition (
        $hasOwnedName -and
        [string]::Equals($parentPath, $tempBase, [StringComparison]::OrdinalIgnoreCase)
    ) "Refusing to remove an unowned temporary path: $fullTempPath"

    $lastRemovalError = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            if ([IO.Directory]::Exists($fullTempPath)) {
                Remove-Item -LiteralPath $fullTempPath -Recurse -Force -ErrorAction Stop
            }
        }
        catch {
            $lastRemovalError = $_
        }

        if (-not [IO.Directory]::Exists($fullTempPath)) {
            return
        }
        if ($attempt -lt 5) {
            Start-Sleep -Milliseconds (100 * $attempt)
        }
    }

    $removalDetail = if ($null -ne $lastRemovalError) {
        Get-ErrorDetail $lastRemovalError
    } else {
        'the directory still exists'
    }
    throw "Could not remove temporary directory after 5 attempts: $fullTempPath ($removalDetail)"
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build-Current.ps1') `
        -Configuration $Configuration `
        -Platform $Platform
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Host "FAIL  Setup - application executable does not exist: $exePath" -ForegroundColor Red
    Write-Host 'Build the requested Configuration/Platform before running this regression script.' -ForegroundColor Yellow
    exit 1
}

try {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('llcom-critical-fixes-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($tempRoot)
    [void][IO.Directory]::CreateDirectory(
        (Join-Path $tempRoot 'user_script_run\logs'))

    Push-Location $outputDir
    $locationPushed = $true

    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName PresentationFramework

    # A CLR-owned worker does not need a PowerShell runspace. It is always a
    # background thread, so even a regression to an uncancellable infinite
    # delay cannot keep this short-lived test host alive.
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;

public sealed class LlcomCriticalInvocationResult
{
    public Thread Worker { get; internal set; }
    public bool Completed { get; internal set; }
    public bool WorkerIsBackground { get; internal set; }
    public Exception Exception { get; internal set; }
}

public static class LlcomCriticalBoundedInvoker
{
    public static LlcomCriticalInvocationResult InvokeStatic(
        MethodInfo method,
        object[] arguments,
        int timeoutMilliseconds)
    {
        if (method == null)
            throw new ArgumentNullException("method");
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException("timeoutMilliseconds");

        var result = new LlcomCriticalInvocationResult();
        var worker = new Thread(delegate()
        {
            try
            {
                method.Invoke(null, arguments);
            }
            catch (Exception ex)
            {
                result.Exception = ex;
            }
        });
        worker.IsBackground = true;
        result.WorkerIsBackground = worker.IsBackground;
        result.Worker = worker;
        worker.Start();
        result.Completed = worker.Join(timeoutMilliseconds);
        return result;
    }
}
'@

    # Preload adjacent managed dependencies. Native or architecture-specific
    # libraries are intentionally ignored here and remain demand-loaded.
    Get-ChildItem -LiteralPath $outputDir -Filter '*.dll' | ForEach-Object {
        try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch { }
    }
    $assembly = [Reflection.Assembly]::LoadFrom($exePath)

    $globalType = Get-RequiredType $assembly 'llcom_plus.Tools.Global'
    $loggerType = Get-RequiredType $assembly 'llcom_plus.Tools.Logger'
    $settingsType = Get-RequiredType $assembly 'llcom_plus.Model.Settings'
    $globalSettingField = $globalType.GetField('setting', [Reflection.BindingFlags]'Public,Static')
    $globalProfilePathField = $globalType.GetField('ProfilePath', [Reflection.BindingFlags]'Public,Static')
    Assert-CriticalCondition ($null -ne $globalSettingField) 'Global.setting was not found.'
    Assert-CriticalCondition ($null -ne $globalProfilePathField) 'Global.ProfilePath was not found.'

    $originalSetting = $globalSettingField.GetValue($null)
    $originalProfilePath = $globalProfilePathField.GetValue($null)
    $globalStateCaptured = $true
    $testSettings = [Activator]::CreateInstance($settingsType, $true)
    $globalProfilePathField.SetValue(
        $null,
        $tempRoot + [IO.Path]::DirectorySeparatorChar)
    $globalSettingField.SetValue($null, $testSettings)

    # PowerShell is the process entry assembly, so explicitly direct relative
    # pack URIs in App.xaml to the loaded application before initialization.
    [System.Windows.Application]::ResourceAssembly = $assembly
    $appType = Get-RequiredType $assembly 'llcom_plus.App'
    Assert-CriticalCondition (
        $null -eq [System.Windows.Application]::Current
    ) 'A WPF Application already exists in the isolated Windows PowerShell host.'
    $createdApplication = [Activator]::CreateInstance($appType)
    $initializeComponent = Get-RequiredMethod `
        $appType `
        'InitializeComponent' `
        ([Reflection.BindingFlags]'Public,Instance') `
        ([Type[]]@())
    [void]$initializeComponent.Invoke($createdApplication, $null)
    Assert-CriticalCondition (
        [object]::ReferenceEquals(
            [System.Windows.Application]::Current,
            $createdApplication) -and
        $createdApplication.Resources.Count -gt 0 -and
        $createdApplication.Resources.MergedDictionaries.Count -gt 0
    ) 'App.xaml resources were not initialized in the test host.'

    Invoke-CriticalCheck 1 'JavaScript negative sleep is rejected by a bounded background probe' {
        $loaderType = Get-RequiredType $assembly 'llcom_plus.ScriptEnv.JavaScriptLoader'
        $sleepMethod = Get-RequiredMethod `
            $loaderType `
            'Sleep' `
            ([Reflection.BindingFlags]'NonPublic,Static') `
            ([Type[]]@(
                [double],
                [TimeSpan],
                [Threading.CancellationToken]))
        $sleepCancellation = New-Object Threading.CancellationTokenSource
        $probeResult = $null
        try {
            $sleepArguments = New-Object object[] 3
            $sleepArguments[0] = [double]-1
            $sleepArguments[1] = [TimeSpan]::FromSeconds(10)
            $sleepArguments[2] = $sleepCancellation.Token
            $probeResult = [LlcomCriticalBoundedInvoker]::InvokeStatic(
                $sleepMethod,
                $sleepArguments,
                5000)

            Assert-CriticalCondition (
                $null -ne $probeResult.Worker -and
                $probeResult.WorkerIsBackground
            ) 'The negative-sleep probe worker was not a background thread.'
            if (-not $probeResult.Completed) {
                $sleepCancellation.Cancel()
                [void]$probeResult.Worker.Join(1000)
                throw 'Sleep(-1) did not finish within the 5-second probe timeout.'
            }

            $negativeSleepException = $probeResult.Exception
            while ($null -ne $negativeSleepException -and
                $null -ne $negativeSleepException.InnerException) {
                $negativeSleepException = $negativeSleepException.InnerException
            }
            Assert-CriticalCondition (
                $negativeSleepException -is [ArgumentOutOfRangeException] -and
                $negativeSleepException.ParamName -eq 'milliseconds'
            ) 'Sleep(-1) did not raise ArgumentOutOfRangeException for milliseconds.'
        }
        finally {
            if ($null -ne $probeResult -and $probeResult.Worker.IsAlive) {
                $sleepCancellation.Cancel()
                [void]$probeResult.Worker.Join(1000)
            }
            if ($null -eq $probeResult -or -not $probeResult.Worker.IsAlive) {
                $sleepCancellation.Dispose()
            }
        }
    }
    Invoke-CriticalCheck 2 'Log replay accepts canonical and legacy records and restores continuation/HEX data' {
        $pageType = Get-RequiredType $assembly 'llcom_plus.Pages.LogReplayPage'
        $parseMethod = Get-RequiredMethod `
            $pageType `
            'ParseLogLines' `
            $staticFlags `
            ([Type[]]@([Collections.Generic.IEnumerable[string]], [bool]))
        $bar = [char]0x2502

        [string[]]$textLines = @(
            ('2026-08-31 12:00:00.000  TX ' + $bar + ' A\n'),
            ('    ' + $bar + ' B'),
            ('2026-08-31 12:00:00.010  RX ' + $bar + ' C'),
            '[2026/08/31 12:00:00.020] [send] D',
            '[2026/08/31 12:00:00.030] [recv] E'
        )
        $textArguments = New-Object object[] 2
        $textArguments[0] = $textLines
        $textArguments[1] = $false
        $textSteps = $parseMethod.Invoke($null, $textArguments)

        Assert-CriticalCondition ($textSteps.Count -eq 4) "Expected 4 mixed-format replay steps, got $($textSteps.Count)."
        Assert-CriticalCondition (
            $textSteps[0].Direction.ToString() -eq 'Send' -and
            [BitConverter]::ToString([byte[]]$textSteps[0].Data) -eq '41-0A-42'
        ) 'Canonical continuation bytes were not restored as A, LF, B.'
        Assert-CriticalCondition (
            $textSteps[1].Direction.ToString() -eq 'Receive' -and
            [BitConverter]::ToString([byte[]]$textSteps[1].Data) -eq '43' -and
            $textSteps[2].Direction.ToString() -eq 'Send' -and
            [BitConverter]::ToString([byte[]]$textSteps[2].Data) -eq '44' -and
            $textSteps[3].Direction.ToString() -eq 'Receive' -and
            [BitConverter]::ToString([byte[]]$textSteps[3].Data) -eq '45'
        ) 'Canonical TX/RX or legacy send/recv directions/data were not preserved.'

        [string[]]$hexLines = @(
            ('2026-08-31 12:00:01.000  TX ' + $bar + ' DE AD BE EF'),
            ('2026-08-31 12:00:01.010  RX ' + $bar + ' 00 7F A5')
        )
        $hexArguments = New-Object object[] 2
        $hexArguments[0] = $hexLines
        $hexArguments[1] = $true
        $hexSteps = $parseMethod.Invoke($null, $hexArguments)
        Assert-CriticalCondition (
            $hexSteps.Count -eq 2 -and
            [BitConverter]::ToString([byte[]]$hexSteps[0].Data) -eq 'DE-AD-BE-EF' -and
            [BitConverter]::ToString([byte[]]$hexSteps[1].Data) -eq '00-7F-A5'
        ) 'Canonical HEX records were not restored byte-for-byte.'
    }

    Invoke-CriticalCheck 3 'Unique session log writers create paired files and use _001 without overwrite' {
        $createWriters = Get-RequiredMethod `
            $loggerType `
            'CreateUniqueSessionLogWriters' `
            $staticFlags `
            ([Type[]]@([string], [string], [string]))
        $stringFolder = Join-Path $tempRoot 'session\STRING'
        $hexFolder = Join-Path $tempRoot 'session\HEX'

        $firstArguments = New-Object object[] 3
        $firstArguments[0] = [string]$stringFolder
        $firstArguments[1] = [string]$hexFolder
        $firstArguments[2] = [string]'capture.log'
        $firstPair = $createWriters.Invoke($null, $firstArguments)
        [void]$ownedWriters.Add([IDisposable]$firstPair.StringWriter)
        [void]$ownedWriters.Add([IDisposable]$firstPair.HexWriter)
        $firstPair.StringWriter.WriteLine('FIRST-STRING')
        $firstPair.HexWriter.WriteLine('FIRST-HEX')

        $secondArguments = New-Object object[] 3
        $secondArguments[0] = [string]$stringFolder
        $secondArguments[1] = [string]$hexFolder
        $secondArguments[2] = [string]'capture.log'
        $secondPair = $createWriters.Invoke($null, $secondArguments)
        [void]$ownedWriters.Add([IDisposable]$secondPair.StringWriter)
        [void]$ownedWriters.Add([IDisposable]$secondPair.HexWriter)
        $secondPair.StringWriter.WriteLine('SECOND-STRING')
        $secondPair.HexWriter.WriteLine('SECOND-HEX')

        foreach ($writer in @(
            $firstPair.StringWriter,
            $firstPair.HexWriter,
            $secondPair.StringWriter,
            $secondPair.HexWriter)) {
            $writer.Flush()
            $writer.Dispose()
        }

        Assert-CriticalCondition (
            $firstPair.FileName -eq 'capture.log' -and
            $secondPair.FileName -eq 'capture_001.log'
        ) 'Collision suffixes were not capture.log and capture_001.log.'
        Assert-CriticalCondition (
            [IO.Path]::GetFileName($firstPair.StringLogFilePath) -eq $firstPair.FileName -and
            [IO.Path]::GetFileName($firstPair.HexLogFilePath) -eq $firstPair.FileName -and
            [IO.Path]::GetFileName($secondPair.StringLogFilePath) -eq $secondPair.FileName -and
            [IO.Path]::GetFileName($secondPair.HexLogFilePath) -eq $secondPair.FileName
        ) 'STRING and HEX writers were not created as filename-matched pairs.'
        Assert-CriticalCondition (
            [IO.File]::ReadAllText($firstPair.StringLogFilePath).Contains('FIRST-STRING') -and
            -not [IO.File]::ReadAllText($firstPair.StringLogFilePath).Contains('SECOND-STRING') -and
            [IO.File]::ReadAllText($firstPair.HexLogFilePath).Contains('FIRST-HEX') -and
            -not [IO.File]::ReadAllText($firstPair.HexLogFilePath).Contains('SECOND-HEX')
        ) 'The second writer pair overwrote the first pair.'
    }

    Invoke-CriticalCheck 4 'Stream data calculation matches byte[] CRC16/CRC32/MD5 for 123456789' {
        $calculatorType = Get-RequiredType $assembly 'llcom_plus.Tools.DataCalcCalculator'
        $calculateBytes = Get-RequiredMethod `
            $calculatorType `
            'Calculate' `
            ([Reflection.BindingFlags]'Public,Static') `
            ([Type[]]@([byte[]]))
        $calculateStream = Get-RequiredMethod `
            $calculatorType `
            'Calculate' `
            ([Reflection.BindingFlags]'Public,Static') `
            ([Type[]]@([IO.Stream]))
        [byte[]]$vector = [Text.Encoding]::ASCII.GetBytes('123456789')

        $byteArguments = New-Object object[] 1
        $byteArguments[0] = $vector
        $byteResult = $calculateBytes.Invoke($null, $byteArguments)

        $stream = New-Object IO.MemoryStream -ArgumentList @(,$vector)
        try {
            $streamArguments = New-Object object[] 1
            $streamArguments[0] = [IO.Stream]$stream
            $streamResult = $calculateStream.Invoke($null, $streamArguments)
        }
        finally {
            $stream.Dispose()
        }

        Assert-CriticalCondition (
            $byteResult.Crc16Modbus -eq '0x4B37' -and
            $byteResult.Crc32 -eq '0xCBF43926' -and
            $byteResult.Md5 -eq '25F9E794323B453885F5181F1B624D0B'
        ) 'The byte[] baseline did not match the standard vector.'
        Assert-CriticalCondition (
            $streamResult.Crc16Modbus -eq $byteResult.Crc16Modbus -and
            $streamResult.Crc32 -eq $byteResult.Crc32 -and
            $streamResult.Md5 -eq $byteResult.Md5
        ) 'Stream CRC16/CRC32/MD5 differed from byte[] results.'
    }

    Invoke-CriticalCheck 5 'Finite and infinite all-zero-delay circular send loops yield' {
        $circularType = Get-RequiredType $assembly 'llcom_plus.Pages.CircularSendPage'
        $shouldYield = Get-RequiredMethod `
            $circularType `
            'ShouldYieldForZeroDelayLoop' `
            $staticFlags `
            ([Type[]]@([int], [bool]))

        $infiniteZero = [bool]$shouldYield.Invoke($null, [object[]]@(0, $true))
        $finiteZero = [bool]$shouldYield.Invoke($null, [object[]]@(1, $true))
        $infiniteDelayed = [bool]$shouldYield.Invoke($null, [object[]]@(0, $false))
        Assert-CriticalCondition (
            $infiniteZero -and $finiteZero -and -not $infiniteDelayed
        ) 'Finite or infinite zero-delay work did not select bounded yielding, or delayed work did.'
    }

    Invoke-CriticalCheck 6 'NTP fixed timestamps produce the expected offset and delay' {
        $socketType = Get-RequiredType $assembly 'llcom_plus.Pages.SocketClientPage'
        $buildRequest = Get-RequiredMethod `
            $socketType `
            'BuildNtpRequest' `
            $staticFlags `
            ([Type[]]@([DateTime]))
        $parseResponse = Get-RequiredMethod `
            $socketType `
            'ParseNtpResponse' `
            $staticFlags `
            ([Type[]]@([byte[]], [byte[]], [DateTime]))
        $writeTimestamp = Get-RequiredMethod `
            $socketType `
            'WriteNtpTimestamp' `
            $staticFlags `
            ([Type[]]@([byte[]], [int], [DateTime]))

        $t1 = [DateTime]::SpecifyKind([DateTime]'2024-01-01 00:00:00', [DateTimeKind]::Utc)
        $t2 = $t1.AddMilliseconds(100)
        $t3 = $t1.AddMilliseconds(140)
        $t4 = $t1.AddMilliseconds(260)

        $buildArguments = New-Object object[] 1
        $buildArguments[0] = $t1
        [byte[]]$request = $buildRequest.Invoke($null, $buildArguments)
        [byte[]]$response = New-Object byte[] 48
        $response[0] = 0x24
        $response[1] = 2
        [Array]::Copy($request, 40, $response, 24, 8)

        $receiveArguments = New-Object object[] 3
        $receiveArguments[0] = $response
        $receiveArguments[1] = 32
        $receiveArguments[2] = $t2
        [void]$writeTimestamp.Invoke($null, $receiveArguments)
        $transmitArguments = New-Object object[] 3
        $transmitArguments[0] = $response
        $transmitArguments[1] = 40
        $transmitArguments[2] = $t3
        [void]$writeTimestamp.Invoke($null, $transmitArguments)

        $parseArguments = New-Object object[] 3
        $parseArguments[0] = $request
        $parseArguments[1] = $response
        $parseArguments[2] = $t4
        $measurement = $parseResponse.Invoke($null, $parseArguments)

        Assert-CriticalCondition (
            $measurement.Mode -eq 4 -and
            $measurement.Version -eq 4 -and
            $measurement.Stratum -eq 2
        ) 'NTP response metadata was not parsed correctly.'
        Assert-CriticalCondition (
            [Math]::Abs($measurement.Offset.TotalMilliseconds - (-10.0)) -lt 0.01 -and
            [Math]::Abs($measurement.Delay.TotalMilliseconds - 220.0) -lt 0.01
        ) ("Expected offset -10 ms and delay 220 ms; got {0} ms and {1} ms." -f
            $measurement.Offset.TotalMilliseconds,
            $measurement.Delay.TotalMilliseconds)
    }

    Invoke-CriticalCheck 7 'HTTP request Transfer-Encoding accepts only a single final chunked coding' {
        $httpType = Get-RequiredType $assembly 'llcom_plus.HttpTools.HttpRequestService'
        $isSupported = Get-RequiredMethod `
            $httpType `
            'IsSupportedRequestTransferEncoding' `
            $staticFlags `
            ([Type[]]@([string]))

        $chunked = [bool]$isSupported.Invoke($null, [object[]]@('chunked'))
        $chunkedCaseInsensitive = [bool]$isSupported.Invoke($null, [object[]]@(' Chunked '))
        $chunkedNotFinal = [bool]$isSupported.Invoke($null, [object[]]@('chunked, gzip'))
        $unknownBeforeChunked = [bool]$isSupported.Invoke($null, [object[]]@('gzip, chunked'))
        $unknownOnly = [bool]$isSupported.Invoke($null, [object[]]@('compress'))
        Assert-CriticalCondition (
            $chunked -and
            $chunkedCaseInsensitive -and
            -not $chunkedNotFinal -and
            -not $unknownBeforeChunked -and
            -not $unknownOnly
        ) 'Transfer-Encoding validation accepted a non-final or unknown coding, or rejected valid chunked.'
    }

    Invoke-CriticalCheck 8 'ActiveSerialTarget keeps its captured identity and reports committed bytes' {
        $targetType = Get-RequiredType $assembly 'llcom_plus.Tools.ActiveSerialTarget'
        $constructors = @($targetType.GetConstructors([Reflection.BindingFlags]'NonPublic,Instance'))
        $constructor = $constructors |
            Where-Object { $_.GetParameters().Count -eq 5 } |
            Select-Object -First 1
        Assert-CriticalCondition ($null -ne $constructor) 'The five-parameter ActiveSerialTarget constructor was not found.'

        $constructorParameters = $constructor.GetParameters()
        $isOpenDelegateType = $constructorParameters[2].ParameterType
        $sendDelegateType = $constructorParameters[3].ParameterType
        $commitDelegateType = [Action[int]]

        $connectionOne = [pscustomobject]@{
            Identity = 'COM7:generation-1'
            IsOpen = $true
            Committed = 0
        }
        $connectionTwo = [pscustomobject]@{
            Identity = 'COM8:generation-2'
            IsOpen = $true
            Committed = 0
        }
        $selection = [pscustomobject]@{ Current = $connectionOne }
        $capturedConnection = $selection.Current

        $isOpenBlock = {
            return [bool]$capturedConnection.IsOpen
        }.GetNewClosure()
        $sendBlock = {
            param(
                [byte[]]$Data,
                [Threading.CancellationToken]$Token,
                [Action[int]]$CommittedBytes
            )
            $Token.ThrowIfCancellationRequested()
            $firstCommit = [Math]::Min(2, $Data.Length)
            $capturedConnection.Committed += $firstCommit
            if ($null -ne $CommittedBytes -and $firstCommit -gt 0) {
                $CommittedBytes.Invoke($firstCommit)
            }
            $remaining = $Data.Length - $firstCommit
            $capturedConnection.Committed += $remaining
            if ($null -ne $CommittedBytes -and $remaining -gt 0) {
                $CommittedBytes.Invoke($remaining)
            }
            return $true
        }.GetNewClosure()

        $isOpenDelegate = [Management.Automation.LanguagePrimitives]::ConvertTo(
            $isOpenBlock,
            $isOpenDelegateType)
        $sendDelegate = [Management.Automation.LanguagePrimitives]::ConvertTo(
            $sendBlock,
            $sendDelegateType)

        $constructorArguments = New-Object object[] 5
        $constructorArguments[0] = [string]$capturedConnection.Identity
        $constructorArguments[1] = 'Captured COM7'
        $constructorArguments[2] = $isOpenDelegate
        $constructorArguments[3] = $sendDelegate
        $constructorArguments[4] = $true
        $target = $constructor.Invoke($constructorArguments)
        $sameTargetReference = $target

        # Change the external selection after capture. The lease and its delegate
        # must continue to address connectionOne, never the new selection.
        $selection.Current = $connectionTwo
        $commitState = [pscustomobject]@{ Total = 0 }
        $commitBlock = {
            param([int]$Count)
            $commitState.Total += $Count
        }.GetNewClosure()
        $commitDelegate = [Management.Automation.LanguagePrimitives]::ConvertTo(
            $commitBlock,
            $commitDelegateType)

        $sendMethod = Get-RequiredMethod `
            $targetType `
            'Send' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@([byte[]], [Threading.CancellationToken], $commitDelegateType))
        $sendArguments = New-Object object[] 3
        $sendArguments[0] = [byte[]](1, 2, 3, 4, 5)
        $sendArguments[1] = [Threading.CancellationToken]::None
        $sendArguments[2] = $commitDelegate
        $sent = [bool]$sendMethod.Invoke($target, $sendArguments)

        Assert-CriticalCondition (
            $sent -and
            $commitState.Total -eq 5 -and
            $connectionOne.Committed -eq 5 -and
            $connectionTwo.Committed -eq 0
        ) 'Send did not accumulate committed bytes on the captured delegate only.'
        Assert-CriticalCondition (
            [object]::ReferenceEquals($sameTargetReference, $target) -and
            $target.Identity -eq 'COM7:generation-1' -and
            $selection.Current.Identity -eq 'COM8:generation-2'
        ) 'An external selection change altered the captured target object or identity.'
    }

    Invoke-CriticalCheck 9 'UART profile snapshots are deep copies' {
        $profileType = Get-RequiredType $assembly 'llcom_plus.Model.UartPortProfile'
        $isolatedSettings = [Activator]::CreateInstance($settingsType, $true)
        $originalProfile = [Activator]::CreateInstance($profileType, $true)
        $profileType.GetProperty('baudRate').SetValue($originalProfile, 57600, $null)
        $profileType.GetProperty('sendScript').SetValue($originalProfile, 'original-script', $null)
        $profileType.GetProperty('hexSend').SetValue($originalProfile, $true, $null)
        $profileType.GetProperty('rts').SetValue($originalProfile, $true, $null)
        $profileType.GetProperty('showHexFormat').SetValue($originalProfile, 2, $null)

        $profilesField = $settingsType.GetField('uartProfiles', [Reflection.BindingFlags]'Public,Instance')
        Assert-CriticalCondition ($null -ne $profilesField) 'Settings.uartProfiles was not found.'
        $profiles = [Collections.IDictionary]$profilesField.GetValue($isolatedSettings)
        $profiles.Add('COM9', $originalProfile)

        $getSnapshot = Get-RequiredMethod `
            $settingsType `
            'GetUartProfileSnapshot' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@([string]))
        $snapshot = $getSnapshot.Invoke($isolatedSettings, [object[]]@('com9'))
        Assert-CriticalCondition (
            $null -ne $snapshot -and
            -not [object]::ReferenceEquals($snapshot, $originalProfile)
        ) 'The profile snapshot reused the stored profile object.'

        $profileType.GetProperty('baudRate').SetValue($snapshot, 115200, $null)
        $profileType.GetProperty('sendScript').SetValue($snapshot, 'mutated-snapshot', $null)
        $profileType.GetProperty('hexSend').SetValue($snapshot, $false, $null)
        $secondSnapshot = $getSnapshot.Invoke($isolatedSettings, [object[]]@('COM9'))

        Assert-CriticalCondition (
            $originalProfile.baudRate -eq 57600 -and
            $originalProfile.sendScript -eq 'original-script' -and
            $originalProfile.hexSend -and
            $secondSnapshot.baudRate -eq 57600 -and
            $secondSnapshot.sendScript -eq 'original-script' -and
            $secondSnapshot.hexSend
        ) 'Mutating a UART profile snapshot polluted the stored profile.'

        $settingsType.GetProperty('showHexFormat').SetValue($isolatedSettings, 1, $null)
        $settingsType.GetProperty('encoding').SetValue($isolatedSettings, 936, $null)
        $settingsType.GetProperty('timeout').SetValue($isolatedSettings, -25, $null)
        $settingsType.GetProperty('sendScript').SetValue($isolatedSettings, 'synced-script', $null)
        $mergeProcessingSettings = Get-RequiredMethod `
            $settingsType `
            'MergeCurrentUartProcessingSettings' `
            $instanceFlags `
            ([Type[]]@($profileType))
        $mergedProfile = $mergeProcessingSettings.Invoke($isolatedSettings, [object[]]@($originalProfile))
        Assert-CriticalCondition (
            $mergedProfile.baudRate -eq 57600 -and
            $mergedProfile.hexSend -and
            $mergedProfile.rts -and
            $mergedProfile.showHexFormat -eq 1 -and
            $mergedProfile.encoding -eq 936 -and
            $mergedProfile.timeout -eq -25 -and
            $mergedProfile.sendScript -eq 'synced-script'
        ) 'Split processing-settings merge did not preserve pane controls or synchronize More Settings.'

        $secondPortProfile = [Activator]::CreateInstance($profileType, $true)
        $profileType.GetProperty('showHexFormat').SetValue($secondPortProfile, 0, $null)
        $profileType.GetProperty('encoding').SetValue($secondPortProfile, 65001, $null)
        $profileType.GetProperty('autoReconnect').SetValue($secondPortProfile, $true, $null)
        $profiles.Add('COM10', $secondPortProfile)
        $setActiveProfile = Get-RequiredMethod `
            $settingsType `
            'SetActiveUartProfile' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@([string], [bool]))
        [void]$setActiveProfile.Invoke($isolatedSettings, [object[]]@('COM9', $false))
        $settingsType.GetProperty('showHexFormat').SetValue($isolatedSettings, 1, $null)
        $settingsType.GetProperty('encoding').SetValue($isolatedSettings, 936, $null)
        $settingsType.GetProperty('autoReconnect').SetValue($isolatedSettings, $false, $null)
        [void]$setActiveProfile.Invoke($isolatedSettings, [object[]]@('COM10', $false))
        $com10Loaded =
            $isolatedSettings.showHexFormat -eq 0 -and
            $isolatedSettings.encoding -eq 65001 -and
            $isolatedSettings.autoReconnect
        [void]$setActiveProfile.Invoke($isolatedSettings, [object[]]@('COM9', $false))
        Assert-CriticalCondition (
            $com10Loaded -and
            $isolatedSettings.showHexFormat -eq 1 -and
            $isolatedSettings.encoding -eq 936 -and
            -not $isolatedSettings.autoReconnect
        ) 'Switching active split COM did not load and preserve each port More Settings profile independently.'
    }

    Invoke-CriticalCheck 10 'Script filename and profile path validation reject traversal' {
        $isValidName = Get-RequiredMethod `
            $globalType `
            'IsValidScriptFileName' `
            $staticFlags `
            ([Type[]]@([string]))
        $tryGetPath = Get-RequiredMethod `
            $globalType `
            'TryGetProfileScriptPath' `
            $staticFlags `
            ([Type[]]@(
                [string],
                [string],
                [string].MakeByRefType(),
                [string].MakeByRefType()))

        $invalidNameAccepted = [bool]$isValidName.Invoke($null, [object[]]@('..\evil'))
        $safeNameAccepted = [bool]$isValidName.Invoke($null, [object[]]@('safe-script'))
        $invalidPathArguments = New-Object object[] 4
        $invalidPathArguments[0] = 'user_script_run'
        $invalidPathArguments[1] = '..\evil'
        $invalidPathArguments[2] = $null
        $invalidPathArguments[3] = $null
        $invalidPathAccepted = [bool]$tryGetPath.Invoke($null, $invalidPathArguments)

        $validPathArguments = New-Object object[] 4
        $validPathArguments[0] = 'user_script_run'
        $validPathArguments[1] = 'safe-script'
        $validPathArguments[2] = $null
        $validPathArguments[3] = $null
        $validPathAccepted = [bool]$tryGetPath.Invoke($null, $validPathArguments)
        $expectedScriptRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot 'user_script_run'))
        $actualScriptRoot = if ($validPathAccepted) {
            [IO.Path]::GetDirectoryName([string]$validPathArguments[3])
        } else {
            ''
        }

        Assert-CriticalCondition (
            -not $invalidNameAccepted -and
            -not $invalidPathAccepted -and
            [string]::IsNullOrEmpty([string]$invalidPathArguments[3]) -and
            $safeNameAccepted -and
            $validPathAccepted -and
            [string]::Equals($expectedScriptRoot, $actualScriptRoot, [StringComparison]::OrdinalIgnoreCase)
        ) 'Traversal was accepted, or the safe script path was not confined to its profile directory.'
    }

    Invoke-CriticalCheck 11 'Paused file identity uses SHA-256 and cancellable verification' {
        $fingerprintType = Get-RequiredType $assembly 'llcom_plus.Tools.DataCalcFileFingerprint'
        $captureStrong = Get-RequiredMethod `
            $fingerprintType `
            'CaptureWithContentIdentity' `
            $staticFlags `
            ([Type[]]@([string], [Threading.CancellationToken]))
        $matchesMetadata = Get-RequiredMethod `
            $fingerprintType `
            'MatchesMetadata' `
            $instanceFlags `
            ([Type[]]@($fingerprintType))
        $matchesContent = Get-RequiredMethod `
            $fingerprintType `
            'MatchesContentIdentity' `
            $instanceFlags `
            ([Type[]]@($fingerprintType))
        $lastWriteProperty = $fingerprintType.GetProperty(
            'LastWriteTimeUtcTicks',
            $instanceFlags)
        Assert-CriticalCondition ($null -ne $lastWriteProperty) 'Fingerprint last-write property was not found.'

        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;

public sealed class LlcomCriticalFileIdentityProbeResult
{
    public bool Completed { get; internal set; }
    public bool MetadataMatches { get; internal set; }
    public bool ContentMatches { get; internal set; }
    public bool CancellationObserved { get; internal set; }
    public string Error { get; internal set; }
}

public static class LlcomCriticalFileIdentityProbe
{
    public static LlcomCriticalFileIdentityProbeResult Run(
        MethodInfo capture,
        MethodInfo matchesMetadata,
        MethodInfo matchesContent,
        PropertyInfo lastWriteTicks,
        string path,
        int timeoutMilliseconds)
    {
        var result = new LlcomCriticalFileIdentityProbeResult();
        var worker = new Thread(delegate()
        {
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                object first = capture.Invoke(
                    null,
                    new object[] { path, CancellationToken.None });
                long ticks = (long)lastWriteTicks.GetValue(first, null);

                File.WriteAllBytes(path, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 });
                File.SetLastWriteTimeUtc(path, new DateTime(ticks, DateTimeKind.Utc));
                object second = capture.Invoke(
                    null,
                    new object[] { path, CancellationToken.None });
                result.MetadataMatches = (bool)matchesMetadata.Invoke(
                    first,
                    new object[] { second });
                result.ContentMatches = (bool)matchesContent.Invoke(
                    first,
                    new object[] { second });

                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    try
                    {
                        capture.Invoke(
                            null,
                            new object[] { path, cancellation.Token });
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null)
                            ex = ex.InnerException;
                        result.CancellationObserved = ex is OperationCanceledException;
                    }
                }
            }
            catch (Exception ex)
            {
                while (ex.InnerException != null)
                    ex = ex.InnerException;
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
        });
        worker.IsBackground = true;
        worker.Start();
        result.Completed = worker.Join(timeoutMilliseconds);
        return result;
    }
}
'@

        $identityPath = Join-Path $tempRoot 'same-metadata-different-content.bin'
        $probe = [LlcomCriticalFileIdentityProbe]::Run(
            $captureStrong,
            $matchesMetadata,
            $matchesContent,
            $lastWriteProperty,
            $identityPath,
            10000)
        Assert-CriticalCondition (
            $probe.Completed -and
            [string]::IsNullOrEmpty($probe.Error) -and
            $probe.MetadataMatches -and
            -not $probe.ContentMatches -and
            $probe.CancellationObserved
        ) ("Strong identity probe failed or timed out: " + $probe.Error)
    }

    Invoke-CriticalCheck 12 'Data calculation cancellation and stale-result guards are enforced' {
        $dataCalcPageType = Get-RequiredType $assembly 'llcom_plus.Pages.DataCalcPage'
        $pageFreshness = Get-RequiredMethod `
            $dataCalcPageType `
            'IsCalculationResultCurrent' `
            $staticFlags `
            ([Type[]]@([int], [int], [bool]))
        $dataCalcViewType = Get-RequiredType $assembly 'llcom_plus.Pages.DataCalcFileSendView'
        $viewFreshness = Get-RequiredMethod `
            $dataCalcViewType `
            'IsCalculationResultCurrent' `
            $staticFlags `
            ([Type[]]@([long], [long], [bool]))

        $pageCurrent = [bool]$pageFreshness.Invoke($null, [object[]]@(7, 7, $false))
        $pageStale = [bool]$pageFreshness.Invoke($null, [object[]]@(6, 7, $false))
        $pageUnloaded = [bool]$pageFreshness.Invoke($null, [object[]]@(7, 7, $true))
        $viewCurrent = [bool]$viewFreshness.Invoke(
            $null,
            [object[]]@([long]9, [long]9, $false))
        $viewStale = [bool]$viewFreshness.Invoke(
            $null,
            [object[]]@([long]8, [long]9, $false))
        $viewUnloaded = [bool]$viewFreshness.Invoke(
            $null,
            [object[]]@([long]9, [long]9, $true))

        $calculatorType = Get-RequiredType $assembly 'llcom_plus.Tools.DataCalcCalculator'
        $calculateCancelable = Get-RequiredMethod `
            $calculatorType `
            'Calculate' `
            ([Reflection.BindingFlags]'Public,Static') `
            ([Type[]]@([IO.Stream], [Threading.CancellationToken]))
        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;

public static class LlcomCriticalReflectionExceptionProbe
{
    private static string GetBaseExceptionTypeName(
        MethodInfo method,
        object[] arguments)
    {
        try
        {
            method.Invoke(null, arguments);
            return String.Empty;
        }
        catch (Exception ex)
        {
            while (ex.InnerException != null)
                ex = ex.InnerException;
            return ex.GetType().FullName;
        }
    }

    public static string GetCancelledCalculationException(MethodInfo method)
    {
        using (var cancellation = new CancellationTokenSource())
        using (var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 }, false))
        {
            cancellation.Cancel();
            return GetBaseExceptionTypeName(
                method,
                new object[] { stream, cancellation.Token });
        }
    }

    public static string GetReplayFileException(MethodInfo method, string path)
    {
        return GetBaseExceptionTypeName(
            method,
            new object[] { path, false });
    }
}
'@
        $calculationExceptionType = [LlcomCriticalReflectionExceptionProbe]::GetCancelledCalculationException(
            $calculateCancelable)
        $calculationCancelled =
            $calculationExceptionType -eq 'System.OperationCanceledException' -or
            $calculationExceptionType -eq 'System.Threading.Tasks.TaskCanceledException'

        Assert-CriticalCondition (
            $pageCurrent -and -not $pageStale -and -not $pageUnloaded -and
            $viewCurrent -and -not $viewStale -and -not $viewUnloaded -and
            $calculationCancelled
        ) ("Calculation guards: pageCurrent={0}, pageStale={1}, pageUnloaded={2}, viewCurrent={3}, viewStale={4}, viewUnloaded={5}, cancelled={6}, exception={7}" -f
            $pageCurrent,
            $pageStale,
            $pageUnloaded,
            $viewCurrent,
            $viewStale,
            $viewUnloaded,
            $calculationCancelled,
            $calculationExceptionType)
    }

    Invoke-CriticalCheck 13 'Log replay gates receive data and enforces explicit resource limits' {
        $pageType = Get-RequiredType $assembly 'llcom_plus.Pages.LogReplayPage'
        $maxReceive = [int]$pageType.GetField('MaxReceiveBufferBytes', $staticFlags).GetRawConstantValue()
        $maxFile = [long]$pageType.GetField('MaxReplayLogFileBytes', $staticFlags).GetRawConstantValue()
        $maxLine = [int]$pageType.GetField('MaxReplayLineCharacters', $staticFlags).GetRawConstantValue()
        $maxRecords = [int]$pageType.GetField('MaxReplayRecords', $staticFlags).GetRawConstantValue()
        $maxSteps = [int]$pageType.GetField('MaxReplaySteps', $staticFlags).GetRawConstantValue()
        $maxField = [int]$pageType.GetField('MaxReplayFieldCharacters', $staticFlags).GetRawConstantValue()
        Assert-CriticalCondition (
            $maxReceive -gt 0 -and $maxFile -gt 0 -and $maxLine -gt 0 -and
            $maxRecords -gt 0 -and $maxSteps -gt 0 -and $maxField -gt 0
        ) 'One or more replay limits were missing or non-positive.'

        $page = [Activator]::CreateInstance($pageType)
        $receiveHandler = Get-RequiredMethod `
            $pageType `
            'ActiveSerialTargetReceived' `
            $instanceFlags `
            ([Type[]]@([object], [byte[]]))
        $receiveBufferField = $pageType.GetField('receiveBuffer', $instanceFlags)
        $captureField = $pageType.GetField('receiveCaptureEnabled', $instanceFlags)
        $stopReplay = Get-RequiredMethod `
            $pageType `
            'StopReplay' `
            $instanceFlags `
            ([Type[]]@())
        Assert-CriticalCondition (
            $null -ne $receiveBufferField -and $null -ne $captureField
        ) 'Replay receive state fields were not found.'

        $smallReceiveArguments = New-Object object[] 2
        $smallReceiveArguments[0] = $null
        $smallReceiveArguments[1] = [byte[]](1, 2, 3)
        [void]$receiveHandler.Invoke($page, $smallReceiveArguments)
        $receiveBuffer = [Collections.IList]$receiveBufferField.GetValue($page)
        $ignoredWhileIdle = $receiveBuffer.Count -eq 0

        $captureField.SetValue($page, 1)
        [byte[]]$largeReceive = New-Object byte[] ($maxReceive + 17)
        $largeReceive[$largeReceive.Length - 1] = 0x5A
        $largeReceiveArguments = New-Object object[] 2
        $largeReceiveArguments[0] = $null
        $largeReceiveArguments[1] = $largeReceive
        [void]$receiveHandler.Invoke($page, $largeReceiveArguments)
        $boundedWhileRunning =
            $receiveBuffer.Count -eq $maxReceive -and
            $receiveBuffer[$receiveBuffer.Count - 1] -eq 0x5A
        [void]$stopReplay.Invoke($page, $null)
        $clearedOnStop = $receiveBuffer.Count -eq 0

        $parseLines = Get-RequiredMethod `
            $pageType `
            'ParseLogLines' `
            $staticFlags `
            ([Type[]]@([Collections.Generic.IEnumerable[string]], [bool]))
        [string[]]$oversizedLines = @(('x' * ($maxLine + 1)))
        $lineRejected = $false
        try {
            $lineArguments = New-Object object[] 2
            $lineArguments[0] = $oversizedLines
            $lineArguments[1] = $false
            [void]$parseLines.Invoke($null, $lineArguments)
        }
        catch {
            $lineException = $_.Exception
            while ($null -ne $lineException.InnerException) {
                $lineException = $lineException.InnerException
            }
            $lineRejected = $lineException -is [IO.InvalidDataException]
        }

        $oversizedLogPath = Join-Path $tempRoot 'oversized-replay.log'
        $oversizedLog = [IO.File]::Open(
            $oversizedLogPath,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $oversizedLog.SetLength($maxFile + 1)
        }
        finally {
            $oversizedLog.Dispose()
        }
        $parseFile = Get-RequiredMethod `
            $pageType `
            'ParseLogFile' `
            $staticFlags `
            ([Type[]]@([string], [bool]))
        $fileExceptionType = [LlcomCriticalReflectionExceptionProbe]::GetReplayFileException(
            $parseFile,
            $oversizedLogPath)
        $fileRejected = $fileExceptionType -eq 'System.IO.InvalidDataException'

        Assert-CriticalCondition (
            $ignoredWhileIdle -and $boundedWhileRunning -and $clearedOnStop -and
            $lineRejected -and $fileRejected
        ) ("Replay guards: idleIgnored={0}, bounded={1}, stopCleared={2}, lineRejected={3}, fileRejected={4}, bufferCount={5}, fileException={6}, fileLength={7}, maxFile={8}" -f
            $ignoredWhileIdle,
            $boundedWhileRunning,
            $clearedOnStop,
            $lineRejected,
            $fileRejected,
            $receiveBuffer.Count,
            $fileExceptionType,
            ([IO.FileInfo]$oversizedLogPath).Length,
            $maxFile)
    }

    Invoke-CriticalCheck 14 'Quick-send import preserves both modes and rejects every configured limit class' {
        $windowType = Get-RequiredType $assembly 'llcom_plus.MainWindow'
        $parseImport = Get-RequiredMethod `
            $windowType `
            'ParseQuickSendImportFile' `
            $staticFlags `
            ([Type[]]@([string], [Threading.CancellationToken]))
        $maxFile = [long]$windowType.GetField('MaxQuickSendImportFileBytes', $staticFlags).GetRawConstantValue()
        $maxDepth = [int]$windowType.GetField('MaxQuickSendImportJsonDepth', $staticFlags).GetRawConstantValue()
        $maxPages = [int]$windowType.GetField('MaxQuickSendImportPages', $staticFlags).GetRawConstantValue()
        $maxItems = [int]$windowType.GetField('MaxQuickSendItemsPerPage', $staticFlags).GetRawConstantValue()
        $maxField = [int]$windowType.GetField('MaxQuickSendFieldCharacters', $staticFlags).GetRawConstantValue()
        $maxTotal = [long]$windowType.GetField('MaxQuickSendTotalCharacters', $staticFlags).GetRawConstantValue()
        Assert-CriticalCondition (
            $maxFile -gt 0 -and $maxDepth -gt 0 -and $maxPages -gt 0 -and
            $maxItems -gt 0 -and $maxField -gt 0 -and $maxTotal -gt 0
        ) 'One or more quick-send import limits were missing or non-positive.'

        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;

public sealed class LlcomCriticalQuickImportProbeResult
{
    public bool Completed { get; internal set; }
    public bool SingleModePreserved { get; internal set; }
    public bool AllModePreserved { get; internal set; }
    public int RejectedLimitCases { get; internal set; }
    public string Error { get; internal set; }
}

public static class LlcomCriticalQuickImportProbe
{
    public static LlcomCriticalQuickImportProbeResult Run(
        MethodInfo parser,
        string singlePath,
        string allPath,
        string[] invalidPaths,
        int timeoutMilliseconds)
    {
        var result = new LlcomCriticalQuickImportProbeResult();
        var worker = new Thread(delegate()
        {
            try
            {
                object single = parser.Invoke(
                    null,
                    new object[] { singlePath, CancellationToken.None });
                Type resultType = single.GetType();
                PropertyInfo importsAll = resultType.GetProperty("ImportsAllPages");
                PropertyInfo pages = resultType.GetProperty("Pages");
                PropertyInfo names = resultType.GetProperty("PageNames");
                result.SingleModePreserved =
                    !(bool)importsAll.GetValue(single, null) &&
                    ((ICollection)pages.GetValue(single, null)).Count == 1;

                object all = parser.Invoke(
                    null,
                    new object[] { allPath, CancellationToken.None });
                result.AllModePreserved =
                    (bool)importsAll.GetValue(all, null) &&
                    ((ICollection)pages.GetValue(all, null)).Count == 2 &&
                    ((ICollection)names.GetValue(all, null)).Count == 2;

                foreach (string invalidPath in invalidPaths)
                {
                    try
                    {
                        parser.Invoke(
                            null,
                            new object[] { invalidPath, CancellationToken.None });
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null)
                            ex = ex.InnerException;
                        if (ex is InvalidDataException)
                            result.RejectedLimitCases++;
                    }
                }
            }
            catch (Exception ex)
            {
                while (ex.InnerException != null)
                    ex = ex.InnerException;
                result.Error = ex.GetType().Name + ": " + ex.Message;
            }
        });
        worker.IsBackground = true;
        worker.Start();
        result.Completed = worker.Join(timeoutMilliseconds);
        return result;
    }
}
'@

        $singlePath = Join-Path $tempRoot 'quick-single.json'
        [IO.File]::WriteAllText(
            $singlePath,
            '[{"id":1,"text":"AT","hex":false,"commit":"Send","appendCrlf":true}]',
            [Text.Encoding]::UTF8)

        $allPath = Join-Path $tempRoot 'quick-all.json'
        [IO.File]::WriteAllText(
            $allPath,
            '{"type":"llcom_plus.quickSend.all","quickSendList":[[{"text":"A"}],[{"text":"B"}]],"quickSendListNames":["One","Two"]}',
            [Text.Encoding]::UTF8)

        $oversizedPath = Join-Path $tempRoot 'quick-too-large.json'
        $oversizedFile = [IO.File]::Open(
            $oversizedPath,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $oversizedFile.SetLength($maxFile + 1)
        }
        finally {
            $oversizedFile.Dispose()
        }

        $deepPath = Join-Path $tempRoot 'quick-too-deep.json'
        $deepCount = $maxDepth + 2
        [IO.File]::WriteAllText(
            $deepPath,
            ('[' * $deepCount) + '0' + (']' * $deepCount),
            [Text.Encoding]::UTF8)

        $pagesPath = Join-Path $tempRoot 'quick-too-many-pages.json'
        [IO.File]::WriteAllText(
            $pagesPath,
            '[' + ((1..($maxPages + 1) | ForEach-Object { '[]' }) -join ',') + ']',
            [Text.Encoding]::UTF8)

        $fieldPath = Join-Path $tempRoot 'quick-field-too-long.json'
        [IO.File]::WriteAllText(
            $fieldPath,
            '[{"text":"' + ('a' * ($maxField + 1)) + '"}]',
            [Text.Encoding]::UTF8)

        $quickImportProbe = [LlcomCriticalQuickImportProbe]::Run(
            $parseImport,
            $singlePath,
            $allPath,
            [string[]]@($oversizedPath, $deepPath, $pagesPath, $fieldPath),
            15000)
        Assert-CriticalCondition (
            $quickImportProbe.Completed -and
            [string]::IsNullOrEmpty($quickImportProbe.Error) -and
            $quickImportProbe.SingleModePreserved -and
            $quickImportProbe.AllModePreserved -and
            $quickImportProbe.RejectedLimitCases -eq 4
        ) ("Quick import probe failed or timed out: completed={0}, single={1}, all={2}, rejected={3}, error={4}" -f
            $quickImportProbe.Completed,
            $quickImportProbe.SingleModePreserved,
            $quickImportProbe.AllModePreserved,
            $quickImportProbe.RejectedLimitCases,
            $quickImportProbe.Error)
    }

    Invoke-CriticalCheck 15 'Script runtime queue is count/byte bounded, copies byte payloads, and retire drains state' {
        $runEnvType = Get-RequiredType $assembly 'llcom_plus.ScriptEnv.JavaScriptRunEnv'
        $sessionType = $runEnvType.GetNestedType(
            'RuntimeSession',
            [Reflection.BindingFlags]'NonPublic')
        Assert-CriticalCondition ($null -ne $sessionType) 'RuntimeSession was not found.'
        $sessionConstructor = $sessionType.GetConstructor(
            $instanceFlags,
            $null,
            [Type[]]@([long]),
            $null)
        Assert-CriticalCondition ($null -ne $sessionConstructor) 'RuntimeSession(long) was not found.'
        $tryEnqueue = Get-RequiredMethod `
            $sessionType `
            'TryEnqueue' `
            $instanceFlags `
            ([Type[]]@([int], [string], [object]))
        $tryDequeue = Get-RequiredMethod `
            $sessionType `
            'TryDequeue' `
            $instanceFlags `
            ([Type[]]@($assembly.GetType('llcom_plus.ScriptEnv.ScriptPool', $true).MakeByRefType()))
        $getSnapshot = Get-RequiredMethod `
            $sessionType `
            'GetQueueSnapshot' `
            $instanceFlags `
            ([Type[]]@())
        $retire = Get-RequiredMethod `
            $sessionType `
            'Retire' `
            $instanceFlags `
            ([Type[]]@())
        $maxEvents = [int]$runEnvType.GetField(
            'MaxQueuedEventCount',
            $staticFlags).GetRawConstantValue()
        $maxPayload = [long]$runEnvType.GetField(
            'MaxQueuedPayloadBytes',
            $staticFlags).GetRawConstantValue()
        $dropPolicy = [string]$runEnvType.GetField(
            'QueueDropPolicy',
            $staticFlags).GetRawConstantValue()
        Assert-CriticalCondition (
            $maxEvents -gt 0 -and $maxPayload -gt 0 -and $dropPolicy -eq 'DropNewest'
        ) 'Queue limits or the observable DropNewest policy were missing.'

        $session = $sessionConstructor.Invoke([object[]]@([long]9001))
        $payloadSession = $null
        try {
            [byte[]]$externalPayload = 1, 2, 3
            $enqueueArguments = New-Object object[] 3
            $enqueueArguments[0] = -1
            $enqueueArguments[1] = 'copy-probe'
            $enqueueArguments[2] = $externalPayload
            $copyAccepted = [bool]$tryEnqueue.Invoke($session, $enqueueArguments)
            $externalPayload[0] = 0x7F
            $dequeueArguments = New-Object object[] 1
            $dequeueArguments[0] = $null
            $copyDequeued = [bool]$tryDequeue.Invoke($session, $dequeueArguments)
            [byte[]]$ownedPayload = $dequeueArguments[0].data

            $callbacks = $sessionType.GetProperty(
                'ChannelCallbacks',
                $instanceFlags).GetValue($session, $null)
            $callbackValueType = $callbacks.GetType().GetGenericArguments()[1]
            $undefinedField = $callbackValueType.GetField(
                'Undefined',
                [Reflection.BindingFlags]'Public,Static')
            $undefinedProperty = $callbackValueType.GetProperty(
                'Undefined',
                [Reflection.BindingFlags]'Public,Static')
            $undefinedValue = if ($null -ne $undefinedField) {
                $undefinedField.GetValue($null)
            } else {
                $undefinedProperty.GetValue($null, $null)
            }
            $tryAddCallback = Get-RequiredMethod `
                $callbacks.GetType() `
                'TryAdd' `
                ([Reflection.BindingFlags]'Public,Instance') `
                ([Type[]]@([string], $callbackValueType))
            [void]$tryAddCallback.Invoke(
                $callbacks,
                [object[]]@('copy-probe', $undefinedValue))

            $acceptedCount = 0
            for ($eventIndex = 0; $eventIndex -lt ($maxEvents + 1); $eventIndex++) {
                $eventArguments = New-Object object[] 3
                $eventArguments[0] = $eventIndex
                $eventArguments[1] = 'count-probe'
                $eventArguments[2] = [byte[]](0x2A)
                if ([bool]$tryEnqueue.Invoke($session, $eventArguments)) {
                    $acceptedCount++
                }
            }
            $boundedSnapshot = $getSnapshot.Invoke($session, $null)
            Assert-CriticalCondition (
                $copyAccepted -and $copyDequeued -and
                $ownedPayload[0] -eq 1 -and
                $acceptedCount -eq $maxEvents -and
                $boundedSnapshot.QueuedEventCount -eq $maxEvents -and
                $boundedSnapshot.QueuedPayloadBytes -le $maxPayload -and
                $boundedSnapshot.DroppedEventCount -ge 1 -and
                $boundedSnapshot.CallbackCount -eq 1
            ) 'Count bounding, byte ownership, or callback diagnostics were incorrect.'

            [void]$retire.Invoke($session, $null)
            $retiredSnapshot = $getSnapshot.Invoke($session, $null)
            $afterRetireArguments = New-Object object[] 3
            $afterRetireArguments[0] = 1
            $afterRetireArguments[1] = 'retired'
            $afterRetireArguments[2] = [byte[]](1)
            $acceptedAfterRetire = [bool]$tryEnqueue.Invoke($session, $afterRetireArguments)
            $rejectedSnapshot = $getSnapshot.Invoke($session, $null)
            Assert-CriticalCondition (
                $retiredSnapshot.IsRetired -and
                $retiredSnapshot.QueuedEventCount -eq 0 -and
                $retiredSnapshot.QueuedPayloadBytes -eq 0 -and
                $retiredSnapshot.CallbackCount -eq 0 -and
                -not $acceptedAfterRetire -and
                $rejectedSnapshot.RejectedAfterRetireCount -ge 1
            ) 'Retire did not reject producers and clear payload/callback references.'

            $payloadSession = $sessionConstructor.Invoke([object[]]@([long]9002))
            $largeLength = [int]([Math]::Floor($maxPayload / 2) + 1)
            [byte[]]$largePayload = New-Object byte[] $largeLength
            $largeArguments = New-Object object[] 3
            $largeArguments[0] = 1
            $largeArguments[1] = 'payload-probe'
            $largeArguments[2] = $largePayload
            $firstLargeAccepted = [bool]$tryEnqueue.Invoke($payloadSession, $largeArguments)
            $secondLargeAccepted = [bool]$tryEnqueue.Invoke($payloadSession, $largeArguments)
            $payloadSnapshot = $getSnapshot.Invoke($payloadSession, $null)
            Assert-CriticalCondition (
                $firstLargeAccepted -and -not $secondLargeAccepted -and
                $payloadSnapshot.QueuedPayloadBytes -le $maxPayload -and
                $payloadSnapshot.DroppedEventCount -eq 1
            ) 'Total payload-byte bounding did not reject the newest oversized aggregate.'
        }
        finally {
            try { [void]$retire.Invoke($session, $null) } catch { }
            if ($null -ne $payloadSession) {
                try { [void]$retire.Invoke($payloadSession, $null) } catch { }
            }
        }
    }

    Invoke-CriticalCheck 16 'JavaScript Run, New, Require, and SettingWindow enforce canonical script roots' {
        $loaderType = Get-RequiredType $assembly 'llcom_plus.ScriptEnv.JavaScriptLoader'
        $tryConverterPath = Get-RequiredMethod `
            $loaderType `
            'TryGetConverterScriptPath' `
            $staticFlags `
            ([Type[]]@(
                [string],
                [string],
                [string].MakeByRefType()))
        $tryGetRunPath = Get-RequiredMethod `
            $globalType `
            'TryGetProfileScriptPathFromRelativePath' `
            $staticFlags `
            ([Type[]]@(
                [string],
                [string],
                [string].MakeByRefType(),
                [string].MakeByRefType()))

        $sendRoot = Join-Path $tempRoot 'user_script_send_convert'
        $receiveRoot = Join-Path $tempRoot 'user_script_recv_convert'
        $runRoot = Join-Path $tempRoot 'user_script_run'
        $requiresRoot = Join-Path $runRoot 'requires'
        $coreRoot = Join-Path $tempRoot 'core_script'
        foreach ($directory in @($sendRoot, $receiveRoot, $runRoot, $requiresRoot, $coreRoot)) {
            [void][IO.Directory]::CreateDirectory($directory)
        }

        $sendArguments = New-Object object[] 3
        $sendArguments[0] = 'user_script_send_convert/'
        $sendArguments[1] = 'safe.js'
        $sendArguments[2] = $null
        $safeSendAccepted = [bool]$tryConverterPath.Invoke($null, $sendArguments)
        $receiveArguments = New-Object object[] 3
        $receiveArguments[0] = 'user_script_recv_convert\'
        $receiveArguments[1] = 'safe'
        $receiveArguments[2] = $null
        $safeReceiveAccepted = [bool]$tryConverterPath.Invoke($null, $receiveArguments)

        $invalidConverterCases = @(
            @('user_script_send_convert/', '..\outside.js'),
            @('user_script_send_convert/', 'nested\safe.js'),
            @('user_script_send_convert/', 'nested/safe.js'),
            @('user_script_send_convert/', '.'),
            @('user_script_send_convert/', '..'),
            @('user_script_send_convert/', (Join-Path $tempRoot 'outside.js')),
            @('..\user_script_send_convert\', 'safe.js'),
            @('user_script_send_convert\nested\', 'safe.js'),
            @($sendRoot, 'safe.js'),
            @('user_script_run/', 'safe.js'))
        $rejectedConverterCases = 0
        foreach ($invalidCase in $invalidConverterCases) {
            $arguments = New-Object object[] 3
            $arguments[0] = [string]$invalidCase[0]
            $arguments[1] = [string]$invalidCase[1]
            $arguments[2] = $null
            if (-not [bool]$tryConverterPath.Invoke($null, $arguments)) {
                $rejectedConverterCases++
            }
        }

        $safeRunArguments = New-Object object[] 4
        $safeRunArguments[0] = 'user_script_run'
        $safeRunArguments[1] = 'user_script_run/safe.js'
        $safeRunArguments[2] = $null
        $safeRunArguments[3] = $null
        $safeRunAccepted = [bool]$tryGetRunPath.Invoke($null, $safeRunArguments)
        [string[]]$invalidNewPaths = @(
            'user_script_run\..\outside.js',
            'user_script_run\.\safe.js',
            'user_script_run\nested\safe.js',
            '..\outside.js',
            'safe.js',
            (Join-Path $tempRoot 'outside.js'))
        $rejectedNewPaths = 0
        foreach ($invalidNewPath in $invalidNewPaths) {
            $arguments = New-Object object[] 4
            $arguments[0] = 'user_script_run'
            $arguments[1] = $invalidNewPath
            $arguments[2] = $null
            $arguments[3] = $null
            if (-not [bool]$tryGetRunPath.Invoke($null, $arguments)) {
                $rejectedNewPaths++
            }
        }

        $loaderSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'Core\ScriptEnv\JavaScriptLoader.cs'))
        $scriptApisSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'Core\ScriptEnv\ScriptApis.cs'))
        $runEnvSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'Core\ScriptEnv\JavaScriptRunEnv.cs'))
        $settingWindowSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'UI\View\SettingWindow.xaml.cs'))
        $loaderUsesCanonicalPaths =
            $loaderSource.Contains('TryGetConverterScriptPath') -and
            $loaderSource.Contains('TryGetCanonicalScriptPath') -and
            $loaderSource.Contains('IsValidScriptFileName') -and
            $loaderSource.Contains('"user_script_run", "requires"') -and
            $loaderSource.Contains('"core_script"')
        $inputCancellationWired =
            $scriptApisSource.Contains('cancellationToken.Register(Cancel)') -and
            $scriptApisSource.Contains('completion.TrySetCanceled()') -and
            $scriptApisSource.Contains('CloseOnDispatcher') -and
            $loaderSource.Contains('ScriptApis.InputBox(prompt, defaultInput, title, cancellationToken)') -and
            $runEnvSource.Contains('cancellationTokenSource.Cancel()')
        $newUsesCanonicalHelper =
            $runEnvSource.Contains('TryGetProfileScriptPathFromRelativePath')
        $settingWindowUsesCanonicalPaths =
            $settingWindowSource.Contains('TryGetProfileScriptPath') -and
            $settingWindowSource.Contains('TryGetCanonicalScriptPath') -and
            $settingWindowSource.Contains('SearchOption.TopDirectoryOnly')

        $expectedSendPath = [IO.Path]::GetFullPath((Join-Path $sendRoot 'safe.js'))
        $expectedReceivePath = [IO.Path]::GetFullPath((Join-Path $receiveRoot 'safe.js'))
        $expectedRunPath = [IO.Path]::GetFullPath((Join-Path $runRoot 'safe.js'))
        Assert-CriticalCondition (
            $safeSendAccepted -and
            [string]::Equals($expectedSendPath, [string]$sendArguments[2], [StringComparison]::OrdinalIgnoreCase) -and
            $safeReceiveAccepted -and
            [string]::Equals($expectedReceivePath, [string]$receiveArguments[2], [StringComparison]::OrdinalIgnoreCase) -and
            $rejectedConverterCases -eq $invalidConverterCases.Count -and
            $safeRunAccepted -and
            [string]::Equals($expectedRunPath, [string]$safeRunArguments[3], [StringComparison]::OrdinalIgnoreCase) -and
            $rejectedNewPaths -eq $invalidNewPaths.Count -and
            $loaderUsesCanonicalPaths -and
            $inputCancellationWired -and
            $newUsesCanonicalHelper -and
            $settingWindowUsesCanonicalPaths
        ) ("Script path/input validation failed: send={0}, receive={1}, converter={2}/{3}, run={4}, new={5}/{6}, loaderRoots={7}, inputCancel={8}, newHelper={9}, setting={10}" -f
            $safeSendAccepted,
            $safeReceiveAccepted,
            $rejectedConverterCases,
            $invalidConverterCases.Count,
            $safeRunAccepted,
            $rejectedNewPaths,
            $invalidNewPaths.Count,
            $loaderUsesCanonicalPaths,
            $inputCancellationWired,
            $newUsesCanonicalHelper,
            $settingWindowUsesCanonicalPaths)
    }

    Invoke-CriticalCheck 17 'Quick-send snapshots are immutable, isolated, and race-free during page add/remove' {
        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections;
using System.Reflection;
using System.Threading;

public sealed class LlcomQuickSnapshotProbeResult
{
    public bool Completed { get; internal set; }
    public string Error { get; internal set; }
}

public static class LlcomQuickSnapshotConcurrencyProbe
{
    public static LlcomQuickSnapshotProbeResult Run(
        object settings,
        MethodInfo getSnapshot,
        MethodInfo addPage,
        MethodInfo removePage,
        int timeoutMilliseconds)
    {
        var result = new LlcomQuickSnapshotProbeResult();
        var start = new ManualResetEventSlim(false);
        Exception failure = null;
        var reader = new Thread(delegate()
        {
            try
            {
                start.Wait();
                for (int index = 0; index < 500; index++)
                {
                    var snapshot = (ICollection)getSnapshot.Invoke(settings, null);
                    int ignored = snapshot.Count;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        var writer = new Thread(delegate()
        {
            try
            {
                start.Wait();
                for (int index = 0; index < 12; index++)
                {
                    int page = (int)addPage.Invoke(settings, null);
                    removePage.Invoke(settings, new object[] { page });
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        reader.IsBackground = true;
        writer.IsBackground = true;
        reader.Start();
        writer.Start();
        start.Set();
        bool readerDone = reader.Join(timeoutMilliseconds);
        bool writerDone = writer.Join(timeoutMilliseconds);
        result.Completed = readerDone && writerDone;
        if (failure != null)
        {
            while (failure.InnerException != null)
                failure = failure.InnerException;
            result.Error = failure.GetType().Name + ": " + failure.Message;
        }
        start.Dispose();
        return result;
    }
}
'@
        $isolatedSettings = [Activator]::CreateInstance($settingsType, $true)
        $suspendSaveField = $settingsType.GetField('_suspendSave', $instanceFlags)
        Assert-CriticalCondition ($null -ne $suspendSaveField) 'Settings save-suspension field was not found.'
        $suspendSaveField.SetValue($isolatedSettings, $true)
        $toSendType = Get-RequiredType $assembly 'llcom_plus.Model.ToSendData'
        $item = [Activator]::CreateInstance($toSendType, $true)
        $item.id = 1
        $item.text = 'before'
        $item.hex = $true
        $outerField = $settingsType.GetField('quickSendList', [Reflection.BindingFlags]'Public,Instance')
        $namesField = $settingsType.GetField('quickListNames', [Reflection.BindingFlags]'Public,Instance')
        $outer = $outerField.GetValue($isolatedSettings)
        $outer.Clear()
        $pageType = $outer.GetType().GetGenericArguments()[0]
        $page = [Activator]::CreateInstance($pageType)
        [void]$page.Add($item)
        [void]$outer.Add($page)
        $names = $namesField.GetValue($isolatedSettings)
        $names.Clear()
        [void]$names.Add('Snapshot page')

        $getQuickSnapshot = Get-RequiredMethod `
            $settingsType `
            'GetQuickSendSnapshot' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@())
        $snapshot = [Collections.IList]$getQuickSnapshot.Invoke($isolatedSettings, $null)
        $snapshotItem = $snapshot[0]
        $item.text = 'after'
        $item.hex = $false
        $snapshotType = $snapshotItem.GetType()
        $immutableAndIsolated =
            $snapshot.IsReadOnly -and
            -not $snapshotType.GetProperty('Text').CanWrite -and
            -not $snapshotType.GetProperty('Hex').CanWrite -and
            $snapshotItem.Text -eq 'before' -and
            $snapshotItem.Hex

        $addPage = Get-RequiredMethod `
            $settingsType `
            'AddQuickSendPage' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@())
        $removePage = Get-RequiredMethod `
            $settingsType `
            'RemoveQuickSendPage' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@([int]))
        $concurrencyProbe = [LlcomQuickSnapshotConcurrencyProbe]::Run(
            $isolatedSettings,
            $getQuickSnapshot,
            $addPage,
            $removePage,
            15000)
        Assert-CriticalCondition (
            $immutableAndIsolated -and
            $concurrencyProbe.Completed -and
            [string]::IsNullOrEmpty($concurrencyProbe.Error)
        ) ("Quick-send snapshot probe failed: immutable={0}, completed={1}, error={2}" -f
            $immutableAndIsolated,
            $concurrencyProbe.Completed,
            $concurrencyProbe.Error)
    }

    Invoke-CriticalCheck 18 'Normal script stop raises ScriptStopped without ScriptRunError' {
        Add-Type -Language CSharp -TypeDefinition @'
using System;

public static class LlcomLifecycleEventProbe
{
    public static int ErrorCount;
    public static int StoppedCount;
    public static readonly EventHandler ErrorHandler = delegate { ErrorCount++; };
    public static readonly EventHandler StoppedHandler = delegate { StoppedCount++; };

    public static void Reset()
    {
        ErrorCount = 0;
        StoppedCount = 0;
    }
}
'@
        $runEnvType = Get-RequiredType $assembly 'llcom_plus.ScriptEnv.JavaScriptRunEnv'
        $errorEvent = $runEnvType.GetEvent('ScriptRunError', [Reflection.BindingFlags]'Public,Static')
        $stoppedEvent = $runEnvType.GetEvent('ScriptStopped', [Reflection.BindingFlags]'Public,Static')
        $stopScript = Get-RequiredMethod `
            $runEnvType `
            'StopScript' `
            ([Reflection.BindingFlags]'Public,Static') `
            ([Type[]]@([string]))
        Assert-CriticalCondition (
            $null -ne $errorEvent -and
            $null -ne $stoppedEvent
        ) 'Faulted/stopped lifecycle events were not available.'
        $scriptLogFileField = $loggerType.GetField('scriptLogFile', $staticFlags)
        Assert-CriticalCondition ($null -ne $scriptLogFileField) 'Script logger field was not found.'
        $originalScriptLogger = $scriptLogFileField.GetValue($null)
        $serilogAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $outputDir 'Serilog.dll'))
        $loggerConfigurationType = $serilogAssembly.GetType('Serilog.LoggerConfiguration', $true)
        $loggerConfiguration = [Activator]::CreateInstance($loggerConfigurationType)
        $createLogger = Get-RequiredMethod `
            $loggerConfigurationType `
            'CreateLogger' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@())
        $noOpScriptLogger = $createLogger.Invoke($loggerConfiguration, $null)
        $scriptLogFileField.SetValue($null, $noOpScriptLogger)
        [LlcomLifecycleEventProbe]::Reset()
        $errorEvent.AddEventHandler($null, [LlcomLifecycleEventProbe]::ErrorHandler)
        $stoppedEvent.AddEventHandler($null, [LlcomLifecycleEventProbe]::StoppedHandler)
        try {
            [void]$stopScript.Invoke($null, [object[]]@(''))
            $normalStopWasClean =
                [LlcomLifecycleEventProbe]::StoppedCount -eq 1 -and
                [LlcomLifecycleEventProbe]::ErrorCount -eq 0
            [void]$stopScript.Invoke($null, [object[]]@('fault probe'))
            Assert-CriticalCondition (
                $normalStopWasClean -and
                [LlcomLifecycleEventProbe]::ErrorCount -eq 1 -and
                [LlcomLifecycleEventProbe]::StoppedCount -eq 1
            ) 'Normal stop raised ScriptRunError, or a fault was reported as stopped.'
        }
        finally {
            $errorEvent.RemoveEventHandler($null, [LlcomLifecycleEventProbe]::ErrorHandler)
            $stoppedEvent.RemoveEventHandler($null, [LlcomLifecycleEventProbe]::StoppedHandler)
            $scriptLogFileField.SetValue($null, $originalScriptLogger)
            if ($noOpScriptLogger -is [IDisposable]) {
                $noOpScriptLogger.Dispose()
            }
        }
    }

    Invoke-CriticalCheck 19 'RuntimeSession owns a dedicated worker and retirement never waits on blocked CLR work' {
        Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Threading;

public static class LlcomRuntimeWorkerLifecycleProbe
{
    private static readonly ManualResetEventSlim Started = new ManualResetEventSlim(false);
    private static readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
    public static int WorkerThreadId;
    public static bool WorkerWasBackground;

    public static readonly ThreadStart BlockingBody = delegate
    {
        WorkerThreadId = Thread.CurrentThread.ManagedThreadId;
        WorkerWasBackground = Thread.CurrentThread.IsBackground;
        Started.Set();
        Release.Wait();
    };

    public static void Reset()
    {
        WorkerThreadId = -1;
        WorkerWasBackground = false;
        Started.Reset();
        Release.Reset();
    }

    public static bool WaitUntilStarted(int milliseconds)
    {
        return Started.Wait(milliseconds);
    }

    public static void ReleaseWorker()
    {
        Release.Set();
    }
}
'@
        $runEnvType = Get-RequiredType $assembly 'llcom_plus.ScriptEnv.JavaScriptRunEnv'
        $sessionType = $runEnvType.GetNestedType(
            'RuntimeSession',
            [Reflection.BindingFlags]'NonPublic')
        Assert-CriticalCondition ($null -ne $sessionType) 'RuntimeSession was not found.'
        $sessionConstructor = $sessionType.GetConstructor(
            $instanceFlags,
            $null,
            [Type[]]@([long]),
            $null)
        $startWorker = Get-RequiredMethod `
            $sessionType `
            'StartWorker' `
            $instanceFlags `
            ([Type[]]@([Threading.ThreadStart]))
        $tryEnqueue = Get-RequiredMethod `
            $sessionType `
            'TryEnqueue' `
            $instanceFlags `
            ([Type[]]@([int], [string], [object]))
        $getSnapshot = Get-RequiredMethod `
            $sessionType `
            'GetQueueSnapshot' `
            $instanceFlags `
            ([Type[]]@())
        $waitForWorker = Get-RequiredMethod `
            $sessionType `
            'WaitForWorker' `
            $instanceFlags `
            ([Type[]]@([int]))
        $retireSession = Get-RequiredMethod `
            $runEnvType `
            'RetireSession' `
            $staticFlags `
            ([Type[]]@($sessionType))

        [LlcomRuntimeWorkerLifecycleProbe]::Reset()
        $session = $sessionConstructor.Invoke([object[]]@([long]9901))
        $producerThreadId = [Threading.Thread]::CurrentThread.ManagedThreadId
        $workerReleased = $false
        try {
            $started = [bool]$startWorker.Invoke(
                $session,
                [object[]]@([LlcomRuntimeWorkerLifecycleProbe]::BlockingBody))
            $workerEntered = [LlcomRuntimeWorkerLifecycleProbe]::WaitUntilStarted(2000)
            $enqueueArguments = New-Object object[] 3
            $enqueueArguments[0] = 1
            $enqueueArguments[1] = 'blocked-worker-probe'
            $enqueueArguments[2] = [byte[]](1, 2, 3)
            $queued = [bool]$tryEnqueue.Invoke($session, $enqueueArguments)

            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            [void]$retireSession.Invoke($null, [object[]]@($session))
            $stopwatch.Stop()
            $retiredSnapshot = $getSnapshot.Invoke($session, $null)

            [LlcomRuntimeWorkerLifecycleProbe]::ReleaseWorker()
            $workerReleased = $true
            $workerExited = [bool]$waitForWorker.Invoke($session, [object[]]@(2000))

            $runEnvSource = [IO.File]::ReadAllText(
                (Join-Path $projectDir 'Core\ScriptEnv\JavaScriptRunEnv.cs'))
            $workerStart = $runEnvSource.IndexOf(
                'private static void RunSessionWorker',
                [StringComparison]::Ordinal)
            $sessionClassStart = $runEnvSource.IndexOf(
                'private sealed class RuntimeSession',
                [StringComparison]::Ordinal)
            $workerSource = if ($workerStart -ge 0 -and $sessionClassStart -gt $workerStart) {
                $runEnvSource.Substring($workerStart, $sessionClassStart - $workerStart)
            } else {
                ''
            }
            $allJintOperationsOwnedByWorker =
                $workerSource.Contains('JavaScriptLoader.CreateEngine') -and
                $workerSource.Contains('localEngine.Execute') -and
                $workerSource.Contains('localEngine.Invoke') -and
                $workerSource.Contains('localEngine.GetValue') -and
                -not $runEnvSource.Contains('public Engine Engine;') -and
                $runEnvSource.Contains('session.TryEnqueue')
            $timerCleanupIsBackground = [Text.RegularExpressions.Regex]::IsMatch(
                $runEnvSource,
                'session\.Retire\(\);\s*ThreadPool\.QueueUserWorkItem\(_ =>\s*\{\s*try\s*\{\s*DisposeTimers\(session\);')

            Assert-CriticalCondition (
                $started -and
                $workerEntered -and
                $queued -and
                [LlcomRuntimeWorkerLifecycleProbe]::WorkerWasBackground -and
                [LlcomRuntimeWorkerLifecycleProbe]::WorkerThreadId -ne $producerThreadId -and
                $stopwatch.ElapsedMilliseconds -lt 500 -and
                $retiredSnapshot.IsRetired -and
                $retiredSnapshot.QueuedEventCount -eq 0 -and
                $retiredSnapshot.QueuedPayloadBytes -eq 0 -and
                $workerExited -and
                $allJintOperationsOwnedByWorker -and
                $timerCleanupIsBackground
            ) ("Runtime worker lifecycle failed: started={0}, entered={1}, queued={2}, background={3}, producer={4}, worker={5}, retireMs={6}, retired={7}, queue={8}/{9}, exited={10}, jintOwned={11}, timerCleanupBackground={12}" -f
                $started,
                $workerEntered,
                $queued,
                [LlcomRuntimeWorkerLifecycleProbe]::WorkerWasBackground,
                $producerThreadId,
                [LlcomRuntimeWorkerLifecycleProbe]::WorkerThreadId,
                $stopwatch.ElapsedMilliseconds,
                $retiredSnapshot.IsRetired,
                $retiredSnapshot.QueuedEventCount,
                $retiredSnapshot.QueuedPayloadBytes,
                $workerExited,
                $allJintOperationsOwnedByWorker,
                $timerCleanupIsBackground)
        }
        finally {
            if (-not $workerReleased) {
                [LlcomRuntimeWorkerLifecycleProbe]::ReleaseWorker()
            }
            try { [void]$waitForWorker.Invoke($session, [object[]]@(2000)) } catch { }
        }
    }

    Invoke-CriticalCheck 20 'TLS/MQTT defaults are secure and passwords never serialize to settings JSON' {
        $isolatedSettings = [Activator]::CreateInstance($settingsType, $true)
        $authMode = [int]$settingsType.GetProperty('tcpClientSslAuthMode').GetValue($isolatedSettings, $null)
        $tlsRevocation = [bool]$settingsType.GetProperty('tcpClientSslCheckRevocation').GetValue($isolatedSettings, $null)
        $mqttRevocation = [bool]$settingsType.GetProperty('mqttTLSCheckRevocation').GetValue($isolatedSettings, $null)

        $settingsType.GetProperty('mqttPassword').SetValue($isolatedSettings, 'broker-secret-probe', $null)
        $settingsType.GetProperty('mqttTLSCertClientPassword').SetValue($isolatedSettings, 'pfx-secret-probe', $null)
        $settingsType.GetProperty('tcpClientSslClientCertPassword').SetValue($isolatedSettings, 'tls-secret-probe', $null)
        $serialized = [Newtonsoft.Json.JsonConvert]::SerializeObject($isolatedSettings)

        $legacyJson = '{"mqttPassword":"legacy-broker","mqttTLSCertClientPassword":"legacy-pfx","tcpClientSslClientCertPassword":"legacy-tls"}'
        $migrated = [Newtonsoft.Json.JsonConvert]::DeserializeObject($legacyJson, $settingsType)
        $settingsType.GetMethod('EnsureRuntimeState', [Reflection.BindingFlags]'Public,Instance').Invoke($migrated, $null)
        $migratedJson = [Newtonsoft.Json.JsonConvert]::SerializeObject($migrated)

        $blockedCredentialRoot = Join-Path $tempRoot 'credential-directory-blocker'
        [IO.File]::WriteAllText($blockedCredentialRoot, 'not a directory')
        $profilePathBeforeFailureProbe = [string]$globalProfilePathField.GetValue($null)
        $failedMigrationJson = ''
        try {
            $globalProfilePathField.SetValue(
                $null,
                $blockedCredentialRoot + [IO.Path]::DirectorySeparatorChar)
            $legacyFailureJson = '{"uartProfileSchemaVersion":2,"mqttPassword":"must-survive"}'
            $failedMigration = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
                $legacyFailureJson,
                $settingsType)
            $settingsType.GetMethod('EnsureRuntimeState', [Reflection.BindingFlags]'Public,Instance').Invoke(
                $failedMigration,
                $null)
            $failedMigrationJson = [Newtonsoft.Json.JsonConvert]::SerializeObject($failedMigration)
        }
        finally {
            $globalProfilePathField.SetValue($null, $profilePathBeforeFailureProbe)
        }

        Assert-CriticalCondition (
            $authMode -eq 1 -and
            $tlsRevocation -and
            $mqttRevocation
        ) 'A new Settings instance did not default to CA/hostname validation and revocation checking.'
        Assert-CriticalCondition (
            -not $serialized.Contains('broker-secret-probe') -and
            -not $serialized.Contains('pfx-secret-probe') -and
            -not $serialized.Contains('tls-secret-probe') -and
            -not $serialized.Contains('"mqttPassword"') -and
            -not $serialized.Contains('"mqttTLSCertClientPassword"') -and
            -not $serialized.Contains('"tcpClientSslClientCertPassword"')
        ) 'A runtime MQTT/TLS password was serialized into settings JSON.'
        $mqttLegacyStateIsSafe =
            -not $migratedJson.Contains('"mqttPassword"') -or
            $migratedJson.Contains('"mqttPassword":"legacy-broker"')
        $mqttCertLegacyStateIsSafe =
            -not $migratedJson.Contains('"mqttTLSCertClientPassword"') -or
            $migratedJson.Contains('"mqttTLSCertClientPassword":"legacy-pfx"')
        $tlsCertLegacyStateIsSafe =
            -not $migratedJson.Contains('"tcpClientSslClientCertPassword"') -or
            $migratedJson.Contains('"tcpClientSslClientCertPassword":"legacy-tls"')
        Assert-CriticalCondition (
            $settingsType.GetProperty('mqttPassword').GetValue($migrated, $null) -eq 'legacy-broker' -and
            $settingsType.GetProperty('mqttTLSCertClientPassword').GetValue($migrated, $null) -eq 'legacy-pfx' -and
            $settingsType.GetProperty('tcpClientSslClientCertPassword').GetValue($migrated, $null) -eq 'legacy-tls' -and
            $mqttLegacyStateIsSafe -and
            $mqttCertLegacyStateIsSafe -and
            $tlsCertLegacyStateIsSafe
        ) 'Legacy credential migration neither scrubbed nor retained the recoverable plaintext value.'
        Assert-CriticalCondition (
            $failedMigrationJson.Contains('"mqttPassword":"must-survive"')
        ) 'A failed DPAPI migration did not retain the recoverable legacy credential for retry.'
    }

    Invoke-CriticalCheck 21 'HTTP response/decompression limits and same-origin redirect policy fail closed' {
        $httpType = Get-RequiredType $assembly 'llcom_plus.HttpTools.HttpRequestService'
        $maxHeaders = [long]$httpType.GetField('MaxResponseHeaderBytes', $staticFlags).GetRawConstantValue()
        $maxCompressed = [long]$httpType.GetField('MaxCompressedResponseBodyBytes', $staticFlags).GetRawConstantValue()
        $maxDecompressed = [long]$httpType.GetField('MaxDecompressedResponseBodyBytes', $staticFlags).GetRawConstantValue()
        $maxRatio = [int]$httpType.GetField('MaxCompressionRatio', $staticFlags).GetRawConstantValue()
        $maxRedirects = [int]$httpType.GetField('MaxRedirectCount', $staticFlags).GetRawConstantValue()

        $isSameOrigin = Get-RequiredMethod `
            $httpType `
            'IsSameOrigin' `
            $staticFlags `
            ([Type[]]@([Uri], [Uri]))
        $sameOriginAccepted = [bool]$isSameOrigin.Invoke(
            $null,
            [object[]]@([Uri]'https://example.test/a', [Uri]'https://example.test/b'))
        $crossOriginRejected = -not [bool]$isSameOrigin.Invoke(
            $null,
            [object[]]@([Uri]'https://example.test/a', [Uri]'https://other.test/b'))
        $portChangeRejected = -not [bool]$isSameOrigin.Invoke(
            $null,
            [object[]]@([Uri]'https://example.test/a', [Uri]'https://example.test:444/b'))

        [byte[]]$largePlaintext = New-Object byte[] (2 * 1024 * 1024)
        $compressedStream = New-Object IO.MemoryStream
        $gzip = New-Object IO.Compression.GZipStream(
            $compressedStream,
            [IO.Compression.CompressionMode]::Compress,
            $true)
        try {
            $gzip.Write($largePlaintext, 0, $largePlaintext.Length)
        }
        finally {
            $gzip.Dispose()
        }
        [byte[]]$compressedBomb = $compressedStream.ToArray()
        $compressedStream.Dispose()
        $headers = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
        $headers.Add('Content-Encoding', 'gzip')
        $decode = Get-RequiredMethod `
            $httpType `
            'DecodeContentEncoding' `
            $staticFlags `
            ([Type[]]@([byte[]], $headers.GetType(), [Threading.CancellationToken]))
        $ratioRejected = $false
        try {
            [void]$decode.Invoke(
                $null,
                [object[]]@($compressedBomb, $headers, [Threading.CancellationToken]::None))
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
            $ratioRejected = $exception -is [IO.InvalidDataException]
        }

        $readBounded = Get-RequiredMethod `
            $httpType `
            'ReadBoundedResponseBodyAsync' `
            $staticFlags `
            ([Type[]]@([IO.Stream], [long], [Threading.CancellationToken]))
        $oversizedStream = New-Object IO.MemoryStream (,[byte[]](New-Object byte[] 1025))
        $bodyLimitRejected = $false
        try {
            $task = $readBounded.Invoke(
                $null,
                [object[]]@($oversizedStream, [long]1024, [Threading.CancellationToken]::None))
            [void]$task.GetAwaiter().GetResult()
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
            $bodyLimitRejected = $exception -is [IO.InvalidDataException]
        }
        finally {
            $oversizedStream.Dispose()
        }

        $validateBodyLength = Get-RequiredMethod `
            $httpType `
            'ValidateResponseBodyLength' `
            $staticFlags `
            ([Type[]]@([long], [long], [string]))
        try {
            [void]$validateBodyLength.Invoke($null, [object[]]@([long]1025, [long]1024, 'probe'))
            $bodyLimitRejected = $false
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
            $bodyLimitRejected = $exception -is [IO.InvalidDataException]
        }

        $validateDecompressedLength = Get-RequiredMethod `
            $httpType `
            'ValidateDecompressedBodyLength' `
            $staticFlags `
            ([Type[]]@([long], [long]))
        try {
            [void]$validateDecompressedLength.Invoke($null, [object[]]@([long]1024, [long](1024 * $maxRatio + 65537)))
            $ratioRejected = $false
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
            $ratioRejected = $exception -is [IO.InvalidDataException]
        }

        $httpSource = [IO.File]::ReadAllText((Join-Path $projectDir 'Core\HttpTools\HttpRequestService.cs'))
        Assert-CriticalCondition (
            $maxHeaders -gt 0 -and
            $maxCompressed -gt $maxHeaders -and
            $maxDecompressed -ge $maxCompressed -and
            $maxRatio -gt 1 -and
            $maxRedirects -gt 0 -and
            $sameOriginAccepted -and
            $crossOriginRejected -and
            $portChangeRejected -and
            $ratioRejected -and
            $bodyLimitRejected -and
            $httpSource.Contains('Refused insecure HTTPS-to-HTTP redirect') -and
            $httpSource.Contains('Refused cross-origin redirect') -and
            $httpSource.Contains('AllowAutoRedirect = false')
        ) ("HTTP safety probe failed: headers={0}, compressed={1}, decompressed={2}, ratio={3}, redirects={4}, same={5}, cross={6}, port={7}, ratioRejected={8}, bodyRejected={9}, downgradeText={10}, crossText={11}, manual={12}" -f
            $maxHeaders,
            $maxCompressed,
            $maxDecompressed,
            $maxRatio,
            $maxRedirects,
            $sameOriginAccepted,
            $crossOriginRejected,
            $portChangeRejected,
            $ratioRejected,
            $bodyLimitRejected,
            $httpSource.Contains('Refused insecure HTTPS-to-HTTP redirect'),
            $httpSource.Contains('Refused cross-origin redirect'),
            $httpSource.Contains('AllowAutoRedirect = false'))
    }

    Invoke-CriticalCheck 22 'OpenSSL, MQTT and Socket state/timer guards are bounded and generation-aware' {
        $openSslType = Get-RequiredType $assembly 'llcom_plus.Tools.OpenSslCli'
        $stdoutLimit = [long]$openSslType.GetField('MaxStandardOutputBytes', $staticFlags).GetRawConstantValue()
        $stderrLimit = [long]$openSslType.GetField('MaxStandardErrorBytes', $staticFlags).GetRawConstantValue()
        $httpHeaderLimit = [long]$openSslType.GetField('MaxHttpResponseHeaderBytes', $staticFlags).GetRawConstantValue()
        $httpBodyLimit = [long]$openSslType.GetField('MaxHttpCompressedBodyBytes', $staticFlags).GetRawConstantValue()

        $mqttType = Get-RequiredType $assembly 'llcom_plus.Pages.MqttTestPage'
        $mqttStateType = $mqttType.GetNestedType('MqttConnectionState', [Reflection.BindingFlags]'NonPublic')
        $mqttSource = [IO.File]::ReadAllText((Join-Path $projectDir 'UI\Pages\MqttTestPage.xaml.cs'))
        $socketType = Get-RequiredType $assembly 'llcom_plus.Pages.SocketClientPage'
        $socketSource = [IO.File]::ReadAllText((Join-Path $projectDir 'UI\Pages\SocketClientPage.xaml.cs'))

        Assert-CriticalCondition (
            $stdoutLimit -eq ($httpHeaderLimit + $httpBodyLimit) -and
            $stderrLimit -gt 0 -and
            $mqttStateType.GetEnumNames() -contains 'Connecting' -and
            $mqttStateType.GetEnumNames() -contains 'Connected' -and
            $mqttStateType.GetEnumNames() -contains 'Disconnecting' -and
            $null -ne $mqttType.GetField('activeMqttAttempt', $instanceFlags) -and
            $null -ne $mqttType.GetField('mqttAttemptCts', $instanceFlags) -and
            $mqttSource.Contains('ConfigureMqttClientHandlers(client, attemptId)') -and
            $mqttSource.Contains('IsCurrentMqttAttempt(client, attemptId') -and
            $mqttSource.Contains('X509RevocationMode.Online') -and
            $mqttSource.Contains('UrlRetrievalTimeout = TimeSpan.FromSeconds(5)')
        ) 'OpenSSL limits or MQTT attempt/state/revocation guards are missing.'
        Assert-CriticalCondition (
            $null -ne $socketType.GetField('reconnectTimer', $instanceFlags) -and
            $null -ne $socketType.GetField('reconnectGeneration', $instanceFlags) -and
            $null -ne $socketType.GetMethod('StopReconnectTimer', $instanceFlags) -and
            $null -ne $socketType.GetMethod('IsReconnectGenerationActive', $instanceFlags) -and
            ([Text.RegularExpressions.Regex]::Matches($socketSource, 'new System\.Timers\.Timer\(').Count -eq 1) -and
            $socketSource.Contains('if (!IsReconnectGenerationActive(generation) || IsConnected)') -and
            $socketSource.Contains('Unloaded += SocketClientPage_Unloaded') -and
            $socketSource.Contains('StopReconnectTimer(clearDisconnectIntent: true)')
        ) 'Socket reconnect does not have a single generation-bound timer with atomic unload/disconnect stop.'
    }

    Invoke-CriticalCheck 23 'Updater enforces download limits and independent-signature fail-closed installation' {
        $updaterType = Get-RequiredType $assembly 'llcom_plus.Tools.GitHubReleaseUpdater'
        $maxDownload = [long]$updaterType.GetField('MaximumUpdateDownloadBytes', $staticFlags).GetRawConstantValue()
        $maxSignature = [long]$updaterType.GetField('MaximumSignatureBytes', $staticFlags).GetRawConstantValue()
        $validateLength = Get-RequiredMethod `
            $updaterType `
            'ValidateDownloadLength' `
            $staticFlags `
            ([Type[]]@([long], [long], [string]))
        [void]$validateLength.Invoke($null, [object[]]@($maxDownload, $maxDownload, 'probe'))
        $oversizedRejected = $false
        try {
            [void]$validateLength.Invoke($null, [object[]]@(($maxDownload + 1), $maxDownload, 'probe'))
        }
        catch {
            $exception = $_.Exception
            while ($null -ne $exception.InnerException) { $exception = $exception.InnerException }
            $oversizedRejected = $exception -is [IO.InvalidDataException]
        }

        $releaseType = Get-RequiredType $assembly 'llcom_plus.Tools.GitHubReleaseInfo'
        $release = [Activator]::CreateInstance($releaseType, $true)
        $releaseType.GetProperty('AssetName').SetValue($release, 'llcom.plus_99.0.0_x64.zip', $null)
        $trustErrorMethod = Get-RequiredMethod `
            $updaterType `
            'GetAutomaticUpdateTrustError' `
            $staticFlags `
            ([Type[]]@($releaseType))
        $trustError = [string]$trustErrorMethod.Invoke($null, [object[]]@($release))

        $updaterSource = [IO.File]::ReadAllText((Join-Path $projectDir 'Core\Tools\GitHubReleaseUpdater.cs'))
        $updateControllerSource = [IO.File]::ReadAllText((Join-Path $projectDir 'UI\View\UpdateCheckController.cs'))
        $projectSource = [IO.File]::ReadAllText((Join-Path $projectDir 'llcom plus.csproj'))
        $trustReadme = Join-Path $projectDir 'Resources\UpdateSigningPublicKey.README.txt'
        $configuredKey = Join-Path $projectDir 'Resources\UpdateSigningPublicKey.xml'
        $configuredKeyIsValid = $false
        if (Test-Path -LiteralPath $configuredKey -PathType Leaf) {
            $keyProbe = New-Object Security.Cryptography.RSACryptoServiceProvider
            $keyProbe.PersistKeyInCsp = $false
            try {
                $keyProbe.FromXmlString([IO.File]::ReadAllText($configuredKey, [Text.Encoding]::UTF8).Trim())
                $configuredKeyIsValid = $keyProbe.PublicOnly -and $keyProbe.KeySize -ge 2048
            }
            finally {
                $keyProbe.Dispose()
            }
        }
        $signingScriptSource = [IO.File]::ReadAllText((Join-Path $root 'build\Sign-Release.ps1'))
        $workflowSource = [IO.File]::ReadAllText((Join-Path $root '.github\workflows\build.yml'))
        $downloadValidationStart = $updaterSource.IndexOf(
            'ValidateTrustedUpdatePackage(',
            [StringComparison]::Ordinal)
        $downloadValidationSource = if ($downloadValidationStart -ge 0) {
            $updaterSource.Substring(
                $downloadValidationStart,
                [Math]::Min(600, $updaterSource.Length - $downloadValidationStart))
        } else {
            ''
        }
        Assert-CriticalCondition (
            $maxDownload -gt 0 -and
            $maxSignature -gt 0 -and
            $oversizedRejected -and
            -not [string]::IsNullOrWhiteSpace($trustError) -and
            $updaterSource.Contains('if (!HasDetachedSignature(zipPath, signaturePath))') -and
            $updaterSource.Contains('.Where(package => IsValidUpdatePackage(package.Path, package.Version))') -and
            $updaterSource.Contains('CancelAfter(DownloadTotalTimeout)') -and
            $updaterSource.Contains('DownloadIdleTimeout') -and
            $updaterSource.Contains('EnsureDownloadDiskSpace') -and
            $updaterSource.Contains('TryDeleteFile(downloadPath)') -and
            $updaterSource.Contains('ValidateTrustedUpdatePackage') -and
            $downloadValidationSource.Contains('cancellationToken);') -and
            -not $downloadValidationSource.Contains('CancellationToken.None);') -and
            ([Text.RegularExpressions.Regex]::Matches($updaterSource, 'Assert-PackageTrust').Count -ge 3) -and
            $updaterSource.Contains('Assert-ExtractedIdentity') -and
            $updateControllerSource.Contains('DownloadUpdateAsync(release, progress, cts.Token)') -and
            $updateControllerSource.Contains('release.CanAutoInstall') -and
            $projectSource.Contains('<ReleasePackageFile Include="$(TargetDir)UpdateSigningPublicKey.xml"') -and
            (Test-Path -LiteralPath $trustReadme -PathType Leaf) -and
            $configuredKeyIsValid -and
            $signingScriptSource.Contains('UPDATE_SIGNING_PRIVATE_KEY_XML') -and
            $signingScriptSource.Contains('llcom-plus-update-signature-v1') -and
            $signingScriptSource.Contains('.SignData(') -and
            $signingScriptSource.Contains('.VerifyData(') -and
            $workflowSource.Contains('secrets.UPDATE_SIGNING_PRIVATE_KEY_XML') -and
            $workflowSource.Contains('artifacts/release/*.zip.sig')
        ) 'Updater download bounds, cancellation, signing deployment, or staging revalidation is incomplete.'
    }

    Invoke-CriticalCheck 24 'Serial monitor ABI, hook lifecycle, queue bounds, architecture policy, and WinUSB close gate are fail-closed' {
        $serialPageType = Get-RequiredType $assembly 'llcom_plus.Pages.SerialMonitorPage'
        $packetType = $serialPageType.GetNestedType(
            'MonitorPacket',
            [Reflection.BindingFlags]'NonPublic')
        Assert-CriticalCondition ($null -ne $packetType) 'Serial monitor ABI packet type was not found.'
        $getPacketSize = Get-RequiredMethod `
            $serialPageType `
            'GetMonitorPacketSize' `
            $staticFlags `
            ([Type[]]@())
        $packetSize = [int]$getPacketSize.Invoke($null, $null)
        $wireMagic = [uint32]$serialPageType.GetField('WireMagic', $staticFlags).GetRawConstantValue()
        $abiVersion = [uint16]$serialPageType.GetField('AbiVersion', $staticFlags).GetRawConstantValue()
        $headerSize = [uint16]$serialPageType.GetField('WireHeaderSize', $staticFlags).GetRawConstantValue()
        $maxFragment = [int]$serialPageType.GetField('MaxFragmentData', $staticFlags).GetRawConstantValue()
        $maxQueueCount = [int]$serialPageType.GetField('MaxQueuedPacketCount', $staticFlags).GetRawConstantValue()
        $maxQueueBytes = [long]$serialPageType.GetField('MaxQueuedPayloadBytes', $staticFlags).GetRawConstantValue()
        $dropPolicy = [string]$serialPageType.GetField('QueueDropPolicy', $staticFlags).GetRawConstantValue()
        $queueLimit = Get-RequiredMethod `
            $serialPageType `
            'IsWithinCallbackQueueLimits' `
            $staticFlags `
            ([Type[]]@([int], [long], [int]))
        $withinLimit = [bool]$queueLimit.Invoke(
            $null,
            [object[]]@(($maxQueueCount - 1), ($maxQueueBytes - 1), 1))
        $countRejected = -not [bool]$queueLimit.Invoke(
            $null,
            [object[]]@($maxQueueCount, [long]0, 1))
        $bytesRejected = -not [bool]$queueLimit.Invoke(
            $null,
            [object[]]@(0, $maxQueueBytes, 1))

        $packetProbe = Get-RequiredMethod `
            $serialPageType `
            'ProbePacketValidationBehavior' `
            $staticFlags `
            ([Type[]]@())
        $packetBehavior = [bool]$packetProbe.Invoke($null, $null)
        $validPacketAccepted = $packetBehavior
        $wrongComRejected = $packetBehavior
        $oversizedPacketRejected = $packetBehavior

        $winUsbType = Get-RequiredType $assembly 'llcom_plus.Pages.WinUSBPage'
        $winUsbProbe = Get-RequiredMethod `
            $winUsbType `
            'ProbeSendLifecycleCloseBehavior' `
            $staticFlags `
            ([Type[]]@())
        $winUsbCloseBehavior = [bool]$winUsbProbe.Invoke($null, $null)

        $serialRoot = Join-Path $root 'serial_monitor_rs'
        $hostSource = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'serial_monitor\src\lib.rs'))
        $hookSource = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'serial_monitor_hook\src\lib.rs'))
        $hostCargo = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'serial_monitor\Cargo.toml'))
        $hookCargo = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'serial_monitor_hook\Cargo.toml'))
        $buildScript = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'build.ps1'))
        $nativeSourceScript = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'native-source.ps1'))
        $verifyBuildScript = [IO.File]::ReadAllText(
            (Join-Path $serialRoot 'verify-build.ps1'))
        $workflow = [IO.File]::ReadAllText(
            (Join-Path $root '.github\workflows\build.yml'))
        $projectSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'llcom plus.csproj'))
        $serialPageSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'UI\Pages\SerialMonitorPage.xaml.cs'))
        $winUsbSource = [IO.File]::ReadAllText(
            (Join-Path $projectDir 'UI\Pages\WinUSBPage.xaml.cs'))

        $x86UnsafeFallbackRemoved =
            -not $hookSource.Contains('JMP_SZ') -and
            -not $hookSource.Contains('VirtualProtect') -and
            -not $hookSource.Contains('manual 5-byte') -and
            $hookSource.Contains('HOOK_ERROR_X86_DISABLED')
        $explicitLifecycle =
            $hookSource.Contains('pub unsafe extern "system" fn SerialMonitorInitialize') -and
            $hookSource.Contains('pub unsafe extern "system" fn SerialMonitorDeactivate') -and
            $hookSource.Contains('DisableThreadLibraryCalls(module)') -and
            $hookSource.Contains('install_hooks_transactional') -and
            $hookSource.Contains('rollback_enabled_hooks') -and
            $hookSource.Contains('ACCEPTING_EVENTS.store(false') -and
            $hookSource.Contains('ACTIVE_CAPTURE_CALLS') -and
            $hookSource.Contains('PIPE_HANDLE.swap(INVALID_PIPE')
        $overlappedSafe =
            $hookSource.Contains('original_error.0 == ERROR_IO_PENDING_RAW') -and
            $hookSource.Contains('requested_length') -and
            $hookSource.Contains('handle_generation') -and
            $hookSource.Contains('issuer_thread_id') -and
            $hookSource.Contains('hook_get_overlapped_result_ex') -and
            $hookSource.Contains('hook_get_queued_completion_status_ex') -and
            $hookSource.Contains('clear_pending_overlap(overlapped)') -and
            $hookSource.Contains('transferred_length.min(operation.requested_length)')
        $filterAndFragments =
            $hookSource.Contains('port != SELECTED_COM.load') -and
            $hookSource.Contains('STATE_DISCONNECT') -and
            $hookSource.Contains('fragment_offset') -and
            $hookSource.Contains('total_length') -and
            $hookSource.Contains('FLAG_TRUNCATED') -and
            $hookSource.Contains('MAX_CAPTURE_BYTES_PER_TRANSFER') -and
            $hostSource.Contains('packet.com_port == selected_com')
        $safeInjection =
            $hostSource.Contains('SerialMonitorGetAbiVersion') -and
            $serialPageSource.Contains('uint nativeAbi = SerialMonitorGetAbiVersion()') -and
            $serialPageSource.Contains('nativePluginAvailable = false') -and
            $hostSource.Contains('IsWow64Process2') -and
            $hostSource.Contains('resolve_remote_system_proc') -and
            $hostSource.Contains('local_owner') -and
            $hostSource.Contains('remote_owner') -and
            $hostSource.Contains('spawn_load_timeout_reaper') -and
            $hostSource.Contains('spawn_late_initialization_cleanup') -and
            $hostSource.Contains('SerialMonitorInitialize') -and
            -not $hostSource.Contains('eject_dll(') -and
            -not $hostSource.Contains('GetProcAddress(hk32')
        $managedQueueSafe =
            $serialPageSource.Contains('QueueDropPolicy = "DropNewest"') -and
            $serialPageSource.Contains('private bool drainScheduled') -and
            $serialPageSource.Contains('DrainCallbackQueue') -and
            $serialPageSource.Contains('callbackQueue.Clear()') -and
            ([Text.RegularExpressions.Regex]::Matches($serialPageSource, 'BeginInvoke\(').Count -le 2)
        $winUsbSingleGate =
            $winUsbSource.Contains('private readonly object gate = new object()') -and
            $winUsbSource.Contains('CloseAndDrainUnderGate') -and
            $winUsbSource.Contains('connection.SendQueue.Count >= MaxQueuedSendRequests') -and
            -not $winUsbSource.Contains('ConcurrentQueue') -and
            -not $winUsbSource.Contains('TryReserveQueueBytes')
        $pinnedAndBuiltFirst =
            $hostCargo.Contains('version = "=0.58.0"') -and
            $hookCargo.Contains('version = "=0.58.0"') -and
            $hookCargo.Contains('retour = "=0.3.1"') -and
            -not $hookCargo.Contains('static-detour') -and
            $hookSource.Contains('GenericDetour') -and
            $buildScript.Contains("[string]`$RustToolchain = '1.82.0'") -and
            $buildScript.Contains('Get-NativeSourceDigest -SourceRoot $scriptDir') -and
            $nativeSourceScript.Contains("'Cargo.lock'") -and
            $verifyBuildScript.Contains('[string]$stamp.sourceSha256 -cne $sourceHash') -and
            $verifyBuildScript.Contains('[string]$stamp.hostDllSha256 -cne $runtimeHash') -and
            $workflow.Contains('rustup toolchain install 1.82.0 --profile minimal') -and
            $workflow.Contains('build.ps1 -Config Release -Arch All -RustToolchain 1.82.0') -and
            $workflow.IndexOf('Build x64 and x86 native monitor from source', [StringComparison]::Ordinal) -lt
                $workflow.IndexOf('/p:Platform=x64 /p:RequireNativeSourceBuild=true', [StringComparison]::Ordinal) -and
            $workflow.Contains('/p:RequireNativeSourceBuild=true') -and
            $projectSource.Contains('NativeRuntimeBuildStamp') -and
            $projectSource.Contains("'`$(RequireNativeSourceBuild)' == 'true'") -and
            $projectSource.Contains('serial_monitor_rs\verify-build.ps1')

        Assert-CriticalCondition (
            $packetSize -eq 8256 -and
            $wireMagic -eq [uint32]0x334D534C -and
            $abiVersion -eq 3 -and
            $headerSize -eq 64 -and
            $maxFragment -eq 8192 -and
            $maxQueueCount -gt 0 -and
            $maxQueueBytes -gt 0 -and
            $dropPolicy -eq 'DropNewest' -and
            $withinLimit -and $countRejected -and $bytesRejected -and
            $validPacketAccepted -and $wrongComRejected -and $oversizedPacketRejected -and
            $winUsbCloseBehavior -and
            $x86UnsafeFallbackRemoved -and $explicitLifecycle -and
            $overlappedSafe -and $filterAndFragments -and $safeInjection -and
            $managedQueueSafe -and $winUsbSingleGate -and $pinnedAndBuiltFirst
        ) ("Native safety regression: packet={0}, ABI={1}/{2}, queue={3}/{4}/{5}, packetChecks={6}/{7}/{8}, winusbProbe={9}, x86={10}, lifecycle={11}, overlapped={12}, filterFragments={13}, injection={14}, managedQueue={15}, usbGate={16}, ci={17}" -f
            $packetSize,
            $abiVersion,
            $headerSize,
            $withinLimit,
            $countRejected,
            $bytesRejected,
            $validPacketAccepted,
            $wrongComRejected,
            $oversizedPacketRejected,
            $winUsbCloseBehavior,
            $x86UnsafeFallbackRemoved,
            $explicitLifecycle,
            $overlappedSafe,
            $filterAndFragments,
            $safeInjection,
            $managedQueueSafe,
            $winUsbSingleGate,
            $pinnedAndBuiltFirst)
    }

    Invoke-CriticalCheck 25 'Quick-send settings preserve 1.2.11 JSON, round-trip all fields, and never write while deserializing' {
        $quickSendPersistenceRoot = Join-Path $tempRoot 'quick-send-settings-persistence'
        [void][IO.Directory]::CreateDirectory($quickSendPersistenceRoot)
        $settingsPath = Join-Path $quickSendPersistenceRoot 'settings.json'
        $legacyJson = @'
{
  "quickSendList": [
    [
      {
        "id": 7,
        "text": "AT+CSQ?",
        "hex": false,
        "commit": "Check signal",
        "recvScriptPath": "alpha",
        "recvScriptPara": "p=1",
        "appendCrlf": true,
        "disableSuggestion": false
      },
      {
        "id": 8,
        "text": "A1 B2 0D 0A",
        "hex": true,
        "commit": "Send hex",
        "recvScriptPath": "beta",
        "recvScriptPara": "mode=hex",
        "appendCrlf": false,
        "disableSuggestion": true
      }
    ],
    [
      {
        "id": 42,
        "text": "AT+RESET",
        "hex": false,
        "commit": "Reset",
        "recvScriptPath": "gamma",
        "recvScriptPara": "delay=5",
        "appendCrlf": true,
        "disableSuggestion": true
      }
    ]
  ],
  "quickListNames": ["Field commands", "Maintenance commands"],
  "quickSendSelect": 1,
  "uartProfileSchemaVersion": 2
}
'@
        [IO.File]::WriteAllText(
            $settingsPath,
            $legacyJson,
            (New-Object Text.UTF8Encoding($false)))
        $fixedWriteTime = [DateTime]::SpecifyKind(
            [DateTime]::ParseExact(
                '2024-01-02 03:04:05',
                'yyyy-MM-dd HH:mm:ss',
                [Globalization.CultureInfo]::InvariantCulture),
            [DateTimeKind]::Utc)
        [IO.File]::SetLastWriteTimeUtc($settingsPath, $fixedWriteTime)
        $beforeHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
        $beforeLength = ([IO.FileInfo]$settingsPath).Length
        $beforeWriteTime = [IO.File]::GetLastWriteTimeUtc($settingsPath)

        $getAllQuickSendLists = Get-RequiredMethod `
            $settingsType `
            'GetAllQuickSendLists' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@())
        $getAllQuickListNames = Get-RequiredMethod `
            $settingsType `
            'GetAllQuickListNames' `
            ([Reflection.BindingFlags]'Public,Instance') `
            ([Type[]]@())
        $quickSendSelectProperty = $settingsType.GetProperty(
            'quickSendSelect',
            [Reflection.BindingFlags]'Public,Instance')
        Assert-CriticalCondition (
            $null -ne $quickSendSelectProperty
        ) 'Settings.quickSendSelect was not found.'

        $assertExpectedQuickSendState = {
            param(
                [object]$Settings,
                [string]$Phase
            )

            $lists = [Collections.IList]$getAllQuickSendLists.Invoke($Settings, $null)
            $names = [Collections.IList]$getAllQuickListNames.Invoke($Settings, $null)
            $selected = [int]$quickSendSelectProperty.GetValue($Settings, $null)
            Assert-CriticalCondition (
                $lists.Count -eq 2 -and
                ([Collections.IList]$lists[0]).Count -eq 2 -and
                ([Collections.IList]$lists[1]).Count -eq 1 -and
                $names.Count -eq 2 -and
                ([string]$names[0]) -ceq 'Field commands' -and
                ([string]$names[1]) -ceq 'Maintenance commands' -and
                $selected -eq 1
            ) "$Phase quick-send page structure, names, or selected page changed."

            $first = ([Collections.IList]$lists[0])[0]
            $second = ([Collections.IList]$lists[0])[1]
            $third = ([Collections.IList]$lists[1])[0]
            Assert-CriticalCondition (
                $first.id -eq 7 -and
                ([string]$first.text) -ceq 'AT+CSQ?' -and
                -not $first.hex -and
                ([string]$first.commit) -ceq 'Check signal' -and
                ([string]$first.recvScriptPath) -ceq 'alpha' -and
                ([string]$first.recvScriptPara) -ceq 'p=1' -and
                $first.appendCrlf -and
                -not $first.disableSuggestion -and
                $second.id -eq 8 -and
                ([string]$second.text) -ceq 'A1 B2 0D 0A' -and
                $second.hex -and
                ([string]$second.commit) -ceq 'Send hex' -and
                ([string]$second.recvScriptPath) -ceq 'beta' -and
                ([string]$second.recvScriptPara) -ceq 'mode=hex' -and
                -not $second.appendCrlf -and
                $second.disableSuggestion -and
                $third.id -eq 42 -and
                ([string]$third.text) -ceq 'AT+RESET' -and
                -not $third.hex -and
                ([string]$third.commit) -ceq 'Reset' -and
                ([string]$third.recvScriptPath) -ceq 'gamma' -and
                ([string]$third.recvScriptPara) -ceq 'delay=5' -and
                $third.appendCrlf -and
                $third.disableSuggestion
            ) "$Phase quick-send item fields changed."
        }

        $profilePathBeforeCheck = $globalProfilePathField.GetValue($null)
        try {
            $globalProfilePathField.SetValue(
                $null,
                $quickSendPersistenceRoot + [IO.Path]::DirectorySeparatorChar)
            $settingsText = [IO.File]::ReadAllText($settingsPath, [Text.Encoding]::UTF8)
            $legacySettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
                $settingsText,
                $settingsType)
            Assert-CriticalCondition ($null -ne $legacySettings) 'The 1.2.11-shaped settings JSON deserialized to null.'
            & $assertExpectedQuickSendState $legacySettings 'Legacy JSON'

            $afterHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
            $afterLength = ([IO.FileInfo]$settingsPath).Length
            $afterWriteTime = [IO.File]::GetLastWriteTimeUtc($settingsPath)
            Assert-CriticalCondition (
                $afterHash -ceq $beforeHash -and
                $afterLength -eq $beforeLength -and
                $afterWriteTime -eq $beforeWriteTime
            ) 'Deserializing settings modified the source settings.json file.'

            $currentJson = [Newtonsoft.Json.JsonConvert]::SerializeObject($legacySettings)
            $currentToken = [Newtonsoft.Json.Linq.JObject]::Parse($currentJson)
            Assert-CriticalCondition (
                $currentToken['quickSendList'] -is [Newtonsoft.Json.Linq.JArray] -and
                $currentToken['quickSendList'].Count -eq 2 -and
                $currentToken['quickListNames'] -is [Newtonsoft.Json.Linq.JArray] -and
                $currentToken['quickListNames'].Count -eq 2
            ) 'Current Settings serialization omitted or changed the canonical quick-send properties.'

            $roundTrippedSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
                $currentJson,
                $settingsType)
            Assert-CriticalCondition ($null -ne $roundTrippedSettings) 'Current Settings JSON round-trip deserialized to null.'
            & $assertExpectedQuickSendState $roundTrippedSettings 'Current round-trip'

            Assert-CriticalCondition (
                (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash -ceq $beforeHash -and
                ([IO.FileInfo]$settingsPath).Length -eq $beforeLength -and
                ([IO.File]::GetLastWriteTimeUtc($settingsPath)) -eq $beforeWriteTime
            ) 'Settings round-trip unexpectedly wrote to the source settings.json file.'

            # Simulate an affected 1.2.12 profile: the current object is empty, while
            # the conventional pre-existing backup still contains the user's pages.
            $legacyBackupPath = Join-Path $quickSendPersistenceRoot 'settings.json.bakup'
            [IO.File]::WriteAllText(
                $legacyBackupPath,
                $legacyJson,
                (New-Object Text.UTF8Encoding($false)))
            $emptySettings = [Activator]::CreateInstance($settingsType)
            $recoverLegacyQuickSend = Get-RequiredMethod `
                $globalType `
                'TryRecoverEmptyQuickSendFromLegacyBackup' `
                ([Reflection.BindingFlags]'NonPublic,Static') `
                ([Type[]]@($settingsType))
            [void]$recoverLegacyQuickSend.Invoke($null, [object[]]@($emptySettings))
            $stillEmptyLists = [Collections.IList]$getAllQuickSendLists.Invoke($emptySettings, $null)
            Assert-CriticalCondition (
                $stillEmptyLists.Count -eq 1 -and
                ([Collections.IList]$stillEmptyLists[0]).Count -eq 10
            ) 'Legacy backup discovery silently overwrote valid current settings instead of asking the user.'

            $candidateField = $globalType.GetField(
                'quickSendLegacyRecoveryCandidate',
                [Reflection.BindingFlags]'NonPublic,Static')
            $candidate = $candidateField.GetValue($null)
            Assert-CriticalCondition ($null -ne $candidate) 'Legacy backup data was not staged as a recovery candidate.'
            $candidateType = $candidate.GetType()
            $candidateLists = $candidateType.GetMethod(
                'CreateModelLists',
                [Reflection.BindingFlags]'NonPublic,Instance').Invoke($candidate, $null)
            $candidateNames = $candidateType.GetProperty(
                'Names',
                [Reflection.BindingFlags]'NonPublic,Instance').GetValue($candidate, $null)
            $candidateSelected = $candidateType.GetProperty(
                'SelectedIndex',
                [Reflection.BindingFlags]'NonPublic,Instance').GetValue($candidate, $null)
            $setAllQuickSendState = @($settingsType.GetMethods(
                [Reflection.BindingFlags]'Public,Instance') | Where-Object {
                    $_.Name -eq 'SetAllQuickSendState' -and $_.GetParameters().Count -eq 3
                })[0]
            [void]$setAllQuickSendState.Invoke(
                $emptySettings,
                [object[]]@($candidateLists, $candidateNames, $candidateSelected))
            & $assertExpectedQuickSendState $emptySettings 'Legacy backup recovery'
        }
        finally {
            $globalProfilePathField.SetValue($null, $profilePathBeforeCheck)
        }
    }

    Invoke-CriticalCheck 26 'Quick-send snapshot store deduplicates, skips corrupt files, and retains at most fifteen valid backups' {
        $backupType = Get-RequiredType $assembly 'llcom_plus.Tools.QuickSendBackupService'
        $initializeBackup = Get-RequiredMethod `
            $backupType `
            'Initialize' `
            ([Reflection.BindingFlags]'NonPublic,Static') `
            ([Type[]]@($settingsType))
        $createBackup = Get-RequiredMethod `
            $backupType `
            'CreateNow' `
            ([Reflection.BindingFlags]'NonPublic,Static') `
            ([Type[]]@($settingsType, [string]))
        $getBackups = Get-RequiredMethod `
            $backupType `
            'GetSnapshots' `
            ([Reflection.BindingFlags]'NonPublic,Static') `
            ([Type[]]@())
        $shutdownBackup = Get-RequiredMethod `
            $backupType `
            'Shutdown' `
            ([Reflection.BindingFlags]'NonPublic,Static') `
            ([Type[]]@())
        $backupDirectoryProperty = $backupType.GetProperty(
            'BackupDirectory',
            [Reflection.BindingFlags]'NonPublic,Static')
        $maximumSnapshotField = $backupType.GetField(
            'MaximumSnapshotCount',
            [Reflection.BindingFlags]'NonPublic,Static')
        Assert-CriticalCondition (
            $null -ne $backupDirectoryProperty -and
            $null -ne $maximumSnapshotField
        ) 'Quick-send snapshot directory or retention constant was not found.'
        $maximumSnapshotCount = [int]$maximumSnapshotField.GetRawConstantValue()
        Assert-CriticalCondition (
            $maximumSnapshotCount -eq 15
        ) 'Quick-send snapshot retention is not capped at fifteen files.'

        $snapshotProfileRoot = Join-Path $tempRoot 'quick-send-snapshot-store'
        [void][IO.Directory]::CreateDirectory($snapshotProfileRoot)
        $profilePathBeforeSnapshotCheck = $globalProfilePathField.GetValue($null)
        try {
            $globalProfilePathField.SetValue(
                $null,
                $snapshotProfileRoot + [IO.Path]::DirectorySeparatorChar)
            $seedJson = '{"quickSendList":[[{"id":1,"text":"snapshot-0","hex":false,"commit":"Send","recvScriptPath":"","recvScriptPara":"","appendCrlf":true,"disableSuggestion":false}]],"quickListNames":["Snapshots"],"quickSendSelect":0,"uartProfileSchemaVersion":2}'
            $seedSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject($seedJson, $settingsType)
            Assert-CriticalCondition ($null -ne $seedSettings) 'Snapshot seed settings deserialized to null.'
            Assert-CriticalCondition (
                [bool]$initializeBackup.Invoke($null, [object[]]@($seedSettings))
            ) 'Quick-send snapshot store failed to initialize.'

            $firstResult = $createBackup.Invoke(
                $null,
                [object[]]@($seedSettings, 'test-initial'))
            $firstStatusProperty = $firstResult.GetType().GetProperty(
                'Status',
                [Reflection.BindingFlags]'Public,NonPublic,Instance')
            Assert-CriticalCondition (
                $null -ne $firstStatusProperty -and
                $firstStatusProperty.GetValue($firstResult, $null).ToString() -ceq 'Created'
            ) 'The first quick-send snapshot was not created.'
            $afterFirst = [Collections.IList]$getBackups.Invoke($null, $null)
            Assert-CriticalCondition ($afterFirst.Count -eq 1) 'The first quick-send snapshot was not enumerable.'

            $duplicateResult = $createBackup.Invoke(
                $null,
                [object[]]@($seedSettings, 'test-duplicate'))
            Assert-CriticalCondition (
                $firstStatusProperty.GetValue($duplicateResult, $null).ToString() -ceq 'Deduplicated' -and
                ([Collections.IList]$getBackups.Invoke($null, $null)).Count -eq 1
            ) 'Content-identical quick-send data created a duplicate snapshot file.'

            for ($snapshotIndex = 1; $snapshotIndex -lt 20; $snapshotIndex++) {
                $uniqueJson = '{"quickSendList":[[{"id":1,"text":"snapshot-' +
                    $snapshotIndex.ToString([Globalization.CultureInfo]::InvariantCulture) +
                    '","hex":false,"commit":"Send","recvScriptPath":"","recvScriptPara":"","appendCrlf":true,"disableSuggestion":false}]],"quickListNames":["Snapshots"],"quickSendSelect":0,"uartProfileSchemaVersion":2}'
                $uniqueSettings = [Newtonsoft.Json.JsonConvert]::DeserializeObject(
                    $uniqueJson,
                    $settingsType)
                $uniqueResult = $createBackup.Invoke(
                    $null,
                    [object[]]@($uniqueSettings, 'test-retention'))
                Assert-CriticalCondition (
                    $firstStatusProperty.GetValue($uniqueResult, $null).ToString() -ceq 'Created'
                ) "Unique quick-send snapshot $snapshotIndex was not created."
            }

            $retained = [Collections.IList]$getBackups.Invoke($null, $null)
            Assert-CriticalCondition (
                $retained.Count -eq $maximumSnapshotCount
            ) "Snapshot retention kept $($retained.Count) valid files instead of $maximumSnapshotCount."

            $backupDirectory = [string]$backupDirectoryProperty.GetValue($null, $null)
            Assert-CriticalCondition (
                -not [string]::IsNullOrWhiteSpace($backupDirectory) -and
                [IO.Directory]::Exists($backupDirectory)
            ) 'Quick-send snapshot directory was not created beneath the profile.'
            $corruptPath = Join-Path $backupDirectory 'quick-send_corrupt.json'
            [IO.File]::WriteAllText(
                $corruptPath,
                '{"schemaVersion":1,"contentSha256":"tampered"',
                (New-Object Text.UTF8Encoding($false)))
            $afterCorrupt = [Collections.IList]$getBackups.Invoke($null, $null)
            Assert-CriticalCondition (
                [IO.File]::Exists($corruptPath) -and
                $afterCorrupt.Count -eq $maximumSnapshotCount
            ) 'A corrupt quick-send snapshot hid valid history or was treated as valid.'
        }
        finally {
            try {
                [void]$shutdownBackup.Invoke($null, $null)
            }
            finally {
                $globalProfilePathField.SetValue($null, $profilePathBeforeSnapshotCheck)
            }
        }
    }
}
catch {
    $detail = Get-ErrorDetail $_
    [void]$failures.Add('Setup/runtime: ' + $detail)
    Write-Host ('FAIL  Setup/runtime: ' + $detail) -ForegroundColor Red
}
finally {
    if ($null -ne $loggerType) {
        try {
            $stopSessionLog = $loggerType.GetMethod(
                'StopSessionLog',
                [Reflection.BindingFlags]'Public,Static')
            if ($null -ne $stopSessionLog) {
                [void]$stopSessionLog.Invoke($null, $null)
            }
        }
        catch {
            Add-CleanupFailure 'session logger' $_
        }
    }

    for ($writerIndex = $ownedWriters.Count - 1; $writerIndex -ge 0; $writerIndex--) {
        try {
            $ownedWriters[$writerIndex].Dispose()
        }
        catch {
            Add-CleanupFailure "writer $writerIndex" $_
        }
    }

    if ($null -ne $createdApplication) {
        try {
            $createdApplication.Shutdown()
        }
        catch {
            Add-CleanupFailure 'WPF Application' $_
        }
    }

    if ($globalStateCaptured) {
        try {
            $globalSettingField.SetValue($null, $originalSetting)
        }
        catch {
            Add-CleanupFailure 'Global.setting' $_
        }
        try {
            $globalProfilePathField.SetValue($null, $originalProfilePath)
        }
        catch {
            Add-CleanupFailure 'Global.ProfilePath' $_
        }
    }

    if ($locationPushed) {
        try {
            Pop-Location
        }
        catch {
            Add-CleanupFailure 'working directory' $_
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($tempRoot)) {
        try {
            Remove-CriticalTempDirectory $tempRoot
        }
        catch {
            Add-CleanupFailure 'temporary directory' $_
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ("`n{0} critical regression check(s) failed." -f $failures.Count) -ForegroundColor Red
    exit 1
}

Write-Host ("`nAll {0} selected critical regression checks passed ({1}-{2})." -f
    $executedCheckCount,
    $CheckFrom,
    $CheckThrough) -ForegroundColor Green
exit 0
