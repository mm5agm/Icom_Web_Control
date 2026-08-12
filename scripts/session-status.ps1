<#
.SYNOPSIS
    Start-of-session briefing: what has moved on GitHub, and where the to-do list lives.

.DESCRIPTION
    Lists open issues (with who spoke last), open pull requests, and discussions,
    then points at the work-queue to-do list.

    Run it by hand with -Plain for readable text. With no switch it emits the JSON
    a Claude Code SessionStart hook expects: a one-line summary for the terminal
    and the full briefing as context.

    Everything is best-effort. No network, no gh, not logged in - it says so and
    exits 0, because a briefing that blocks the session is worse than no briefing.

    ASCII only on purpose: Windows PowerShell 5.1 reads a BOM-less file as ANSI,
    so a stray em dash in here becomes mojibake or a parse error.

.EXAMPLE
    .\scripts\session-status.ps1 -Plain
#>
[CmdletBinding()]
param(
    [switch]$Plain
)

$ErrorActionPreference = 'Stop'
$Repo = 'mm5agm/Icom_Web_Control'
$Maintainer = 'mm5agm'

# The to-do list is a Claude Code auto-memory, not a file in this repo.
$MemoryDir = Join-Path $env:USERPROFILE '.claude\projects\c--Users-colin-source-repos-Icom-Web-Control\memory'
$WorkQueue = Join-Path $MemoryDir 'iwc-work-queue.md'

$lines = New-Object System.Collections.Generic.List[string]
$headline = @()

function Add-Line([string]$Text = '') { $lines.Add($Text) | Out-Null }

function Get-Age([string]$Iso) {
    if (-not $Iso) { return '' }
    $days = [int][Math]::Floor(((Get-Date).ToUniversalTime() - [datetime]::Parse($Iso).ToUniversalTime()).TotalDays)
    if ($days -le 0) { return 'today' }
    if ($days -eq 1) { return 'yesterday' }
    return "$days days ago"
}

function Get-Plural([int]$Count) { if ($Count -ne 1) { 's' } else { '' } }

# Three PS 5.1 traps shape the fetch code below:
#  1. ConvertFrom-Json hands a JSON array to the pipeline as ONE object instead
#     of enumerating it, so piping it straight into Where-Object yields a single
#     item that is itself an array - "0 PRs" would print as "1 PR" with a blank
#     row. Assign the conversion to a variable first, then pipe that.
#  2. A function returning an empty array hands back $null, which would read as
#     "could not read" rather than "none". Hence @( ... ) inline, no helper.
#  3. ConvertFrom-Json can emit a bare $null, hence the Where-Object filter.

# --- is gh usable at all? ------------------------------------------------
$ghOk = $false
try {
    $null = Get-Command gh -ErrorAction Stop
    $null = gh auth status 2>$null
    $ghOk = ($LASTEXITCODE -eq 0)
} catch {
    $ghOk = $false
}

