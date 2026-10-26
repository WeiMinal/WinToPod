# SPDX-License-Identifier: GPL-3.0-or-later
# Exercise publication using a fake gh boundary; never contacts GitHub.
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('WinToPod.ReleaseChecks.' + [guid]::NewGuid().ToString('N'))
$originalTarget = $env:WINTOPOD_RELEASE_TARGET; $originalRepo = $env:GITHUB_REPOSITORY
$global:ReleaseCalls = [Collections.Generic.List[string]]::new()
$global:ReleaseTarget = 'a' * 40; $global:ReleasePrevious = 'b' * 40; $global:ReleaseOldTag = 'c' * 40
function global:gh {
    $a = @($args); $call = $a -join ' '; $global:ReleaseCalls.Add($call); $global:LASTEXITCODE = 0
    if ($a[0] -eq 'api' -and $a -notcontains 'PATCH') {
        if ($a[1] -like '*/git/ref/heads/main') {
            $global:ReleaseHeadReads++
            $sha = if ($global:ReleaseScenario -eq 'stale-main' -or ($global:ReleaseScenario -eq 'head-race' -and $global:ReleaseHeadReads -gt 1)) { 'd' * 40 } else { $global:ReleaseTarget }
            return (@{ object = @{ sha = $sha; type = 'commit' } } | ConvertTo-Json -Compress)
        }
        if ($a[1] -like '*/git/ref/tags/*') {
            $global:ReleaseTagReads++
            $sha = if ($global:ReleaseScenario -eq 'tag-race' -and $global:ReleaseTagReads -gt 1) { 'd' * 40 } elseif ($global:ReleaseScenario -eq 'resume') { $global:ReleaseTarget } else { $global:ReleaseOldTag }
            return (@{ object = @{ sha = $sha; type = 'commit' } } | ConvertTo-Json -Compress)
        }
        if ($a[1] -like '*/compare/*') {
            return (@{ status = $(if ($global:ReleaseScenario -in @('unrelated-tag','sibling-rewrite')) { 'diverged' } else { 'ahead' }); merge_base_commit = @{ sha = $(if ($global:ReleaseScenario -eq 'unrelated-tag') { 'f' * 40 } else { 'e' * 40 }) } } | ConvertTo-Json -Depth 3 -Compress)
        }
        if ($a[1] -like '*/git/commits/*') { return (@{ parents = @(@{ sha = 'e' * 40 }) } | ConvertTo-Json -Depth 3 -Compress) }
        throw ('Unexpected read: ' + $call)
    }
    if ($global:ReleaseScenario -eq 'upload-failure' -and $a[0] -eq 'release' -and $a[1] -eq 'upload') { $global:LASTEXITCODE = 1 }
}
try {
    New-Item -ItemType Directory -Force "$temp/tools", "$temp/docs", "$temp/artifacts/release" | Out-Null
    Copy-Item "$sourceRoot/tools/PublishRelease.ps1" "$temp/tools/PublishRelease.ps1"
    Copy-Item "$sourceRoot/Directory.Build.props" "$temp/Directory.Build.props"
    Copy-Item "$sourceRoot/docs/RELEASE.md" "$temp/docs/RELEASE.md"
    [xml]$props = Get-Content "$temp/Directory.Build.props"
    $tag = 'V' + ([version][string]$props.Project.PropertyGroup.Version).ToString(2)
    foreach ($name in @("WinToPod-$tag-win-x64.zip", "WinToPod-$tag-source.zip", 'SHA256SUMS.txt')) { Set-Content "$temp/artifacts/release/$name" 'synthetic publication fixture' }
    $env:WINTOPOD_RELEASE_TARGET = $global:ReleaseTarget; $env:GITHUB_REPOSITORY = 'fixture/repository'
    foreach ($case in @('new-release','rewrite','sibling-rewrite','resume','stale-main','head-race','tag-race','unrelated-tag','upload-failure')) {
        $global:ReleaseScenario = $case; $global:ReleaseHeadReads = 0; $global:ReleaseTagReads = 0; $global:ReleaseCalls.Clear()
        $errorSeen = $false
        try {
            if ($case -eq 'new-release') { & "$temp/tools/PublishRelease.ps1" }
            else { & "$temp/tools/PublishRelease.ps1" -RewritePreviousHead $global:ReleasePrevious }
        } catch { $errorSeen = $true }
        $shouldFail = $case -in @('stale-main','head-race','tag-race','unrelated-tag','upload-failure')
        if ($errorSeen -ne $shouldFail) { throw ('Unexpected outcome: ' + $case) }
        $writes = @($global:ReleaseCalls | Where-Object { $_ -like 'release *' -or $_ -like '*--method PATCH*' })
        if ($case -in @('stale-main','head-race','tag-race','unrelated-tag') -and $writes.Count) { throw ('Mutation escaped validation: ' + $case) }
        if ($case -eq 'new-release' -and @($writes | Where-Object { $_ -like '*PATCH*' -or $_ -like '*--clobber*' }).Count) { throw 'Ordinary publication overwrote an existing tag/asset' }
        if ($case -in @('rewrite','sibling-rewrite') -and ($writes.Count -ne 4 -or $writes[0] -notlike '*--draft=true*' -or $writes[1] -notlike '*PATCH*' -or $writes[2] -notlike '*--clobber*' -or $writes[3] -notlike '*--draft=false*')) { throw 'Rewrite order is not draft/tag/assets/publish' }
        if ($case -eq 'resume' -and @($writes | Where-Object { $_ -like '*PATCH*' }).Count) { throw 'Resuming publication moved the tag again' }
        if ($case -eq 'upload-failure' -and @($writes | Where-Object { $_ -like '*--draft=false*' }).Count) { throw 'Failed upload was publicly published' }
        Write-Output ('PASS ' + $case)
    }
} finally {
    $env:WINTOPOD_RELEASE_TARGET = $originalTarget; $env:GITHUB_REPOSITORY = $originalRepo
    Remove-Item Function:\gh -ErrorAction SilentlyContinue
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# The final scenario deliberately sets a failure code in the mock. Do not
# leak it into GitHub Actions' surrounding PowerShell step.
$global:LASTEXITCODE = 0
