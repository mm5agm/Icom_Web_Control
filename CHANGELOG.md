# Changelog

Icom Web Control (IWC) starts a fresh `0.x` version line — it is **not** a
continuation of the Yaesu Web Control (YWC) changelog, even though IWC was
carved from the YWC codebase. For the shared history prior to the split, see
the [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) repository.

> ## The release notes in [README.md](README.md#release-notes) are the record
>
> **This file is closed.** It stops at 1.0.3 and is kept only as an archive of
> the entries that were written here. It is not maintained, and a release does
> **not** need an entry adding to it.
>
> From 1.0.4 onwards, the operator-facing release notes in
> [README.md](README.md#release-notes) are the single, authoritative account of
> what changed in each release. That is where the notes were actually being
> written from 1.0.1 onwards; keeping a second, thinner copy here only produced
> a file that was silently out of date — and a `[Unreleased]` heading that read
> "Nothing yet" through three releases.
>
> `finish-release.ps1` checks the README entry for the same reason: that is the
> document a release is blocked on.

## [1.0.3] - 2026-08-05

First full release since 1.0.0, so it also delivers everything in the 1.0.1 and
1.0.2 pre-releases to anyone who tracks full releases only. See
[README.md](README.md#release-notes) for the operator-facing notes.

### Fixed
- **Discussions links led into a category that does not accept replies.** The
  links on the About page, in Settings, and in the README and User Manual
  pointed at an announcement-only category, so following them produced a page
  with no way to post. They now target the right category with a new post
  pre-selected.

### Added
- **Contributed-calibration store with median aggregation**
  (`Services/CalibrationContributionsStore.cs`,
  `Models/Calibration/CalibrationContributions.cs`,
  `calibration-contributions/`). Importing a second operator's calibration used
  to overwrite the first wherever the two disagreed, with nothing recording
  that the first had contributed. Each contribution is now stored separately
  and the shipped default is the per-point median: none keeps the placeholder,
  one is used as-is, two or more are combined. A meter whose median comes out
  non-monotonic or out of range is refused rather than shipped, and the spread
  between contributors is reported so a real disagreement is visible.
  Development-side only — every endpoint returns `NotFound` outside
  Development, and the store directory is never published or installed, so an
  installed copy is unaffected. Design notes:
  [docs/design/calibration-contributions.md](docs/design/calibration-contributions.md).

## [1.0.2] - 2026-08-04 (pre-release)

> This file was not kept up between 1.0.0 and here. [README.md](README.md#release-notes)
> carries the complete, operator-facing notes for 1.0.1 and 1.0.2 — including the
> band-plan, Segment-dropdown and start-up-overlay work not repeated below.

### Fixed
- **The spectrum panel could never appear if the scope never streamed**
  ([#1](https://github.com/mm5agm/Icom_Web_Control/issues/1)). The panel was
  revealed only by an `SdrStatus` message, and the only thing that emitted one
  was a completed sweep — so a radio whose scope was off, or wasn't sending
  `27 00` at all, produced no spectrum card. The **Scope** on/off switch lives
  inside that card, so there was no way to switch it back on. The card now
  follows the radio connection, and the controller announces the scope's real
  state (on / off / waiting / disconnected) whether or not sweeps are arriving.
- **"Already running" was a dead end**
  ([#2](https://github.com/mm5agm/Icom_Web_Control/issues/2)). A copy of IWC
  that failed to exit blocked every relaunch behind an OK-only dialog, leaving
  Task Manager as the only way out. The dialog now names the process and offers
  to open the running copy or to close it and start a fresh one.
- Added a hard-exit watchdog: if the process is still alive 10 s after shutdown
  begins, it exits anyway rather than lingering and blocking the next start.

### Changed
- The About page's diagnostics block reports **band scope** state — on/off,
  sweeps completed, sweeps discarded, age of the last sweep — in place of the
  `SDR device` line, which was inherited from YWC and always read
  "(none configured)" on IWC.
- Voice control gained decomposed commands for attenuator, AGC, RF gain,
  squelch, NR, NB, notch, APF, TX power, mic gain and processor, and the
  screen-reader label keys those controls were advertising but not carrying.
  Phrase-pack schema is now 9, so saved packs reset to defaults on first run;
  the bundled US English pack is rebuilt to match.

## [1.0.0] - 2026-08-01

First public release. Carved from Yaesu Web Control and re-fitted for Icom CI-V,
targeting the **IC-7300 MkII** (CI-V over USB, default address `B6`). See
[docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md) for
the phased roadmap.

### Added
- Rig control: frequency and mode per VFO (incl. DATA modes), band / VFO / split,
  RF power set, ATU, and radio power on/off.
- Metering: S-meter plus Po / SWR / ALC gauges, polled at ~10 Hz.
- Spectrum scope over CI-V (`27 00`, 475 points) with two-stage smoothing and an
  auto noise-floor display driven by a single Range slider; span, on/off and
  CENT/FIX controls.
- RX DSP panel, Twin PBT, and RX/TX tone controls mapped to CI-V.
- Voice control (Windows SAPI) — hands-free tuning, mode, status queries and TX;
  bundled en-GB pack plus a one-click US English pack installer.
- rigctld bridge for WSJT-X / JTAlert / Log4OM.
- Memory-channel banks carried over from YWC.

### Notes
- Tested on a single IC-7300 MkII (firmware Main CPU 1.02, Front CPU 1.01,
  DSP Program 1.01, DSP Data 1.00, FPGA 1.01) by one operator.
- The original IC-7300 shares the CI-V protocol and is expected to work
  (default address `94`) but has not been bench-tested.
- Windows only. The installer is unsigned.
