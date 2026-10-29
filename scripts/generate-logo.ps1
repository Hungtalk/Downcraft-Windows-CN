#requires -Version 5.1
<#
.SYNOPSIS
    Renders the DownCraft logo to PNG sizes and a multi-resolution ICO.

.DESCRIPTION
    Draws the artwork once at 1024x1024 with System.Drawing (GDI+), using the geometry of the
    512x512 design space defined by assets/logo/downcraft.svg, then downsamples it to 512,
    256, 128, 64, 48, 32 and 16 pixels with HighQualityBicubic interpolation.

    The ICO is written by hand: ICONDIR + ICONDIRENTRY records followed by PNG payloads for
    16, 32, 48, 64, 128 and 256 pixels. PNG-compressed icons are supported by Windows Vista
    and later (Windows Explorer and the shell load them at their declared size).

    The script is idempotent: every run recreates the output files from scratch and needs no
    administrator rights.

.PARAMETER OutputDirectory
    Destination folder. Defaults to <repository root>\assets\logo.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\generate-logo.ps1

.NOTES
    Requires Windows PowerShell 5.1 and the .NET Framework System.Drawing assembly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# ---------------------------------------------------------------------------------------------
# Artwork constants (512x512 design space, identical to assets/logo/downcraft.svg)
# ---------------------------------------------------------------------------------------------
$DesignSize = 512.0
$RenderSize = 1024
$PngSizes = @(512, 256, 128, 64, 48, 32, 16)
$IcoSizes = @(16, 32, 48, 64, 128, 256)

$BadgeColorStart = [System.Drawing.ColorTranslator]::FromHtml('#2563EB')
$BadgeColorEnd = [System.Drawing.ColorTranslator]::FromHtml('#7C3AED')
$White = [System.Drawing.Color]::White
$GlossColor = [System.Drawing.Color]::FromArgb(18, 255, 255, 255)   # 18/255 = 7% white

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------
function New-RoundedRectanglePath {
    [CmdletBinding()]
    [OutputType([System.Drawing.Drawing2D.GraphicsPath])]
    param(
        [Parameter(Mandatory = $true)][double]$X,
        [Parameter(Mandatory = $true)][double]$Y,
        [Parameter(Mandatory = $true)][double]$Width,
        [Parameter(Mandatory = $true)][double]$Height,
        [Parameter(Mandatory = $true)][double]$Radius
    )

    $diameter = $Radius * 2.0
    if (($diameter -gt $Width) -or ($diameter -gt $Height)) {
        throw ("Corner radius {0} does not fit into {1}x{2}." -f $Radius, $Width, $Height)
    }

    $topLeft = New-Object System.Drawing.RectangleF -ArgumentList ([float]$X), ([float]$Y), ([float]$diameter), ([float]$diameter)
    $topRight = New-Object System.Drawing.RectangleF -ArgumentList ([float]($X + $Width - $diameter)), ([float]$Y), ([float]$diameter), ([float]$diameter)
    $bottomRight = New-Object System.Drawing.RectangleF -ArgumentList ([float]($X + $Width - $diameter)), ([float]($Y + $Height - $diameter)), ([float]$diameter), ([float]$diameter)
    $bottomLeft = New-Object System.Drawing.RectangleF -ArgumentList ([float]$X), ([float]($Y + $Height - $diameter)), ([float]$diameter), ([float]$diameter)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    try {
        $path.AddArc($topLeft, [float]180.0, [float]90.0)
        $path.AddArc($topRight, [float]270.0, [float]90.0)
        $path.AddArc($bottomRight, [float]0.0, [float]90.0)
        $path.AddArc($bottomLeft, [float]90.0, [float]90.0)
        $path.CloseFigure()
    }
    catch {
        $path.Dispose()
        throw
    }
    return $path
}

