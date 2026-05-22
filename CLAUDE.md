# CLAUDE.md

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

User settings persist to `%APPDATA%\MM5AGM\FTdx101 WebApp\appsettings.user.json`.  
Radio state persists to `%APPDATA%\MM5AGM\FTdx101 WebApp\radio_state.json`.

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
SdrBackgroundService (IHostedService) — SDR device lifecycle + FFT → SignalR
RigctldServer (IHostedService) — exposes rigctld TCP interface for WSJT-X etc.
```

### SignalR Message Envelope

All real-time updates use a single hub method `RadioStateUpdate` with envelope `{ property, value }`.  
The frontend's `WsUpdatePipeline` routes on `property`. The same hub carries:
- CAT state (FrequencyA, FrequencyB, PowerMeter, SMeter, etc.)
- SDR lifecycle (SdrStatus, SdrError)
- SDR spectrum frames (SpectrumUpdate `{ bins, centreHz, spanHz }`)

### CAT Frequency Format

`CatMessageDispatcher` parses `FA` / `FB` CAT responses. The FTdx101 sends frequencies as a plain integer string in **Hz** (e.g. `FA000880600;` = 880,600 Hz = 880.6 kHz). Values are stored and broadcast in Hz with no unit conversion. The FTdx101MP range is 30 kHz–75 MHz.

### Settings

`SettingsService` reads/writes `appsettings.user.json` via a read-modify-write pattern.  
`Settings.cshtml.cs`: `ModelState.Remove("Settings.SdrDeviceKey")` **must** appear before `ModelState.IsValid` — `<Nullable>enable</Nullable>` adds implicit `[Required]` to non-nullable strings, which silently blocks saves of empty `SdrDeviceKey` otherwise.

### SDR Subsystem (`Services/Sdr/`)

- `SdrBackgroundService` — lifecycle loop: open → configure → stream → FFT → broadcast. Retries every 5 s on failure; heartbeats "streaming" status every ~3 s so late-connecting clients receive it.
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

### Spectrum Display

`SdrSpectrumPipeline` creates its own SignalR connection. It registers handlers for `SpectrumUpdate`, `SdrStatus`, `SdrError`, `FrequencyA`, `FrequencyB`.

`SpectrumPanel` frequency axis uses `_vfoHz` (RF frequency in Hz from `FrequencyA` SignalR updates) to label the x-axis with actual RF frequencies. The initial value is server-rendered via `@Model.RadioState.FrequencyA`. If that value is below 100,000 Hz (indicating a stale/corrupt persisted state), the axis shows a "Waiting for VFO frequency…" placeholder until a live update arrives.

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
- **SignalR heartbeat:** `SdrBackgroundService` re-broadcasts "streaming" every 30 frames (~3 s) so clients that load after startup receive current status.
