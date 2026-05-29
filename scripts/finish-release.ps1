# finish-release.ps1
# Completes a release where the develop work is done but the tag and GitHub release
# were not created. Run from the develop branch.
#
# Usage: .\scripts\finish-release.ps1 -Version v1.1.0

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
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
# build artefacts like Yaesu_Web_Control_Setup.exe (which the .gitignore
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

# 3. Merge develop into main
Write-Host "Merging develop into main..." -ForegroundColor Cyan
git checkout main
git pull origin main
git merge develop --no-ff -m "Release $Version"
git push origin main

# 4. Return to develop
git checkout develop

# 5. Create and push the tag
Write-Host "Creating tag $Version..." -ForegroundColor Cyan
git tag $Version main
git push origin $Version

# 6. Create the GitHub release (triggers Build and Release Installer workflow)
Write-Host "Creating GitHub release $Version..." -ForegroundColor Cyan
gh release create $Version `
    --title $Version `
    --notes "Release $Version - please send bug reports to mm5agm@outlook.com" `
    --latest

Write-Host ""
Write-Host "Release $Version created successfully." -ForegroundColor Green
Write-Host "Build workflow: https://github.com/mm5agm/Yaesu_Web_Control/actions" -ForegroundColor Yellow
Write-Host "Releases:       https://github.com/mm5agm/Yaesu_Web_Control/releases" -ForegroundColor Yellow
