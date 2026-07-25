# Icom Web Control (IWC) — Clone-and-Split Plan

**Status:** design agreed, not yet started. Trigger to begin = I have the IC-7300 MkII in hand and say go.
**Target radio (v1):** Icom **IC-7300 MkII**, CI-V over USB Type-C (`FE FE` … `FD`, default radio address **`0xB6`**, controller `0xE0`).
**End goal:** full feature parity with YWC *wherever the Icom radio supports the feature* — including **voice control**, which is required (partially-sighted users: Yuri W4YSW, Thomas OZ1JTE, Bill W1WRH).

This document supersedes the older "clone whole YWC and cut Yaesu code" idea. (Radio target changed from IC-705 to IC-7300 MkII on 2026-07-19 — see the verified-facts section below; the plan is unchanged and if anything simpler.)

---

## Target radio — IC-7300 MkII, facts verified from the manuals (2026-07-19)

Manuals in `docs/manuals/`: `IC-7300MK2_ENG_CI-V_0.pdf`, `IC-7300MK2_ENG_Basic_2b.pdf`, `IC-7300MK2_ENG_Advanced_0.pdf`. USB driver guide is `IC-705_USB_driver_ENG_Inst_USB3_4.pdf` (Icom's shared USB driver — same one across current models).

- **CI-V address `B6`** (controller `E0`). Frame `FE FE B6 E0 Cn Sc <data> FD`; ack `FB`=OK / `FA`=NG. **Read the ID at connect via `19 00`, don't hard-code** — same auto-detect approach the doc already uses.
- **Single-receiver radio** — one receiver, VFO A/B. Maps directly onto YWC's existing FTdx10 / FT-710 single-receiver UI path (see `docs/decisions/0003-single-vs-dual-receiver-ui.md`). Simpler than the FTdx101's dual-receiver machinery.
- **Coverage:** RX 0.030–74.8 MHz; TX = HF ham bands **+ 6m (50–54)**, and **4m (70–70.5) on European versions only** — my UK/Region-1 unit likely has 4m (same bonus as the FTdx101MP). **No VHF/UHF** — this sidesteps the wider-range scaling pitfalls that bit the FTX-1 VHF/UHF memories (#71). Band control is HF+6m(+4m), nothing exotic.
- **Modes:** USB/LSB, CW, RTTY, AM, FM. No DV/D-STAR (that was a 705-only concern — now moot).
- **101 memory channels** (incl. 2 scan edges).
- **Scope over CI-V CONFIRMED — this is the key win, and the original IC-7300's big limitation is gone.** Command **`27 00`**, waveform **475 points**, values **0–160 (`00`–`A0`)**, modes Center/Fixed/**SCROLL-C**/**SCROLL-F**. Over **USB it arrives as 11 sequential segments** (1st = header only, 2nd–11th = min header + data); the decoder reassembles them. Control commands: `27 11` (wave output on/off), `27 14` (mode), `27 15` (span), `27 1E` (fixed edges), `27 19` (ref level). So IWC needs **no SDRplay, no worker exe, no FFT** — identical shape to the IC-705 plan.
- **Bonus the MkII adds over both the original IC-7300 and the 705's USB path: a rear [LAN] port.** Over LAN the scope is sent **all at once (490-length, no chunking)** and faster. USB is the simpler first target; LAN is a ready-made "make the waterfall faster later" upgrade with no new protocol work — just skip the reassembly step.

---

## Guiding decision

Don't clone-and-subtract, and don't start from an empty repo. Instead:

1. **Clone the whole YWC repo** (keeps git history for everything we retain).
2. **Carve it down in the first two commits** — rebrand, then a single deliberate "great deletion" commit whose diff *is* the record of what was Yaesu-specific.
3. **Build the CI-V layer additively** — one command block per commit, each independently testable, each lighting up UI + voice + rigctld together.

The layer worth reusing (SignalR envelope, gauges, spectrum rendering, settings, rigctld, Razor shell, voice recognition) is protocol-agnostic. The layer that differs completely (the CAT/CI-V wire protocol) gets built fresh — Yaesu CAT is ASCII/unaddressed/`;`-terminated; Icom CI-V is binary/addressed/BCD/`FE FE…FD`. Bending the Yaesu code to CI-V is slower and riskier than deleting it and building clean.

---

## The one design decision that makes voice + full parity affordable

YWC constructs radio commands as **raw Yaesu strings inline**, scattered across features — e.g. `IntentDispatcher` builds `FA{hz:D9};` itself; `CatController` does likewise. There is no semantic seam, so every feature knows the wire format.

**IWC introduces the seam YWC lacks: `IRadioController`.**

```
IRadioController  (semantic, protocol-free)
    SetFrequencyHz(vfo, hz) / GetFrequencyHz(vfo)
    SetMode(vfo, mode)      / SetPtt(on)
    ReadSMeter()            / ReadPowerSwr()
    SetSplit(...) / SelectBand(...) / GetScopeFrame() ...
        │
        └── CivRadioController : IRadioController   ← the ONLY class that emits CI-V
```

Everything above the seam — `IntentDispatcher` (voice), `CatController` (touch UI), `RigctldServer` (WSJT-X/Hamlib), meter polling — calls semantic methods and stops caring about the protocol. This converts the large parity surface from "rewrite" to "lift + repoint," and it is what lets each new CI-V command light up **touch, voice, and rigctld together**. Highest-leverage move in the project.

---

## File split (three categories — nothing is permanently dropped except one legacy subsystem)

### LIFT — copy wholesale, keep as-is or rename only

| Area | Files | Notes |
|---|---|---|
| SignalR transport | `RadioHub.cs`, `wwwroot/js/websocket/*` | `{property,value}` envelope + `WsUpdatePipeline` are 100% agnostic. Verbatim. |
| Gauges/meters (frontend) | `wwwroot/js/guages/*` | Pure rendering. Verbatim. |
| Spectrum (frontend) | `wwwroot/js/sdr/*` | `SpectrumPanel`/`SdrSpectrumPipeline` render bins regardless of source. Kept — the IC-7300 MkII scope (`27 00`) just becomes a new data source. |
| Settings/state plumbing | `SettingsService`, `ISettingsService`, `RadioStatePersistenceService`, `AppStatus`, `AppMemory`, `ProcessStatusCacheService`, `HttpPortInfo` | Trim Yaesu fields from `ApplicationSettings`. |
| Host/OS glue | `Program.cs` (rewire DI), `BrowserLauncher`, `SystemTrayService` | Keep skeleton, swap CAT registrations. |
| rigctld | `Services/RigctldServer.cs` | Hamlib protocol is radio-agnostic. Repoint to `IRadioController`. |
| REST surface | Controllers: `Sdr`, `Settings`, `SettingsBackup`, `Backup`, `DxCluster`, `Memory`, `MemoryBank`, `ExternalApps`, `Wsjtx`; adapt `Cat` | Repoint `CatController` at the seam. |
| DX / memories / misc | `DxClusterService`, `MemoryService`, `MemoryBankService`, `AdifParser`, `WsjtxUdpService`, `wwwroot/js/ui/{site,memories,dx-spots-panel,meter-formatters,a11y-labels}.js` | Verbatim or near. |
| Razor shell | `_Layout`, `_ViewImports`, `_ViewStart`, `Error`, `About`, `UserManual`, `Settings`, `Diagnostics`, `Ports`, `Memories` | Rebrand text only. |
| CSS | `wwwroot/css/*` | Verbatim. |
| **Voice stack** | `Services/Voice/*` (`VoiceControlService`, `IntentDispatcher`, `VoiceTtsService`, `VCTuneRecognizer`, `VoiceGrammar`, `VoicePhrase*`, `VoiceStatus`), `Controllers/VoiceController.cs`, `wwwroot/js/ui/voice-control.js` | SAPI recognition + TTS is protocol-agnostic. **Only change:** the ~6 command-building lines in `IntentDispatcher` call `IRadioController` instead of building `FA…;` strings. |
| Voice grammar | `Grammars/Commands.en-GB.srgs` | Ham terms/bands/modes are radio-agnostic; prune any Yaesu-only phrasing. |

### BUILD NEW — the CI-V protocol layer, behind `IRadioController`

| YWC file (delete) | IWC replacement (new) | What changes |
|---|---|---|
| `CatMessageBuffer.cs` | `CivFrameBuffer.cs` | Frame on `FE FE … FD`; strip the bus echo of our own TX. |
| `CatMessageDispatcher.cs` | `CivMessageDispatcher.cs` | Parse binary addressed frames; decode BCD, not ASCII Hz. |
| `CatCommands.cs` | `CivCommands.cs` | BCD encoders per command/sub-command. |
| `CatMultiplexerService.cs`, `MultiplexedCatClient.cs`, `ICatClient.cs` | `CivBusService.cs`, `ICivClient.cs` | Multiplexer concept is *more* relevant on CI-V (real shared bus + collisions); keep the shape, rewrite internals. |
| — (new) | **`IRadioController` + `CivRadioController`** | The semantic seam. Only class that emits CI-V. |
| `MeterPollingService.cs` | keep name, rewrite command set | 10 Hz `IHostedService` structure reusable; poll `1C`/`15` sub-commands. |
| `RadioInitializationService.cs` | keep name, rewrite handshake | CI-V transceive-mode setup. |
| `RadioCapabilities.cs` | rewrite for Icom models | IC-7300 MkII first (single-receiver, HF+6m+4m); per-model band/meter gating. |
| `wwwroot/js/orchestrators/FTdx101Meters.js` | `Ic705Meters.js` | New orchestrator wiring ws → calibration → MeterPanel. |
| `wwwroot/js/calibration/*` | new Icom calibration tables | Icom S-meter scaling differs. |
| — (new) | **`CivScopeDecoder`** | Parse `27 00` frames, reassemble the 11 USB chunks → 475 bins → existing `SpectrumPanel`. Replaces the whole SDRplay backend. wfview (GPL) is a study reference — implement fresh from the spec. |

### PORT-LATER — on the parity roadmap, not v1

- Calibration UI: `Pages/Calibration/*`, `Pages/MeterCalibration/*`, `CalibrationService`, `CalibrationStorage`, `Models/Calibration/*` — rebuild against Icom S-meter tables.
- Model-specific bits: `Ftdx3000Roofing.cs` stays behind; Icom per-model analogues written as reporters appear.
- Audit individually (some lift with small edits): `wwwroot/js/ui/{band-plan,if-width-tables,filter-scope-panel,freq-keyboard}.js` — band plan and IF-width tables carry Yaesu assumptions.

### DROP FOR GOOD

- **The entire SDRplay backend** — `Services/Sdr/*` (`SdrManager`, `SdrplayDevice`, `SoapySdrDevice`, `FftProcessor`, `FrameReader`, `WorkerProcess`, …), the `Workers/Yaesu_Sdr_Worker/*` project, the dual-process csproj wiring, `sdrplay_api.dll` P/Invoke, and `soapysdr-dist/*`. **This is the big simplification win:** the radio does its own FFT and hands over 475 calibrated bins over CI-V (`27 00`), so IWC needs no SDR hardware, no worker exe, no struct-offset grief. The spectrum *frontend* (`wwwroot/js/sdr/*`, above) stays and gets its bins from the CI-V scope decoder instead. *(An external-SDR panadapter could return later as an optional feature, but it is explicitly out of v1.)*
- **`Services/VcTune/*` (≈45 files)** + `Controllers/VcTuneController.cs` — the older, heavily-abstracted "VC-Tune" subsystem, superseded by the lean `Services/Voice/*` stack (nothing references it except `VCTuneRecognizer`/`Program.cs`). Do **not** carry it; rebuild any still-used capability minimally behind the seam. *(Confirm this is agreed-legacy before banking.)*

---

## Phased implementation plan

**Phase 0 — prep (do before the radio arrives)**
- Target confirmed: IC-7300 MkII, CI-V over USB Type-C, address `B6` (read via `19 00` at connect).
- Extract the needed CI-V command subset from the CI-V reference in `docs/manuals/` into a table (cmd, sub-cmd, BCD layout). This becomes the build checklist.

**Phase 1 — clone & carve (one sitting)**
- Clone → **rebrand commit** (`Yaesu_Web_Control`→`Icom_Web_Control`, worker rename, `%APPDATA%` path migration — reuse the YWC-rename migration pattern) → **great-deletion commit**.
- Get it to *compile against a stubbed `IRadioController`* returning canned values. App launches, page loads, gauges render fake data, voice stack loads. Proves all LIFTed plumbing survived the cut.

**Phase 2 — CI-V transport (first real command)**
- Build `CivFrameBuffer` + `CivBusService` + `ICivClient` + `CivRadioController.GetFrequencyHz` for **read frequency (`03`)** only, wired through the unchanged dispatcher → `RadioStateService` → SignalR path.
- Real VFO-A frequency on the web page = entire spine proven.
- **Accessibility win already here:** voice readback ("what's my frequency?") + TTS works the moment `GetFrequencyHz` is real. Put in front of Yuri/Thomas early.

**Phase 3 — additive command roadmap** (one block per commit; each lights up touch + voice + rigctld together)
1. ✅ Set frequency (`05`) — click-to-tune + keypad + "set frequency…" voice
2. ✅ Read/set mode (`04`/`06`, plus DATA modes via `1A 06` → DATA-U/L/FM; full IC-7300 mode set, dropdown trimmed)
3. ✅ S-meter (`15 02`, big-endian BCD) → existing gauge; poll loop restructured (freq=liveness every loop, S-meter every loop, mode every 3rd), 150 ms interval, SignalR push for smooth needle
4. PTT / TX status, power/SWR meters (`1C 00`, `15 11`/`15 12`)
5. Band/VFO select, split
6. Scope waveform stream (`27 00`, 475 bins, 11 USB segments reassembled) → existing `SpectrumPanel` — **no SDRplay needed**. (Later: switch the feed to the rear LAN port for a faster, single-segment 490-bin stream — no protocol rewrite, just skip reassembly.)

**Phase 4 — parity & polish**
- rigctld verification (WSJT-X), settings/diagnostics rebrand.
- Then work the PORT-LATER list: calibration UI against Icom tables, per-model capabilities.

---

## Why this order

After Phase 1 there's a running app with a hole where the protocol goes. Every phase after is "implement one CI-V method behind one semantic call, watch touch + voice + Hamlib come alive together." That is the gradual, always-tested build — without re-inventing the SignalR/gauge/spectrum/settings/voice scaffolding YWC already got right. "Full parity where the Icom supports it" reduces to one honest checklist against the CI-V reference.

---

## Phase 5 (future) — "Pseudo-dual-receiver" via single-RX time-slicing

**Depends on:** Phase 3 block 6 (scope). Uses primitives from blocks 1–3 (set freq `05`, set mode `06`/`1A 06`, read S-meter `15 02`) and block 6 (scope `27 00`).

**Idea (MM5AGM):** the IC-7300 MkII has one receiver, but if we flick the operating VFO between two frequencies/modes fast enough, the *display* can present as two receivers — a spectrum panel per "VFO", each with its own frequency/mode/S-meter. Clicking a panel commits to it as the single active receiver.

**The hard constraint that shapes the whole feature — one RX = one audio stream.** You cannot split the audio by time-slicing: alternating the audio A/B/A/B produces chopped, garbled speech/CW, not two simultaneous signals. The IC-7300 MkII has no dual-watch hardware (that is IC-7610-class). So the illusion is **visual only**; listening always commits to exactly one VFO.

**Chosen model — "primary + silent visual watch":**
- One VFO is **primary**: it holds the operating frequency/mode and gets **continuous audio**. Its panel is the normal live receiver.
- The other VFO is a **silent watch**: the supervisor spends spare CI-V bus time briefly retuning to it, grabbing its scope + S-meter (+ freq/mode), and returning to primary. Its panel answers "is there activity there / is the DX still up / is my other net busy" at a glance — no audio.
- **Clicking the watch panel promotes it to primary** (audio + full attention swap to it). This is the "click a spectrum → revert to single receiver" behaviour: it is really "choose which VFO you're actually listening to."

**Design decision still open — snapshot vs. rapid-alternate:**
- **Snapshot (recommended):** park on primary (audio rock-solid), peek at the watch VFO every ~1–2 s, come back. Protects the listening experience, which is the thing that actually matters. The watch panel updates slowly but usefully.
- **Rapid-alternate:** both panels near-live by switching continuously — but audio must still commit to one, and the constant retuning fights the primary's own scope sweep and audio continuity. More flicker, little real gain over snapshot given the audio limit.

**Reuses what already exists:** the dual-VFO spectrum scaffolding carried over from YWC — `SpectrumPanel` is instance-able per-VFO ("A"/"B"), the two card containers (`spectrumContainerA/B`), and the **Mono A / Mono B / Both** toggle (`localStorage.ywc.spectrumMode`). This feature repoints that shell from "two SDR devices" to "one radio, time-sliced," driven by a small supervisor above the `IRadioController` seam. No new spectrum frontend.

**Constraints / cautions:**
- **Scope transfer speed is the real bottleneck, not the switching.** Over USB CI-V a `27 00` frame is 475 bins in 11 segments ≈ ~0.3 s to transfer at 19200 baud, so two alternating scopes refresh at ~1 Hz each. Treat smooth "Both" mode as a **rear-LAN-port-era** feature (faster single-segment stream); ship a slower USB version first.
- **Prefer keeping both VFOs on the same band.** Frequency changes within a band are pure DSP — instant, silent, zero relay wear. Crossing bands clicks band-pass/tuner relays on every switch (mechanical wear + audible clunk). If both signals fall inside one **fixed-mode** scope span, don't switch the scope at all — one sweep shows both, with two cursors (elegant for two stations close together).
- **TX always commits to primary** — only one VFO can transmit; that's just existing split semantics.
- **Accessibility angle:** the silent watch could gain a voice/tone cue ("signal on VFO B") for partially-sighted ops, turning a visual-first feature into an audible watch too.
