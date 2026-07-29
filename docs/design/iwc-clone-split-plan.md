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
4. ✅ PTT / TX status, power/SWR meters (`1C 00`, `15 11`/`15 12`) — software PTT (web + API + voice), Po/SWR gauges rise on TX / zero on RX; hardware-verified into dummy load
5. ✅ Band/VFO select, split — VFO-select (`07 00`/`07 01`), exchange (`07 B0`), equalize (`07 A0`), split read/set (`0F`), plus **true per-VFO readout** via `25` (selected/unselected freq) and `26` (selected/unselected mode+DATA+filter in one frame). Note: `25`/`26` address *selected/unselected*, not A/B — track `ActiveVfo` from our own `07` sends (front-panel A/B presses are a known desync). (MM5AGM chose full per-VFO scope over active-VFO-only.)
6. Scope waveform stream (`27 00`, 475 bins, 11 USB segments reassembled) → existing `SpectrumPanel` — **no SDRplay needed**. (Later: switch the feed to the rear LAN port for a faster, single-segment 490-bin stream — no protocol rewrite, just skip reassembly.)
7. **Radio power on/off (`18`)** — MM5AGM request. `18 00` = OFF (plain frame); `18 01` = ON but the CI-V circuit is asleep, so it must be prefixed with a baud-dependent burst of wake-up `FE` bytes (**19200 → 25 × `FE`**, 9600 → 13, 4800 → 7) before `FE FE B6 E0 18 01 FD`. **Gotcha:** remote power-on only works if USB stayed enumerated while "off" (soft shutdown via `18 00`/standby) — a front-panel power-off that drops USB removes the COM port, leaving nothing to send `18 01` to. Related menu `01 09` "Power OFF Setting (for Remote Control)". Seam: one `SetPowerAsync(bool)` method + a UI power button (accessibility/remote-op win). Small block; independent of the others.

**RF output power set (`14 0A`)** — separate from the read-back Po *meter* (`15 11`). The web power slider sets the radio's 0–100 % RF level via `14 0A` (two big-endian BCD bytes, 0000–0255; watts map 1:1 to percent on the 100 W IC-7300). Replaces the inherited Yaesu `PCnnn;` ASCII command, which is inert on CI-V. Wired through the seam as `Set/GetRfPowerPercentAsync`; hardware-verified 2026-07-26 (set 25/50/75 W, radio read the same value back).

**RX-control panel over CI-V** ✅ (commit `17339dc`, 2026-07-26) — rewired the home-page DSP controls off the inert Yaesu multiplexer onto the seam and rebuilt them to mirror the radio's own UI. Dropdowns → inline **segmented button groups** (AGC/Preamp/ATT/NR/NB). **Notch merged** into one OFF/AN/MN control (auto `16 41` + manual `16 48`, kept mutually exclusive). Added **MN Width** WIDE/MID/NAR (`16 57`) and **IF Shape** SHARP/SOFT (`16 56`), both audible-verified. **NR shown 0–15, NB shown 0–100 %** to match the radio's readouts (CI-V still 0–255, scaled at the UI edge only). Rotating RX-control poll (14 controls, 2/loop) reflects front-panel changes back to the app. **CI-V gaps confirmed absent** (front-panel only): NB **Depth** & **Width**, and the **"1/4" fine-tune** toggle — none exist in the 14-family, 16-family, or the full `1A 05` SET-menu list.

> **Finding — no PA temperature over CI-V** (2026-07-26, MM5AGM spotted the front-panel bar; verified against `docs/manuals/IC-7300MK2_ENG_CI-V_0.pdf`): the IC-7300 MkII front panel shows a **COOL→HOT** temperature bar graph (blue→red), **but it is display-only — there is no CI-V command to read it.** The `15`-family meter set is exactly S-meter `02`, Po `11`, SWR `12`, ALC `13`, COMP `14`, Vd `15`, Id `16`; the words "temperature", "thermal", "heat", "fan", "cool" appear **nowhere** in the CI-V reference. So the inherited YWC temperature gauge was correctly dropped for this radio, and a *live* temp gauge is **not achievable** over CI-V. Do not re-open without new evidence (e.g. an undocumented command surfaced by probing the real radio).

