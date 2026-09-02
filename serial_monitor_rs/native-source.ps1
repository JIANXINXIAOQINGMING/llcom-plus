Set-StrictMode -Version 2.0

function Get-NativeSourceFiles {
    return @(
        'Cargo.toml',
        'Cargo.lock',
        'build.ps1',
        'native-source.ps1',
        'serial_monitor\Cargo.toml',
        'serial_monitor\build.rs',
        'serial_monitor\src\lib.rs',
        'serial_monitor_hook\Cargo.toml',
        'serial_monitor_hook\src\lib.rs'
    )
}

function Get-Sha256FileDigest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Get-NativeSourceDigest {
    param([Parameter(Mandatory = $true)][string]$SourceRoot)

    $manifest = foreach ($relative in Get-NativeSourceFiles) {
        $path = Join-Path $SourceRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Native source file is missing: $path"
        }
        $hash = Get-Sha256FileDigest -Path $path
        '{0}:{1}' -f $relative.Replace('\', '/'), $hash
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest -join "`n"))
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}