function Save-ScaledPng {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Drawing.Bitmap]$Source,
        [Parameter(Mandatory = $true)][int]$Size,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $imageAttributes = New-Object System.Drawing.Imaging.ImageAttributes
            try {
                $imageAttributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
                $destination = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $Size, $Size
                $graphics.DrawImage($Source, $destination, 0, 0, $Source.Width, $Source.Height, [System.Drawing.GraphicsUnit]::Pixel, $imageAttributes)
            }
            finally {
                $imageAttributes.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-MultiSizeIcon {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Entries
    )

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter -ArgumentList $stream
    try {
        # ICONDIR: reserved=0, type=1 (icon), count
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$Entries.Count)

        # ICONDIRENTRY records: 16 bytes each, payloads start right after the directory.
        $offset = 6 + (16 * $Entries.Count)
        foreach ($entry in $Entries) {
            $payload = [byte[]]$entry.Bytes
            $size = [int]$entry.Size
            if ($size -ge 256) {
                $dimension = [byte]0          # 0 means 256 in the ICO format
            }
            else {
                $dimension = [byte]$size
            }

            $writer.Write([byte]$dimension)   # bWidth
            $writer.Write([byte]$dimension)   # bHeight
            $writer.Write([byte]0)            # bColorCount (0 = true colour)
            $writer.Write([byte]0)            # bReserved
            $writer.Write([UInt16]1)          # wPlanes
            $writer.Write([UInt16]32)         # wBitCount
            $writer.Write([UInt32]$payload.Length)
            $writer.Write([UInt32]$offset)
            $offset += $payload.Length
        }

        # PNG payloads, back to back.
        foreach ($entry in $Entries) {
            $payload = [byte[]]$entry.Bytes
            $writer.Write($payload, 0, $payload.Length)
        }
        $writer.Flush()
    }
    finally {
        $writer.Dispose()
    }

    $data = $stream.ToArray()
    $stream.Dispose()
    [System.IO.File]::WriteAllBytes($Path, $data)
}

