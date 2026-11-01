# Erzeugt src\Folio\Assets\folio.ico: ein Blatt mit Eselsohr und Lesezeichen auf tintenblauem Grund.
# Laeuft mit Windows PowerShell 5.1 (System.Drawing), reines ASCII.
param([string]$Ziel = (Join-Path $PSScriptRoot '..\src\Folio\Assets\folio.ico'))

Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function P([single]$x, [single]$y, [single]$k) { return New-Object System.Drawing.PointF(($x * $k), ($y * $k)) }

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 32.0

    $rect = New-RoundRect 0 0 ($s - 1) ($s - 1) ($s * 0.25)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 92, 123, 255), [System.Drawing.Color]::FromArgb(255, 52, 70, 201))
    $g.FillPath($bg, $rect)

    # Blatt (bei kleinen Groessen etwas groesser, damit es lesbar bleibt)
    $z = if ($s -le 24) { 1.12 } else { 1.0 }
    $ox = 16 - 16 * $z; $oy = 16.5 - 16.5 * $z
    function Q([single]$x, [single]$y) { return P ($ox + $x * $z) ($oy + $y * $z) $k }

    $blatt = New-Object System.Drawing.Drawing2D.GraphicsPath
    $blatt.AddPolygon([System.Drawing.PointF[]]@((Q 10 7), (Q 19 7), (Q 23 11), (Q 23 26), (Q 10 26)))
    $g.FillPath([System.Drawing.Brushes]::White, $blatt)
    $ohr = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ohr.AddPolygon([System.Drawing.PointF[]]@((Q 19 7), (Q 19 11), (Q 23 11)))
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 201, 211, 255))), $ohr)

    if ($s -ge 32) {
        $linie = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 61, 90, 214))
        foreach ($z2 in @(@(15.2, 8), @(18.6, 8), @(22, 5.2))) {
            $a = Q 12.5 $z2[0]
            $g.FillRectangle($linie, $a.X, $a.Y, ($z2[1] * $k * $z), (1.7 * $k * $z))
        }
    } elseif ($s -ge 20) {
        $linie = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 61, 90, 214))
        foreach ($z2 in @(@(16, 8), @(20.5, 6))) {
            $a = Q 12.5 $z2[0]
            $g.FillRectangle($linie, $a.X, $a.Y, ($z2[1] * $k * $z), (2.2 * $k * $z))
        }
    }

    $band = New-Object System.Drawing.Drawing2D.GraphicsPath
    $band.AddPolygon([System.Drawing.PointF[]]@((Q 13.2 7), (Q 16.2 7), (Q 16.2 13), (Q 14.7 11.8), (Q 13.2 13)))
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 167, 58))), $band)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$groessen = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$bilder = @()
foreach ($s in $groessen) { $bilder += ,(New-IconPng $s) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$groessen.Count)
$offset = 6 + 16 * $groessen.Count
for ($i = 0; $i -lt $groessen.Count; $i++) {
    $s = $groessen[$i]; $daten = $bilder[$i]
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$daten.Length); $w.Write([UInt32]$offset)
    $offset += $daten.Length
}
foreach ($daten in $bilder) { $w.Write($daten) }
$w.Flush()

$ordner = Split-Path $Ziel -Parent
if (-not (Test-Path $ordner)) { New-Item -ItemType Directory -Force $ordner | Out-Null }
[System.IO.File]::WriteAllBytes($Ziel, $out.ToArray())
"Icon geschrieben: $Ziel"
