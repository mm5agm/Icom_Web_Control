#Requires -Version 5.1
<#
.SYNOPSIS
    Comprehensive API test suite for Icom Web Control.
    Tests all endpoints: Memory CRUD, CAT controls, validation errors.
    Detects whether radio is connected; skips radio-dependent tests if not.

.USAGE
    .\Tests\test-api.ps1
    .\Tests\test-api.ps1 -BaseUrl http://localhost:8080
    .\Tests\test-api.ps1 -SkipRadioTests
#>
param(
    [string]$BaseUrl = "http://localhost:8080",
    [switch]$SkipRadioTests
)

# Counters
$script:Passed  = 0
$script:Failed  = 0
$script:Skipped = 0

# Saved radio state (restored at end)
$script:OrigFreqA   = 0
$script:OrigFreqB   = 0
$script:OrigModeA   = ""
$script:OrigModeB   = ""
$script:RadioOnline = $false

# ---- Helpers -----------------------------------------------------------------

function Pass([string]$Name) {
    $script:Passed++
    Write-Host "  [PASS] $Name" -ForegroundColor Green
}

function Fail([string]$Name, [string]$Reason) {
    $script:Failed++
    Write-Host "  [FAIL] $Name -- $Reason" -ForegroundColor Red
}

function Skip([string]$Name, [string]$Reason) {
    $script:Skipped++
    Write-Host "  [SKIP] $Name -- $Reason" -ForegroundColor Yellow
}

function Invoke-Api {
    param(
        [string]$Method = "GET",
        [string]$Path,
        [object]$Body = $null,
        [int]$TimeoutSec = 30
    )
    $uri = "$BaseUrl$Path"
    try {
        $params = @{
            Method          = $Method
            Uri             = $uri
            UseBasicParsing = $true
            TimeoutSec      = $TimeoutSec
            ErrorAction     = "Stop"
        }
        if ($null -ne $Body) {
            $params.ContentType = "application/json"
            $params.Body = ($Body | ConvertTo-Json -Depth 5)
        }
        $resp = Invoke-WebRequest @params
        return @{ Ok = $true; Status = [int]$resp.StatusCode; Body = $resp.Content }
    } catch {
        $statusCode = 0
        if ($null -ne $_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $content = $reader.ReadToEnd()
                return @{ Ok = $false; Status = $statusCode; Body = $content }
            } catch {}
        }
        return @{ Ok = $false; Status = $statusCode; Body = $_.Exception.Message }
    }
}

function Test-Ok {
    param(
        [string]$Name,
        [string]$Method = "GET",
        [string]$Path,
        [object]$Body = $null,
        [int]$TimeoutSec = 30
    )
    $r = Invoke-Api -Method $Method -Path $Path -Body $Body -TimeoutSec $TimeoutSec
    if ($r.Ok) { Pass $Name } else { Fail $Name "HTTP $($r.Status): $($r.Body)" }
    return $r
}

function Test-Status {
    param(
        [string]$Name,
        [string]$Method = "GET",
        [string]$Path,
        [object]$Body = $null,
        [int]$ExpectedStatus
    )
    $r = Invoke-Api -Method $Method -Path $Path -Body $Body
    if ($r.Status -eq $ExpectedStatus) {
        Pass $Name
    } else {
        Fail $Name "Expected $ExpectedStatus, got $($r.Status): $($r.Body)"
    }
    return $r
}

function Get-Json([string]$Path) {
    $r = Invoke-Api -Path $Path
    if ($r.Ok) {
        try { return $r.Body | ConvertFrom-Json } catch { return $null }
    }
    return $null
}

function Section([string]$Title) {
    Write-Host ""
    Write-Host "-- $Title" -ForegroundColor Cyan
}

# ==============================================================================
#  0. CONNECTIVITY CHECK
# ==============================================================================
Section "Server connectivity"

$pingResp = Invoke-Api -Path "/api/cat/status/init"
if (-not $pingResp.Ok) {
    Write-Host ""
    Write-Host "ERROR: Cannot reach $BaseUrl -- is Icom Web Control running?" -ForegroundColor Red
    Write-Host "Start the application then re-run this script." -ForegroundColor Red
    exit 1
}
Pass "Server is reachable"

