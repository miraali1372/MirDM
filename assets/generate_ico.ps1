Add-Type -AssemblyName System.Drawing

$masterPath = "D:\rea\mirdm\assets\icon_master.png"
$assetsDir = "D:\rea\mirdm\assets"
$sizes = @(256, 128, 64, 48, 32, 16)

$master = [System.Drawing.Image]::FromFile($masterPath)

$pngEntries = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()

    $pngPath = "$assetsDir\icon_$s.png"
    $bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    $bytes = [System.IO.File]::ReadAllBytes($pngPath)
    Write-Host "Generated $s x $s PNG: $($bytes.Length) bytes"
    $pngEntries += [PSCustomObject]@{
        Size = $s
        Bytes = $bytes
    }
}
$master.Dispose()

# Build Multi-Resolution ICO
$count = $sizes.Count
$headerSize = 6 + ($count * 16)
$currentOffset = $headerSize

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# ICONDIR
$bw.Write([UInt16]0)      # Reserved
$bw.Write([UInt16]1)      # Type 1 = Icon
$bw.Write([UInt16]$count)  # Count

# ICONDIRENTRY list
foreach ($entry in $pngEntries) {
    $w = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
    $h = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
    $bw.Write([Byte]$w)
    $bw.Write([Byte]$h)
    $bw.Write([Byte]0)    # Color count
    $bw.Write([Byte]0)    # Reserved
    $bw.Write([UInt16]1)  # Planes
    $bw.Write([UInt16]32) # BitCount
    $bw.Write([UInt32]$entry.Bytes.Length)
    $bw.Write([UInt32]$currentOffset)

    $currentOffset += $entry.Bytes.Length
}

# Image Data
foreach ($entry in $pngEntries) {
    $bw.Write($entry.Bytes)
}

$icoBytes = $ms.ToArray()
$bw.Dispose()
$ms.Dispose()

$icoPath = "$assetsDir\icon.ico"
[System.IO.File]::WriteAllBytes($icoPath, $icoBytes)
Write-Host "Generated Multi-Resolution ICO: $icoPath ($($icoBytes.Length) bytes)"

Copy-Item $icoPath "D:\rea\mirdm\icon.ico" -Force
Copy-Item "$assetsDir\icon_256.png" "D:\rea\mirdm\assets\icon.png" -Force
Copy-Item $icoPath "D:\rea\mirdm\src\server\public\favicon.ico" -Force
if (Test-Path "D:\rea\mirdm\dist\server\public") {
    Copy-Item $icoPath "D:\rea\mirdm\dist\server\public\favicon.ico" -Force
}
Write-Host "Multi-resolution icon files copied to all required locations."