function Complete-Script {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode
    )

    # Exit through Environment.Exit so the process return code is always deterministic; the
    # normal PowerShell shutdown path can be delayed or faulted by third-party shell hooks,
    # which would otherwise replace the exit code we set here.
    try {
        [System.Console]::Out.Flush()
    }
    catch {
        # No console (for example a redirected or headless host); nothing to flush.
    }
    [System.Environment]::Exit($ExitCode)
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------
try {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $scriptDirectory = Split-Path -Parent $PSCommandPath
        $OutputDirectory = Join-Path (Split-Path -Parent $scriptDirectory) 'assets\logo'
    }
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $OutputDirectory -Force -ErrorAction Stop | Out-Null
    }
    if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
        throw ('Cannot create the output directory: {0}' -f $OutputDirectory)
    }

    Write-Host ''
    Write-Host 'DownCraft logo generator'
    Write-Host ('  design space : {0}x{0} (assets/logo/downcraft.svg)' -f $DesignSize)
    Write-Host ('  output       : {0}' -f $OutputDirectory)
    Write-Host ''

    # -----------------------------------------------------------------------------------------
    # Draw the master image at 1024x1024.
    # -----------------------------------------------------------------------------------------
    $master = New-Object System.Drawing.Bitmap -ArgumentList $RenderSize, $RenderSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($master)
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)

            $scale = $RenderSize / $DesignSize

            # Badge: rounded square with the 135 degree blue -> violet gradient.
            $badgePath = New-RoundedRectanglePath -X 0.0 -Y 0.0 -Width ($DesignSize * $scale) -Height ($DesignSize * $scale) -Radius (112.0 * $scale)
            try {
                $gradientStart = New-Object System.Drawing.PointF -ArgumentList ([float]0.0), ([float]0.0)
                $gradientEnd = New-Object System.Drawing.PointF -ArgumentList ([float]($DesignSize * $scale)), ([float]($DesignSize * $scale))
                $badgeBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $gradientStart, $gradientEnd, $BadgeColorStart, $BadgeColorEnd
                try {
                    $graphics.FillPath($badgeBrush, $badgePath)
                }
                finally {
                    $badgeBrush.Dispose()
                }
            }
            finally {
                $badgePath.Dispose()
            }

            # Gloss: rounded rect (24,24)-(488,232), radius 88, white at 7%.
            $glossPath = New-RoundedRectanglePath -X (24.0 * $scale) -Y (24.0 * $scale) -Width (464.0 * $scale) -Height (208.0 * $scale) -Radius (88.0 * $scale)
            try {
                $glossBrush = New-Object System.Drawing.SolidBrush -ArgumentList $GlossColor
                try {
                    $graphics.FillPath($glossBrush, $glossPath)
                }
                finally {
                    $glossBrush.Dispose()
                }
            }
            finally {
                $glossPath.Dispose()
            }

            # Tray and arrow, stroked with white 44 (design units), round caps and joins.
            $pen = New-Object System.Drawing.Pen -ArgumentList $White, ([float](44.0 * $scale))
            try {
                $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

                # Open-top tray: M 128 292 V 364 A 44 44 0 0 0 172 408
                #                  H 340 A 44 44 0 0 0 384 364 V 292
                $trayPath = New-Object System.Drawing.Drawing2D.GraphicsPath
                try {
                    $trayPath.AddLine([float](128.0 * $scale), [float](292.0 * $scale), [float](128.0 * $scale), [float](364.0 * $scale))

                    $bottomLeftArc = New-Object System.Drawing.RectangleF -ArgumentList ([float](128.0 * $scale)), ([float](320.0 * $scale)), ([float](88.0 * $scale)), ([float](88.0 * $scale))
                    $trayPath.AddArc($bottomLeftArc, [float]180.0, [float]-90.0)

                    $trayPath.AddLine([float](172.0 * $scale), [float](408.0 * $scale), [float](340.0 * $scale), [float](408.0 * $scale))

                    $bottomRightArc = New-Object System.Drawing.RectangleF -ArgumentList ([float](296.0 * $scale)), ([float](320.0 * $scale)), ([float](88.0 * $scale)), ([float](88.0 * $scale))
                    $trayPath.AddArc($bottomRightArc, [float]90.0, [float]-90.0)

                    $trayPath.AddLine([float](384.0 * $scale), [float](364.0 * $scale), [float](384.0 * $scale), [float](292.0 * $scale))

                    $graphics.DrawPath($pen, $trayPath)
                }
                finally {
                    $trayPath.Dispose()
                }

                # Arrow: shaft M 256 96 V 224, chevron M 176 184 L 256 264 L 336 184.
                $arrowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
                try {
                    $arrowPath.AddLine([float](256.0 * $scale), [float](96.0 * $scale), [float](256.0 * $scale), [float](224.0 * $scale))
                    $arrowPath.AddLine([float](176.0 * $scale), [float](184.0 * $scale), [float](256.0 * $scale), [float](264.0 * $scale))
                    $arrowPath.AddLine([float](256.0 * $scale), [float](264.0 * $scale), [float](336.0 * $scale), [float](184.0 * $scale))
                    $graphics.DrawPath($pen, $arrowPath)
                }
                finally {
                    $arrowPath.Dispose()
                }
            }
            finally {
                $pen.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }

        # Master PNG (drawn 1:1 at 1024, never resampled).
        $masterPath = Join-Path $OutputDirectory 'downcraft-1024.png'
        $master.Save($masterPath, [System.Drawing.Imaging.ImageFormat]::Png)

        # Downsample the 1024 master to every smaller size.
        foreach ($size in $PngSizes) {
            $pngPath = Join-Path $OutputDirectory ('downcraft-{0}.png' -f $size)
            Save-ScaledPng -Source $master -Size $size -Path $pngPath
        }
    }
    finally {
        $master.Dispose()
    }

    # -----------------------------------------------------------------------------------------
    # Report PNG files (read back from disk).
    # -----------------------------------------------------------------------------------------
    $pngPaths = New-Object 'System.Collections.Generic.List[string]'
    $pngPaths.Add((Join-Path $OutputDirectory 'downcraft-1024.png'))
    foreach ($size in $PngSizes) {
        $pngPaths.Add((Join-Path $OutputDirectory ('downcraft-{0}.png' -f $size)))
    }

    Write-Host 'PNG files:'
    foreach ($path in $pngPaths) {
        $length = (Get-Item -LiteralPath $path).Length
        $image = [System.Drawing.Image]::FromFile($path)
        try {
            Write-Host ('  {0,-20} {1,4} x {2,-4} {3,9} bytes' -f (Split-Path -Leaf $path), $image.Width, $image.Height, $length)
        }
        finally {
            $image.Dispose()
        }
    }

    # -----------------------------------------------------------------------------------------
    # Build the ICO from the exact PNG bytes written above.
    # -----------------------------------------------------------------------------------------
    $icoEntries = New-Object 'System.Collections.Generic.List[object]'
    foreach ($size in $IcoSizes) {
        $pngPath = Join-Path $OutputDirectory ('downcraft-{0}.png' -f $size)
        $icoEntries.Add([pscustomobject]@{
            Size  = $size
            Bytes = [System.IO.File]::ReadAllBytes($pngPath)
        })
    }

    $icoPath = Join-Path $OutputDirectory 'downcraft.ico'
    Write-MultiSizeIcon -Path $icoPath -Entries ($icoEntries.ToArray())

    Write-Host ''
    Write-Host 'ICO file:'
    Write-Host ('  {0,-20} {1,9} bytes' -f 'downcraft.ico', (Get-Item -LiteralPath $icoPath).Length)

    # Verify the directory we just wrote.
    $icoBytes = [System.IO.File]::ReadAllBytes($icoPath)
    $entryCount = [BitConverter]::ToUInt16($icoBytes, 4)
    if ($entryCount -ne $IcoSizes.Count) {
        throw ('ICO contains {0} directory entries, expected {1}.' -f $entryCount, $IcoSizes.Count)
    }
    Write-Host '  ICO directory entries:'
    for ($i = 0; $i -lt $entryCount; $i++) {
        $base = 6 + (16 * $i)
        $declaredWidth = [int]$icoBytes[$base]
        if ($declaredWidth -eq 0) { $declaredWidth = 256 }
        $declaredHeight = [int]$icoBytes[$base + 1]
        if ($declaredHeight -eq 0) { $declaredHeight = 256 }
        $payloadLength = [BitConverter]::ToUInt32($icoBytes, $base + 8)
        $payloadOffset = [BitConverter]::ToUInt32($icoBytes, $base + 12)
        $isPng = ($icoBytes[$payloadOffset] -eq 0x89) -and ($icoBytes[$payloadOffset + 1] -eq 0x50) -and ($icoBytes[$payloadOffset + 2] -eq 0x4E) -and ($icoBytes[$payloadOffset + 3] -eq 0x47)

        if (-not $isPng) {
            throw ('ICO entry {0} does not contain a PNG payload.' -f $i)
        }
        if ($declaredWidth -ne $IcoSizes[$i]) {
            throw ('ICO entry {0} is {1}px, expected {2}px.' -f $i, $declaredWidth, $IcoSizes[$i])
        }

        Write-Host ('    {0,2}. {1,3} x {2,-3} PNG  {3,6} bytes  offset {4}' -f ($i + 1), $declaredWidth, $declaredHeight, $payloadLength, $payloadOffset)
    }

    # Load the icon back through System.Drawing.
    $icon = New-Object System.Drawing.Icon($icoPath)
    try {
        Write-Host ('  New-Object System.Drawing.Icon(...) -> {0} x {1}' -f $icon.Width, $icon.Height)
        if (($icon.Width -ne 256) -or ($icon.Height -ne 256)) {
            Write-Host '  note: System.Drawing selects the system large-icon size (32 px) when no size'
            Write-Host '        is requested, so it reports 32 x 32 here. The 256 px entry is present'
            Write-Host '        in the directory above; Explorer and the shell load it at 256 px.'
        }
    }
    finally {
        $icon.Dispose()
    }

    Write-Host ''
    Write-Host ('Done: {0} files written to {1}' -f ($pngPaths.Count + 1), $OutputDirectory)
    Complete-Script -ExitCode 0
}
catch {
    Write-Host ''
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    if ($_.InvocationInfo -and $_.InvocationInfo.PositionMessage) {
        Write-Host $_.InvocationInfo.PositionMessage
    }
    Complete-Script -ExitCode 1
}
