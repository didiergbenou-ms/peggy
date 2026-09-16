[CmdletBinding()]
param(
    [string]$OutputPath = (
        Join-Path (
            Split-Path -Parent $PSScriptRoot
        ) 'src\Peggy.Tray\Assets\Peggy.ico'
    ),
    [string]$PreviewPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-RoundedRectangle {
    param(
        [float]$X,
        [float]$Y,
        [float]$Width,
        [float]$Height,
        [float]$Radius
    )

    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc(
        $X + $Width - $diameter,
        $Y,
        $diameter,
        $diameter,
        270,
        90)
    $path.AddArc(
        $X + $Width - $diameter,
        $Y + $Height - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc(
        $X,
        $Y + $Height - $diameter,
        $diameter,
        $diameter,
        90,
        90)
    $path.CloseFigure()
    return $path
}

function New-PeggyMasterImage {
    $scale = 4.0
    $bitmap = [Drawing.Bitmap]::new(
        1024,
        1024,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode =
            [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality =
            [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode =
            [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)

        $backgroundRectangle = [Drawing.RectangleF]::new(
            8 * $scale,
            8 * $scale,
            240 * $scale,
            240 * $scale)
        $backgroundPath = New-RoundedRectangle `
            -X $backgroundRectangle.X `
            -Y $backgroundRectangle.Y `
            -Width $backgroundRectangle.Width `
            -Height $backgroundRectangle.Height `
            -Radius (52 * $scale)
        try {
            $background = [Drawing.Drawing2D.LinearGradientBrush]::new(
                $backgroundRectangle,
                [Drawing.Color]::FromArgb(255, 249, 160, 111),
                [Drawing.Color]::FromArgb(255, 213, 90, 57),
                [Drawing.Drawing2D.LinearGradientMode]::Vertical)
            $border = [Drawing.Pen]::new(
                [Drawing.Color]::FromArgb(255, 81, 59, 50),
                7 * $scale)
            try {
                $graphics.FillPath($background, $backgroundPath)
                $graphics.DrawPath($border, $backgroundPath)
            }
            finally {
                $border.Dispose()
                $background.Dispose()
            }
        }
        finally {
            $backgroundPath.Dispose()
        }

        $shadow = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(48, 53, 30, 21))
        $body = [Drawing.Drawing2D.LinearGradientBrush]::new(
            [Drawing.RectangleF]::new(
                61 * $scale,
                76 * $scale,
                134 * $scale,
                154 * $scale),
            [Drawing.Color]::FromArgb(255, 157, 108, 72),
            [Drawing.Color]::FromArgb(255, 111, 70, 48),
            [Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $wing = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(255, 100, 61, 39))
        $belly = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(255, 255, 228, 207))
        $eyeWhite = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        $eyeDark = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(255, 43, 43, 43))
        $highlight = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(230, 255, 255, 255))
        $beak = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(255, 249, 182, 77))
        $crest = [Drawing.SolidBrush]::new(
            [Drawing.Color]::FromArgb(255, 94, 56, 38))
        $feet = [Drawing.Pen]::new(
            [Drawing.Color]::FromArgb(255, 249, 182, 77),
            5 * $scale)
        $feet.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $feet.EndCap = [Drawing.Drawing2D.LineCap]::Round
        try {
            $graphics.FillEllipse(
                $shadow,
                66 * $scale,
                84 * $scale,
                134 * $scale,
                154 * $scale)
            $graphics.FillEllipse(
                $body,
                61 * $scale,
                76 * $scale,
                134 * $scale,
                154 * $scale)
            $graphics.FillEllipse(
                $wing,
                54 * $scale,
                119 * $scale,
                48 * $scale,
                91 * $scale)
            $graphics.FillEllipse(
                $wing,
                154 * $scale,
                119 * $scale,
                48 * $scale,
                91 * $scale)
            $graphics.FillEllipse(
                $belly,
                87 * $scale,
                137 * $scale,
                82 * $scale,
                88 * $scale)

            $graphics.FillEllipse(
                $body,
                70 * $scale,
                48 * $scale,
                116 * $scale,
                108 * $scale)
            $graphics.FillPolygon(
                $crest,
                [Drawing.PointF[]]@(
                    [Drawing.PointF]::new(104 * $scale, 57 * $scale),
                    [Drawing.PointF]::new(117 * $scale, 24 * $scale),
                    [Drawing.PointF]::new(129 * $scale, 61 * $scale)
                ))
            $graphics.FillPolygon(
                $crest,
                [Drawing.PointF[]]@(
                    [Drawing.PointF]::new(126 * $scale, 60 * $scale),
                    [Drawing.PointF]::new(143 * $scale, 31 * $scale),
                    [Drawing.PointF]::new(149 * $scale, 69 * $scale)
                ))

            $graphics.FillEllipse(
                $eyeWhite,
                87 * $scale,
                78 * $scale,
                36 * $scale,
                39 * $scale)
            $graphics.FillEllipse(
                $eyeWhite,
                133 * $scale,
                78 * $scale,
                36 * $scale,
                39 * $scale)
            $graphics.FillEllipse(
                $eyeDark,
                99 * $scale,
                90 * $scale,
                17 * $scale,
                18 * $scale)
            $graphics.FillEllipse(
                $eyeDark,
                140 * $scale,
                90 * $scale,
                17 * $scale,
                18 * $scale)
            $graphics.FillEllipse(
                $highlight,
                102 * $scale,
                92 * $scale,
                6 * $scale,
                6 * $scale)
            $graphics.FillEllipse(
                $highlight,
                143 * $scale,
                92 * $scale,
                6 * $scale,
                6 * $scale)
            $graphics.FillPolygon(
                $beak,
                [Drawing.PointF[]]@(
                    [Drawing.PointF]::new(113 * $scale, 116 * $scale),
                    [Drawing.PointF]::new(143 * $scale, 116 * $scale),
                    [Drawing.PointF]::new(128 * $scale, 138 * $scale)
                ))

            $graphics.DrawLine(
                $feet,
                106 * $scale,
                220 * $scale,
                106 * $scale,
                237 * $scale)
            $graphics.DrawLine(
                $feet,
                150 * $scale,
                220 * $scale,
                150 * $scale,
                237 * $scale)
            $graphics.DrawLine(
                $feet,
                106 * $scale,
                236 * $scale,
                96 * $scale,
                244 * $scale)
            $graphics.DrawLine(
                $feet,
                106 * $scale,
                236 * $scale,
                116 * $scale,
                244 * $scale)
            $graphics.DrawLine(
                $feet,
                150 * $scale,
                236 * $scale,
                140 * $scale,
                244 * $scale)
            $graphics.DrawLine(
                $feet,
                150 * $scale,
                236 * $scale,
                160 * $scale,
                244 * $scale)
        }
        finally {
            $feet.Dispose()
            $crest.Dispose()
            $beak.Dispose()
            $highlight.Dispose()
            $eyeDark.Dispose()
            $eyeWhite.Dispose()
            $belly.Dispose()
            $wing.Dispose()
            $body.Dispose()
            $shadow.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function ConvertTo-PngFrame {
    param(
        [Parameter(Mandatory)][Drawing.Bitmap]$Master,
        [Parameter(Mandatory)][int]$Size
    )

    $bitmap = [Drawing.Bitmap]::new(
        $Size,
        $Size,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode =
            [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality =
            [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode =
            [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode =
            [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode =
            [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage(
            $Master,
            [Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0,
            0,
            $Master.Width,
            $Master.Height,
            [Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    $stream = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$master = New-PeggyMasterImage
try {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = @(
        foreach ($size in $sizes) {
            [pscustomobject]@{
                Size = $size
                Data = ConvertTo-PngFrame -Master $master -Size $size
            }
        }
    )

    $stream = [IO.File]::Open(
        $resolvedOutput,
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) {
                [byte]0
            } else {
                [byte]$frame.Size
            }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame.Data)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    if (-not [string]::IsNullOrWhiteSpace($PreviewPath)) {
        $resolvedPreview = [IO.Path]::GetFullPath($PreviewPath)
        New-Item `
            -ItemType Directory `
            -Path (Split-Path -Parent $resolvedPreview) `
            -Force |
            Out-Null
        $preview = [Drawing.Bitmap]::new(
            512,
            512,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($preview)
        try {
            $graphics.InterpolationMode =
                [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($master, 0, 0, 512, 512)
            $preview.Save(
                $resolvedPreview,
                [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $preview.Dispose()
        }
    }
}
finally {
    $master.Dispose()
}

Write-Output $resolvedOutput
