Add-Type -AssemblyName System.Drawing

function Save-Ico {
    param([System.Drawing.Bitmap]$bmp, [string]$path)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $png = $ms.ToArray(); $ms.Close()
    $offset = 22
    $size   = $png.Length
    $ico = [byte[]](
        0x00,0x00,0x01,0x00,0x01,0x00,   # ICO header (1 image)
        0x00,                             # width  (0 = 256)
        0x00,                             # height (0 = 256)
        0x00, 0x00,                       # colour count, reserved
        0x01,0x00, 0x20,0x00,            # planes, bit depth
        ($size-band 0xFF),(($size-shr 8)-band 0xFF),(($size-shr 16)-band 0xFF),(($size-shr 24)-band 0xFF),
        ($offset-band 0xFF),(($offset-shr 8)-band 0xFF),(($offset-shr 16)-band 0xFF),(($offset-shr 24)-band 0xFF)
    ) + $png
    [System.IO.File]::WriteAllBytes($path, $ico)
}

$sz = 256
$bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# Background circle
$bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,30,90,160))
$g.FillEllipse($bg, 4, 4, 248, 248)

# Colours for drawing
$white  = [System.Drawing.Color]::White
$wave   = [System.Drawing.Color]::FromArgb(200,180,220,255)

$pMast  = New-Object System.Drawing.Pen($white, 9)
$pCross = New-Object System.Drawing.Pen($white, 6)
$pWire  = New-Object System.Drawing.Pen($white, 4)
$pWave  = New-Object System.Drawing.Pen($wave,  5)

foreach ($p in @($pMast,$pCross,$pWire,$pWave)) {
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
}

# Mast
$g.DrawLine($pMast, 128, 32, 128, 210)

# Base platform
$g.DrawLine($pMast, 70, 210, 186, 210)

# Cross members
$g.DrawLine($pCross, 100, 95,  156, 95)
$g.DrawLine($pCross,  86, 145, 170, 145)
$g.DrawLine($pCross,  72, 195, 184, 195)

# Guy wires
$g.DrawLine($pWire, 128, 40,  72, 195)
$g.DrawLine($pWire, 128, 40, 184, 195)

# Radio waves
$g.DrawArc($pWave, 104, 14,  48, 32, 210, 120)
$g.DrawArc($pWave,  88,  4,  80, 50, 210, 120)
$g.DrawArc($pWave,  72, -8, 112, 70, 210, 120)

$g.Dispose()

$out = "C:\Users\colin\source\repos\Icom_Web_Control\wwwroot\favicon.ico"
Save-Ico $bmp $out
$bmp.Dispose()
Write-Host "Icon saved to $out"