**Phase 4 — parity & polish**
- rigctld verification (WSJT-X), settings/diagnostics rebrand.
- Then work the PORT-LATER list: calibration UI against Icom tables, per-model capabilities.
- **Control layout / whitespace pass (MM5AGM request, 2026-07-26).** The home page carries a lot of dead whitespace that could be reclaimed by rearranging controls, and several controls that belong together aren't grouped. Rework the layout so related controls sit together and the screen is used efficiently. Specifics called out:
  - **Group related controls** — e.g. **NR and NB** together (both noise controls); likewise review AGC/Preamp/ATT and the notch cluster for sensible grouping.
  - **Move Bands left**, and **relocate "Save to Mem"** to free up the space the band buttons need.
  - Reclaim the general whitespace across the panel rather than leaving controls sparse.
  - Constraints: keep it **accessibility-first** (this is a partially-sighted-operator app) — don't shrink hit targets or crowd controls to the point they're hard to find; keep the segmented-button and slider affordances legible. Fold in the themeable-UI goal (see the UI theme memory) rather than fighting it. Pure layout/CSS/Razor work — no CI-V changes.

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

**Watch cadence — ship both as a user-selectable mode (MM5AGM):** rather than pick one, offer both behind a **Settings toggle** and let operators choose what feels best on their setup. Build **snapshot first** (it's the safe baseline that works on every transport), then add rapid-alternate.
- **Snapshot (default):** park on primary (audio rock-solid), peek at the watch VFO every ~1–2 s, come back. Protects the listening experience, which is the thing that actually matters. The watch panel updates slowly but usefully. Works over **USB or LAN**.
- **Rapid-alternate:** both panels near-live by switching continuously — but audio must still commit to one, and the constant retuning fights the primary's own scope sweep and audio continuity. Only comfortable when scope frames are fast, i.e. over the **rear LAN port**; on USB (~1 Hz/panel) it just flickers with little gain. So it's effectively a **LAN-only** option.
- **Setting:** `WatchMode = Snapshot | RapidAlternate` in `ApplicationSettings`, exposed on the Settings page (mind the `ModelState.Remove` string gotcha). Default **Snapshot**. Since rapid-alternate needs LAN to be worth it, the UI can hint/grey it when the active transport is USB rather than hard-blocking it — let the user try and judge.

**Reuses what already exists:** the dual-VFO spectrum scaffolding carried over from YWC — `SpectrumPanel` is instance-able per-VFO ("A"/"B"), the two card containers (`spectrumContainerA/B`), and the **Mono A / Mono B / Both** toggle (`localStorage.ywc.spectrumMode`). This feature repoints that shell from "two SDR devices" to "one radio, time-sliced," driven by a small supervisor above the `IRadioController` seam. No new spectrum frontend.

**Constraints / cautions:**
- **Scope transfer speed is the real bottleneck, not the switching.** Over USB CI-V a `27 00` frame is 475 bins in 11 segments ≈ ~0.3 s to transfer at 19200 baud, so two alternating scopes refresh at ~1 Hz each. Treat smooth "Both" mode as a **rear-LAN-port-era** feature (faster single-segment stream); ship a slower USB version first.
- **Prefer keeping both VFOs on the same band.** Frequency changes within a band are pure DSP — instant, silent, zero relay wear. Crossing bands clicks band-pass/tuner relays on every switch (mechanical wear + audible clunk). If both signals fall inside one **fixed-mode** scope span, don't switch the scope at all — one sweep shows both, with two cursors (elegant for two stations close together).
- **TX always commits to primary** — only one VFO can transmit; that's just existing split semantics.
- **Accessibility angle:** the silent watch could gain a voice/tone cue ("signal on VFO B") for partially-sighted ops, turning a visual-first feature into an audible watch too.

---

## Phase 6 (future) — CW decode on screen (MM5AGM request)

**Idea:** decode received CW and show the text live in the app, toggled on/off by an on-screen button.

**The source question — this is NOT a simple CAT read.** The IC-7300 / MkII decodes CW *internally* for its own front-panel display, but **does not expose the decoded ASCII text over CI-V.** There is no "read decoded CW" command. So IWC has to decode the audio itself:
- **Where the audio is:** the radio presents USB audio (the same codec WSJT-X/fldigi use). A CW decoder needs that receive-audio stream.
- **Decode options:** (a) do it in the browser off the operator's local audio via Web Audio API + a Goertzel/FFT tone detector + Morse timing state machine — keeps it client-side, no server audio plumbing; or (b) decode server-side from the radio's USB audio device and push text over SignalR (`CwDecode` envelope) like any other state update. (b) matches the existing architecture (server owns the radio, frontend renders pushed state) and works for remote operators who don't have the audio locally.
- **Accessibility tie-in:** decoded CW could also feed the existing `window.voiceAnnounce` hooks (the voice-announcement system inline in `Index.cshtml`) to read the text aloud — directly useful for partially-sighted CW ops.

**UI:** an on-screen toggle button (per MM5AGM) shows/hides a CW-text panel; off by default. Independent of the pseudo-dual-receiver work — can land any time after the audio path is decided.

**Open decisions before building:** client-side vs server-side decode (leaning server-side for remote parity); which audio device/stream; whether to gate the panel behind a Settings toggle as well as the on-screen button.

### 6a — Software ZIN / auto-zero-beat (rides the CW audio path)

**What it is:** the Icom equivalent of Yaesu's **ZIN** is the front-panel **AUTOTUNE** function — it nudges the VFO so a received CW signal lands exactly on the operator's set CW pitch (zero-beat). Useful for netting onto a station before answering; a natural companion to CW decode.

**Why it can't be a CAT passthrough:** there is **no CI-V command that triggers AUTOTUNE**. The only related opcode, `1A 05 00 58`, merely *assigns* the AUTOTUNE function to the multifunction button — it does not fire the action. So IWC cannot ask the radio to zero-beat; it must do the zero-beat itself.

**How IWC does it (software ZIN):** this is a small extension of the CW-decode tone detector, so it reuses the same receive-audio path (option (b) above):
1. Detect the dominant CW tone in the passband → its audio frequency `f_tone` (Hz). The Goertzel/FFT front-end of the decoder already produces this.
2. Read the operator's target CW pitch `f_pitch` (the sidetone/BFO offset the radio zero-beats to).
3. Compute the error `Δ = f_tone − f_pitch` and QSY: `SetFrequencyAsync(currentHz ± Δ)` through the existing `IRadioController` seam. **Sign depends on sideband** — CW-U (USB CW) and CW-L (LSB CW) move the VFO in opposite directions for the same audio-tone error; resolve from the current CW mode.

**UI:** a "ZIN" (or "Zero-beat") on-screen button next to the CW-decode toggle; one-shot action (measure → single QSY), not a continuous servo. Works for remote operators too — which the radio's own front-panel AUTOTUNE never could.

**⚠️ Open item before building:** confirm the **exact CI-V read for the CW pitch** so the target tone `f_pitch` is precise rather than assumed (the IC-7300 MkII CW pitch is a SET-menu value in the `1A 05 nn nn` family — nail down the sub-address and its Hz encoding from `docs/manuals/IC-7300MK2_ENG_CI-V_0.pdf` at implementation time). Until confirmed, ZIN could fall back to a user-entered pitch, but reading it from the radio is the correct source of truth.

---

## Phase 7 (future) — Skins: switchable full-layout screens (MM5AGM request, 2026-07-29)

**Idea:** let the operator pick between several complete **skins**, where a skin is not just a colour theme but a whole screen — its own **layout** (which panels sit where, and their sizes), its own **control set** (which controls are shown or hidden), *and* its own **colours/typography**, bundled together as one selectable look. **One selector, not a skin × theme matrix** (MM5AGM decision 2026-07-29: "full bundle… simpler mental model, less flexible" — chosen deliberately over keeping layout and colour as independent axes).

**Relationship to the theme-system goal (reconciled — read this before building):** the earlier frontend plan (see the UI-theme memory) framed theming as a *separate* colour/font axis driven by CSS custom properties, shipping a list of themes (**IC-7300 / Classic Yaesu-style / Dark / Light**) chosen independently of layout. That token infrastructure is still the **implementation foundation** — nothing hardcodes a colour, everything reads a CSS custom property — but the *user-facing* unit is now the **skin**, which packages { token set + layout + control-visibility } together. The old theme list **folds into** skins: e.g. the "IC-7300" theme becomes the look-half of the Front-Panel-Replica skin; "Light" is the default skin's palette. Users pick a skin, not a theme-then-layout pair. This supersedes the "theme switcher in header + Settings" framing only at the UI level — the persistence mechanism and token approach carry over unchanged.

**Planned skins, in MM5AGM's priority order:**
1. **Front-Panel Replica** *(first deliverable)* — arranged to resemble the physical IC-7300 face: scope-dominant, bold blocky frequency readout, controls placed where the radio has them, black/blue Icom palette. The "feels like my radio" skin.
2. **Large-print** *(second)* — big frequency readout, large hit-targets, high contrast, minimal clutter, essentials only. Directly serves IWC's core **partially-sighted-operator + voice-control** goal. Must honour the accessibility-first layout constraints already called out in the Phase 4 layout pass (don't shrink hit targets, keep segmented-button/slider affordances legible).
3. **Others to follow** — not fixed; candidates include Minimalist/clean (casual listening: freq + mode + S-meter + one spectrum) and Contest/DX operating (surfaces split, the Phase-5 dual-VFO watch, memories, DX spots; de-emphasises rarely-touched DSP). Add as wanted.

