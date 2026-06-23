param(
    [Parameter(Mandatory = $true)]
    [string]$IconPath,

    [Parameter(Mandatory = $true)]
    [string]$ImagesPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$resolvedIconPath = (Resolve-Path -LiteralPath $IconPath).Path
$resolvedImagesPath = (Resolve-Path -LiteralPath $ImagesPath).Path

function Get-LargestIconBitmap {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null

    for ($i = 0; $i -lt $count; $i++) {
        $entryOffset = 6 + 16 * $i
        $width = $bytes[$entryOffset]
        if ($width -eq 0) { $width = 256 }

        $height = $bytes[$entryOffset + 1]
        if ($height -eq 0) { $height = 256 }

        $size = [BitConverter]::ToUInt32($bytes, $entryOffset + 8)
        $imageOffset = [BitConverter]::ToUInt32($bytes, $entryOffset + 12)

        if ($null -eq $best -or ($width * $height) -gt ($best.Width * $best.Height)) {
            $best = [pscustomobject]@{
                Width = $width
                Height = $height
                Size = $size
                Offset = $imageOffset
            }
        }
    }

    $imageBytes = New-Object byte[] $best.Size
    [Array]::Copy($bytes, [int]$best.Offset, $imageBytes, 0, [int]$best.Size)

    $stream = [System.IO.MemoryStream]::new($imageBytes)
    try {
        $image = [System.Drawing.Image]::FromStream($stream)
        return [System.Drawing.Bitmap]::new($image)
    }
    finally {
        if ($image) { $image.Dispose() }
        $stream.Dispose()
    }
}

function Save-PngIfChanged {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [string]$Path
    )

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $newBytes = $stream.ToArray()

        if ((Test-Path -LiteralPath $Path)) {
            $oldBytes = [System.IO.File]::ReadAllBytes($Path)
            if ($oldBytes.Length -eq $newBytes.Length) {
                $same = $true
                for ($i = 0; $i -lt $newBytes.Length; $i++) {
                    if ($oldBytes[$i] -ne $newBytes[$i]) {
                        $same = $false
                        break
                    }
                }

                if ($same) { return }
            }
        }

        [System.IO.File]::WriteAllBytes($Path, $newBytes)
    }
    finally {
        $stream.Dispose()
    }
}

function Save-IconAsset {
    param(
        [System.Drawing.Image]$Source,
        [System.Drawing.Rectangle]$SourceBounds,
        [string]$Name,
        [int]$Width,
        [int]$Height,
        [double]$IconRatio
    )

    $path = Join-Path $resolvedImagesPath $Name
    $canvas = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        $iconSize = [int][Math]::Round([Math]::Min($Width, $Height) * $IconRatio)
        if ($iconSize -lt 1) { $iconSize = [Math]::Min($Width, $Height) }

        $x = [int][Math]::Round(($Width - $iconSize) / 2)
        $y = [int][Math]::Round(($Height - $iconSize) / 2)
        $destination = [System.Drawing.Rectangle]::new($x, $y, $iconSize, $iconSize)
        $graphics.DrawImage($Source, $destination, $SourceBounds, [System.Drawing.GraphicsUnit]::Pixel)

        Save-PngIfChanged -Bitmap $canvas -Path $path
    }
    finally {
        $graphics.Dispose()
        $canvas.Dispose()
    }
}

function Get-AlphaBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -gt 10) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    if ($maxX -lt 0 -or $maxY -lt 0) {
        return [System.Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    }

    return [System.Drawing.Rectangle]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1)
}
$source = Get-LargestIconBitmap -Path $resolvedIconPath
$sourceBounds = Get-AlphaBounds -Bitmap $source

try {
    $assets = @(
        @{ Prefix = "LargeTile"; Sizes = @(@(100,310,310),@(125,388,388),@(150,465,465),@(200,620,620),@(400,1240,1240)); Ratio = 0.30 },
        @{ Prefix = "SmallTile"; Sizes = @(@(100,71,71),@(125,89,89),@(150,107,107),@(200,142,142),@(400,284,284)); Ratio = 0.45 },
        @{ Prefix = "SplashScreen"; Sizes = @(@(100,620,300),@(125,775,375),@(150,930,450),@(200,1240,600),@(400,2480,1200)); Ratio = 0.30 },
        @{ Prefix = "Square150x150Logo"; Sizes = @(@(100,150,150),@(125,188,188),@(150,225,225),@(200,300,300),@(400,600,600)); Ratio = 0.31 },
        @{ Prefix = "Square44x44Logo"; Sizes = @(@(100,44,44),@(125,55,55),@(150,66,66),@(200,88,88),@(400,176,176)); Ratio = 0.70 },
        @{ Prefix = "StoreLogo"; Sizes = @(@(100,50,50),@(125,63,63),@(150,75,75),@(200,100,100),@(400,200,200)); Ratio = 0.92 },
        @{ Prefix = "Wide310x150Logo"; Sizes = @(@(100,310,150),@(125,388,188),@(150,465,225),@(200,620,300),@(400,1240,600)); Ratio = 0.31 }
    )

    foreach ($asset in $assets) {
        foreach ($size in $asset.Sizes) {
            Save-IconAsset -Source $source -SourceBounds $sourceBounds -Name ("{0}.scale-{1}.png" -f $asset.Prefix, $size[0]) -Width $size[1] -Height $size[2] -IconRatio $asset.Ratio
        }
    }

    Save-IconAsset -Source $source -SourceBounds $sourceBounds -Name "LockScreenLogo.scale-200.png" -Width 48 -Height 48 -IconRatio 0.92

    foreach ($target in @(16,24,32,48,256)) {
        Save-IconAsset -Source $source -SourceBounds $sourceBounds -Name ("Square44x44Logo.targetsize-{0}.png" -f $target) -Width $target -Height $target -IconRatio 0.92
    }

    Save-IconAsset -Source $source -SourceBounds $sourceBounds -Name "Square44x44Logo.targetsize-24_altform-unplated.png" -Width 24 -Height 24 -IconRatio 0.92

    foreach ($target in @(16,24,32,48,256)) {
        Save-IconAsset -Source $source -SourceBounds $sourceBounds -Name ("Square44x44Logo.altform-lightunplated_targetsize-{0}.png" -f $target) -Width $target -Height $target -IconRatio 0.92
    }

    foreach ($target in @(16,32,48,256)) {
        Save-IconAsset -Source $source -SourceBounds $sourceBounds -Name ("Square44x44Logo.altform-unplated_targetsize-{0}.png" -f $target) -Width $target -Height $target -IconRatio 0.92
    }
}
finally {
    $source.Dispose()
}
