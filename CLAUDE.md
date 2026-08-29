# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

> ## ⭐ START HERE
>
> **Icom Web Control (IWC)** is a browser-based control and monitoring interface
> for the **Icom IC-7300 MkII** over CI-V. It is a sibling of, and was cloned
> from, [Yaesu Web Control (YWC)](https://github.com/mm5agm/Yaesu_Web_Control) —
> but the carve is **done**: the Yaesu CAT layer is gone, replaced by a CI-V
> stack behind a protocol-free `IRadioController` seam.
>
> **v1.0.0 shipped on 2026-08-01** and controls the radio end-to-end. This is a
> working application, not scaffolding.
>
> - **Radio:** IC-7300 MkII, CI-V over the USB Type-C "Serial A" port.
>   Default **COM8, 19200 8N1**, radio address **B6** (`94` on the original
>   IC-7300), controller address **E0**. Manuals are in `docs/manuals/`
>   (local, git-ignored).
> - **Voice control is a required feature**, not optional — it exists for
>   partially-sighted operators. Treat regressions in it as release blockers.
> - **Separate repo from YWC** (`origin` = `mm5agm/Icom_Web_Control`). Keep YWC
>   concerns out of here and vice-versa. Where a comment still says "Yaesu", it
>   is almost always explaining *why the Icom code differs* — read before
>   "fixing".
> - **Roadmap:** [`docs/design/iwc-clone-split-plan.md`](docs/design/iwc-clone-split-plan.md).
>   Phases 0–5 are built (carve → CI-V transport → command roadmap → parity →
>   pseudo-dual receiver). Phase 6 (CW decode), Phase 7 (skins) and Phase 8
>   (settings backup/restore over CI-V) are open.

---

---

## Shared code lives in Radio_Web_Control_Core. This is a hard rule.

`core/` is a **git subtree** of
[Radio_Web_Control_Core](https://github.com/mm5agm/Radio_Web_Control_Core),
shared with Yaesu Web Control. It is not a vendored copy and it is not a snapshot.

**If code is radio-agnostic, it belongs in `core/`.** Not in this repo's own
tree "for now", not "until it settles", not "until the other app needs it".
The exception is code that genuinely cannot be shared - see the table.

This rule exists because it was broken. The whole CW decoder - six services and
five test files - was authored inside `core/` and then sat in one branch of one
repo for weeks with no second copy anywhere, and the reader panel was written
straight into `wwwroot/js/ui/` where the other app could never see it. Nobody
decided that; it just never got pushed.

### Does it belong in core?

| goes in `core/` | stays in this repo |
|---|---|
| Signal processing, decoders, DSP | CAT / CI-V framing and addressing |
| Data models exchanged with other tools (ADIF, DX spots) | Anything reading a radio-specific register or code |
| Pure algorithms with no radio in them | Per-radio lookup tables and calibration numbers |
| Browser modules that only talk to an HTTP API | Anything touching this app's DI, hubs or Razor pages |
| Tests for all of the above | Tests for this app's own wiring |

The seam is the radio. `CwDecoderEngine` takes samples and a pitch, so it is
core. `YaesuIfWidth` maps a Yaesu SH code to Hz, so it is not - and Icom's
widths are a formula rather than a table, which is the proof that it never
could have been.

A shared browser module goes in `core/js/<area>/`. The `CopySharedCoreJs`
target copies `core\js\**\*.js` into `wwwroot\js\` preserving the subdirectory,
so `core/js/cw/x.js` is served at `/js/cw/x.js` with **no csproj change**. That
target also writes the `.gitignore` for what it generated, so the copies never
need a hand-written ignore rule either.

**Moving a JS file *into* `core/js/` can silently delete someone's work.** The
moment it moves, its old `wwwroot/js/...` path becomes a generated, gitignored
build artefact. Any branch still modifying that path then merges as
**modify/delete** - and resolving that as a delete, which is the tempting
reading now that the path is generated, drops the branch's change with no
conflict marker, no build error and nothing in the diff to notice. Before
moving a file into `core/js/`, run `gh pr list` and check for open PRs
touching it; if there are any, fold their changes into the `core/` copy first
and say so in the commit. This happened on 2026-08-27 in the other app, with
`audio-playback.js` and PR #112.

C# under `core/` is excluded from this project's compile globs
(`<Compile Remove="core\**" />` and friends) and consumed as a project
reference. That exclusion is mandatory - the Web SDK globs `**/*.cs`.

### The workflow, and the step that gets forgotten

Authoring happens **inside `core/` in whichever app you are working in**. The
push up to Radio_Web_Control_Core is a **separate command**, and it is the one
that gets missed.

```powershell
./scripts/core-sync.ps1 -Check   # is anything owed upstream?
./scripts/core-sync.ps1 -Push    # send core/ commits up (pulls first)
./scripts/core-sync.ps1 -Pull    # bring the sibling's core work down
```

`-Push` refuses on a dirty tree, because `git subtree split` only sees
committed content and would silently leave uncommitted `core/` work behind.
The split walks the whole repo history and prints nothing for a couple of
minutes; it is not hung.

### Claude: your standing instructions

1. Before writing any new file, ask whether it is radio-agnostic. If it is, it
   goes under `core/`. Say so at the time rather than moving it later.
2. **Run `./scripts/core-sync.ps1 -Check` at the end of any session in which
   anything under `core/` changed.** Do not wait to be asked. The point of this
   rule is that Colin does not have to remember it.
3. If `-Check` reports work owed upstream, **push it without asking.** Colin
   gave standing authorisation for this on 2026-08-26, in as many words:
   *"I want ALL shared code to be in Radio_Web_Control_Core without me having
   to remember to specifically ask for that to happen."*
4. **This authorisation is narrow.** It covers pushing `core/` to
   Radio_Web_Control_Core and nothing else. Pushing this repo to its own
   origin, tagging, releasing and opening PRs all still need Colin's explicit
   word, as before.
5. After a successful `-Push`, run `-Pull` in Yaesu Web Control so both carry the
   same core. A push that only one app has is half a job.


## Architecture Rules

Before making changes, read `.claude/rules.md` and `.claude/project-overview.md`.
They are non-negotiable and override default behaviour.

---

## Build & Run

**Target:** .NET 10, x64 Windows only (`net10.0-windows`, `OutputType=WinExe`,
`UseWindowsForms=true` — a WinForms host process running Kestrel).

```bash
dotnet build Icom_Web_Control.csproj
dotnet run --project Icom_Web_Control.csproj      # WinForms host + Kestrel on http://0.0.0.0:8080
dotnet publish -c Release -r win-x64 --self-contained
```

There are **no automated tests** — `Tests/test-api.ps1` is a manual API-poking
script, not a suite. Verification is manual, in the browser at
`http://localhost:8080`, against the radio. When a change is protocol-level,
say so and let Colin bench-check it; do not report CI-V behaviour as verified
on the strength of a build succeeding.

User data lives in `%APPDATA%\MM5AGM\Icom Web Control\`:
`appsettings.user.json`, `radio_state.json`, `memories.json`,
`memory-banks.json`, `voice-phrases.json`, `logs\iwc-YYYYMMDD.log`.

---

## Release Process

Before releasing, bump the version in **all three** files — five sites in total:

- `Models/AppVersion.cs` — `Current` (and `ReleaseDate`)
- `installer.nsi` — `!define VERSION`
- `Icom_Web_Control.csproj` — `<Version>`, `<FileVersion>`, `<AssemblyVersion>`
  (the last two are four-part: `X.Y.Z.0`)

The csproj is easy to forget — it shipped on 1.0.2 through the whole v1.0.3
release. `finish-release.ps1` now refuses to run unless all five agree.

**Then update the documentation — mandatory, pre-releases included. See rules 13
and 14 in `.claude/rules.md`.**

- `README.md` — add the release-notes entry, and bump the per-release download
  badge **only for a full release**, never a pre-release.
- `USER_MANUAL.md` — bring every section the release touches in line with what
  the app now does, and re-capture any screenshot the change makes wrong.

Do not start the git steps until both documents are done. The script helps with
the mechanical half — it rewrites the version strings in both documents, and
the download badge on a full release — but it refuses to release at all unless
you have written the README release-notes entry yourself, and it can only
*warn* that the manual looks stale, never judge whether a section is right.

```powershell
git add -A
git commit -m "Release vX.Y.Z: ..."
git checkout main
git merge develop --no-ff -m "Release vX.Y.Z"
git tag vX.Y.Z
git checkout develop
git push origin develop; git push origin main; git push origin vX.Y.Z
gh release create vX.Y.Z --title "vX.Y.Z" --notes "See README.md for full release notes."
```

**The `gh release create` step is required** — the build workflow triggers on
`release: [created]`, not on a tag push.

`.\scripts\finish-release.ps1 -Version vX.Y.Z` does all of the above, with the
version and documentation checks in front of it, and stops before tagging if
anything is wrong. Prefer it to the raw commands: run by hand, the merge can
conflict and leave `main` unmerged while the tag and release go out anyway,
which is exactly how v1.0.3 first shipped v1.0.0's code.

---

## Backend Architecture

### The `IRadioController` seam — the one rule that matters most

`Services/IRadioController.cs` is the semantic seam IWC introduced and YWC
lacked. **Everything above it speaks radio concepts** — frequency in Hz, mode
as a display string ("USB", "CW-U"), S-meter units — and knows nothing about
the wire protocol. That includes `CatController` (touch UI), `IntentDispatcher`
(voice), `RigctldServer` (Hamlib/WSJT-X) and the Razor pages.

**Exactly one class below the seam emits bytes:**

- `CivRadioController` — the real one (~2,200 lines: connect, poll loop, all
  commands, scope assembly, pseudo-dual routing).
- `StubRadioController` — canned values, no hardware. Selected in `Program.cs`
  when no radio is configured, so the UI is developable without the rig.

The seam's **only** raw-bytes escape hatch is
`SendRawCommandAsync(IReadOnlyList<byte>)`, and it exists for user-defined
voice macros alone. It takes a command *body*; `CivRadioController` still
applies framing and addressing, so a shared phrase pack can neither forge a
radio address nor split a frame. **Anything else that wants to talk to the
radio gets a semantic member on the interface instead.**

### Service map

```
CivBusService (ICivClient) — owns the serial port
  ├─ binary reads via SerialPort.Read (never ReadExisting — it would corrupt
  │  the 0x00–0xFF payload as text)
  ├─ drops the CI-V bus echo (frames addressed To the radio, not To us)
  └─ DTR/RTS left de-asserted — on Icom's Serial A those lines can be PTT
       │
       ├─ CivFrameBuffer  — reassembles FE FE … FD frames from the byte stream
       ├─ CivFrame        — encode/decode, BCD helpers
       └─ CivScopeAssembler — concatenates 27 00 waveform segments into one
                              475-point sweep (BinCount = 475)

CivRadioController (IRadioController, IHostedService)
  ├─ poll loop: 150 ms (~6–7 Hz), backing off to 280 ms while the scope
  │  streams — the scope and the meters share one 19200-baud bus, so polling
  │  slower is how the trace stays smooth. Mode every 3rd loop, split every 4th.
  ├─ RadioStateService → SignalR (RadioHub) → browser
  └─ RadioStatePersistenceService → radio_state.json

RigctldServer (IHostedService)       — rigctld TCP for WSJT-X, Log4OM, etc.
WsjtxUdpService (IHostedService)     — WSJT-X UDP status/QSO feed
DxClusterService (IHostedService)    — cluster telnet feed → DX spot overlay
VoiceControlService (IHostedService) — SAPI recognition → IntentDispatcher
SystemTrayService (IHostedService)
```

There is **no SDR subsystem**. The spectrum comes from the radio's own scope
over CI-V. `Services/Sdr/`, `Workers/Yaesu_Sdr_Worker/` and the SDRplay P/Invoke
layer were dropped in the carve; only the *names* survive on the wire (see
below), because renaming them would have churned the frontend for nothing.

### SignalR

One hub, `RadioHub` at `/radioHub`. Messages:

| Message | Payload |
|---|---|
| `RadioStateUpdate` | `{ property, value }` — all CAT state; routed by `property` in `ws-update-pipeline.js` |
| `SpectrumUpdate` | `{ sdrId, bins, centreHz, spanHz, mode }` — `sdrId` is `"A"` or `"B"` |
| `SdrStatus` | `{ sdrId, status }` — `streaming` / `outofrange` / `disconnected` / `unconfigured` |
| `VoiceStatusUpdate` | voice recogniser state |
| `InitializationStatus` | start-up overlay text |

`sdrId` and `SdrStatus` are **inherited names for the scope panels**, not SDR
hardware. `RadioHub.OnConnectedAsync` replays a full state snapshot to the
joining client and asks the controller to re-announce scope status, so a second
tab or another computer gets a populated UI immediately.

The hub also owns app lifetime: the main page heartbeats every 5 s, and when
the last heartbeating tab closes the app stops after a 30 s grace period.

### Spectrum over CI-V, and the pseudo-dual receiver

The IC-7300 has **one receiver and one scope**. `CivRadioController` broadcasts
each 475-bin sweep as `SpectrumUpdate`:

- **Normal:** one sweep → `sdrId: "A"`.
- **Pseudo-dual on, same band:** the Centre-mode sweep feeds the panel for the
  operating VFO; a window cropped around the watch VFO feeds the other. No extra
  CI-V, no scope-mode churn, and the audio VFO never moves.
- **Watch VFO outside the sweep:** that panel reports `outofrange` ("Off-screen").
- **Cross-band peek (opt-in):** the loop briefly retunes the receiver, routes the
  whole sweep to the watch panel, and dips audio ~0.4 s.

Scope on/off is `27 10` / `27 11`; a manual scope-off is honoured across
reconnects (`_operatorScopeOff`) so the app never switches it back on unasked.

### Settings

`SettingsService` uses read-modify-write on `appsettings.user.json`.

**`Settings.cshtml.cs` gotcha:** `<Nullable>enable</Nullable>` puts an implicit
`[Required]` on every non-nullable string, which silently blocks saving an
*empty* value. Any setting where empty means "feature off" therefore needs a
`ModelState.Remove("Settings.X")` **before** `ModelState.IsValid`. Currently:
`DxClusterHost`, `DxClusterLoginCallsign`, `DxClusterPostLoginCommands`,
`TxToggleKey`. Add to that list when adding an optional string setting.

---

## Frontend Architecture

### Module map (`wwwroot/js/`)

```
websocket/
  ws-connection.js        — SignalR transport only
  ws-update-pipeline.js   — routes { property, value } to registered handlers

calibration/
  calibration-engine.js   — pure functions, no DOM, no side effects
  calibration-tables.js   — single source of truth for all scaling tables
  Ic7300Calibration.js

guages/                   — (sic: the folder name is misspelt; leave it alone
  gauge.js                   unless deliberately renaming — it is referenced by
  gaugeFactory.js            path in every importing module)
  meter-gauge.js
  meter-panel.js          — owns all meter DOM and canvas rendering
  smeter-history-panel.js
  update-engine.js        — performs gauge updates

orchestrators/
  Ic7300Meters.js         — wires websocket → calibration → MeterPanel; no logic of its own

sdr/
  sdr-spectrum-pipeline.js — SignalR transport for spectrum; no DOM
  spectrum-panel.js        — owns the spectrum canvas; DOM access intentional here

ui/
  site.js, meter-formatters.js, band-plan.js, a11y-labels.js, voice-control.js,
  memories.js, dx-spots-panel.js, freq-keyboard.js, calibration-editor.js,
  ic7300-if-width.js
```

### Value flow (strict — never bypass or reorder)

```
SignalR RadioStateUpdate
  → WsUpdatePipeline (route by property)
  → calibration-engine (pure transform)
  → Ic7300Meters (orchestrate)
  → MeterPanel.update()
  → gaugeFactory / update-engine
  → canvas
```

### Spectrum panels

`SdrSpectrumPipeline` opens its own SignalR connection and keeps per-`sdrId`
handler maps, dispatching `SpectrumUpdate` / `SdrStatus` to whichever
`SpectrumPanel` registered for that id. It also carries `FrequencyA`,
`FrequencyB`, `DxSpot`, `DxClusterStatus` and `DxAlert`.

`SpectrumPanel` takes a `vfo` ("A"/"B") in its constructor, which decides which
`/api/cat/frequency/{a|b}` endpoint click- and wheel-tune hit and which
`window.setMode('A'|'B', mode)` follows a click. Each panel tracks its own
`_vfoHz` from the matching frequency update.

`Pages/Index.cshtml` lays out `spectrumContainerA` / `spectrumContainerB` with
Stacked / Side-by-side and VFO A / VFO B / Both toggles (both persisted in
`localStorage`, both hidden unless two panels are showing). The outer container
hides itself when no scope source is configured.

**`Pages/Index.cshtml` is ~4,500 lines** — markup plus the page's own script
block. Most spectrum-control wiring lives there rather than in a module. That is
a known wart; prefer extending an existing block over adding a new global.

### Razor pages

`Index` (main control panel; `RadioState` exposes `RadioStateService` for
server-rendered initial values), `Settings`, `Diagnostics`, `Labels`
(accessibility label overrides), `Calibration`, `About`, `Memories`.

---

## Key Domain Facts

- **IC-7300 MkII:** single receiver, HF + 6 m (+ 4 m in EU). Up to **100 W**
  (25 W AM). Frequencies are **always Hz** in this codebase.
- **Scope:** CI-V `27 00` waveform, **475 points** per sweep, assembled from
  segments by `CivScopeAssembler`. Span is set with `27 15`; the eight UI
  buttons are ± half-widths, ±2.5 kHz … ±500 kHz.
- **CI-V is a bus.** The radio echoes our own transmission back before replying;
  echo frames are addressed to the radio, real replies to us.
- **Trusted voice-macro command set:** `05 06 07 0F 11 14 16 17 1A 1C 25 26 27`.
  `18` (power) is deliberately excluded — over USB CI-V a power-off drops the
  serial port with it, leaving nothing able to switch the radio back on.
  Advanced Mode bypasses the set entirely.
- **S-meter:** 0–255 raw → S0 to S9+60 dB via the calibration tables.
- **Poll rate:** ~6–7 Hz, dropping to ~3–4 Hz while the scope streams.

---

## Known dead / stale code

Do not treat these as authoritative when reading the codebase:

- `Pages/Labels.cshtml` advertises **28 keys no element carries** — the whole
  `controls.*` group (TX power, mic gain, AF gain, IF shift, AGC, NR, NB,
  notch, and the Yaesu-only IPO / roofing / antenna entries), plus
  `vfo.*.up/down/mode`, `meters.temp` and `spectrum.display`. Labels are bound
  strictly by `data-a11y-key`, and those elements have none, so editing those
  rows does nothing. The real controls need the attribute adding before the
  rows mean anything — worth doing, since voice/screen-reader support is a
  release blocker here.
- `bin/`, `obj/` and `Workers/Yaesu_Sdr_Worker/obj/` still hold
  `Yaesu_Web_Control.*` artefacts from before the rebrand. Git-ignored;
  `dotnet clean` clears them.

Cleared in the 2026-08-04 sweep, so do **not** go looking for them: the VC Tune
preselector UI and its `site.js` driver (a Yaesu-only control whose API
endpoints the carve had already removed), `scripts/collect-soapy-deps.ps1`,
`Yaesu_Web_Control.slnx`, the "No SDR" / "No SoapySDR" badge strings, and the
root `Plan.md` / `docs/VoiceControl/v1-plan.md` (both pure YWC planning docs
full of Yaesu CAT commands).

Cleared in the follow-up `CatController` sweep, likewise gone for good:
`ICatClient` / `NullCatClient` and the `_catClient` field (every call site is
now on the seam), `EnsureConnectedAsync` / `GetMainVfoAsync`, the contour and
IF-shift endpoints, the `/api/cat/ifwidth` pair (superseded by
`/api/radio/ifwidth`), the per-VFO antenna selector end-to-end (markup,
`setAntenna`, `RadioCapabilities.HasAntennaSelector`), and the dead
roofing-filter JS. **`CatController.cs` no longer contains a Yaesu code path.**
Where it still says "Yaesu", it is explaining why the Icom code differs.

Yaesu residue does survive elsewhere in comments and inherited names —
`CalibrationStorage.cs`, `RadioStateService.cs` NR notes, `AppMemory.RoofingCode`,
the `site.js` header and several FTdx10/FTdx101 anecdotes, plus the inherited
`ipo` route name (it drives the IC-7300 preamp) and `sdrId` on the hub. Names on
the wire are deliberate; the comments are simply unswept.

`Services/RadioCapabilities.cs` is **live**, not dead — `Index.cshtml` reads
`IsSingleReceiver` from it, and `IntentDispatcher` and `CatController` both
route per-VFO writes through `VfoIsB`. (`VfoP1` is gone: it returned the P1
*digit* of a Yaesu CAT command, and its last caller was the IF-width voice
nudge, which now goes through the seam.)
Every method returns the same answer for both supported models; that is
deliberate, so the assumption is stated in one place.
