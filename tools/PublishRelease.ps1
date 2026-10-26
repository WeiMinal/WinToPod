# SPDX-License-Identifier: GPL-3.0-or-later
param([string]$RewritePreviousHead)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Get-GitHubJson([string]$Endpoint) {
    $json = & gh api $Endpoint
    if ($LASTEXITCODE -ne 0) { throw ('GitHub read failed: ' + $Endpoint) }
    return ($json -join "`n") | ConvertFrom-Json
}
Push-Location $root
try {
    [xml]$props = Get-Content Directory.Build.props
    $version = [string]$props.Project.PropertyGroup.Version
    $tag = 'V' + ([version]$version).ToString(2)
    $notes = Get-Content -Raw -Encoding utf8 docs/RELEASE.md
    $title, $body = $notes -split "`n", 2
    $title = $title.Trim(); $body = $body.Trim()
    if (-not $title.Contains($tag)) { throw 'Release title must include the product version' }
    $target = $env:WINTOPOD_RELEASE_TARGET
    if ($target -notmatch '^[0-9a-f]{40}$') { throw 'An exact commit SHA is required' }
    $repo = $env:GITHUB_REPOSITORY
    if ($repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'An explicit repository is required' }
    $files = @(Get-ChildItem artifacts/release -File | Select-Object -ExpandProperty FullName)
    $expected = @("WinToPod-$tag-win-x64.zip", "WinToPod-$tag-source.zip", 'SHA256SUMS.txt')
    if ($files.Count -ne 3 -or @(Compare-Object ($files | Split-Path -Leaf) $expected).Count) { throw 'Expected runtime ZIP, source ZIP and SHA256SUMS' }
    $noteFile = Join-Path $root 'artifacts/release-notes.md'
    [IO.File]::WriteAllText($noteFile, $body, [Text.UTF8Encoding]::new($false))
    $head = Get-GitHubJson "repos/$repo/git/ref/heads/main"
    if ($head.object.sha -ne $target) { throw 'Main changed; refusing to publish an obsolete build' }
    if ($RewritePreviousHead) {
        # Only the explicit force-push path may replace an existing version.
        # The previous tag must belong to the history the user replaced.
        if ($RewritePreviousHead -notmatch '^[0-9a-f]{40}$' -or $RewritePreviousHead -eq $target) { throw 'Invalid previous branch head' }
        $ref = Get-GitHubJson "repos/$repo/git/ref/tags/$tag"
        if ($ref.object.type -ne 'commit') { throw 'Annotated tags require explicit manual handling' }
        $old = $ref.object.sha
        if ($old -ne $target) {
            $ancestry = Get-GitHubJson "repos/$repo/compare/${old}...$RewritePreviousHead"
            if ($ancestry.status -notin @('ahead', 'identical')) {
                # A repeated amend can leave the old tag on a sibling commit.
                # Allow only single-commit rewrites sharing the exact base.
                $base = $ancestry.merge_base_commit.sha
                if ($ancestry.status -ne 'diverged' -or -not $base) { throw 'Release tag is outside the replaced history' }
                $nextCommit = Get-GitHubJson "repos/$repo/git/commits/$target"
                $priorCommit = Get-GitHubJson "repos/$repo/git/commits/$RewritePreviousHead"
                if (@($nextCommit.parents).Count -ne 1 -or @($priorCommit.parents).Count -ne 1 -or
                    $nextCommit.parents[0].sha -ne $base -or $priorCommit.parents[0].sha -ne $base) {
                    throw 'Release tag is outside the replaced history'
                }
            }
        }
        # Validate both references again before any release/tag mutation.
        $head = Get-GitHubJson "repos/$repo/git/ref/heads/main"
        $lease = Get-GitHubJson "repos/$repo/git/ref/tags/$tag"
        if ($head.object.sha -ne $target -or $lease.object.sha -ne $old) { throw 'Remote refs changed; refusing to replace the release' }
        & gh release edit $tag --draft=true
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage existing release update' }
        if ($old -ne $target) {
            & gh api "repos/$repo/git/refs/tags/$tag" --method PATCH -f "sha=$target" -F force=true
            if ($LASTEXITCODE -ne 0) { throw 'Tag update failed; release remains a draft' }
        }
        & gh release upload $tag --clobber @files
        if ($LASTEXITCODE -ne 0) { throw 'Asset update failed; release remains a draft' }
        & gh release edit $tag --target $target --title $title --notes-file $noteFile --draft=false --latest
    } else {
        # Ordinary pushes never implicitly overwrite existing tags/releases.
        & gh release create $tag --target $target --title $title --notes-file $noteFile --draft @files
        if ($LASTEXITCODE -ne 0) { throw 'Release creation/upload failed; existing release was not overwritten' }
        & gh release edit $tag --draft=false --latest
    }
    if ($LASTEXITCODE -ne 0) { throw 'Release remains a draft; publication failed' }
} finally { Pop-Location }
