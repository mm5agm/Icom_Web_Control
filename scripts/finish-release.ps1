# finish-release.ps1
# Completes a release where the develop work is done but the tag and GitHub release
# were not created. Run from the develop branch.
#
# Usage:
#   Production release: .\scripts\finish-release.ps1 -Version v1.1.0
#   Pre-release:        .\scripts\finish-release.ps1 -Version v1.1.0-pre1 -PreRelease
#
# A pre-release differs in three ways:
#   1. develop is NOT merged into main (pre-releases live on the develop line only).
#   2. The tag is created on develop, not main.
#   3. The GitHub release is created with --prerelease (not --latest), so IWC's
#      auto-updater -- which polls /releases/latest -- ignores it. Only users
#      who follow the direct link see the build.
#
# ---------------------------------------------------------------------------
# Why this script checks exit codes on every git call
#
# It did not, until v1.0.3 was released on 2026-08-05. The develop -> main merge
# hit a conflict in README.md (both branches had edited the download badge). Git
# stopped with a conflicted index and a non-zero exit code, and the script -- which
# had no way to notice -- carried straight on: it tagged $Version on UNMERGED main
# and published a GitHub release from it. That release was v1.0.0 code wearing a
# v1.0.3 label, and the build workflow had already started packaging it into an
# installer for the public download link.
#
# $ErrorActionPreference = 'Stop' does NOT catch this. It governs PowerShell
# errors; a native executable returning non-zero is not one. The only thing that
# sees a failed git command is $LASTEXITCODE, so every git and gh call in this
# script goes through Invoke-Checked below.
# ---------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$PreRelease,

    # Skip the preflight that checks the version in the source files and the
    # README release notes. Use only when you know why they disagree.
    [switch]$SkipVersionCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Repo root, so the script works from anywhere.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# --- helpers ---------------------------------------------------------------

# Run a native command and throw if it fails. Output is streamed to the console
# as normal so the transcript still reads like the old script.
function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$What
    )
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (exit $LASTEXITCODE). Command: $Exe $($Arguments -join ' ')"
    }
}

# Run a git command for its output. Throws on failure.
function Get-GitOutput {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $out = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed (exit $LASTEXITCODE)."
    }
    return $out
}

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Warn { param([string]$Text) Write-Host $Text -ForegroundColor DarkYellow }

# --- preflight -------------------------------------------------------------

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is not on PATH."
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is not installed. Download from https://cli.github.com/"
}

# gh being installed is not the same as gh being logged in. Check now rather
# than at the last step, when main has already been merged, pushed and tagged.
& gh auth status
if ($LASTEXITCODE -ne 0) {
    throw "gh is not authenticated. Run: gh auth login"
}

if (-not $Version.StartsWith('v')) { $Version = "v$Version" }

# "v1.2.3-pre1" -> "1.2.3". The source files carry the numeric core only; the
# tag carries any pre-release label.
$VersionCore = ($Version.TrimStart('v') -split '-')[0]
if ($VersionCore -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' does not contain a three-part numeric version (got '$VersionCore')."
}

$branch = Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD')
if ($branch -ne 'develop') {
    throw "Must be on the develop branch. Currently on: $branch"
}

# A conflicted or half-finished merge from a previous run must not be built on.
$unmerged = Get-GitOutput @('diff', '--name-only', '--diff-filter=U')
if ($unmerged) {
    throw "The working tree has unresolved merge conflicts:`n  $($unmerged -join "`n  ")`nResolve them and commit before releasing."
}

Write-Step "Fetching from origin..."
Invoke-Checked -Exe 'git' -Arguments @('fetch', 'origin', '--tags', '--prune') -What 'git fetch'

# A tag that already exists is the other way this script can publish the wrong
# commit: `git tag` fails, and if nobody checks, `gh release create` happily
# builds a release from wherever the OLD tag points.
$existingLocal = Get-GitOutput @('tag', '--list', $Version)
if ($existingLocal) {
    throw "Tag $Version already exists locally (points at $(Get-GitOutput @('rev-parse', '--short', $Version))).`nTo redo the release: gh release delete $Version --yes --cleanup-tag"
}
$existingRemote = Get-GitOutput @('ls-remote', '--tags', 'origin', $Version)
if ($existingRemote) {
    throw "Tag $Version already exists on origin.`nTo redo the release: gh release delete $Version --yes --cleanup-tag"
}

