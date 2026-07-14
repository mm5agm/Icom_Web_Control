# ADR 0002 — Rejected: hidden CAT command to unlock SDR-free spectrum streaming

**Status:** Rejected — 2026-07-12
**Decision-makers:** Colin (MM5AGM), with investigation support

## Context

A YouTube video showed StationMaster displaying a live spectrum for an FT‑710 connected only by USB — no visible SCU‑LAN10 or network cable. Asked to explain this, Copilot produced a detailed but uncited technical narrative: that Yaesu radios contain a hidden CAT command (`LAN1;ON` or `REMOTE;1`) which activates the SCU‑LAN10's UDP spectrum/IQ broadcast engine (allegedly already present in firmware) over the existing USB CAT link, with spectrum appearing on UDP port 50003 and IQ on 50000–50002.

If true, this would remove the need for external SDR hardware (SDRplay/RSP1 on the IF output) for any Yaesu radio with a CAT connection — a significant simplification versus the two-process SDR-worker architecture in [ADR 0001](0001-dual-sdr-architecture.md).

## Investigation

**Documentary check** — reviewed the official CAT and operating manuals in `docs/manuals/` for FTDX101MP, FTDX10, and FT‑710:
- All three define an `SS` (SPECTRUM SCOPE) command, but it only sets/reads display parameters (speed, span, mode, color, marker, level) — it never dumps waveform/bin data.
- Neither the FTDX101MP CAT manual nor its operating manual contains any reference to Ethernet, IP address, TCP, or UDP.
- The FTDX101MP operating manual lists `SCU-LAN10` only as a purchasable external accessory ("LAN Unit"), alongside the mic and antenna tuner — not as a firmware capability to unlock.
- FT‑710 doesn't list SCU‑LAN10 as a supported accessory at all, so whatever StationMaster does for FT‑710 can't be the SCU‑LAN10 protocol regardless.

**Live probe, Colin's own FTDX101MP (COM4, 38400 8‑N‑2):**
- `scripts/probe/cat-command-sweep.ps1` swept all 2-letter CAT mnemonics *not* in the documented set (559 tested) as bare queries. No reply longer than the interesting-length threshold; nothing resembling scope/waveform data.
- `scripts/probe/cat-lan-remote-probe.ps1` sent the literal candidate strings from the Copilot narrative (`LAN1;ON;`, `LAN1;`, `REMOTE;1;`, case variants, a few near-misses). No unusual replies, and the radio answered `ID;` normally after each one (CAT parser unaffected).
- Simultaneous Wireshark capture on the active network adapter, filtered to the claimed port range using explicit `udp.srcport`/`udp.dstport` bounds (the naive `udp.port >= X && udp.port <= Y` form falsely matches because `udp.port` has two values per packet and Wireshark satisfies each bound against a different one) — zero packets in the 50000–50010 range while the probe ran.

**Independent corroboration** — Colin has a UK-based contact who reverse-engineered the real SCU‑LAN10 network protocol from genuine packet captures (real FTDX101MP + SCU‑LAN10 hardware, captured with Colin's help via remote access) and built a working implementation (`101cats`) from it. That implementation still requires the physical SCU‑LAN10 unit. Someone already deep inside the real protocol is the person most likely to have found a USB-only bypass if one existed, and hasn't.

**Further corroboration (2026-07-13)** — the author of StationMaster itself (the program whose video originally prompted this investigation) confirmed that StationMaster does not work with the FTDX101MP at all. A developer already building spectrum-over-USB integrations for Yaesu radios would be the person most likely to have found and shipped a hidden LAN/CAT streaming path on this radio if one existed — and instead his program simply doesn't support it. This closes out any remaining doubt specific to the FTDX101MP.

## Decision

**Reject the hidden-LAN-CAT-command hypothesis.** The FTdx101MP (and, by the accessory-list argument, the FT‑710) has no built-in network hardware capable of UDP spectrum streaming; there is no undocumented CAT command that activates one. SDR-free wideband spectrum display is not achievable this way on this hardware. No further engineering effort should go toward it.

The two-process SDR-worker architecture ([ADR 0001](0001-dual-sdr-architecture.md)) remains the correct path for wideband spectrum display.

## Open alternative (unconfirmed, not pursued yet)

The FT‑710 video's "just a USB cable" spectrum is most plausibly an **audio-derived scope** — an FFT over the radio's USB audio codec (used for digital modes on modern Yaesu rigs), giving a narrowband (audio-passband-width) display rather than a true wideband IF panorama. This is a different, much cheaper technique than IF-based SDR and doesn't need any hidden command — but it's a hypothesis, not verified, and is out of scope for this ADR. Worth a fresh, small investigation later if a lightweight fallback panadapter (no SDR hardware required) becomes a priority.

## References

- Probe scripts: [`scripts/probe/cat-command-sweep.ps1`](../../scripts/probe/cat-command-sweep.ps1), [`scripts/probe/cat-lan-remote-probe.ps1`](../../scripts/probe/cat-lan-remote-probe.ps1)
- Sweep results: `scripts/probe/cat-sweep-results.csv`
- Manuals checked: `docs/manuals/FTDX101MP_D_CAT_OM_ENG_2308-L.pdf`, `docs/manuals/FTDX101MP_D_OM_ENG_EH068H213_2102P-JS-3.pdf`, `docs/manuals/FT-710_CAT_OM_ENG_2306-C.pdf`
- Investigation session: chat transcript 2026-07-12
