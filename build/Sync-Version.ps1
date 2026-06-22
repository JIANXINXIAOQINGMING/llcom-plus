param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$AutoUpdate,
    [switch]$AppxManifest
)

$ErrorActionPreference = "Stop"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$rootPath = (Resolve-Path -LiteralPath $Root).Path

function Update-TextFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Updater
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $text = [System.IO.File]::ReadAllText($Path)
    $updated = & $Updater $text
    if ($updated -ne $text) {
        [System.IO.File]::WriteAllText($Path, $updated, $utf8NoBom)
    }
}

if ($AutoUpdate) {
    Update-TextFile -Path (Join-Path $rootPath "changlog\autoUpdate.xml") -Updater {
        param($text)
        [regex]::Replace(
            $text,
            "(<version>)[^<]+(</version>)",
            { param($m) $m.Groups[1].Value + $Version + $m.Groups[2].Value },
            1)
    }
}

if ($AppxManifest) {
    Update-TextFile -Path (Join-Path $rootPath "WapProj\Package.appxmanifest") -Updater {
        param($text)
        [regex]::Replace(
            $text,
            '(<Identity\b[^>]*\bVersion=")[^"]+(")',
            { param($m) $m.Groups[1].Value + $Version + $m.Groups[2].Value },
            1)
    }
}
