# SPDX-License-Identifier: GPL-3.0-or-later
param([switch]$WindowsSelfCheck)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    New-Item -ItemType Directory -Force artifacts/validation | Out-Null
    dotnet build WinToPod.slnx -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll | Tee-Object artifacts/validation/checks.txt
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
    foreach ($pair in @(@('--test-automatic-connection', 'automatic-connection.json'), @('--test-video-sync', 'video-sync.json'))) {
        dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll $pair[0] | Set-Content -Encoding utf8 (Join-Path artifacts/validation $pair[1])
        if ($LASTEXITCODE -ne 0) { throw "Checks failed: $($pair[0])" }
    }
    dotnet run --project tests/WinToPod.CaptureChecks -c Release | Set-Content -Encoding utf8 artifacts/validation/capture.jsonl
    if ($LASTEXITCODE -ne 0) { throw 'Capture/volume boundary checks failed' }
    dotnet run --project tests/WinToPod.CaptureInteropChecks -c Release | Set-Content -Encoding utf8 artifacts/validation/capture-interop.json
    if ($LASTEXITCODE -ne 0) { throw 'Native activation interface checks failed' }
    dotnet publish src/WinToPod.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:RuntimeFrameworkVersion=10.0.13 -p:DebugType=None -p:DebugSymbols=false -m:1 -o artifacts/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    if ($WindowsSelfCheck) {
        if (-not $IsWindows) { throw 'The UI self-check requires Windows' }
        $report = Join-Path $projectRoot 'artifacts/validation/windows-ui.json'
        $process = Start-Process (Join-Path $projectRoot 'artifacts/win-x64/WinToPod.exe') -ArgumentList '--self-check', ('"' + $report + '"') -Wait -PassThru
        if ($process.ExitCode -ne 0 -or -not (Test-Path $report)) { throw 'Windows UI self-check failed' }
    }
    Get-FileHash artifacts/win-x64/WinToPod.exe -Algorithm SHA256
} finally { Pop-Location }
