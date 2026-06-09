# bump-version.ps1
# Updates the four version markers before a release. Run from develop before
# finish-release.ps1.
#
# Updates:
#   1. Models\AppVersion.cs        - Current        ("X.Y.Z")
#   2. Models\AppVersion.cs        - ReleaseDate    ("YYYY-MM-DD")
#   3. installer.nsi               - !define VERSION
#   4. README.md                   - Latest-release shields.io badge URL
#
# Usage:
#   .\scripts\bump-version.ps1 -Version 2.2.0
#   .\scripts\bump-version.ps1 -Version v2.2.0
#   .\scripts\bump-version.ps1 -Version 2.2.0 -ReleaseDate 2026-06-10
#
# ReleaseDate defaults to today (system date) in ISO format.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$ReleaseDate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Normalise: strip leading 'v' for the semver value; keep the v-prefixed form
# for the README badge.
$semver = $Version.TrimStart('v', 'V')
$vtag   = "v$semver"

if (-not ($semver -match '^\d+\.\d+\.\d+$')) {
    Write-Error "Version must look like X.Y.Z or vX.Y.Z (got '$Version')"
    exit 1
}

if (-not $ReleaseDate) {
    $ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
}
if (-not ($ReleaseDate -match '^\d{4}-\d{2}-\d{2}$')) {
    Write-Error "ReleaseDate must be ISO (YYYY-MM-DD), got '$ReleaseDate'"
    exit 1
}

# Resolve repo root from script location.
$repoRoot = Split-Path $PSScriptRoot -Parent

$appVersionPath = Join-Path $repoRoot 'Models\AppVersion.cs'
$installerPath  = Join-Path $repoRoot 'installer.nsi'
$readmePath     = Join-Path $repoRoot 'README.md'

foreach ($p in @($appVersionPath, $installerPath, $readmePath)) {
    if (-not (Test-Path $p)) { Write-Error "File not found: $p"; exit 1 }
}

# Explicit UTF-8 reader/writer. PowerShell 5.1's `Get-Content` defaults to
# the system codepage (typically Windows-1252 in en-GB / en-US locales),
# which would mangle any non-ASCII bytes (emoji, em-dashes, smart quotes)
# on read — and `Set-Content -Encoding utf8` adds a BOM that some downstream
# tools (NSIS, some git diff readers) don't expect. Doing the byte-level
# read/write via System.IO.File side-steps both issues.
function Read-Utf8([string]$Path) {
    return [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Replace-OrFail([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Label) {
    # Verify the pattern actually matches somewhere — protects against
    # silently leaving a file untouched if e.g. the user reformatted the
    # source and our pattern no longer fits. We do NOT fail if the
    # replacement happens to equal the original (that's the legitimate
    # case where the value is already correct, e.g. two releases on the
    # same day so ReleaseDate doesn't change).
    if (-not [regex]::IsMatch($Text, $Pattern)) {
        Write-Error "No match for $Label (pattern: $Pattern). File untouched."
        exit 1
    }
    return [regex]::Replace($Text, $Pattern, $Replacement)
}

# ---- 1 & 2. Models\AppVersion.cs ----
$av = Read-Utf8 $appVersionPath
$av = Replace-OrFail $av 'Current\s*=\s*"\d+\.\d+\.\d+"' "Current = `"$semver`""      'AppVersion.Current'
$av = Replace-OrFail $av 'ReleaseDate\s*=\s*"\d{4}-\d{2}-\d{2}"' "ReleaseDate = `"$ReleaseDate`"" 'AppVersion.ReleaseDate'
Write-Utf8NoBom $appVersionPath $av
Write-Host ("[1/3] AppVersion.cs   -> Current={0,-8} ReleaseDate={1}" -f $semver, $ReleaseDate) -ForegroundColor Green

# ---- 3. installer.nsi ----
$ns = Read-Utf8 $installerPath
$ns = Replace-OrFail $ns '!define\s+VERSION\s+"\d+\.\d+\.\d+"' "!define VERSION `"$semver`"" 'installer.nsi VERSION'
Write-Utf8NoBom $installerPath $ns
Write-Host ("[2/3] installer.nsi   -> VERSION={0}" -f $semver) -ForegroundColor Green

# ---- 4. README.md shields.io badge ----
# Pattern matches the per-release Latest-release badge embedded in the URL.
# Leaves the Downloads badge (which references the installer asset URL) for
# any future per-release-asset bumps the user wants to make.
$rm = Read-Utf8 $readmePath
$rm = Replace-OrFail $rm 'Latest%20release-v\d+\.\d+\.\d+-' "Latest%20release-$vtag-" 'README Latest-release badge'
Write-Utf8NoBom $readmePath $rm
Write-Host ("[3/3] README.md       -> Latest-release badge={0}" -f $vtag) -ForegroundColor Green

Write-Host ""
Write-Host "All four version markers bumped to $vtag (release date $ReleaseDate)." -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. git diff                                       # review the changes"
Write-Host "  2. Verify a v2.2.0-style release-notes section already exists in README.md"
Write-Host "  3. .\scripts\finish-release.ps1 -Version $vtag    # commit, merge, tag, GitHub release"