**Architecture sketch (decide specifics at build time):**
- A **skin descriptor** selects three things: (a) a **layout** — the panel arrangement, realistically a CSS-grid `grid-template` and/or a per-skin Razor partial for the home page; (b) a **token set** — the existing CSS-custom-property values; (c) a **control manifest** — which control groups render vs collapse. The current single hardcoded `Index.cshtml` arrangement becomes the *default* skin.
- **Selector** in the header + Settings; persisted to `localStorage` for instant no-reload apply and mirrored to `appsettings.user.json` so it survives restart — the same pattern the theme switcher was already going to use.
- Keep all the live plumbing **skin-agnostic**: the SignalR value flow, gauges, spectrum panels, and voice hooks must not fork per skin — a skin only *re-arranges and re-styles existing components*, never duplicates the data path. This is the constraint that keeps N skins maintainable.

**Effort note:** the token/colour half is incremental; the **layout** half is the real work. The home page is currently one fixed arrangement, so making layout swappable (a grid template each skin sets, or a partial-per-skin) is the foundational task the *first* skin pays for — later skins are then cheap. **Fold this together with the Phase 4 "control layout / whitespace pass"** rather than doing two separate layout reworks. Pure frontend/CSS/Razor work — no CI-V changes.

**Sequencing:** capture-only for now (MM5AGM 2026-07-29, "decide later"). Lands after the Phase 5 cross-band peek, on top of the CSS-token theming foundation; Front-Panel-Replica is deliverable #1, Large-print #2.