# Version consistency. Every one of these has to say the same thing, or the
# installer, the About page and the tag disagree about what this build is.
# Icom_Web_Control.csproj is included because it was missed by hand in v1.0.3 --
# it is NOT in the release checklist in CLAUDE.md.
if (-not $SkipVersionCheck) {
    $versionSites = @(
        @{ File = 'Models/AppVersion.cs'; Pattern = 'Current\s*=\s*"([^"]+)"' },
        @{ File = 'installer.nsi';        Pattern = '!define\s+VERSION\s+"([^"]+)"' },
        @{ File = 'Icom_Web_Control.csproj'; Pattern = '<Version>([^<]+)</Version>' },
        @{ File = 'Icom_Web_Control.csproj'; Pattern = '<FileVersion>([^<]+)</FileVersion>' },
        @{ File = 'Icom_Web_Control.csproj'; Pattern = '<AssemblyVersion>([^<]+)</AssemblyVersion>' }
    )

    $mismatches = @()
    foreach ($site in $versionSites) {
        $path = Join-Path $RepoRoot $site.File
        if (-not (Test-Path $path)) {
            $mismatches += "$($site.File) -- file not found"
            continue
        }
        $text = Get-Content $path -Raw
        $m = [regex]::Match($text, $site.Pattern)
        if (-not $m.Success) {
            $mismatches += "$($site.File) -- no match for $($site.Pattern)"
            continue
        }
        $found = $m.Groups[1].Value
        # AssemblyVersion/FileVersion are four-part (1.0.3.0); compare the stem.
        if ($found -ne $VersionCore -and $found -ne "$VersionCore.0") {
            $mismatches += "$($site.File) says '$found', expected '$VersionCore'"
        }
    }

    if ($mismatches.Count -gt 0) {
        throw "Version bump incomplete:`n  $($mismatches -join "`n  ")`nBump them, or re-run with -SkipVersionCheck."
    }
    Write-Host "Version $VersionCore is consistent across AppVersion.cs, installer.nsi and the csproj." -ForegroundColor Green

    # Rules 13/14 in .claude/rules.md: README release notes are mandatory,
    # pre-releases included. The download badge tracks FULL releases only.
    $readme = Get-Content (Join-Path $RepoRoot 'README.md') -Raw
    if ($readme -notmatch "(?m)^#{2,4}\s+v$([regex]::Escape($VersionCore))\b") {
        throw "README.md has no release-notes heading for v$VersionCore (rule 13). Add it, or re-run with -SkipVersionCheck."
    }
    $badgeCurrent = [regex]::Match($readme, 'badge/Download-v([0-9.]+)-')
    if ($badgeCurrent.Success) {
        $badgeVersion = $badgeCurrent.Groups[1].Value
        if (-not $PreRelease -and $badgeVersion -ne $VersionCore) {
            throw "README download badge still says v$badgeVersion but this is a full release of v$VersionCore (rule 13)."
        }
        if ($PreRelease -and $badgeVersion -eq $VersionCore) {
            Write-Warn "README download badge has been bumped to v$VersionCore, but rule 13 says the badge tracks full releases only. Continuing."
        }
    }
}

# --- 1. commit pending work on develop -------------------------------------

# Use `git add -u` (not `git add .`) so we only stage modifications to ALREADY
# tracked files. This stops the script accidentally committing untracked
# build artefacts like Icom_Web_Control_Setup.exe (which the .gitignore
# correctly excludes but `git add .` would override).
# If you genuinely want new files in the release, stage them manually before
# running this script.
$pending = Get-GitOutput @('status', '--porcelain', '--untracked-files=no')
if ($pending) {
    Write-Host "About to commit these pending tracked changes on develop:" -ForegroundColor Yellow
    $pending | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    $label = 'Release'
    if ($PreRelease) { $label = 'Pre-release' }
    Invoke-Checked -Exe 'git' -Arguments @('add', '-u') -What 'git add -u'
    Invoke-Checked -Exe 'git' -Arguments @('commit', '-m', "${label}: pending changes for $Version") -What 'git commit'
}

# Warn if untracked files are present but do not stage them.
$untracked = Get-GitOutput @('ls-files', '--others', '--exclude-standard')
if ($untracked) {
    Write-Warn "Note: ignoring untracked files (not committed):"
    $untracked | ForEach-Object { Write-Warn "  $_" }
}

# --- 2. push develop -------------------------------------------------------

Write-Step "Pushing develop..."
Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', 'develop') -What 'git push origin develop'

$developSha = Get-GitOutput @('rev-parse', 'develop')

# --- 3. tag, and release ---------------------------------------------------

