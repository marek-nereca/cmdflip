# Builds Cmdflip.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or runtime install needed.
#
#   .\build.ps1                      dev build (version 0.0.0-dev)
#   .\build.ps1 -Version 1.2.0       release build
#   .\build.ps1 -Version 1.2.0 -Installer   also builds the Inno Setup installer
param(
    [string]$Version = '0.0.0-dev',
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'

$Version = $Version.TrimStart('v')
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') { throw "Version must look like 1.2.3 or 1.2.3-beta.1, got '$Version'" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' } # 32-bit Windows
$out = Join-Path $PSScriptRoot 'bin'
$obj = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force $out, $obj | Out-Null

$year = (Get-Date).Year
@"
using System.Reflection;
[assembly: AssemblyTitle("Cmdflip")]
[assembly: AssemblyProduct("Cmdflip")]
[assembly: AssemblyDescription("macOS keyboard shortcuts for Windows")]
[assembly: AssemblyCopyright("Copyright (c) $year Marek Nereca")]
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -Encoding UTF8 (Join-Path $obj 'AssemblyInfo.g.cs')

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:"$out\Cmdflip.exe" `
    /win32manifest:"$PSScriptRoot\src\app.manifest" `
    /win32icon:"$PSScriptRoot\assets\Cmdflip.ico" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    "$PSScriptRoot\src\*.cs" "$obj\AssemblyInfo.g.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\Cmdflip.exe ($Version)"

if ($Installer) {
    $iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
    & $iscc /Q "/DAppVersion=$Version" "/DAppNumericVersion=$numeric" "/O$out" "$PSScriptRoot\installer\Cmdflip.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Built $out\Cmdflip-$Version-setup.exe"
}