$statusJson = Get-Json "/api/cat/status"
if ($null -ne $statusJson -and $statusJson.vfoA.frequency -gt 100000) {
    $script:RadioOnline = $true
    $script:OrigFreqA   = $statusJson.vfoA.frequency
    $script:OrigFreqB   = $statusJson.vfoB.frequency
    $script:OrigModeA   = $statusJson.vfoA.mode
    $script:OrigModeB   = $statusJson.vfoB.mode
    Pass "Radio is online (VFO A = $($statusJson.vfoA.frequency) Hz)"
} else {
    Write-Host "  [INFO] Radio appears offline -- radio-dependent tests will be skipped." -ForegroundColor Yellow
}

if ($SkipRadioTests) {
    $script:RadioOnline = $false
    Write-Host "  [INFO] -SkipRadioTests flag set -- all radio CAT tests skipped." -ForegroundColor Yellow
}

# ==============================================================================
#  1. CAT STATUS ENDPOINTS (no radio write -- always run)
# ==============================================================================
Section "CAT status (read-only)"

Test-Ok "GET /api/cat/status"      -Path "/api/cat/status"
Test-Ok "GET /api/cat/status/init" -Path "/api/cat/status/init"
Test-Ok "GET /api/cat/radiopower"  -Path "/api/cat/radiopower"
Test-Ok "GET /api/cat/tx"          -Path "/api/cat/tx"

# ==============================================================================
#  2. MEMORY CRUD (no radio access -- always run)
# ==============================================================================
Section "Memory CRUD"

Test-Ok "GET /api/memory" -Path "/api/memory"

$newMem = @{
    label             = "TestCH"
    frequencyHz       = 14074000
    mode              = "USB"
    clarifierOffsetHz = 0
    rxClarOn          = $false
    txClarOn          = $false
}
$createResp = Invoke-Api -Method POST -Path "/api/memory" -Body $newMem
if ($createResp.Ok) {
    Pass "POST /api/memory (create)"
    $created = $createResp.Body | ConvertFrom-Json
    $newId   = $created.id

    $allMems = Get-Json "/api/memory"
    $found   = $allMems | Where-Object { $_.id -eq $newId }
    if ($null -ne $found) {
        Pass "Created memory visible in GET /api/memory"
    } else {
        Fail "Created memory visible in GET /api/memory" "id $newId not found"
    }

    $updMem = @{
        label             = "TestCH-Updated"
        frequencyHz       = 7074000
        mode              = "LSB"
        clarifierOffsetHz = 100
        rxClarOn          = $true
        txClarOn          = $false
    }
    $updResp = Invoke-Api -Method PUT -Path "/api/memory/$newId" -Body $updMem
    if ($updResp.Ok) {
        Pass "PUT /api/memory/$newId (update)"
        $updJson = $updResp.Body | ConvertFrom-Json
        if ($updJson.label -eq "TestCH-Updated") {
            Pass "Updated label matches"
        } else {
            Fail "Updated label matches" "got '$($updJson.label)'"
        }
        if ($updJson.frequencyHz -eq 7074000) {
            Pass "Updated frequency matches"
        } else {
            Fail "Updated frequency matches" "got $($updJson.frequencyHz)"
        }
    } else {
        Fail "PUT /api/memory/$newId (update)" "HTTP $($updResp.Status)"
    }

    $delResp = Invoke-Api -Method DELETE -Path "/api/memory/$newId"
    if ($delResp.Ok) {
        Pass "DELETE /api/memory/$newId"
        $afterDelete = Get-Json "/api/memory"
        $stillThere  = $afterDelete | Where-Object { $_.id -eq $newId }
        if ($null -eq $stillThere) {
            Pass "Deleted memory no longer returned by GET"
        } else {
            Fail "Deleted memory no longer returned by GET" "id $newId still present"
        }
    } else {
        Fail "DELETE /api/memory/$newId" "HTTP $($delResp.Status)"
    }

    Test-Status "DELETE non-existent memory returns 404" -Method DELETE -Path "/api/memory/999999" -ExpectedStatus 404

} else {
    Fail "POST /api/memory (create)" "HTTP $($createResp.Status): $($createResp.Body)"
}

