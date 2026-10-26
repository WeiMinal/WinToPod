# SPDX-License-Identifier: GPL-3.0-or-later
param([switch]$WindowsSelfCheck)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $stage = Join-Path $projectRoot 'artifacts/win-x64'
    # A new staging directory prevents stale runtime files entering a release.
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force artifacts/validation | Out-Null
    dotnet build src/WinToPod.Checks -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Core build failed' }
    dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll | Tee-Object artifacts/validation/checks.txt
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
    foreach ($pair in @(@('--test-automatic-connection', 'automatic-connection.json'), @('--test-video-sync', 'video-sync.json'))) {
        dotnet src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll $pair[0] | Set-Content -Encoding utf8 (Join-Path artifacts/validation $pair[1])
        if ($LASTEXITCODE -ne 0) { throw "Checks failed: $($pair[0])" }
    }
    foreach ($pair in @(@('CaptureChecks', 'capture.jsonl'), @('CaptureInteropChecks', 'capture-interop.json'), @('OutputRoutingChecks', 'output-routing.json'), @('WinUIControllerChecks', 'controller.json'))) {
        dotnet run --project (Join-Path tests ('WinToPod.' + $pair[0])) -c Release | Set-Content -Encoding utf8 (Join-Path artifacts/validation $pair[1])
        if ($LASTEXITCODE -ne 0) { throw "Checks failed: $($pair[0])" }
    }
    dotnet publish src/WinToPod.WinUI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 -o "$stage/app"
    if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed' }
    & "$PSScriptRoot/BuildLauncher.ps1" -Output "$stage/WinToPod.exe"
    Get-ChildItem $stage -Recurse -Filter '*.pdb' | Remove-Item
    Copy-Item -Recurse browser/WinToPod.VideoSync "$stage/app/浏览器音画补偿"
    Copy-Item -Recurse licenses "$stage/app/licenses"
    Copy-Item LICENSE, THIRD-PARTY.md, CHANGELOG.md "$stage/app"
    Copy-Item docs/USAGE.md "$stage/使用说明.md"
    if ($WindowsSelfCheck) {
        $report = Join-Path $projectRoot 'artifacts/validation/windows-ui.json'
        $env:WINTOPOD_NONINTERACTIVE = '1'
        try {
            # Test the root launcher, including a quoted Unicode report path.
            $process = Start-Process "$stage/WinToPod.exe" -ArgumentList '--self-check', ('"' + $report + '"') -PassThru
            if (-not $process.WaitForExit(100000)) { $process.Kill(); throw 'WinUI startup self-check timed out' }
            if ($process.ExitCode -ne 0 -or -not (Test-Path $report)) { throw 'WinUI startup self-check failed' }
            if (-not (Get-Content -Raw $report | ConvertFrom-Json).Passed) { throw 'WinUI layout checks failed' }
        } finally { Remove-Item Env:WINTOPOD_NONINTERACTIVE -ErrorAction SilentlyContinue }
    }
} finally { Pop-Location }
