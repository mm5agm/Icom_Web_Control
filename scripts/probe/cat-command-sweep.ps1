# Sweeps all undocumented 2-letter CAT mnemonics against a Yaesu radio over serial,
# looking for an undocumented scope/spectrum data command (see docs/manuals CAT PDFs
# for the documented set, extracted into $Documented below).
#
# Stop Yaesu_Web_Control first -- this needs exclusive access to the port.
#
# Usage:
#   .\cat-command-sweep.ps1
#   .\cat-command-sweep.ps1 -PortName COM4 -BaudRate 38400

param(
    [string]$PortName = "COM4",
    [int]$BaudRate = 38400,
    [int]$PollMs = 50,
    [int]$IdlePolls = 4,
    [int]$InterestingThreshold = 12,
    [string]$LogPath = "$PSScriptRoot\cat-sweep-results.csv"
)

# Every 2-letter mnemonic documented in docs/manuals/FTDX101MP_D_CAT_OM_ENG_2308-L.pdf
# (also covers FTDX10 / FT-710 / FTDX3000, which share most of this set).
$Documented = @(
    "AB","AC","AG","AI","AM","AN","AO","AV","BA","BC","BD","BI","BM","BP","BS","BU","BW","BY",
    "CH","CN","CO","CS","CT","CW","DA","DN","DT","ED","EM","EN","EU","EX","FA","FB","FM","FN",
    "FR","FS","FT","GT","HI","ID","IF","IN","IS","KM","KP","KR","KS","KY","LF","LK","LM","MA",
    "MB","MC","MD","MG","ML","MR","MS","MT","MW","MX","NA","NB","NL","NR","OI","ON","OS","PA",
    "PB","PC","PL","PR","PS","QI","QR","QS","RA","RC","RD","RF","RG","RI","RL","RM","RS","RT",
    "RU","RX","SC","SD","SF","SH","SM","SQ","SS","ST","SV","SW","SY","TX","UK","UL","UP","US",
    "VD","VG","VM","VR","VS","VT","VX","XT","ZI"
)

# Maps every byte 1:1 to a char and back, so binary replies (e.g. real scope/waveform
# data) survive round-tripping through the string-based serial API without corruption.
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

function Send-Command([string]$cmd) {
    $port.DiscardInBuffer()
    $port.Write("$cmd;")
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

# Sanity check: confirm the radio and port actually work before sweeping.
$idReply = Send-Command "ID"
Write-Host "Sanity check -- sent ID; got: '$idReply'"
if ($idReply -notmatch "ID0682") {
    Write-Host "Expected ID0682; (FTDX101MP) and didn't get it. Check port/baud/cabling before trusting sweep results." -ForegroundColor Yellow
}

$results = New-Object System.Collections.Generic.List[object]
$letters = 65..90 | ForEach-Object { [char]$_ }
$total = 0
$interesting = New-Object System.Collections.Generic.List[object]

foreach ($l1 in $letters) {
    foreach ($l2 in $letters) {
        $cmd = "$l1$l2"
        if ($Documented -contains $cmd) { continue }

        $total++
        $reply = Send-Command $cmd
        $bytes = $Latin1.GetBytes($reply)
        $hex = ($bytes | ForEach-Object { $_.ToString("X2") }) -join " "

        $row = [PSCustomObject]@{
            Command      = $cmd
            ReplyLength  = $bytes.Length
            ReplyHex     = $hex
            ReplyAscii   = ($reply -replace '[^\x20-\x7E]', '.')
        }
        $results.Add($row)

        if ($bytes.Length -gt $InterestingThreshold) {
            $interesting.Add($row)
            Write-Host "[$cmd] INTERESTING -- $($bytes.Length) bytes: $($row.ReplyAscii)" -ForegroundColor Cyan
        } elseif ($bytes.Length -gt 0) {
            Write-Host "[$cmd] replied ($($bytes.Length) bytes): $($row.ReplyAscii)"
        }
    }
}

$port.Close()

$results | Export-Csv -Path $LogPath -NoTypeInformation
Write-Host ""
Write-Host "Swept $total undocumented mnemonics. Full log: $LogPath"

if ($interesting.Count -eq 0) {
    Write-Host "No replies longer than $InterestingThreshold bytes -- no candidate scope/data command found." -ForegroundColor Yellow
} else {
    Write-Host "$($interesting.Count) command(s) worth a closer look:" -ForegroundColor Cyan
    $interesting | Format-Table Command, ReplyLength, ReplyAscii -AutoSize
    Write-Host ""
    Write-Host "Next step for each: re-run it a couple of times, and try it again after changing SS span/mode, to see if the reply changes accordingly (that's the signature of real scope data vs. a coincidental ack)."
}
