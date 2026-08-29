# Icom Web Control — Project Overview for Claude

This document gives Claude a high-level understanding of the Icom Web Control
project: what the application is for, its major subsystems, the architectural
philosophy, and the domain concepts used throughout the codebase.

Use it to understand *intent*. `.claude/rules.md` is the enforceable
specification; `CLAUDE.md` is the working map of the repository.

---

## PROJECT PURPOSE

Icom Web Control (IWC) is a browser-based control and monitoring interface for
the **Icom IC-7300 MkII**, talking to the radio over **CI-V**. It gives the
operator real-time metering (S-meter, power, SWR, ALC, compression), full
frequency/mode/band control, the RX DSP panel, memories, a band scope with
waterfall, DX cluster overlay, voice control, and a rigctld bridge so WSJT-X and
Log4OM can share the radio.

Written in:

- **.NET 10** — backend, hosted in a WinForms shell that runs Kestrel
- **SignalR** — real-time push to the browser
- **JavaScript ES modules** — frontend
- **Razor Pages + Bootstrap** — UI

It is a **sibling of Yaesu Web Control (YWC)**, cloned from it and then carved:
the Yaesu CAT layer was removed and replaced with a CI-V stack behind a
protocol-free seam. The two are deliberately separate applications with separate
repositories. YWC stays Yaesu-only; IWC stays Icom-only.

The goal is a professional-grade, modular, maintainable interface that mirrors
the behaviour of the physical radio while remaining easy to extend — and that a
partially-sighted operator can drive by voice.

---

## THE ONE ARCHITECTURAL IDEA

Everything else follows from this: **`IRadioController` is a semantic seam.**

Above it, the application speaks radio concepts — "tune VFO A to 14074000 Hz",
"set mode USB", "read the S-meter". Below it, exactly one class turns those into
bytes. YWC had no such seam, which is why its CAT strings leaked into voice
macros, controllers and pages; re-fitting it for another protocol meant touching
everything. IWC's seam is what makes a second Icom model, or a mock radio for
UI work, a contained change.

`StubRadioController` exists to prove the seam holds: the whole UI is
developable with no radio attached.

---

## MAJOR SUBSYSTEMS

1. **CI-V transport** (`Services/Civ/`)
   - `CivBusService` owns the serial port; binary reads only, bus-echo
     suppression, DTR/RTS deliberately de-asserted (they can be PTT lines).
   - `CivFrameBuffer` / `CivFrame` reassemble and encode `FE FE … FD` frames.
   - `CivScopeAssembler` stitches `27 00` waveform segments into one 475-point
     sweep.

2. **Radio controller** (`Services/CivRadioController.cs`)
   - The only byte-emitting class. Owns the poll loop, every command, scope
     handling, and the pseudo-dual-receiver routing.
   - Backs its poll rate off while the scope streams — one 19200-baud bus
     carries both, and a smooth trace is worth slower meters.

3. **State and push** (`RadioStateService`, `RadioHub`)
   - A single `RadioStateUpdate` envelope `{ property, value }` carries all CAT
     state to the browser. New clients get a full snapshot replay on connect.

4. **Gauge system** (`wwwroot/js/guages/`)
   - Renders S-meter, Power, SWR, ALC and the rest via canvas-gauges.
   - All gauge creation goes through `gaugeFactory`; all layout lives in
     `gauge.js`; meter classes supply configuration only.

5. **Calibration engine** (`wwwroot/js/calibration/`)
   - Converts raw radio values into calibrated meter values.
   - Pure functions, no DOM, no side effects. Single source of truth for the
     scaling tables. Operators can edit and share calibration files.
   - **Shared with YWC** — the engine itself lives in `core/js/calibration/`
     and is copied into `wwwroot/js/` at build time. See SHARED CORE below.

