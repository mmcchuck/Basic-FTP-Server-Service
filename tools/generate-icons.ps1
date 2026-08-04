<#
.SYNOPSIS
    Generates the application and tray icons.

.DESCRIPTION
    The .ico files under src/BasicFtpServer.App/Resources are build artefacts of this
    script, which is kept in the repository so the artwork stays reproducible and tweakable
    rather than being an opaque binary somebody has to redraw by hand.

    The glyph is an upload arrow over a base bar: the arrow reads as "send", the bar as the
    machine receiving it. It is deliberately blunt, because the icon spends most of its life
    at 16x16 in a notification area.

    Run after changing any geometry or colour below:
        powershell -File tools\generate-icons.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutputDirectory) {
    # Resolved here rather than as a parameter default: $PSScriptRoot is not reliably
    # populated during parameter binding.
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputDirectory = Join-Path $scriptDirectory '..\src\BasicFtpServer.App\Resources'
}

# Sizes Windows actually asks for: notification area, Start Menu, Explorer, alt-tab.
$SmallSizes = @(16, 20, 24, 32, 48, 64)
$AppSizes = $SmallSizes + @(128, 256)

function New-RoundedRectPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-GlyphBitmap {
    param([int]$Size, [string]$TopColour, [string]$BottomColour)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # All geometry is authored on a 32x32 grid and scaled, so every size stays identical.
    $s = $Size / 32.0

    $body = New-RoundedRectPath -X (1*$s) -Y (1*$s) -W (30*$s) -H (30*$s) -R (7*$s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, $Size)),
        [System.Drawing.ColorTranslator]::FromHtml($TopColour),
        [System.Drawing.ColorTranslator]::FromHtml($BottomColour))
    $g.FillPath($brush, $body)

    # Upload arrow.
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF((16.0*$s), (5.5*$s))),
        (New-Object System.Drawing.PointF((24.0*$s), (14.0*$s))),
        (New-Object System.Drawing.PointF((19.3*$s), (14.0*$s))),
        (New-Object System.Drawing.PointF((19.3*$s), (21.0*$s))),
        (New-Object System.Drawing.PointF((12.7*$s), (21.0*$s))),
        (New-Object System.Drawing.PointF((12.7*$s), (14.0*$s))),
        (New-Object System.Drawing.PointF((8.0*$s),  (14.0*$s)))
    ))

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillPath($white, $arrow)

    # Base bar: the thing receiving the upload.
    $bar = New-RoundedRectPath -X (8.5*$s) -Y (23.5*$s) -W (15*$s) -H (4*$s) -R (1.6*$s)
    $g.FillPath($white, $bar)

    $bar.Dispose(); $arrow.Dispose(); $body.Dispose()
    $white.Dispose(); $brush.Dispose(); $g.Dispose()
    return $bmp
}

# A 32-bit BMP icon entry: BITMAPINFOHEADER, then bottom-up BGRA, then the AND mask.
# The mask is all zeros because transparency comes from the alpha channel, but it still has
# to be present and padded to a 4-byte stride or Windows renders garbage.
function Get-BmpEntry {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([uint32]40)
    $writer.Write([int32]$w)
    $writer.Write([int32]($h * 2))   # height counts XOR + AND
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)         # BI_RGB
    $writer.Write([uint32]($w * $h * 4))
    $writer.Write([int32]0); $writer.Write([int32]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $locked = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                               [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $buffer = New-Object byte[] ($locked.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $buffer, 0, $buffer.Length)
    $Bitmap.UnlockBits($locked)

    for ($y = $h - 1; $y -ge 0; $y--) {
        $writer.Write($buffer, $y * $locked.Stride, $w * 4)
    }

    $maskStride = [math]::Floor(($w + 31) / 32) * 4
    $writer.Write((New-Object byte[] ($maskStride * $h)), 0, $maskStride * $h)

    $writer.Flush()
    return $stream.ToArray()
}

function Save-Icon {
    param([string]$Path, [int[]]$Sizes, [string]$TopColour, [string]$BottomColour)

    $entries = foreach ($size in $Sizes) {
        $bmp = New-GlyphBitmap -Size $size -TopColour $TopColour -BottomColour $BottomColour
        # Every frame is BMP, including 128 and 256 where the Vista+ convention is PNG.
        # PNG frames would save ~300 KB, but GDI+ cannot decode them: System.Drawing throws
        # "Requested range extends past the end of the array" on any attempt to read one.
        # The shell copes either way, but WinForms asks for a frame sized to the current DPI
        # and would hit the large ones on a high-DPI display.
        $data = Get-BmpEntry $bmp
        $bmp.Dispose()
        [PSCustomObject]@{ Size = $size; Data = $data }
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)

    $writer.Write([uint16]0)                 # reserved
    $writer.Write([uint16]1)                 # type: icon
    $writer.Write([uint16]$entries.Count)

    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)               # palette size
        $writer.Write([byte]0)               # reserved
        $writer.Write([uint16]1)             # colour planes
        $writer.Write([uint16]32)            # bits per pixel
        $writer.Write([uint32]$entry.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Data.Length
    }

    foreach ($entry in $entries) {
        $writer.Write($entry.Data, 0, $entry.Data.Length)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    $writer.Dispose()
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$icons = @(
    @{ File = 'App.ico';         Sizes = $AppSizes;   Top = '#4C8DF6'; Bottom = '#1D4ED8' }
    @{ File = 'TrayRunning.ico'; Sizes = $SmallSizes; Top = '#34D06B'; Bottom = '#15803D' }
    @{ File = 'TrayWarning.ico'; Sizes = $SmallSizes; Top = '#FBBF24'; Bottom = '#B45309' }
    @{ File = 'TrayStopped.ico'; Sizes = $SmallSizes; Top = '#F87171'; Bottom = '#B91C1C' }
)

foreach ($icon in $icons) {
    $path = Join-Path $OutputDirectory $icon.File
    Save-Icon -Path $path -Sizes $icon.Sizes -TopColour $icon.Top -BottomColour $icon.Bottom
    $size = (Get-Item $path).Length
    Write-Host ("  {0,-18} {1,3} sizes  {2,7:N0} bytes" -f $icon.File, $icon.Sizes.Count, $size)
}

Write-Host "Icons written to $OutputDirectory"
