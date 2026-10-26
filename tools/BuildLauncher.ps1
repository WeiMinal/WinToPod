# SPDX-License-Identifier: GPL-3.0-or-later
param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$root = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Install Visual Studio C++ x64 build tools and Windows SDK.' }
    $setup = Join-Path $vs 'Common7/Tools/VsDevCmd.bat'
    & cmd.exe /d /c "`"$setup`" -no_logo -arch=x64 && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
    }
    if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize C++ tools.' }
}
$temp = Join-Path $root 'artifacts/launcher'
New-Item -ItemType Directory -Force $temp, (Split-Path -Parent $Output) | Out-Null
Push-Location (Join-Path $root 'packaging')
try {
    & rc.exe /nologo /fo "$temp/launcher.res" launcher.rc
    if ($LASTEXITCODE -ne 0) { throw 'Launcher resource build failed.' }
    & cl.exe /nologo /W4 /WX /utf-8 /O2 /MT /D_CRT_SECURE_NO_WARNINGS launcher.c "/Fo$temp/launcher.obj" "/Fe$Output" /link "$temp/launcher.res" user32.lib shell32.lib /SUBSYSTEM:WINDOWS /MANIFEST:NO /DYNAMICBASE /NXCOMPAT
    if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
} finally { Pop-Location }