6. **CW decode** (`core/Services/Cw/`, `core/js/cw/`) — **shared, engine
   built here, UI not wired up yet**
   - Takes mono float audio and a tuned pitch, produces text. Never sees a
     CI-V command or a radio model — see `core/docs/design/cw-decoder.md` for
     the full pipeline (FFT tone tracking + Goertzel envelope detection +
     readability gating).
   - **The engine's completeness here vs. YWC's side needs checking fresh,
     not assumed** — the two `core/tests/.../Cw/` directories are the same
     subtree and drift between them (in either direction) is exactly what
     "pull before you start" below is protecting against. Don't treat either
     copy as the reference without diffing first. **IWC doesn't consume the
     engine yet either way**: there's no Razor page or CAT wiring hooking
     `core/js/cw/cw-reader-panel.js` up to the CI-V audio path. If you're
     asked to add CW reading to IWC, pull `core/` first — the engine may
     already exist and the work may be wiring, not decoding.
   - **The standalone `Radio_Web_Control_Core` clone is not the reference
     for this either.** It lags both applications' local `core/` — check
     each app's own subtree, never the standalone repo, when comparing test
     coverage or deciding what's current.

7. **Spectrum** (`wwwroot/js/sdr/`)
   - `SpectrumPanel` owns its canvas; `SdrSpectrumPipeline` is transport only.
   - Instance-able per VFO. The `sdr` / `sdrId` naming is inherited from YWC —
     **there is no SDR in IWC**; the scope is the radio's own.

8. **Voice control** (`Services/Voice/`, `wwwroot/js/ui/voice-control.js`)
   - SAPI recognition against a generated grammar, an intent dispatcher that
     calls the semantic seam, and TTS feedback. Phrase packs are user-editable
     and shareable, versioned by schema.

9. **External integration**
   - `RigctldServer` (Hamlib TCP), `WsjtxUdpService`, `DxClusterService`.
   - `Models/DxSpot` — the spot type — **already lives in `core/`**; the
     service around it (connection handling, watch-list logic) hasn't moved.
   - **Remote Audio's backend does not exist in IWC.** `Services/Audio` is
     absent: no session manager, no bridge, no device enumeration. This is
     what YWC's Remote Audio work is expected to bring here once it's ready
     to share.
   - **The browser half may already be present — check, don't assume.** The
     transport JS (`audio-session.js`, `audio-protocol.js`, `audio-capture.js`,
     `audio-playback.js`) is authored in `core/js/audio/` and arrives here by
     subtree merge, so whether it's in your tree depends on when `core/` was
     last pulled and whether that merge has been pushed. `ls core/js/audio`
     answers it in one command; `./scripts/core-sync.ps1 -Pull` fetches it if
     it's missing. Don't write these modules from scratch without looking.
   - **Remote Video and VC Tune will never exist in IWC.** Both are
     Yaesu-specific subsystems in YWC (VC Tune is Yaesu's external tuner
     protocol; has no CI-V equivalent) and are permanently YWC-only. This is
     also recorded in this repo's own `CLAUDE.md`, under "Known dead/stale
     code," which notes the VC Tune UI was deliberately removed during the
     carve.

---

## SHARED CORE (`core/`)

IWC and YWC share code through **[`Radio_Web_Control_Core`](https://github.com/mm5agm/Radio_Web_Control_Core)**,
consumed here as a **git subtree at `core/`** — not a submodule, not a NuGet
package. A plain clone of this repo must still build with no extra steps.

**Why:** IWC was cloned from YWC. A 2026-08-13 measurement found 62 of 89
first-party files at shared paths between the two repos were effectively
identical — the same code, maintained twice. `core/` exists to stop that.

**The rule for what belongs there:** if it needs to know what a radio is, it
doesn't go in `core/` — no CI-V, no CAT, no serial framing, no
`IRadioController` or Yaesu equivalent of it. Everything else radio-agnostic
— DX cluster handling, memories, ADIF, calibration maths, decoders — is
eligible.

**IWC is the easier side of this, structurally.** Because `IRadioController`
already draws a clean line between semantics and protocol, radio-agnostic
code here is usually already isolated from CI-V — YWC has to do more
refactoring on the way into `core/` because it has no equivalent seam yet.
Don't assume that means IWC should move code faster than YWC can review it,
though — see the workflow below.

**What's already there** (check `core/docs/design/shared-core-plan.md` for
the live, authoritative checklist before assuming anything here is current):
- `Models/DxSpot.cs`, `Services/AdifParser.cs`, `js/calibration/calibration-engine.js`
  — the original plumbing proof and first pure-function tests.