Test-Status "PUT non-existent memory returns 404" -Method PUT -Path "/api/memory/999999" -Body $newMem -ExpectedStatus 404

# ==============================================================================
#  3. MEMORY BANKS (no radio access -- always run)
# ==============================================================================
Section "Memory banks"

# Seed one memory so the bank has something in it
$bankSeed = @{
    label             = "BankSeed"
    frequencyHz       = 21074000
    mode              = "USB"
    clarifierOffsetHz = 0
    rxClarOn          = $false
    txClarOn          = $false
}
$bankSeedResp = Invoke-Api -Method POST -Path "/api/memory" -Body $bankSeed
if ($bankSeedResp.Ok) {
    # GET /api/memorybank -- initial list (may or may not have entries from prior runs)
    $r = Invoke-Api -Path "/api/memorybank"
    if ($r.Ok) { Pass "GET /api/memorybank" } else { Fail "GET /api/memorybank" "HTTP $($r.Status)" }

    # Save current memories as "TestBank"
    $saveResp = Invoke-Api -Method POST -Path "/api/memorybank/save" -Body @{ name = "TestBank" }
    if ($saveResp.Ok) {
        Pass "POST /api/memorybank/save (create TestBank)"
        $bankNameReturned = ($saveResp.Body | ConvertFrom-Json).name
        if ($bankNameReturned -eq "TestBank") {
            Pass "Saved bank name matches"
        } else {
            Fail "Saved bank name matches" "got '$bankNameReturned'"
        }
    } else {
        Fail "POST /api/memorybank/save (create TestBank)" "HTTP $($saveResp.Status): $($saveResp.Body)"
    }

    # TestBank should now appear in the list
    $banks = Get-Json "/api/memorybank"
    if ($null -ne $banks -and $banks -contains "TestBank") {
        Pass "TestBank visible in GET /api/memorybank"
    } else {
        Fail "TestBank visible in GET /api/memorybank" "not found in: $($banks -join ', ')"
    }

    # Load TestBank -- replaces current memories with bank contents
    $loadResp = Invoke-Api -Method POST -Path ("/api/memorybank/" + [System.Uri]::EscapeDataString("TestBank") + "/load")
    if ($loadResp.Ok) {
        Pass "POST /api/memorybank/TestBank/load"
        # Loaded memories should contain the seeded entry
        $loadedMems = Get-Json "/api/memory"
        $foundSeed = $loadedMems | Where-Object { $_.frequencyHz -eq 21074000 -and $_.label -eq "BankSeed" }
        if ($null -ne $foundSeed) {
            Pass "Loaded bank contains seeded memory"
        } else {
            Fail "Loaded bank contains seeded memory" "21074000 Hz / BankSeed not found after load"
        }
    } else {
        Fail "POST /api/memorybank/TestBank/load" "HTTP $($loadResp.Status): $($loadResp.Body)"
    }

    # Delete TestBank
    $delBankResp = Invoke-Api -Method DELETE -Path ("/api/memorybank/" + [System.Uri]::EscapeDataString("TestBank"))
    if ($delBankResp.Ok) {
        Pass "DELETE /api/memorybank/TestBank"
        $banksAfter = Get-Json "/api/memorybank"
        if ($null -eq $banksAfter -or -not ($banksAfter -contains "TestBank")) {
            Pass "Deleted bank no longer in list"
        } else {
            Fail "Deleted bank no longer in list" "TestBank still present"
        }
    } else {
        Fail "DELETE /api/memorybank/TestBank" "HTTP $($delBankResp.Status)"
    }

    # Clean up seeded memory (load may have restored it with a new id)
    $allAfter = Get-Json "/api/memory"
    $toClean  = $allAfter | Where-Object { $_.label -eq "BankSeed" }
    foreach ($m in $toClean) { Invoke-Api -Method DELETE -Path "/api/memory/$($m.id)" | Out-Null }

} else {
    Fail "Setup seed memory for bank tests" "HTTP $($bankSeedResp.Status)"
}

