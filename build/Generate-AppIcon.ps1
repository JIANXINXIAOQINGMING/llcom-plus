param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = Join-Path $Root 'llcom plus\Resources\Assets\AppIcon.Source.png'
$outputPath = Join-Path $Root 'llcom plus\Resources\Assets\AppIcon.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngFrames = New-Object 'System.Collections.Generic.List[byte[]]'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Missing master icon source: $sourcePath"
}

function Get-VisibleBounds {
    param([Drawing.Bitmap]$Bitmap)

    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -le 8) { continue }
            $minX = [Math]::Min($minX, $x)
            $minY = [Math]::Min($minY, $y)
            $maxX = [Math]::Max($maxX, $x)
            $maxY = [Math]::Max($maxY, $y)
        }
    }

    if ($maxX -lt $minX -or $maxY -lt $minY) {
        return [Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    }
    return [Drawing.Rectangle]::new(
        $minX,
        $minY,
        $maxX - $minX + 1,
        $maxY - $minY + 1)
}

function New-ResizedPngFrame {
    param(
        [string]$SourcePath,
        [int]$Size
    )

    $source = [Drawing.Bitmap]::FromFile($SourcePath)
    $target = New-Object Drawing.Bitmap($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $target.SetResolution(96, 96)
        $graphics = [Drawing.Graphics]::FromImage($target)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality

            # The master already contains the high-contrast plate. Cropping its outer
            # transparent margin lets the logo visibly fill every Windows icon frame.
            $sourceBounds = Get-VisibleBounds -Bitmap $source
            $graphics.DrawImage(
                $source,
                ([Drawing.Rectangle]::new(0, 0, $Size, $Size)),
                $sourceBounds.X,
                $sourceBounds.Y,
                $sourceBounds.Width,
                $sourceBounds.Height,
                [Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = New-Object IO.MemoryStream
        try {
            $target.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $target.Dispose()
        $source.Dispose()
    }
}

foreach ($size in $sizes) {
    $pngFrames.Add((New-ResizedPngFrame -SourcePath $sourcePath -Size $size))
}

$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $frame = $pngFrames[$index]
        $iconDimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$iconDimension)
        $writer.Write([byte]$iconDimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Length
    }

    foreach ($frame in $pngFrames) {
        $writer.Write($frame)
    }

    $writer.Flush()
    [IO.File]::WriteAllBytes($outputPath, $stream.ToArray())
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated $outputPath with sizes: $($sizes -join ', ')"
