<#
.SYNOPSIS
  Draws the Cadence icon and writes src/Cadence.App/Assets/Cadence.ico.

.DESCRIPTION
  The welcome window's brand mark in icon form: a steel-blue C on the navy surface colour. The C is
  an open ring rather than a font glyph, so it stays legible at 16 px and needs no font installed,
  and with its gap to the right it reads as a gauge as much as a letter.

  Every size is drawn on its own rather than scaled down from 256 px, so the small sizes get strokes
  thick enough to survive. The .ico is committed; run this again only when the mark changes.

.PARAMETER Preview
  Optional folder to also receive each size as a PNG, for checking the result by eye.
#>
[CmdletBinding()]
param(
    [string]$Output = '',
    [string]$Preview = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'src\Cadence.App\Assets\Cadence.ico' }

# Theme/Dark.xaml: SurfaceColor, Surface3Color, AccentColor.
$surface = [Drawing.Color]::FromArgb(255, 0x0D, 0x15, 0x23)
$edge    = [Drawing.Color]::FromArgb(255, 0x1A, 0x27, 0x3A)
$accent  = [Drawing.Color]::FromArgb(255, 0x7D, 0xAB, 0xDD)

function New-IconPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)

    # Rounded square, inset half a pixel so its edge falls on pixel centres.
    $inset = 0.5
    $w = $size - 1.0
    $d = 2 * [Math]::Max(2.0, $size * 0.2)
    $square = New-Object Drawing.Drawing2D.GraphicsPath
    $square.AddArc($inset, $inset, $d, $d, 180, 90)
    $square.AddArc($inset + $w - $d, $inset, $d, $d, 270, 90)
    $square.AddArc($inset + $w - $d, $inset + $w - $d, $d, $d, 0, 90)
    $square.AddArc($inset, $inset + $w - $d, $d, $d, 90, 90)
    $square.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush $surface), $square)
    if ($size -ge 32) { $g.DrawPath((New-Object Drawing.Pen $edge, ([Math]::Max(1.0, $size / 64.0))), $square) }

    # The C: three quarters of a ring, open to the right, with round ends.
    $stroke = [Math]::Max(2.0, $size * 0.14)
    $radius = $size * 0.27
    $c = $size / 2.0
    $pen = New-Object Drawing.Pen $accent, $stroke
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pen, $c - $radius, $c - $radius, 2 * $radius, 2 * $radius, 45, 270)

    $g.Dispose()
    $stream = New-Object IO.MemoryStream
    $bmp.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $stream.ToArray()
}

# The sizes Windows asks for across DPI settings: shell lists, taskbar, Start, Alt+Tab, Explorer.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { $images.Add((New-IconPng $size)) }

# ICO: a 6-byte header, a 16-byte entry per image, then the PNG-compressed images themselves.
$ico = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $ico
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

New-Item -ItemType Directory -Force (Split-Path -Parent $Output) | Out-Null
[IO.File]::WriteAllBytes($Output, $ico.ToArray())
Write-Host ("Wrote {0} ({1:N0} bytes, {2} sizes)" -f $Output, $ico.Length, $sizes.Count)

if ($Preview) {
    New-Item -ItemType Directory -Force $Preview | Out-Null
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        [IO.File]::WriteAllBytes((Join-Path $Preview ("icon-{0}.png" -f $sizes[$i])), $images[$i])
    }
}