if ($PreRelease) {
    # Pre-release: tag from develop, no merge into main.
    $tagTarget = $developSha
    Write-Step "Creating tag $Version on develop (pre-release path -- no main merge)..."
}
else {
    # Production release: merge develop into main, tag main.
    Write-Step "Merging develop into main..."
    Invoke-Checked -Exe 'git' -Arguments @('checkout', 'main') -What 'git checkout main'

    try {
        # --ff-only: main should never carry commits of its own. If it does,
        # stop rather than opening an editor for a merge nobody expected.
        Invoke-Checked -Exe 'git' -Arguments @('pull', '--ff-only', 'origin', 'main') -What 'git pull --ff-only origin main'

        & git merge develop --no-ff -m "Release $Version"
        if ($LASTEXITCODE -ne 0) {
            $conflicts = & git diff --name-only --diff-filter=U
            $detail = "git merge exited $LASTEXITCODE."
            if ($conflicts) {
                $detail = "Merge conflicts in:`n  $($conflicts -join "`n  ")"
            }
            throw @"
$detail

NOTHING HAS BEEN TAGGED OR RELEASED. You are on main with a half-finished merge.
To finish by hand:
  1. Resolve the conflicts (usually 'git checkout --theirs <file>' to take develop's side).
  2. git add <file> ; git commit --no-edit
  3. Confirm main matches develop:  git diff develop --stat   (must be empty)
  4. git push origin main
  5. Re-run this script -- the merge will then be a no-op.
To abandon instead: git merge --abort ; git checkout develop
"@
        }

        # The check that would have caught the v1.0.3 accident. After a clean
        # merge, main's tree must be identical to develop's -- if it is not,
        # something was dropped and the tag would ship the wrong code.
        $treeDiff = Get-GitOutput @('diff', 'develop', '--stat')
        if ($treeDiff) {
            throw "main does not match develop after the merge:`n$($treeDiff -join "`n")`nNothing has been tagged. Investigate before releasing."
        }

        Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', 'main') -What 'git push origin main'
        $tagTarget = Get-GitOutput @('rev-parse', 'main')
    }
    finally {
        # Come back to develop so the working copy is never left on main by
        # accident. (Being left on main is also what makes VS Code lose the
        # solution file and show a red "No Solution" banner -- main did not
        # carry Icom_Web_Control.slnx until the v1.0.3 merge.)
        #
        # A conflicted merge pins us to main: git refuses to switch branches
        # with an unresolved index, and it is right to. Don't attempt it and
        # don't bury the real error under git's complaint about it -- the
        # message thrown above already explains how to finish or abandon.
        $stillConflicted = & git diff --name-only --diff-filter=U
        if ($stillConflicted) {
            Write-Warn "Left on main with the conflicted merge in place -- see the instructions above."
        }
        else {
            & git checkout develop
        }
    }

    Write-Step "Creating tag $Version on main ($(Get-GitOutput @('rev-parse', '--short', $tagTarget)))..."
}

# Tag the SHA that was actually verified above, not a branch name -- a branch
# name is what let the old script tag an unmerged main.
Invoke-Checked -Exe 'git' -Arguments @('tag', $Version, $tagTarget) -What "git tag $Version"
Invoke-Checked -Exe 'git' -Arguments @('push', 'origin', $Version) -What "git push origin $Version"

$pushedTag = Get-GitOutput @('ls-remote', '--tags', 'origin', $Version)
if (-not $pushedTag) {
    throw "Tag $Version is not on origin after pushing it. Stopping before the release is created."
}

if ($PreRelease) {
    Write-Step "Creating GitHub pre-release $Version..."
    $notesBody = @"
**Pre-release for testing -- not a public build.**

Do not install unless you're prepared for bugs and will report them on GitHub. IWC's in-app auto-updater ignores pre-releases, so existing users will not be prompted to upgrade to this build.

Please send feedback via GitHub Issues (or mm5agm@outlook.com). Mention the ``$Version`` tag when reporting.
"@
    $ghArgs = @('release', 'create', $Version, '--title', $Version, '--notes', $notesBody, '--prerelease')
}
else {
    Write-Step "Creating GitHub release $Version..."
    $ghArgs = @('release', 'create', $Version, '--title', $Version, '--notes', "Release $Version - see README.md for the full release notes. Please send bug reports to mm5agm@outlook.com", '--latest')
}

# The build workflow triggers on release:[created], not on the tag push, so this
# step is what actually produces the installer. If it fails, the tag exists but
# no build ever runs -- say so plainly rather than reporting success.
& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    throw @"
gh release create failed (exit $LASTEXITCODE), but tag $Version IS pushed.
No build has run -- the workflow triggers on release creation, not on the tag.
Either retry the release:
  gh release create $Version --title $Version --notes "Release $Version" $(if ($PreRelease) { '--prerelease' } else { '--latest' })
or remove the tag and start over:
  git tag -d $Version ; git push origin :refs/tags/$Version
"@
}

Write-Host ""
$kind = 'Release'
if ($PreRelease) { $kind = 'Pre-release' }
Write-Host "$kind $Version created successfully at $(Get-GitOutput @('rev-parse', '--short', $tagTarget))." -ForegroundColor Green
Write-Host "The installer is built by the workflow, not by this script -- it is not downloadable until that finishes." -ForegroundColor Yellow
Write-Host "Watch it:       gh run watch" -ForegroundColor Yellow
Write-Host "Build workflow: https://github.com/mm5agm/Icom_Web_Control/actions" -ForegroundColor Yellow
Write-Host "Releases:       https://github.com/mm5agm/Icom_Web_Control/releases" -ForegroundColor Yellow
