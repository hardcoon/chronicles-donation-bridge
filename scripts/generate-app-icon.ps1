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
    $backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 28, 31, 34))
    $backgroundPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 75, 82, 90), [single](2.5 * $scale))
    $markBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 119, 184, 232))
    $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 240, 244, 248))
    $font = [System.Drawing.Font]::new('Segoe UI', [single](43 * $scale), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        Add-RoundedRectanglePath $backgroundPath (4 * $scale) (4 * $scale) (120 * $scale) (120 * $scale) (25 * $scale)
        $graphics.FillPath($backgroundBrush, $backgroundPath)
        $graphics.DrawPath($backgroundPen, $backgroundPath)
        # The DB monogram matches the application's header and remains legible
        # in both the title bar and the 16-pixel notification-area variant.
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $graphics.DrawString(
            'DB',
            $font,
            $textBrush,
            [System.Drawing.RectangleF]::new(10 * $scale, 16 * $scale, 108 * $scale, 82 * $scale),
            $format)
        $graphics.FillRectangle($markBrush, 28 * $scale, 99 * $scale, 72 * $scale, 7 * $scale)
    }
    finally {
        $graphics.Dispose()
        $backgroundPath.Dispose()
        $backgroundBrush.Dispose()
        $backgroundPen.Dispose()
        $markBrush.Dispose()
        $textBrush.Dispose()
        $font.Dispose()
        $format.Dispose()
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
