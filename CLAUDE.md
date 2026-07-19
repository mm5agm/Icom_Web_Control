# CLAUDE.md

> ## ⭐ START HERE — read this first
>
> This is **Icom Web Control (IWC)**, a **pre-alpha** sibling of [Yaesu Web Control (YWC)](https://github.com/mm5agm/Yaesu_Web_Control). It was cloned from YWC and is being re-fitted for Icom CI-V radios. **Nothing here controls a radio yet.**
>
> **Before doing anything, read [`docs/design/iwc-clone-split-plan.md`](docs/design/iwc-clone-split-plan.md)** — the authoritative plan for how IWC is carved out of YWC (what's kept, what's rebuilt, the phased CI-V roadmap, and the `IRadioController` seam).
>
> **Key facts:**
> - **Target radio (v1):** Icom **IC-7300 MkII** — CI-V over USB Type-C, address `B6`, single-receiver HF+6m(+4m EU), scope over CI-V (`27 00`, 475 bins). Manuals are in `docs/manuals/` (local, git-ignored).
> - **Voice control is a required feature** (partially-sighted operators), not optional.
> - This is a **separate repo** from YWC — its `origin` points at `mm5agm/Icom_Web_Control`. Keep YWC concerns out of here and vice-versa.
>
> **⛔ Work is GATED:** do **not** start the rebrand/carve or any coding until Colin (MM5AGM) has the IC-7300 MkII in hand **and** explicitly says go. Until then this repo is scaffolding only. When he says go, the design doc's **Phase 0 → Phase 1** checklist is the starting point.
>
> **Everything below this banner is inherited verbatim from YWC.** It accurately describes the parts IWC keeps (SignalR pipeline, gauges, spectrum frontend, settings, rigctld, voice), but the Yaesu-specific CAT sections will be replaced during the carve — treat them as reference, not current IWC truth, until this file is rewritten in Phase 1.

---

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Architecture Rules

Before making any changes, read and follow all rules in `.claude/rules.md` and `.claude/project-overview.md`. These are non-negotiable and override any default behaviour.

---

## Build & Run

**Target:** .NET 10, x64 Windows only (`net10.0-windows`, `OutputType=WinExe`, `UseWindowsForms=true`).

```bash
# Build
dotnet build Yaesu_Web_Control.csproj

# Run (launches WinForms host + Kestrel on http://0.0.0.0:8080)
dotnet run --project Yaesu_Web_Control.csproj

# Publish self-contained
dotnet publish -c Release -r win-x64 --self-contained
```

There are no automated tests. Verification is manual via the browser at `http://localhost:8080`.

---

## Release Process

Before releasing: bump `Models/AppVersion.cs`, `installer.nsi`, add release notes to `README.md`, update `USER_MANUAL.md` if needed, and bump the per-release download badge in `README.md` to the new version (search for the previous `vX.Y.Z` tag in the shields.io URL near the top of the file).

```powershell
# 1. Commit everything on develop
git add -A
git commit -m "Release vX.Y.Z: ..."

# 2. Merge to main and tag
git checkout main
git merge develop --no-ff -m "Release vX.Y.Z"
git tag vX.Y.Z
git checkout develop

# 3. Push branches and tag
git push origin develop
git push origin main
git push origin vX.Y.Z

# 4. Create the GitHub Release — this triggers the build workflow
gh release create vX.Y.Z --title "vX.Y.Z" --notes "See README.md for full release notes."
```

**Step 4 is required.** The build workflow triggers on `release: [created]`, not on tag push alone.
Alternatively run `.\scripts\finish-release.ps1 -Version vX.Y.Z` which does all four steps.

User settings persist to `%APPDATA%\MM5AGM\Yaesu Web Control\appsettings.user.json`.  
Radio state persists to `%APPDATA%\MM5AGM\Yaesu Web Control\radio_state.json`.

---

## Backend Architecture

### Service Dependency Map

```
RadioInitializationService (IHostedService)
  └─ opens serial port via CatMultiplexerService
       └─ MultiplexedCatClient (ICatClient)
            └─ CatMessageDispatcher → RadioStateService → SignalR (RadioHub)
                                                        → RadioStatePersistenceService

MeterPollingService (IHostedService) — polls CAT FA/FB/SM etc. at ~10 Hz
SdrManager (IHostedService) — supervises one Yaesu_Sdr_Worker.exe per
                              configured SDR; reads FFT frames over
                              localhost TCP and broadcasts via SignalR
RigctldServer (IHostedService) — exposes rigctld TCP interface for WSJT-X etc.
```

### SignalR Message Envelope

All real-time updates use a single hub method `RadioStateUpdate` with envelope `{ property, value }`.  
The frontend's `WsUpdatePipeline` routes on `property`. The same hub carries:
- CAT state (FrequencyA, FrequencyB, PowerMeter, SMeter, etc.)
- SDR lifecycle (sdrId-tagged from v2.3.0):
  - `SdrStatus`     value = `{ sdrId, status }` — "unconfigured" / "connecting" / "streaming" / "disconnected" / "nodll"
  - `SdrError`      value = `{ sdrId, error  }` — human-readable detail
- SDR spectrum frames:
  - `SpectrumUpdate` value = `{ sdrId, bins, centreHz, spanHz }`

`sdrId` is `"A"` or `"B"`. `SdrSpectrumPipeline` routes by sdrId to the appropriate `SpectrumPanel` instance.

### CAT Frequency Format

`CatMessageDispatcher` parses `FA` / `FB` CAT responses. The FTdx101 sends frequencies as a plain integer string in **Hz** (e.g. `FA000880600;` = 880,600 Hz = 880.6 kHz). Values are stored and broadcast in Hz with no unit conversion. The FTdx101MP range is 30 kHz–75 MHz.

### Settings

`SettingsService` reads/writes `appsettings.user.json` via a read-modify-write pattern.  
`Settings.cshtml.cs`: `ModelState.Remove("Settings.SdrDeviceKeyA")` and `Settings.SdrDeviceKeyB` **must** appear before `ModelState.IsValid` — `<Nullable>enable</Nullable>` adds implicit `[Required]` to non-nullable strings, which silently blocks saves of empty values otherwise. The legacy `Settings.SdrDeviceKey` is also removed for the same reason; it's kept on the model as a migration anchor only.

**v2.2.x → v2.3.0 SDR settings migration:** the single `SdrDeviceKey` split into per-VFO `SdrDeviceKeyA` / `SdrDeviceKeyB`. `SettingsService.MigrateSdrDeviceKey` auto-promotes any legacy value into `SdrDeviceKeyA` on read; the legacy field is cleared on the next save.

### SDR Subsystem — Dual-process architecture (v2.3.0+)

The SDRplay API v3 service enforces one Selected device per host process (confirmed by [scripts/probe/](../scripts/probe/) — see [docs/decisions/0001-dual-sdr-architecture.md](../docs/decisions/0001-dual-sdr-architecture.md) for the four-pattern probe evidence). So:

- **YWC main never opens an SDR directly.**
- **`SdrManager`** (`Services/Sdr/SdrManager.cs`) spawns one `Yaesu_Sdr_Worker.exe` per configured device, connects to its localhost TCP port, reads FFT frames via `FrameReader`, and broadcasts them via SignalR with sdrId tagging.
- **`Yaesu_Sdr_Worker`** (`Workers/Yaesu_Sdr_Worker/`) — separate `.exe` per SDR. Each holds exactly one device. File-links the device code from `Services/Sdr/` so one source of truth.
- **`WorkerProcess`** — spawn/stop/locate the worker exe; picks free TCP port from 17001-17099; pipes worker stderr into the main Serilog stream.
- **Wire protocol** — length-prefixed binary, big-endian. Three message types: `SpectrumFrame` (sequence + centreHz + spanHz + bins[]), `StatusUpdate` (string), `ErrorReport` (string). See `Workers/Yaesu_Sdr_Worker/WireProtocol.cs` (writer) and `Services/Sdr/FrameReader.cs` (reader).
- **Build pipeline** — YWC's `.csproj` has a `<ProjectReference>` (no DLL link) so the worker builds first; `<None>` items with `CopyToOutputDirectory`/`CopyToPublishDirectory` land the worker exe alongside YWC's main exe. `installer.nsi` picks it up automatically via `File /r "publish\*"`.

Device code (still in `Services/Sdr/` but linked into both projects):
- `SdrplayDevice` — P/Invoke into `sdrplay_api.dll` (SDRplay API v3). Critical struct offsets verified against `C:\Program Files\SDRplay\API\inc\sdrplay_api_tuner.h`:
  - `tunerParams.bwType` @ offset 0
  - `tunerParams.gain.gRdB` @ offset 12 (`int`)
  - `tunerParams.gain.LNAstate` @ offset 16 (`unsigned char` — **not** int)
  - `tunerParams.rfFreq.rfHz` @ offset **40** (gain is 24 bytes; padding aligns double to 8-byte boundary)
  - `tunerParams.dcOffsetTuner.refreshRateTime` @ offset **64** (`sizeof(RfFreqT)`=16 due to 7-byte tail padding after syncUpdate uchar)
  - `ctrlParams.decimation.enable` @ offset **74**, `.decimationFactor` @ **75** (`sizeof(TunerParamsT)`=72)
  - `devParams.fsFreq.fsHz` @ offset 8 within DevParamsT
- `SoapySdrDevice` — SoapySDR wrapper for RTL-SDR, Airspy, etc.
- `FftProcessor` — Hann-windowed FFT → dBFS bins.

---

## Frontend Architecture

### Module Map (`wwwroot/js/`)

```
websocket/
  ws-connection.js        — SignalR transport only
  ws-update-pipeline.js   — routes { property, value } to registered handlers

calibration/
  calibration-engine.js   — pure functions, no DOM, no side effects
  calibration-tables.js   — single source of truth for all scaling tables
  FTdx101Calibration.js

ui/
  meter-panel.js          — owns all meter DOM and canvas rendering
  gaugeFactory.js         — ONLY place RadialGauge instances are created
  update-engine.js        — performs gauge updates
  meter-formatters.js     — ALL UI text formatting lives here
  overlays.js

orchestrators/
  FTdx101Meters.js        — wires websocket → calibration → MeterPanel; no logic of its own

sdr/
  sdr-spectrum-pipeline.js  — SignalR transport for spectrum; no DOM
  spectrum-panel.js         — owns spectrum canvas; DOM access intentional here
```

### Value Flow (strict — never bypass or reorder)

```
SignalR RadioStateUpdate
  → WsUpdatePipeline (route by property)
  → calibration-engine (pure transform)
  → FTdx101Meters (orchestrate)
  → MeterPanel.update()
  → gaugeFactory / update-engine
  → canvas
```

### Spectrum Display (dual-VFO, v2.3.0+)

`SdrSpectrumPipeline` creates its own SignalR connection. It maintains per-sdrId handler maps and dispatches `SpectrumUpdate` / `SdrStatus` / `SdrError` to whichever `SpectrumPanel` registered for that sdrId. Also handles `FrequencyA`, `FrequencyB`, `DxSpot`, `DxClusterStatus`, `DxAlert`.

`SpectrumPanel` is instance-able with a `vfo` parameter ("A" or "B") in the constructor. The vfo arg determines which `/api/cat/frequency/{a|b}` endpoint click-to-tune and wheel-tune use, and which `window.setMode('A'|'B', mode)` call follows a click. Each panel's frequency axis uses its own `_vfoHz` from the matching `FrequencyA`/`FrequencyB` SignalR updates.

Index page lays out two card panels (`spectrumContainerA`, `spectrumContainerB`) with a Mono A / Mono B / Both toggle persisted in `localStorage.ywc.spectrumMode`. Outer container hides when neither VFO has an SDR; toggle hides when only one is configured.

### Razor Pages

- `Index.cshtml` / `Index.cshtml.cs` — main control panel; `RadioState` property exposes `RadioStateService` for server-rendered initial values.
- `Settings.cshtml` / `Settings.cshtml.cs` — persists `ApplicationSettings`; note the `ModelState.Remove` order requirement above.
- `Diagnostics.cshtml` — SDR device scanning, port listing.

---

## Key Domain Facts

- **FTdx101MP frequency range:** 30 kHz – 75 MHz. Frequencies are always in Hz in this codebase.
- **IF output:** 9 MHz rear-panel IF fed to RSP1 antenna input for spectrum display.
- **SDR default sample rate:** 2,048,000 Hz (2 MHz span). Spectrum centred on `SdrIfFrequencyHz` (default 9 MHz); axis labels show RF frequencies derived from VFO-A.
- **S-meter raw values:** 0–255 → S0 to S9+60 dB via calibration tables.
- **Meter poll rate:** ~10 Hz via `MeterPollingService`.
- **SignalR heartbeat:** `SdrManager` re-broadcasts each worker's "streaming" status every 30 frames (~3 s) so clients that load after startup receive the current per-VFO status.