- `Services/Cw/*` (decoder engine, fully tested) and `js/cw/cw-reader-panel.js`
  — see MAJOR SUBSYSTEMS above. Built directly in `core/`, not migrated in
  after the fact.

**What's eligible but not yet moved:** `DxClusterService`, `MemoryService`,
`MemoryBankService`, the gauge JS modules, the SignalR transport modules,
`VoicePackMetadata`. The rule is **move on next touch**, not batch
migration — when you'd be editing one of these for its own reason anyway,
move it into `core/` as part of that same change.

**Before starting new work in CW decode**, confirm `core/` is up to date
(`./scripts/core-sync.ps1 -Pull`) — the engine may already cover what you're
about to write, and wiring it up is a much smaller job than building it.

**Getting a change into both apps:**

```powershell
./scripts/core-sync.ps1 -Check   # is anything owed upstream?
./scripts/core-sync.ps1 -Push    # send core/ commits up (pulls first)
./scripts/core-sync.ps1 -Pull    # bring YWC's core work down
```

Never author feature changes to `core/` in a standalone clone of
`Radio_Web_Control_Core` — it isn't compiled against a real consumer there,
so a subtle break won't surface until someone pulls it. Work inside this
repo's `core/`, or YWC's.

**Constraints specific to `core/`:**
- Targets `net10.0`, never `net10.0-windows` — YWC multi-targets for a
  macOS/Linux CAT-only host; a Windows-only dependency here would build fine
  against IWC and silently break YWC's second target framework.
- No ASP.NET Core references, no DI, no hosting — consumers wire it up.
- `<Compile Remove="core\**" />` must stay in `Icom_Web_Control.csproj` — the
  Web SDK globs `**/*.cs`, so without it every file in `core/` compiles
  twice.
- `core/js/**/*.js` is copied, not linked, into `wwwroot/js/` by a build
  target. Edit the `core/` copy; never edit the generated `wwwroot` one.

---

## ARCHITECTURAL PHILOSOPHY

- Single-responsibility modules.
- One seam between semantics and protocol, and nothing sneaks through it.
- No duplication of logic or configuration.
- No global variables; no magic strings.
- Pure functions where possible.
- Clear separation of UI, calibration, and data flow.
- ES module imports for all frontend code.
- **Comments that record verified hardware behaviour are load-bearing.** Much of
  what this codebase knows about CI-V was established at the bench, once, and
  the comment is the only record.

The codebase should feel like it was written by a disciplined engineering team.

---

## DOMAIN CONCEPTS

Claude should understand:

- **CI-V** — Icom's binary control bus. Frames are `FE FE <to> <from> <cmd>
  [subcmd] [data] FD`. It is a *bus*: the radio echoes the controller's own
  transmission back before replying.
- **Addresses** — radio `B6` (IC-7300 MkII; `94` on the original IC-7300),
  controller `E0`.
- **BCD data** — frequencies and many parameters travel as packed BCD, not
  binary integers.
- **S-meter** — 0–255 raw → S0 to S9+60 dB via the calibration tables.
- **Power** — up to 100 W (25 W AM).
- **Band scope** — `27 00` waveform, 475 points per sweep; span via `27 15`;
  on/off via `27 10` / `27 11`; Centre vs Fixed mode affects whether the trace
  lines up with the frequency axis.
- **Single receiver** — VFO B is a frequency/mode slot the one receiver is
  steered to, not a second listening chain. Everything about the "pseudo-dual
  receiver" is time-slicing that one receiver.
- **Frequencies are always in Hz** in this codebase.

---

## GOALS FOR GENERATED CODE

Code should be:

- Modular and predictable
- Consistent with existing patterns
- Free of duplication — check whether new radio-agnostic code belongs in
  `core/` before writing it locally (see SHARED CORE above)
- ES-module-friendly on the frontend
- Efficient for real-time updates
- Respectful of the `IRadioController` seam above all else
- Accompanied by an accessibility label and a voice intent when it adds an
  operator-facing control
