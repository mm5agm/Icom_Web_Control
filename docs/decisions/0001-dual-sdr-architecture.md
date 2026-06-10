# ADR 0001 — Dual-SDR architecture: separate worker process per SDR

**Status:** Accepted — 2026-06-10
**Decision-makers:** Colin (MM5AGM), with implementation planning support

## Context

YWC ships single-SDR support via the `SdrBackgroundService` (which opens the SDRplay API directly from the main YWC process). The next planned feature is **dual-SDR** — one SDR per VFO on dual-receiver radios (FTdx101MP/D), so the operator sees both VFO A's and VFO B's spectrum simultaneously. The natural design was to extend `SdrBackgroundService` to hold two `SdrplayDevice` instances side by side in the YWC main process.

Before committing to the refactor, we wrote a small standalone probe (`scripts/probe/SdrplayConcurrentOpen`) to verify the SDRplay API actually supports two RSPs Selected and Init'd concurrently in a single process. The probe ran multiple iterations covering every documented pattern in the SDRplay API:

1. **No lock, sequential Select+Init** — original YWC pattern applied twice → first Select OK, first Init OK, second Select → `err=1`.
2. **LockDeviceApi + Select both + UnlockDeviceApi + per-device Init** → first Select OK, second Select → "Device already selected".
3. **Per-device Lock cycle: Lock → Select(1) → Unlock → Init(1) → Lock → Select(2) → Unlock → Init(2)** with `valid=1` flag set on each device struct → first Select OK, second Select → `err=1`.
4. **Select BOTH devices before calling Init on either** (theory: active Init/streaming on device 1 blocks Select on device 2) → first Select OK, second Select → `err=1` even with no Init active anywhere.

Across all four patterns, the SDRplay API on the test machine (SDRplay API v3, one RSP1B + one RSP1, Windows 11) returns error 1 on the second SelectDevice as long as the first device remains Selected in the process. Additional symptoms confirmed the limit is at the API/service layer:

