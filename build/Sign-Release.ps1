[CmdletBinding()]
param(
    [string]$ArtifactsDirectory,
    [string]$Version,
    [string]$PublicKeyFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path $root 'artifacts\release'
}
if ([string]::IsNullOrWhiteSpace($PublicKeyFile)) {
    $PublicKeyFile = Join-Path $root 'llcom plus\Resources\UpdateSigningPublicKey.xml'
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$versionProps = Get-Content -LiteralPath (Join-Path $root 'Version.props')
    $Version = [string]$versionProps.Project.PropertyGroup.AppVersion
}

$parsedVersion = $null
if (-not [Version]::TryParse($Version, [ref]$parsedVersion)) {
    throw "Invalid release version: $Version"
}
$normalizedVersion = '{0}.{1}.{2}.{3}' -f `
    $parsedVersion.Major,
    $parsedVersion.Minor,
    $(if ($parsedVersion.Build -ge 0) { $parsedVersion.Build } else { 0 }),
    $(if ($parsedVersion.Revision -ge 0) { $parsedVersion.Revision } else { 0 })
$packageVersion = '{0}.{1}.{2}' -f `
    $parsedVersion.Major,
    $parsedVersion.Minor,
    $(if ($parsedVersion.Build -ge 0) { $parsedVersion.Build } else { 0 })

if (-not (Test-Path -LiteralPath $ArtifactsDirectory -PathType Container)) {
    throw "Release artifact directory does not exist: $ArtifactsDirectory"
}
if (-not (Test-Path -LiteralPath $PublicKeyFile -PathType Leaf)) {
    throw "Update-signing public key does not exist: $PublicKeyFile"
}

$privateKeyXml = [Environment]::GetEnvironmentVariable(
    'UPDATE_SIGNING_PRIVATE_KEY_XML',
    [EnvironmentVariableTarget]::Process)
if ([string]::IsNullOrWhiteSpace($privateKeyXml)) {
    throw 'UPDATE_SIGNING_PRIVATE_KEY_XML is not configured.'
}
if (-not $privateKeyXml.Contains('<RSAKeyValue>') -or -not $privateKeyXml.Contains('<D>')) {
    throw 'UPDATE_SIGNING_PRIVATE_KEY_XML is not RSA XML private-key material.'
}

$publicKeyXml = [IO.File]::ReadAllText($PublicKeyFile, [Text.Encoding]::UTF8).Trim()
$utf8 = New-Object Text.UTF8Encoding($false)
$privateRsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$publicRsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$privateRsa.PersistKeyInCsp = $false
$publicRsa.PersistKeyInCsp = $false
try {
    $privateRsa.FromXmlString($privateKeyXml)
    $publicRsa.FromXmlString($publicKeyXml)
    if ($privateRsa.KeySize -lt 2048 -or $publicRsa.KeySize -lt 2048) {
        throw 'The update-signing RSA key must be at least 2048 bits.'
    }

    $versionPattern = [Text.RegularExpressions.Regex]::Escape($packageVersion)
    $zipFiles = @(Get-ChildItem -LiteralPath $ArtifactsDirectory -Filter '*.zip' -File |
        Where-Object { $_.Name -match "_$versionPattern`_(x64|x86)\.zip$" })
    foreach ($architecture in @('x64', 'x86')) {
        $matches = @($zipFiles | Where-Object { $_.Name -match "_$architecture\.zip$" })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one $architecture ZIP for version $packageVersion, found $($matches.Count)."
        }
    }

    foreach ($zip in ($zipFiles | Sort-Object Name)) {
        $architectureMatch = [Text.RegularExpressions.Regex]::Match(
            $zip.Name,
            '_(x64|x86)\.zip$',
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $architectureMatch.Success) {
            throw "Cannot determine architecture from release ZIP: $($zip.Name)"
        }
        $architecture = $architectureMatch.Groups[1].Value.ToLowerInvariant()
        $sha256 = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        $payload = "llcom-plus-update-signature-v1`n" +
            "asset=$($zip.Name)`n" +
            "version=$normalizedVersion`n" +
            "architecture=$architecture`n" +
            "sha256=$sha256`n"
        $payloadBytes = $utf8.GetBytes($payload)
        $signatureBytes = $privateRsa.SignData(
            $payloadBytes,
            [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
        if (-not $publicRsa.VerifyData(
                $payloadBytes,
                [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),
                $signatureBytes)) {
            throw "Generated signature failed public-key verification: $($zip.Name)"
        }

        $envelope = [ordered]@{
            schema = 'llcom-plus-update-signature-v1'
            asset = $zip.Name
            version = $normalizedVersion
            architecture = $architecture
            sha256 = $sha256
            signature = [Convert]::ToBase64String($signatureBytes)
        }
        $signaturePath = $zip.FullName + '.sig'
        [IO.File]::WriteAllText(
            $signaturePath,
            ($envelope | ConvertTo-Json -Compress),
            $utf8)
        Write-Host "Signed release asset: $($zip.Name) -> $([IO.Path]::GetFileName($signaturePath))" -ForegroundColor Green
    }
}
finally {
    $privateRsa.Dispose()
    $publicRsa.Dispose()
    $privateKeyXml = $null
}
