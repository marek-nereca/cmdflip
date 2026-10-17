# Builds Cmdflip.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or runtime install needed.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' } # 32-bit Windows
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:"$out\Cmdflip.exe" `
    /win32manifest:"$PSScriptRoot\src\app.manifest" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    "$PSScriptRoot\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\Cmdflip.exe"
