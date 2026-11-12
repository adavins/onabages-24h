# Crea la icona de l'executable (.ico) a partir del logotip (app\renderer\img\logo.png). La fa servir empaquetar.cmd.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\crea-icona.ps1 <logo.png> <sortida.ico>
#
# Un .ico porta la mateixa imatge a diverses mides perquè Windows triï la que li cal (explorador, barra de tasques,
# escriptori). Les mides petites hi van en mapa de bits (el format clàssic, que tothom llegeix) i la de 256, en PNG.
# Si el logotip no és quadrat, es centra amb un marge transparent (no es deforma).
# Només fa servir el que ja porta Windows (System.Drawing): cap dependència.

param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Una mida en mapa de bits de 32 bits (amb transparència), com la vol un .ico: capçalera BITMAPINFOHEADER amb l'alçada
# doble, les files de baix a dalt en BGRA i al final la màscara (tota a zero: la transparència ja la porta el canal alfa).
function BitmapEntry([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $maskRow = [Math]::Ceiling($size / 32) * 4
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    $bytes = $ms.ToArray()
    $w.Dispose()
    return , $bytes
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$logo = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    $images = @()
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        # Mantenir la proporció: el costat més llarg ocupa tota la icona
        $scale = $size / [Math]::Max($logo.Width, $logo.Height)
        $w = [int][Math]::Round($logo.Width * $scale)
        $h = [int][Math]::Round($logo.Height * $scale)
        $g.DrawImage($logo, [int](($size - $w) / 2), [int](($size - $h) / 2), $w, $h)
        $g.Dispose()
        if ($size -ge 256) {
            # La gran, en PNG (ocupa molt menys)
            $stream = New-Object System.IO.MemoryStream
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $data = $stream.ToArray()
            $stream.Dispose()
        } else {
            $data = BitmapEntry $bitmap
        }
        $bitmap.Dispose()
        $images += , @($size, $data)
    }
} finally {
    $logo.Dispose()
}

# Format .ico: capçalera (6 bytes), una entrada de 16 bytes per mida i després les imatges
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0)               # reservat
$writer.Write([UInt16]1)               # 1 = icona
$writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $size = $image[0]; $data = $image[1]
    $dim = if ($size -ge 256) { 0 } else { $size }   # 0 vol dir 256
    $writer.Write([byte]$dim)              # amplada
    $writer.Write([byte]$dim)              # alçada
    $writer.Write([byte]0)                 # colors de paleta (cap)
    $writer.Write([byte]0)                 # reservat
    $writer.Write([UInt16]1)               # plans de color
    $writer.Write([UInt16]32)              # bits per píxel
    $writer.Write([UInt32]$data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image[1]) }
$writer.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Destination), $out.ToArray())
$writer.Dispose()
Write-Host "Icona creada: $Destination ($($sizes -join ', ') px)"
