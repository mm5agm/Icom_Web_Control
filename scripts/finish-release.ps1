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
#   3. The GitHub release is created with --prerelease (not --latest), so YWC's
#      auto-updater -- which polls /releases/latest -- ignores it. Only users
#      who follow the direct link see the build.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$PreRelease
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) is not installed. Download from https://cli.github.com/"
    exit 1
}

$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne "develop") {
    Write-Error "Must be on the develop branch. Currently on: $branch"
    exit 1
}

if (-not $Version.StartsWith('v')) { $Version = "v$Version" }

# 1. Commit any pending changes on develop.
# Use `git add -u` (not `git add .`) so we only stage modifications to ALREADY
# tracked files. This stops the script accidentally committing untracked
# build artefacts like Icom_Web_Control_Setup.exe (which the .gitignore
# correctly excludes but `git add .` would override).
# If you genuinely want new files in the release, stage them manually before
# running this script.
$pending = git status --porcelain --untracked-files=no
if ($pending) {
    Write-Host "Committing pending tracked changes on develop..." -ForegroundColor Yellow
    git add -u
    git commit -m "Pre-release: pending changes for $Version"
}

# Warn if untracked files are present but do not stage them.
$untracked = git ls-files --others --exclude-standard
if ($untracked) {
    Write-Host "Note: ignoring untracked files (not committed):" -ForegroundColor DarkYellow
    $untracked | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkYellow }
}

# 2. Push develop
Write-Host "Pushing develop..." -ForegroundColor Cyan
git push origin develop

if ($PreRelease) {
    # Pre-release: tag from develop, no merge into main.
    Write-Host "Creating tag $Version on develop (pre-release path -- no main merge)..." -ForegroundColor Cyan
    git tag $Version develop
    git push origin $Version

    Write-Host "Creating GitHub pre-release $Version..." -ForegroundColor Cyan
    $notesBody = @"
**Pre-release for testing -- not a public build.**

Do not install unless you're prepared for bugs and will report them on GitHub. YWC's in-app auto-updater ignores pre-releases, so existing users will not be prompted to upgrade to this build.

Please send feedback via GitHub Issues (or mm5agm@outlook.com). Mention the ``$Version`` tag when reporting.
"@
    gh release create $Version `
        --title $Version `
        --notes $notesBody `
        --prerelease

    Write-Host ""
    Write-Host "Pre-release $Version created successfully." -ForegroundColor Green
    Write-Host "Build workflow: https://github.com/mm5agm/Icom_Web_Control/actions" -ForegroundColor Yellow
    Write-Host "Releases:       https://github.com/mm5agm/Icom_Web_Control/releases" -ForegroundColor Yellow
}
else {
    # Production release: merge develop into main, tag main.
    Write-Host "Merging develop into main..." -ForegroundColor Cyan
    git checkout main
    git pull origin main
    git merge develop --no-ff -m "Release $Version"
    git push origin main

    # Return to develop
    git checkout develop

    Write-Host "Creating tag $Version on main..." -ForegroundColor Cyan
    git tag $Version main
    git push origin $Version

    Write-Host "Creating GitHub release $Version..." -ForegroundColor Cyan
    gh release create $Version `
        --title $Version `
        --notes "Release $Version - please send bug reports to mm5agm@outlook.com" `
        --latest

    Write-Host ""
    Write-Host "Release $Version created successfully." -ForegroundColor Green
    Write-Host "Build workflow: https://github.com/mm5agm/Icom_Web_Control/actions" -ForegroundColor Yellow
    Write-Host "Releases:       https://github.com/mm5agm/Icom_Web_Control/releases" -ForegroundColor Yellow
}