- After `SelectDevice` succeeds on device 1, a subsequent `GetDevices` returns only **one** device (the API removes the Selected device from enumeration — it's now "owned" by this process).
- Failed Select calls leave the API in a state where `GetLastError` itself crashes with access violation.
- The SDRplay API Service has to be restarted (via `services.msc`) after each failed run before another `Open` will succeed.

## The limit, stated plainly

The SDRplay API service maintains **one Selected device per host process**. Multi-device support exists at the API surface (`LockDeviceApi`, `GetDevices` returning an array, the `valid` flag) but the actual enforcement allows only one device to be in the Selected state per process at any given time. Calling SelectDevice on a second device while one is already Selected returns `sdrplay_api_Fail` (error code 1). The API documentation hints at multi-device support but in practice this appears designed for the RSPduo's two-tuner internal architecture (which presents as one device with two channels), not for two physically-separate RSPs in one process.

This is documented further in the probe code at `scripts/probe/Program.cs` and is reproducible against SDRplay API v3 as installed from sdrplay.com as of 2026-06-10.

## Decision

**Adopt a two-process model: YWC spawns a dedicated SDR worker process for each SDR. The main YWC process never opens an SDR directly.**

High-level shape:

```
┌─────────────────────────────────────────────┐
│ Yaesu_Web_Control.exe (main)                │
│   ┌──────────────────────────────────────┐  │
│   │ Kestrel web + SignalR + CAT + UI     │  │
│   │ SdrManager (NEW) — supervises        │  │
│   │   workers, forwards FFT bins         │  │
│   │   to browser via SignalR             │  │
│   └─────────┬───────────────┬────────────┘  │
└─────────────┼───────────────┼───────────────┘
              │ TCP localhost │
              │ (FFT frames)  │
   ┌──────────▼─────┐   ┌─────▼────────┐
   │ SdrWorker A    │   │ SdrWorker B  │
   │  (own process) │   │  (own proc.) │
   │  → SDRplay API │   │  → SDRplay   │
   │  → RSP for     │   │    API       │
   │    VFO A       │   │  → RSP for   │
   │                │   │    VFO B     │
   └────────────────┘   └──────────────┘
```

Each SDR worker is a small .NET console app that holds exactly one SDRplay device, performs the FFT, and streams bins back to YWC main over a TCP socket on a localhost-only port. Workers are spawned by `SdrManager` (the replacement for `SdrBackgroundService`) on YWC startup if SDRs are configured, killed and respawned when the user changes SDR settings, and auto-restarted with backoff if they crash.

## Why this over the alternatives

| Option considered | Reason rejected |
|---|---|
| Single-process dual-SDR (original plan) | Confirmed not possible by the probe. SDRplay API one-device-per-process limit is hard. |
| Recommend users buy an RSPduo (£200 + special handling) | Pushes hardware cost onto users; constrains the feature to one specific device family; doesn't help operators who already have two separate RSPs (which Colin does). |
| Software toggle on a single SDR | Doesn't deliver the actual user value (concurrent dual visibility). Would require physical coax switching or per-band cable swaps. |
| Wait for SDRplay to fix the API limit | Open-ended dependency on a third party. No public roadmap commitment from SDRplay. |
| Use SoapySDR's sdrplay plugin instead | SoapySDR's sdrplay plugin wraps the same `sdrplay_api.dll`, so it would hit the same limit. (Worth a probe variant for RTL-SDR / Airspy users but doesn't help SDRplay users.) |

The two-process model is the only design that:
1. Works with the SDRplay API as it exists today.
2. Doesn't require specific hardware purchases.
3. Cleanly supports mixed SDR types in the future (one RSP + one RTL-SDR each in its own worker — no shared-API problems).
4. Gives crash isolation: a worker dying doesn't take YWC down.

## Consequences

**Positive:**
- Dual-SDR works with any combination of SDRs the user has.
- Worker process boundary contains SDR-driver crashes (which do happen with experimental hardware).
- Worker can be restarted independently if the SDR comes/goes (USB hot-plug).
- Each worker can use the existing `SdrplayDevice` / `SoapySdrDevice` code essentially unchanged — they just run in a different process.

**Negative:**
- Two-process IPC: needs a wire protocol for FFT frames (length-prefixed binary, ~1024 floats per frame at ~10 Hz = ~80 kB/s per SDR — trivial for localhost TCP).
- Child-process lifecycle management: spawn, supervise, restart-on-crash, clean shutdown on YWC exit, port allocation that doesn't collide with the user's other software.
- Three things now have to install correctly (YWC main, the worker exe, the SDRplay API). The installer needs to copy the worker exe into the install folder.
- Settings changes require killing+respawning workers rather than just reconfiguring an in-process object.
- More log files / more places to look when something goes wrong. YWC should aggregate worker stdout/stderr into its existing Serilog file sink so users only have one log to share in bug reports.

**Mitigations:**
- Worker is a separate `.csproj` in the same solution (`Workers/Yaesu_Sdr_Worker/`), shipped as part of the main installer.
- IPC is a single small class on each side (`SdrWorkerClient` in main, `SdrWorkerServer` in the worker). Keep the protocol as boring as possible — length-prefix + a 16-byte header (sequence, bin-count, centre-freq, span) + raw float array.
- Worker spawning supervised by `SdrManager` — handles re-spawn with exponential backoff (max ~30 s), and surfaces worker state via the existing SignalR `SdrStatus` envelope so the browser UI shows what's actually happening.
- Worker stderr piped into YWC's Serilog stream with a `[SdrWorker A]` prefix so debugging stays in one log.

## Out of scope for this decision

- The specific UI design (Mono A / Mono B / Multi toggle, persistent Cursor, Expand, Hold) is unchanged from the original dual-SDR plan in [`memory/project_dual_sdr_plan.md`](../../memory/project_dual_sdr_plan.md). Backend swap to two-process doesn't affect what the user sees on screen.
- 3DSS is still out (decided 2026-06-06).

## References

- Probe code: [`scripts/probe/Program.cs`](../../scripts/probe/Program.cs)
- Original dual-SDR plan: in Claude memory, `project_dual_sdr_plan.md`
- Probe sessions: chat transcripts 2026-06-10 morning, four iterations confirming the limit