# Validation: empty name returns 400
Test-Status "Save bank with empty name returns 400" `
    -Method POST -Path "/api/memorybank/save" -Body @{ name = "" } -ExpectedStatus 400

# Not found cases
Test-Status "Load non-existent bank returns 404" `
    -Method POST -Path "/api/memorybank/NoSuchBank99/load" -ExpectedStatus 404

Test-Status "Delete non-existent bank returns 404" `
    -Method DELETE -Path "/api/memorybank/NoSuchBank99" -ExpectedStatus 404

# ==============================================================================
#  5. MEMORY RECALL
# ==============================================================================
Section "Memory recall"

$mem20m = @{
    label             = "20m Test"
    frequencyHz       = 14074000
    mode              = "USB"
    clarifierOffsetHz = 0
    rxClarOn          = $false
    txClarOn          = $false
}
$c20 = Invoke-Api -Method POST -Path "/api/memory" -Body $mem20m
if ($c20.Ok) {
    $id20 = ($c20.Body | ConvertFrom-Json).id
    if ($script:RadioOnline) {
        $recallResp = Invoke-Api -Method POST -Path "/api/memory/$id20/recall"
        if ($recallResp.Ok) {
            Pass "POST /api/memory/$id20/recall"
        } else {
            Fail "POST /api/memory/$id20/recall" "HTTP $($recallResp.Status): $($recallResp.Body)"
        }
    } else {
        Skip "POST /api/memory/recall" "Radio offline"
    }
    Invoke-Api -Method DELETE -Path "/api/memory/$id20" | Out-Null
} else {
    Fail "Setup memory for recall test" "HTTP $($c20.Status)"
}

if ($script:RadioOnline) {
    Test-Status "Recall non-existent memory returns 404" -Method POST -Path "/api/memory/999999/recall" -ExpectedStatus 404
} else {
    Skip "Recall non-existent memory returns 404" "Radio offline"
}

# ==============================================================================
#  6. MEMORY EXPORT AND IMPORT
# ==============================================================================
Section "Memory export and import"

if ($script:RadioOnline) {
    $seed1 = @{ label = "Export-A"; frequencyHz = 14074000; mode = "USB"; clarifierOffsetHz = 0; rxClarOn = $false; txClarOn = $false }
    $seed2 = @{ label = "Export-B"; frequencyHz = 7074000;  mode = "LSB"; clarifierOffsetHz = 0; rxClarOn = $false; txClarOn = $false }
    $s1    = (Invoke-Api -Method POST -Path "/api/memory" -Body $seed1).Body | ConvertFrom-Json
    $s2    = (Invoke-Api -Method POST -Path "/api/memory" -Body $seed2).Body | ConvertFrom-Json

    $expResp = Invoke-Api -Method POST -Path "/api/memory/export-radio" -TimeoutSec 60
    if ($expResp.Ok) {
        $expJson = $expResp.Body | ConvertFrom-Json
        if ($expJson.written -ge 1) {
            Pass "POST /api/memory/export-radio (wrote $($expJson.written) channels)"
        } else {
            Fail "POST /api/memory/export-radio" "written=$($expJson.written)"
        }
    } else {
        Fail "POST /api/memory/export-radio" "HTTP $($expResp.Status): $($expResp.Body)"
    }

    $impResp = Invoke-Api -Method POST -Path "/api/memory/import-radio" -Body @{ mode = "replace" } -TimeoutSec 120
    if ($impResp.Ok) {
        $impJson = $impResp.Body | ConvertFrom-Json
        if ($impJson.imported -ge 1) {
            Pass "POST /api/memory/import-radio replace (imported $($impJson.imported) channels)"
        } elseif ($null -ne $impJson.warning) {
            Pass "POST /api/memory/import-radio replace (no channels on radio -- not an error)"
        } else {
            Fail "POST /api/memory/import-radio replace" "imported=$($impJson.imported) and no warning"
        }
    } else {
        Fail "POST /api/memory/import-radio replace" "HTTP $($impResp.Status): $($impResp.Body)"
    }

    $impMerge = Invoke-Api -Method POST -Path "/api/memory/import-radio" -Body @{ mode = "merge" } -TimeoutSec 120
    if ($impMerge.Ok) {
        Pass "POST /api/memory/import-radio merge"
    } else {
        Fail "POST /api/memory/import-radio merge" "HTTP $($impMerge.Status): $($impMerge.Body)"
    }

    Invoke-Api -Method DELETE -Path "/api/memory/$($s1.id)" | Out-Null
    Invoke-Api -Method DELETE -Path "/api/memory/$($s2.id)" | Out-Null

} else {
    Skip "POST /api/memory/export-radio"         "Radio offline"
    Skip "POST /api/memory/import-radio replace" "Radio offline"
    Skip "POST /api/memory/import-radio merge"   "Radio offline"
}

# ==============================================================================
#  7. CAT -- FREQUENCY
# ==============================================================================
Section "CAT -- Frequency"

if ($script:RadioOnline) {
    Test-Ok "POST /api/cat/frequency/a 14 MHz" -Method POST -Path "/api/cat/frequency/a" -Body @{ frequencyHz = 14074000 }
    Test-Ok "POST /api/cat/frequency/b 7 MHz"  -Method POST -Path "/api/cat/frequency/b" -Body @{ frequencyHz = 7074000 }
    Test-Status "Frequency too low returns 400"  -Method POST -Path "/api/cat/frequency/a" -Body @{ frequencyHz = 1000 }     -ExpectedStatus 400
    Test-Status "Frequency too high returns 400" -Method POST -Path "/api/cat/frequency/a" -Body @{ frequencyHz = 200000000 } -ExpectedStatus 400
} else {
    Skip "Frequency tests" "Radio offline"
}

# ==============================================================================
#  8. CAT -- BAND
# ==============================================================================
Section "CAT -- Band"

if ($script:RadioOnline) {
    Test-Ok "POST /api/cat/band/a 20m" -Method POST -Path "/api/cat/band/a" -Body @{ band = "20m" }
    Test-Ok "POST /api/cat/band/b 40m" -Method POST -Path "/api/cat/band/b" -Body @{ band = "40m" }
    Test-Status "Invalid band returns 400" -Method POST -Path "/api/cat/band/a" -Body @{ band = "99m" } -ExpectedStatus 400
} else {
    Skip "Band tests" "Radio offline"
}

# ==============================================================================
#  9. CAT -- MODE
# ==============================================================================
Section "CAT -- Mode"

if ($script:RadioOnline) {
    Test-Ok "POST /api/cat/mode/a USB" -Method POST -Path "/api/cat/mode/a" -Body @{ mode = "2" }
    Test-Ok "POST /api/cat/mode/b LSB" -Method POST -Path "/api/cat/mode/b" -Body @{ mode = "1" }
    Test-Status "Invalid receiver returns 400" -Method POST -Path "/api/cat/mode/z" -Body @{ mode = "2" } -ExpectedStatus 400
} else {
    Skip "Mode tests" "Radio offline"
}

# ==============================================================================
#  10. CAT -- RECEIVER DSP CONTROLS (AGC, IPO, NR, NB, Auto Notch, Att)
# ==============================================================================
Section "CAT -- Receiver DSP controls"

if ($script:RadioOnline) {
    Test-Ok "AGC A medium"             -Method POST -Path "/api/cat/agc/a"          -Body @{ code = "2" }
    Test-Ok "AGC B fast"               -Method POST -Path "/api/cat/agc/b"          -Body @{ code = "3" }
    Test-Status "AGC invalid code 400" -Method POST -Path "/api/cat/agc/a"          -Body @{ code = "9" } -ExpectedStatus 400

    Test-Ok "IPO A off"                -Method POST -Path "/api/cat/ipo/a"          -Body @{ code = "0" }
    Test-Ok "IPO B AMP1"               -Method POST -Path "/api/cat/ipo/b"          -Body @{ code = "1" }
    Test-Status "IPO invalid code 400" -Method POST -Path "/api/cat/ipo/a"          -Body @{ code = "5" } -ExpectedStatus 400

    Test-Ok "NR A on"                  -Method POST -Path "/api/cat/nr/a"           -Body @{ code = "1" }
    Test-Ok "NR A off"                 -Method POST -Path "/api/cat/nr/a"           -Body @{ code = "0" }
    Test-Status "NR invalid code 400"  -Method POST -Path "/api/cat/nr/a"           -Body @{ code = "9" } -ExpectedStatus 400

    Test-Ok "Noise blanker A on"              -Method POST -Path "/api/cat/noiseblanker/a" -Body @{ enabled = "1" }
    Test-Ok "Noise blanker A off"             -Method POST -Path "/api/cat/noiseblanker/a" -Body @{ enabled = "0" }
    Test-Status "Noise blanker invalid 400"   -Method POST -Path "/api/cat/noiseblanker/a" -Body @{ enabled = "2" } -ExpectedStatus 400

    Test-Ok "Auto notch A on"                 -Method POST -Path "/api/cat/autonotch/a"    -Body @{ code = "1" }
    Test-Ok "Auto notch A off"                -Method POST -Path "/api/cat/autonotch/a"    -Body @{ code = "0" }
    Test-Status "Auto notch invalid 400"      -Method POST -Path "/api/cat/autonotch/a"    -Body @{ code = "2" } -ExpectedStatus 400

    Test-Ok "Attenuator A 6 dB"               -Method POST -Path "/api/cat/attenuator/a"   -Body @{ code = "06" }
    Test-Ok "Attenuator A off"                -Method POST -Path "/api/cat/attenuator/a"   -Body @{ code = "00" }
    Test-Status "Attenuator invalid code 400" -Method POST -Path "/api/cat/attenuator/a"   -Body @{ code = "99" } -ExpectedStatus 400
} else {
    Skip "AGC/IPO/NR/NB/AutoNotch/Att tests" "Radio offline"
}

# ==============================================================================
#  11. IF WIDTH, FILTER SLOT, MANUAL NOTCH
# ==============================================================================
# IF Width lives on /api/radio/ifwidth (the IC-7300 filter-select command), not
# on /api/cat. There is no IF Shift endpoint -- the radio splits the passband
# edges into Twin PBT instead, covered in the Twin PBT section.
Section "IF Width, Filter Slot, Manual Notch"

if ($script:RadioOnline) {
    Test-Ok "IF Width A 2400 Hz"           -Method POST -Path "/api/radio/ifwidth/a"      -Body @{ hz = 2400 }
    Test-Ok "IF Width B 1800 Hz"           -Method POST -Path "/api/radio/ifwidth/b"      -Body @{ hz = 1800 }
    Test-Status "IF Width bad receiver 400" -Method POST -Path "/api/radio/ifwidth/z"     -Body @{ hz = 2400 } -ExpectedStatus 400

    Test-Ok "Filter slot A = FIL2"         -Method POST -Path "/api/radio/filter/a"       -Body @{ fil = 2 }
    Test-Ok "Filter slot A = FIL1"         -Method POST -Path "/api/radio/filter/a"       -Body @{ fil = 1 }
    Test-Status "Filter slot invalid 400"  -Method POST -Path "/api/radio/filter/a"       -Body @{ fil = 4 } -ExpectedStatus 400

    Test-Ok "Manual notch A on"            -Method POST -Path "/api/cat/manualnotch/a"    -Body @{ enabled = "1" }
    Test-Ok "Manual notch A off"           -Method POST -Path "/api/cat/manualnotch/a"    -Body @{ enabled = "0" }
    Test-Status "Manual notch invalid 400" -Method POST -Path "/api/cat/manualnotch/a"    -Body @{ enabled = "2" } -ExpectedStatus 400

    Test-Ok "Manual notch freq A 1000 Hz"        -Method POST -Path "/api/cat/manualnotchfreq/a" -Body @{ frequencyHz = 1000 }
    Test-Status "Manual notch freq out of range" -Method POST -Path "/api/cat/manualnotchfreq/a" -Body @{ frequencyHz = 5 } -ExpectedStatus 400
} else {
    Skip "IF Width/Shift/Manual Notch tests" "Radio offline"
}

# ==============================================================================
#  12. CAT -- AF GAIN, MIC GAIN, POWER
# ==============================================================================
Section "CAT -- Gain and power controls"

if ($script:RadioOnline) {
    Test-Ok "AF Gain A 150"              -Method POST -Path "/api/cat/afgain/a"   -Body 150
    Test-Ok "AF Gain B 150"              -Method POST -Path "/api/cat/afgain/b"   -Body 150
    Test-Status "AF Gain out of range 400" -Method POST -Path "/api/cat/afgain/a" -Body 300 -ExpectedStatus 400

    Test-Ok "AF Gain band=0 val=150"     -Method POST -Path "/api/cat/afgain"     -Body @{ band = "0"; value = "150" }
    Test-Status "AF Gain invalid band 400" -Method POST -Path "/api/cat/afgain"   -Body @{ band = "2"; value = "100" } -ExpectedStatus 400

    Test-Ok "Mic gain 50"                -Method POST -Path "/api/cat/micgain"    -Body @{ value = 50 }
    Test-Status "Mic gain out of range 400" -Method POST -Path "/api/cat/micgain" -Body @{ value = 200 } -ExpectedStatus 400

    Test-Ok "Power 100W"                 -Method POST -Path "/api/cat/power/a"    -Body @{ watts = 100 }
    Test-Status "Power out of range 400" -Method POST -Path "/api/cat/power/a"    -Body @{ watts = 1 }   -ExpectedStatus 400
} else {
    Skip "AF Gain/Mic Gain/Power tests" "Radio offline"
}

# ==============================================================================
#  13. CAT -- APF
# ==============================================================================
# No antenna section: the IC-7300 family has one SO-239 jack.
# No roofing-filter section: it is a direct-sampling SDR and has none.
# No contour section: contour is a Yaesu control with no IC-7300 equivalent.
Section "CAT -- APF"

if ($script:RadioOnline) {
    # Width, not a frequency offset: 0=OFF, 1=WIDE, 2=MID, 3=NAR (CI-V 16 32).
    Test-Ok "APF A wide"        -Method POST -Path "/api/cat/apf/a" -Body @{ width = 1 }
    Test-Ok "APF A mid"         -Method POST -Path "/api/cat/apf/a" -Body @{ width = 2 }
    Test-Ok "APF B narrow"      -Method POST -Path "/api/cat/apf/b" -Body @{ width = 3 }
    Test-Ok "APF A off"         -Method POST -Path "/api/cat/apf/a" -Body @{ width = 0 }
    Test-Status "APF width out of range 400" -Method POST -Path "/api/cat/apf/a" -Body @{ width = 4 } -ExpectedStatus 400
} else {
    Skip "APF tests" "Radio offline"
}

# ==============================================================================
#  16. CAT -- CLARIFIER
# ==============================================================================
Section "CAT -- Clarifier"

if ($script:RadioOnline) {
    Test-Ok "Clarifier RX on +500 Hz"  -Method POST -Path "/api/cat/clarifier"       -Body @{ vfo = "A"; rxOn = $true;  txOn = $false; offsetHz = 500 }
    Test-Ok "Clarifier reset"          -Method POST -Path "/api/cat/clarifier/reset"  -Body @{ vfo = "A"; rxOn = $true;  txOn = $false; offsetHz = 0 }
    Test-Ok "Clarifier nudge +100 Hz"  -Method POST -Path "/api/cat/clarifier/nudge"  -Body @{ vfo = "A"; deltaHz = 100 }
    Test-Status "Clarifier offset out of range 400" -Method POST -Path "/api/cat/clarifier" `
        -Body @{ vfo = "A"; rxOn = $false; txOn = $false; offsetHz = 99999 } -ExpectedStatus 400
    Test-Status "Clarifier nudge zero delta 400" -Method POST -Path "/api/cat/clarifier/nudge" `
        -Body @{ vfo = "A"; deltaHz = 0 } -ExpectedStatus 400
    Invoke-Api -Method POST -Path "/api/cat/clarifier" -Body @{ vfo = "A"; rxOn = $false; txOn = $false; offsetHz = 0 } | Out-Null
} else {
    Skip "Clarifier tests" "Radio offline"
}

# ==============================================================================
#  17. CAT -- SPLIT AND VFO SWAP
# ==============================================================================
Section "CAT -- Split and VFO swap"

if ($script:RadioOnline) {
    Test-Ok "Split off"         -Method POST -Path "/api/cat/split/0"
    Test-Ok "Split on"          -Method POST -Path "/api/cat/split/1"
    Test-Ok "Split off restore" -Method POST -Path "/api/cat/split/0"
    Test-Status "Split invalid mode 400" -Method POST -Path "/api/cat/split/9" -ExpectedStatus 400
    Test-Ok "Swap VFO"          -Method POST -Path "/api/cat/swap-vfo"
    Test-Ok "Swap VFO back"     -Method POST -Path "/api/cat/swap-vfo"
} else {
    Skip "Split/VFO swap tests" "Radio offline"
}

# ==============================================================================
#  18. CAT -- REINITIALIZE
# ==============================================================================
Section "CAT -- Reinitialize"

if ($script:RadioOnline) {
    Test-Ok "POST /api/cat/reinitialize" -Method POST -Path "/api/cat/reinitialize" -TimeoutSec 30
} else {
    Skip "POST /api/cat/reinitialize" "Radio offline"
}

# ==============================================================================
#  RESTORE ORIGINAL RADIO STATE
# ==============================================================================
if ($script:RadioOnline) {
    Section "Restoring original radio state"

    $modeMap = @{
        "LSB"       = "1"; "USB"     = "2"; "CW-U"     = "3"; "FM"    = "4"
        "AM"        = "5"; "RTTY-L"  = "6"; "CW-L"     = "7"; "DATA-L" = "8"
        "RTTY-U"    = "9"; "DATA-FM" = "A"; "FM-N"     = "B"; "DATA-U" = "C"
        "AM-N"      = "D"; "PSK"     = "E"; "DATA-FM-N" = "F"
    }

    $r1 = Invoke-Api -Method POST -Path "/api/cat/frequency/a" -Body @{ frequencyHz = $script:OrigFreqA }
    $r2 = Invoke-Api -Method POST -Path "/api/cat/frequency/b" -Body @{ frequencyHz = $script:OrigFreqB }

    $modeCodeA = $modeMap[$script:OrigModeA]
    if ($null -ne $modeCodeA) {
        Invoke-Api -Method POST -Path "/api/cat/mode/a" -Body @{ mode = $modeCodeA } | Out-Null
    }
    $modeCodeB = $modeMap[$script:OrigModeB]
    if ($null -ne $modeCodeB) {
        Invoke-Api -Method POST -Path "/api/cat/mode/b" -Body @{ mode = $modeCodeB } | Out-Null
    }

    if ($r1.Ok -and $r2.Ok) {
        Pass "VFO A/B frequencies restored"
    } else {
        Fail "VFO frequencies restore" "A: HTTP $($r1.Status)  B: HTTP $($r2.Status)"
    }
}

# ==============================================================================
#  SUMMARY
# ==============================================================================
$total = $script:Passed + $script:Failed + $script:Skipped
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Results: $total tests"                   -ForegroundColor Cyan
Write-Host "  Passed:  $($script:Passed)"              -ForegroundColor Green
if ($script:Failed -gt 0) {
    Write-Host "  Failed:  $($script:Failed)"          -ForegroundColor Red
} else {
    Write-Host "  Failed:  $($script:Failed)"          -ForegroundColor Green
}
Write-Host "  Skipped: $($script:Skipped)"             -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Cyan

if ($script:Failed -gt 0) { exit 1 } else { exit 0 }
