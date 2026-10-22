# Disegna l'icona di Coprifuoco: un tasto di accensione che e' anche un orologio.
# Un solo concetto, forme grosse e colori piatti, cosi' si legge anche a 16 pixel.
# Serve solo per rigenerare l'icona (coprifuoco.ico): il programma finale non usa PowerShell.
Add-Type -AssemblyName System.Drawing

$ochre = [System.Drawing.Color]::FromArgb(222, 164, 92)
$cream = [System.Drawing.Color]::FromArgb(246, 236, 218)
$dark  = [System.Drawing.Color]::FromArgb(38, 32, 27)

function New-RoundedRect([float]$s, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $m = 0.5
    $p.AddArc($m, $m, $d, $d, 180, 90)
    $p.AddArc($s - $d - $m, $m, $d, $d, 270, 90)
    $p.AddArc($s - $d - $m, $s - $d - $m, $d, $d, 0, 90)
    $p.AddArc($m, $s - $d - $m, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-RoundPen([System.Drawing.Color]$c, [float]$w) {
    $p = New-Object System.Drawing.Pen $c, $w
    $p.StartCap = 'Round'; $p.EndCap = 'Round'
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush $dark), (New-RoundedRect $s ($s * 0.22)))

    # sulle dimensioni piccole il disegno e' piu' grosso e piu' spesso, per restare leggibile
    $k = if ($s -le 24) { 1.14 } elseif ($s -le 48) { 1.06 } else { 1.0 }
    $w  = [Math]::Max(2, $s * 0.085 * $(if ($s -le 24) { 1.15 } else { 1.0 }))
    $r  = $s * 0.27 * $k
    $cx = $s / 2
    $cy = $s * 0.54
    # anello aperto in alto
    $g.DrawArc((New-RoundPen $ochre $w), ($cx - $r), ($cy - $r), (2 * $r), (2 * $r), -55, 290)
    # lancetta delle 12 che attraversa l'apertura (e' anche la barretta del tasto di accensione)
    $g.DrawLine((New-RoundPen $cream $w), $cx, ($cy - $r * 1.25), $cx, $cy)
    # lancetta corta
    $g.DrawLine((New-RoundPen $cream $w), $cx, $cy, ($cx + $r * 0.56), ($cy + $r * 0.30))
    $g.Dispose()
    return $bmp
}

# Le dimensioni piccole vanno nel formato classico (DIB): la classe Icon di .NET, usata
# per l'icona vicino all'orologio, non legge i PNG dentro un .ico. Solo i 256 restano PNG.
function ConvertTo-IconBytes([System.Drawing.Bitmap]$bmp, [int]$s) {
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $ms.ToArray()
    }
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {          # le righe vanno dal basso verso l'alto
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $s)), 0, ($maskRow * $s))   # maschera AND vuota: conta l'alfa
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { $b = New-IconPng $s; , (ConvertTo-IconBytes $b $s); $b.Dispose() }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data, 0, $data.Length) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'coprifuoco.ico'), $out.ToArray())
Write-Host 'coprifuoco.ico creata'
