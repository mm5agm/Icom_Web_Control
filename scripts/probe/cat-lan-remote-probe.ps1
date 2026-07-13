# Tests literal candidate strings (LAN/REMOTE variants) against a Yaesu radio over
# serial. Follow-up to cat-command-sweep.ps1, which ruled out every undocumented
# 2-letter mnemonic -- this checks the specific multi-char strings from the
# LAN-streaming-over-USB theory instead, in case the real syntax isn't 2-letter CAT.
#
# Stop Yaesu_Web_Control first -- this needs exclusive access to the port.
#
# Usage:
#   .\cat-lan-remote-probe.ps1

param(
    [string]$PortName = "COM4",
    [int]$BaudRate = 38400,
    [int]$PollMs = 50,
    [int]$IdlePolls = 6
)

$Candidates = @(
    "LAN1;ON;", "LAN1;ON", "LAN1;", "LAN0;", "LAN;ON;", "LAN;1;", "LAN;",
    "lan1;on;", "Lan1;ON;",
    "REMOTE;1;", "REMOTE1;", "REMOTE;ON;", "REMOTE;", "remote;1;",
    "NET;1;", "NET1;", "NET;ON;"
)

$Latin1 = [System.Text.Encoding]::GetEncoding(28591)

$port = New-Object System.IO.Ports.SerialPort $PortName, $BaudRate, ([System.IO.Ports.Parity]::None), 8, ([System.IO.Ports.StopBits]::Two)
$port.Encoding = $Latin1
$port.WriteTimeout = 500

try {
    $port.Open()
} catch {
    Write-Host "Could not open $PortName -- is Yaesu_Web_Control (or another app) holding the port? Stop it and retry." -ForegroundColor Red
    exit 1
}

function Send-Raw([string]$raw) {
    $port.DiscardInBuffer()
    $port.Write($raw)
    $sb = New-Object System.Text.StringBuilder
    $idle = 0
    while ($idle -lt $IdlePolls) {
        Start-Sleep -Milliseconds $PollMs
        if ($port.BytesToRead -gt 0) {
            [void]$sb.Append($port.ReadExisting())
            $idle = 0
        } else {
            $idle++
        }
    }
    return $sb.ToString()
}

function Show-AndRecoverIfNeeded {
    # After sending garbage, confirm the radio's CAT parser is still responsive
    # before moving on to the next candidate.
    $idReply = Send-Raw "ID;"
    if ($idReply -notmatch "ID0682") {
        Write-Host "  (radio didn't answer ID; cleanly afterwards -- reply was '$idReply')" -ForegroundColor Yellow
    }
}

Write-Host "Sanity check -- sent ID; got: '$(Send-Raw "ID;")'"
Write-Host ""

foreach ($c in $Candidates) {
    $reply = Send-Raw $c
    $bytes = $Latin1.GetBytes($reply)
    $hex = ($bytes | ForEach-Object { $_.ToString("X2") }) -join " "
    $ascii = ($reply -replace '[^\x20-\x7E]', '.')

    Write-Host "[$c]" -ForegroundColor Cyan
    Write-Host "  reply ($($bytes.Length) bytes): '$ascii'"
    if ($bytes.Length -gt 0) {
        Write-Host "  hex: $hex"
    }
    Show-AndRecoverIfNeeded
    Write-Host ""
}

$port.Close()
Write-Host "Done. If every reply above was empty (or just the ID; confirmations), none of these strings did anything."
