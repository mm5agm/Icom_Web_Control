# Icom Web Control

![Status](https://img.shields.io/badge/Status-pre--alpha%20(not%20yet%20functional)-orange?style=flat-square)
![Licence](https://img.shields.io/badge/Licence-GPL--3.0-blue?style=flat-square)

> **Pre-alpha — this does not control a radio yet.** I'm building Icom Web Control (**IWC**) as a sibling to my [Yaesu Web Control](https://github.com/mm5agm/Yaesu_Web_Control) (YWC) project, for Icom CI-V transceivers. The two are deliberately separate applications with separate repositories — YWC stays Yaesu-only, IWC stays Icom-only.

## What this is

IWC is a web-based control panel and panadapter for Icom transceivers, cloned from YWC and re-fitted for Icom's CI-V protocol. The plumbing YWC already got right — the real-time SignalR pipeline, the meter gauges, the spectrum display, the settings and rigctld bridge, and the voice control — is being kept; the Yaesu CAT layer is being replaced with a fresh CI-V layer behind a clean radio-control seam.

**Voice control is a first-class requirement, not an add-on** — several of the operators I build for are partially sighted, so hands-free operation matters from day one.

## First target radio: Icom IC-7300 MkII

- CI-V over USB Type-C (default address `B6`)
- Single receiver, HF + 6 m (+ 4 m on European versions)
- Spectrum scope streamed **over CI-V** (`27 00`, 475 points) — so no external SDR is needed, unlike some setups. The MkII's rear LAN port offers a faster scope feed later.

Other Icom CI-V radios (IC-705, IC-7610, IC-9700, …) share the same protocol family and can follow once the IC-7300 MkII works end-to-end.

## Status & plan

Nothing here controls a radio yet. The full build plan — how IWC is carved out of YWC, what's kept, what's rebuilt, and the phased CI-V roadmap — lives in [docs/design/iwc-clone-split-plan.md](docs/design/iwc-clone-split-plan.md).

## ⚠️ Warning

This software will interact with radio hardware. When it reaches a testable state I will use only the official Icom CI-V commands as documented, but you will use it entirely at your own risk. Always verify transmit frequencies, power levels, and settings before use.

## Licence

GPL-3.0, the same as YWC. See [LICENSE](LICENSE).

---

*Colin Campbell, MM5AGM*
