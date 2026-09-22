[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$sourceRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $sourceRoot 'src\ChroniclesDonationBridge.App\Assets\ChroniclesDonationBridge.ico'
}

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $OutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

function Add-RoundedRectanglePath {
    param(
        [System.Drawing.Drawing2D.GraphicsPath]$Path,
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $diameter = [single]($Radius * 2)
    $Path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $Path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $Path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $Path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $Path.CloseFigure()
}

function Convert-ToIconFrame {
    param([int]$Size)

    $renderSize = 512
    $scale = [single]($renderSize / 128.0)
    $canvas = [System.Drawing.Bitmap]::new($renderSize, $renderSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $backgroundPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $bellPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 28, 31, 34))
    $backgroundPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 75, 82, 90), [single](2.5 * $scale))
    $bellBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 242, 174, 74))

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        Add-RoundedRectanglePath $backgroundPath (4 * $scale) (4 * $scale) (120 * $scale) (120 * $scale) (25 * $scale)
        $graphics.FillPath($backgroundBrush, $backgroundPath)
        $graphics.DrawPath($backgroundPen, $backgroundPath)

        # Bell handle.
        $graphics.FillEllipse($bellBrush, 55 * $scale, 17 * $scale, 18 * $scale, 16 * $scale)

        # Wide, symmetrical bell silhouette. It intentionally avoids thin lines,
        # so the mark remains recognisable in the 16-pixel taskbar variant.
        $bellPath.StartFigure()
        $bellPath.AddBezier(64 * $scale, 25 * $scale, 47 * $scale, 25 * $scale, 38 * $scale, 40 * $scale, 38 * $scale, 58 * $scale)
        $bellPath.AddLine([single](38 * $scale), [single](58 * $scale), [single](38 * $scale), [single](72 * $scale))
        $bellPath.AddBezier(38 * $scale, 72 * $scale, 38 * $scale, 80 * $scale, 33 * $scale, 87 * $scale, 27 * $scale, 91 * $scale)
        $bellPath.AddBezier(27 * $scale, 91 * $scale, 24 * $scale, 93 * $scale, 26 * $scale, 98 * $scale, 31 * $scale, 98 * $scale)
        $bellPath.AddLine([single](31 * $scale), [single](98 * $scale), [single](97 * $scale), [single](98 * $scale))
        $bellPath.AddBezier(97 * $scale, 98 * $scale, 102 * $scale, 98 * $scale, 104 * $scale, 93 * $scale, 101 * $scale, 91 * $scale)
        $bellPath.AddBezier(101 * $scale, 91 * $scale, 95 * $scale, 87 * $scale, 90 * $scale, 80 * $scale, 90 * $scale, 72 * $scale)
        $bellPath.AddLine([single](90 * $scale), [single](72 * $scale), [single](90 * $scale), [single](58 * $scale))
        $bellPath.AddBezier(90 * $scale, 58 * $scale, 90 * $scale, 40 * $scale, 81 * $scale, 25 * $scale, 64 * $scale, 25 * $scale)
        $bellPath.CloseFigure()
        $graphics.FillPath($bellBrush, $bellPath)
        $graphics.FillEllipse($bellBrush, 55 * $scale, 98 * $scale, 18 * $scale, 13 * $scale)
    }
    finally {
        $graphics.Dispose()
        $backgroundPath.Dispose()
        $bellPath.Dispose()
        $backgroundBrush.Dispose()
        $backgroundPen.Dispose()
        $bellBrush.Dispose()
    }

    $frame = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $frameGraphics = [System.Drawing.Graphics]::FromImage($frame)
    try {
        $frameGraphics.Clear([System.Drawing.Color]::Transparent)
        $frameGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $frameGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $frameGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $frameGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $frameGraphics.DrawImage($canvas, 0, 0, $Size, $Size)

        $stream = [System.IO.MemoryStream]::new()
        try {
            $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $stream.ToArray()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $frameGraphics.Dispose()
        $frame.Dispose()
        $canvas.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = foreach ($size in $sizes) {
    [pscustomobject]@{
        Size = $size
        Data = Convert-ToIconFrame -Size $size
    }
}

$fileStream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0) # Reserved.
    $writer.Write([uint16]1) # ICO.
    $writer.Write([uint16]$frames.Count)

    $dataOffset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0) # Palette.
        $writer.Write([byte]0) # Reserved.
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$dataOffset)
        $dataOffset += $frame.Data.Length
    }

    foreach ($frame in $frames) {
        $writer.Write([byte[]]$frame.Data)
    }
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Write-Host "Application icon generated: $OutputPath"
Write-Host "Frames: $($sizes -join ', ') px"