**Open decisions before building:** layout mechanism (one responsive grid whose template each skin sets, vs a Razor partial per skin); whether control-hiding is per-skin only or also allows a user override on top; how a skin interacts with the existing per-VFO waterfall/spectrum `localStorage` prefs; whether skins stay a fixed built-in set or become user-authored/JSON-defined later.

## Phase 8 (future) — Radio Settings Backup / Restore over CI-V (MM5AGM request, 2026-07-29)

**Idea:** let the operator **save the radio's user settings to a file and restore them later**, entirely over CI-V — no SD card. This is a direct answer to a limitation MM5AGM hit on Yaesu Web Control, where the only way to back up radio settings was the physical SD-card save/restore; the Yaesu CAT set gave no read access to menu items. **The IC-7300 MkII is different** — verified against `docs/manuals/IC-7300MK2_ENG_CI-V_0.pdf`, nearly every user-set parameter is *both readable and writable* over CI-V, so a full software backup/restore is genuinely achievable.

**What CI-V exposes (the raw material):**
- **`1A 05 00 xx`** — the SET-menu table, **~100+ items**, each documented "Sets or reads the …": RTTY, SPEECH, beep levels, screen-capture, filters, and the CI-V settings themselves (Transceive `00 89`, Output `00 91`, USB Echo `00 92/93`), etc. This table *is* the enumeration list for a backup walk.
- **`14 xx`** — level settings (AF, RF, squelch, notch, NR, power, beep level…), read/write.
- **`16 xx`** — function on/off states (AGC, NB, NR, preamp…).
- **`1A 06`, `03/04/06`** — data mode, mode/filter per band.

**The one deliberate exception — CI-V baud rate.** It is **not** a CI-V-readable/writable item: the manual mentions baud only in its intro and a preamble-timing note, never in the `1A 05` table. That's by design (reading/writing the link speed over the link itself is a chicken-and-egg problem, and it's often "Auto"). So **baud stays a front-panel/manual setting** and must be *excluded* from any restore. The CI-V **address** is exposed, but arguably exclude it from restore too, for the same "don't break the link you're using" reason.

**Approach sketch (decide specifics at build time):**
- **No single "dump everything" command** — enumerate the manual's item table and read each (many round-trips, but reliable). Store as a versioned JSON file (radio model + firmware note + timestamp).
- **Some items are band/mode-scoped** — a *full* backup means capturing per-band state, not just the current band. Decide backup granularity (current state vs full per-band sweep) at build time.
- **Restore = write the saved items back**, skipping the exclusion list (baud, and probably CI-V address). Offer a dry-run/diff ("these 6 settings differ from the file") before committing writes.
- **Use *this* manual as the source of truth** — MkII item numbers differ from the original IC-7300; pin the item table to `docs/manuals/IC-7300MK2_ENG_CI-V_0.pdf`.
- Ties naturally into IWC's **voice-control** goal ("save my settings" / "restore my settings") and is a clean marketing differentiator over YWC.

**Sequencing:** capture-only for now (MM5AGM 2026-07-29). Lower priority than the on-air features (Phases 5–7); it's a fair bit of table-enumeration work with no live-operating payoff, so it lands once the core control/UX phases are settled.
