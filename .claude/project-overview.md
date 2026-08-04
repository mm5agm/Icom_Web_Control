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

6. **Spectrum** (`wwwroot/js/sdr/`)
   - `SpectrumPanel` owns its canvas; `SdrSpectrumPipeline` is transport only.
   - Instance-able per VFO. The `sdr` / `sdrId` naming is inherited from YWC —
     **there is no SDR in IWC**; the scope is the radio's own.

7. **Voice control** (`Services/Voice/`, `wwwroot/js/ui/voice-control.js`)
   - SAPI recognition against a generated grammar, an intent dispatcher that
     calls the semantic seam, and TTS feedback. Phrase packs are user-editable
     and shareable, versioned by schema.

8. **External integration**
   - `RigctldServer` (Hamlib TCP), `WsjtxUdpService`, `DxClusterService`.

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
- Free of duplication
- ES-module-friendly on the frontend
- Efficient for real-time updates
- Respectful of the `IRadioController` seam above all else
- Accompanied by an accessibility label and a voice intent when it adds an
  operator-facing control