if (-not $ghOk) {
    Add-Line 'GitHub: could not check (gh missing, not logged in, or offline).'
} else {
    # --- open issues, and who spoke last ---------------------------------
    try {
        $issuesJson = gh issue list --repo $Repo --state open --limit 30 --json number,title,updatedAt,author,comments |
                      ConvertFrom-Json
        $issues = @($issuesJson | Where-Object { $null -ne $_ })
    } catch { $issues = $null }

    if ($null -eq $issues) {
        Add-Line 'Open issues: could not read.'
    } elseif ($issues.Count -eq 0) {
        Add-Line 'Open issues: none.'
    } else {
        $waiting = 0
        Add-Line "Open issues ($($issues.Count)):"
        foreach ($i in $issues) {
            $last = $i.comments | Select-Object -Last 1
            $tail = ''
            if ($last) {
                $who = $last.author.login
                $tail = " - last reply by $who, " + (Get-Age $last.createdAt)
                # Anyone other than the maintainer speaking last means the ball is ours.
                if ($who -ne $Maintainer) { $waiting++; $tail += ' [needs a reply]' }
            }
            Add-Line ("  #" + $i.number + " " + $i.title + $tail)
        }
        $headline += "$($issues.Count) open issue" + (Get-Plural $issues.Count)
        if ($waiting -gt 0) { $headline += "$waiting awaiting your reply" }
    }

    # --- open pull requests ----------------------------------------------
    try {
        $prsJson = gh pr list --repo $Repo --state open --limit 30 --json number,title,updatedAt,isDraft |
                   ConvertFrom-Json
        $prs = @($prsJson | Where-Object { $null -ne $_ })
    } catch { $prs = $null }

    if ($null -eq $prs) {
        Add-Line 'Open PRs: could not read.'
    } elseif ($prs.Count -eq 0) {
        Add-Line 'Open PRs: none.'
    } else {
        Add-Line "Open PRs ($($prs.Count)):"
        foreach ($p in $prs) {
            $draft = if ($p.isDraft) { ' (draft)' } else { '' }
            Add-Line ("  #" + $p.number + " " + $p.title + $draft + " - updated " + (Get-Age $p.updatedAt))
        }
        $headline += "$($prs.Count) open PR" + (Get-Plural $prs.Count)
    }

    # --- discussions ------------------------------------------------------
    # Repo discussions are GraphQL-only; there is no REST endpoint for them.
    # The query goes via a temp file because PowerShell 5.1 mangles embedded
    # quotes when passing a multi-line string to a native executable.
    $queryFile = Join-Path ([System.IO.Path]::GetTempPath()) 'iwc-discussions.graphql'
    $query = @'
query($owner: String!, $name: String!) {
  repository(owner: $owner, name: $name) {
    discussions(first: 20, orderBy: {field: UPDATED_AT, direction: DESC}) {
      nodes {
        number
        title
        updatedAt
        category { name }
        comments { totalCount }
      }
    }
  }
}
'@
    try {
        # No BOM: Set-Content -Encoding utf8 writes one in PS 5.1 and gh rejects
        # it as an unknown character at the head of the query.
        [System.IO.File]::WriteAllText($queryFile, $query, (New-Object System.Text.UTF8Encoding($false)))
        $parts = $Repo.Split('/')
        $raw = gh api graphql -F ("owner=" + $parts[0]) -F ("name=" + $parts[1]) -F ("query=@" + $queryFile)
        $nodes = ($raw | ConvertFrom-Json).data.repository.discussions.nodes
        $discussions = @($nodes | Where-Object { $null -ne $_ })
    } catch { $discussions = $null }

    if ($null -eq $discussions) {
        Add-Line 'Discussions: could not read.'
    } elseif ($discussions.Count -eq 0) {
        Add-Line 'Discussions: none.'
    } else {
        Add-Line "Discussions ($($discussions.Count)):"
        foreach ($g in $discussions) {
            Add-Line ("  #" + $g.number + " [" + $g.category.name + "] " + $g.title +
                      " - " + $g.comments.totalCount + " comments, updated " + (Get-Age $g.updatedAt))
        }
        $headline += "$($discussions.Count) discussion" + (Get-Plural $discussions.Count)
    }
}

# --- the to-do list ------------------------------------------------------
Add-Line
if (Test-Path $WorkQueue) {
    Add-Line "To-do list: $WorkQueue"
    Add-Line 'Read it before planning work, and verify anything it claims about the code - it is a point-in-time note, not live state.'
} else {
    Add-Line "To-do list: expected at $WorkQueue but it is not there. Check MEMORY.md."
}

# --- output --------------------------------------------------------------
$body = ($lines -join "`n")

if ($Plain) {
    Write-Output $body
    exit 0
}

if ($headline.Count -eq 0) { $headline = @('GitHub not checked') }
$summary = 'IWC: ' + ($headline -join ', ')

$payload = [ordered]@{
    systemMessage      = $summary
    hookSpecificOutput = [ordered]@{
        hookEventName     = 'SessionStart'
        additionalContext = "Start-of-session status for Icom Web Control:`n`n$body"
    }
}
$payload | ConvertTo-Json -Depth 5 -Compress
exit 0
