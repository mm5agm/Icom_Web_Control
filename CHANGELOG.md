# Changelog

Icom Web Control (IWC) starts a fresh `0.x` version line — it is **not** a
continuation of the Yaesu Web Control (YWC) changelog, even though IWC was
carved from the YWC codebase. For the shared history prior to the split, see
the [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) repository.

## [Unreleased]

_Nothing yet._

## [0.1.0-alpha] - 2026-08-01

First public preview. Carved from Yaesu Web Control and re-fitted for Icom CI-V,
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
