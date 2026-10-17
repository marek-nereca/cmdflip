# Renders assets\Cmdflip.ico in all sizes Windows uses, from the same drawing code as the tray
# icon (src\IconArt.cs). Run once after changing the design; the .ico is committed so builds
# don't need this.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot '..\src\IconArt.cs') -ReferencedAssemblies System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot '..\assets\Cmdflip.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($s in $sizes) {
    $bmp = [Cmdflip.IconArt]::Render($s, $true)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $images.Add($ms.ToArray())
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $out"
