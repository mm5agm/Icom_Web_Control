# Icom Web Control — Strict Architectural Rules (Authoritative Specification)
# Claude must follow these rules for ALL code in this repository.

---

# 1. Subsystem Boundaries (Non-Negotiable)

Icom Web Control is composed of strict, isolated subsystems.
No subsystem may contain logic belonging to another.

## 1. Calibration Engine (Pure Logic Only)
- Pure functions only.
- No DOM access.
- No UI logic.
- No WebSocket logic.
- No gauge logic.
- No formatting logic.
- No side effects.
- Single source of truth for calibration tables.
- All calibration must follow: raw → calibrated → UI.

## 2. WebSocket Subsystem
- `WsConnection` handles transport only.
- `WsUpdatePipeline` handles message routing only.
- No DOM access.
- No gauge updates.
- No formatting.
- No calibration logic.
- No UI logic.
- No global state.

## 3. Meter Subsystem
- `MeterPanel` owns all UI meter rendering.
- `gaugeFactory` creates all gauges.
- `update-engine` performs gauge updates.
- `meter-formatters` handles UI text formatting.
- DOM access allowed ONLY inside UI modules.
- No calibration logic.
- No WebSocket logic.

## 4. Orchestrator Subsystem
- `Ic7300Meters` is the only orchestrator.
- It wires together WebSocket → pipeline → MeterPanel.
- It must not contain:
  - calibration logic
  - gauge creation logic
  - formatting logic
  - DOM manipulation
- It may call `MeterPanel.update()` only.

## 5. Radio / CI-V / Serial Subsystem
- **`IRadioController` is the seam.** Above it, everything speaks radio
  concepts — frequency in Hz, mode as a display string, S-meter units — and
  knows nothing about the wire protocol.
- **Exactly one class below the seam emits bytes** (`CivRadioController`;
  `StubRadioController` emits none and fakes the same semantics).
- Serial timing lives ONLY in `CivBusService`.
- Frame assembly lives ONLY in `CivFrameBuffer` / `CivFrame`.
- Scope segment reassembly lives ONLY in `CivScopeAssembler`.
- No UI logic. No DOM access. No SignalR logic. No calibration logic.

## 6. UI/State Subsystem
- The ONLY subsystem allowed to touch the DOM.
- All DOM access must be isolated to UI modules.
- No calibration logic.
- No decoding logic.
- No WebSocket logic.
- No serial/CI-V logic.

---

# 2. Value Flow Rules (Strict)

All meter values must follow this exact pipeline:

SignalR `RadioStateUpdate`
    ↓
WsUpdatePipeline (routing only)
    ↓
calibration-engine (pure functions)
    ↓
Ic7300Meters orchestrator
    ↓
MeterPanel.update()
    ↓
gaugeFactory + update-engine
    ↓
Canvas rendering

Claude must never generate code that bypasses or rearranges this flow.

---

# 3. The Radio Seam (Strict)

This is the rule IWC exists to enforce, and the one YWC lacked.

## Forbidden
- Any CI-V byte, frame, address or command code appearing **above**
  `IRadioController` — in a controller, a Razor page, `IntentDispatcher`,
  `RigctldServer`, or any JS.
- Any new raw-bytes path through the seam.
- Reaching around the seam to `CivBusService` from application code.

## Allowed
- Adding a **semantic** member to `IRadioController` and implementing it in
  both `CivRadioController` and `StubRadioController`.
- `SendRawCommandAsync(IReadOnlyList<byte>)` — the single, deliberate escape
  hatch, for user-defined **voice macros only**. It takes a command *body*;
  framing and addressing stay with the controller so a shared phrase pack can
  neither forge an address nor split a frame. Nothing else may call it.

When a new feature needs the radio, the answer is a new semantic member, not
a raw send.

---

# 4. DOM Access Rules (Strict)

## Allowed
- Only inside UI/state subsystem modules:
  - `MeterPanel`
  - `gaugeFactory`
  - `SpectrumPanel` (intentional — it owns its canvas)
  - `ui/*` modules
  - the script block in `Pages/Index.cshtml`

## Forbidden Everywhere Else
- calibration engine
- WebSocket subsystem
- spectrum pipeline (`sdr-spectrum-pipeline.js` is transport only)
- orchestrator
- helpers
- logic modules

Claude must refuse to generate DOM access in forbidden layers.

---

# 5. Gauge Rules (Strict)

## Allowed
- Gauges must be created ONLY through `gaugeFactory` (`createGauge` and its
  per-meter helpers). New meter types are registered there.
- `gauge.js` is the ONLY place that constructs the underlying canvas-gauges
  `RadialGauge` — it is the base class every meter extends, and meter classes
  supply configuration only.
- Gauge updates must go through `update-engine`.
- `MeterPanel` owns all gauge instances.

## Forbidden
- `new RadialGauge` anywhere but `gauge.js`.
- Constructing a meter class directly instead of going through `gaugeFactory`.
- Inline gauge configuration.
- Layout logic outside `gauge.js`.
- Gauge logic inside calibration, WebSocket, or orchestrator layers.

---

# 6. Formatting Rules (Strict)

## Allowed
- All UI text formatting must live in `meter-formatters.js`.

## Forbidden
- Formatting logic inside the calibration engine.
- Formatting logic inside the WebSocket pipeline.
- Formatting logic inside the orchestrator.
- Formatting logic inside `gaugeFactory`.

---

# 7. Naming Rules (Strict)

## PascalCase
For architectural units:
- Classes
- Services
- Subsystems
- Modules
- Namespaces
- UI components

## camelCase
For flow-level identifiers:
- variables
- parameters
- internal helpers
- temporary state

## Forbidden
- PascalCase for flow-level identifiers
- camelCase for architectural units
- ambiguous names
- names that no longer reflect behaviour

---

# 8. Empirical Behaviour Rules (Strict)

- Empirical findings (timing, scaling, decoding quirks) must be preserved.
- They must live in the correct subsystem.
- UI/state must never contain empirical logic.
- Calibration tables must remain the single source of truth.
- Decoding quirks must remain in the decoding layer.

Several comments in this codebase record a *verified-on-the-radio* fact — bus
echo behaviour, DTR/RTS being PTT lines on Serial A, the `18` power-command
exclusion, poll-rate backoff while the scope streams. **Do not delete or
"simplify" a comment that records why something is the way it is.** If the code
changes, update the reasoning; don't drop it.

---

# 9. Folder Structure Rules (Strict)

Claude must maintain this structure:

```
wwwroot/js/
  websocket/      ws-connection.js, ws-update-pipeline.js
  calibration/    calibration-engine.js, calibration-tables.js, Ic7300Calibration.js
  guages/         gauge.js, gaugeFactory.js, meter-gauge.js, meter-panel.js,
                  smeter-history-panel.js, update-engine.js
  orchestrators/  Ic7300Meters.js
  sdr/            sdr-spectrum-pipeline.js, spectrum-panel.js
  ui/             site.js, meter-formatters.js, band-plan.js, a11y-labels.js,
                  voice-control.js, memories.js, dx-spots-panel.js,
                  freq-keyboard.js, calibration-editor.js, ic7300-if-width.js

Services/
  Civ/            CivBusService.cs, CivFrame.cs, CivFrameBuffer.cs,
                  CivMacroCodec.cs, CivScopeAssembler.cs, ICivClient.cs
  Voice/          VoiceControlService.cs, IntentDispatcher.cs, VoiceGrammar.cs,
                  VoicePhraseStore.cs, VoicePhraseValidator.cs, VoiceTtsService.cs,
                  VoiceHelpBuilder.cs, MicrophoneCapture.cs, AudioOutput.cs,
                  VoiceStatus.cs
  (root)          IRadioController.cs, CivRadioController.cs, StubRadioController.cs,
                  RadioStateService.cs, SettingsService.cs, RigctldServer.cs, …
```

Two names are historical and **must not be "corrected" casually**:

- `wwwroot/js/guages/` is misspelt. Every importing module references it by
  path; renaming is a deliberate, whole-repo change, not a drive-by fix.
- `wwwroot/js/sdr/` and the `sdrId` / `SdrStatus` wire names survive from the
  YWC clone. There is no SDR in IWC — these identify the **scope panels**.

Claude must:
- create new modules in the correct folder
- refuse to place logic in the wrong folder
- reorganise helpers when boundaries evolve

---

# 10. Global State Rules (Strict)

## Forbidden
- Global variables
- Global orchestrator instance
- Global gauge instances
- Global calibration tables
- Global WebSocket references

## Allowed
- Local orchestrator instance created on page load.
- The small set of deliberate `window.*` hooks the Razor page already exports
  (`window.setMode`, `window.onActiveVfoChanged`, `window.__markActiveSpan`).
  Extend an existing one rather than adding another.

---

# 11. Refactoring Rules (Strict)

When Claude refactors code, it must:
- preserve subsystem boundaries
- eliminate duplication
- correct drift
- update comments immediately
- maintain architectural purity
- never introduce cross-layer leakage
- never weaken the architecture

---

# 12. Output Style Rules

Claude must:
- use clear, natural language
- avoid boilerplate
- explain architectural intent when rewriting
- maintain consistency with these rules

---

# 13. Scope

These rules apply to:
- all code
- all refactors
- all comments
- all documentation
- all UI/state logic
- all SignalR logic
- all calibration logic
- all CI-V / decoding logic
- all serial logic
- all architectural decisions

Claude must follow these rules for every change in this repository.

---

# 14. Release Documentation (Non-Negotiable)

**Before any release or pre-release, `README.md` and `USER_MANUAL.md` must both
be updated. Every time. No exceptions, and no "if needed".**

A release is any of: bumping `Models/AppVersion.cs` or `installer.nsi`, tagging,
merging to `main` for a release, or running `gh release create` /
`scripts/finish-release.ps1`. **Pre-releases count** — operators install those
and read the same two documents.

Claude must, before the first commit of the release:

1. **`README.md`** — add the release-notes entry for the new version, and bump
   the per-release download badge near the top **if this is a full release**.
   The badge is the front page's "get this one" button, so it tracks the newest
   **full** release only — never a pre-release. That is the same call rule 15
   makes for the in-app banner, for the same reason: an operator who lands on
   the repo should be pointed at the tested build, and reach a pre-release only
   by going to the releases page on purpose. So a pre-release bumps
   `AppVersion.cs`, `installer.nsi` and the release notes, and leaves the badge
   pointing at the last full release. Promoting a pre-release to full
   (`gh release edit vX.Y.Z --prerelease=false`) is what finally moves it.
2. **`USER_MANUAL.md`** — bring every section the release touches in line with
   what the app now does: new or changed controls, renamed buttons, altered
   behaviour, new settings, new voice commands. Re-capture any screenshot the
   change makes wrong.
3. Tell the operator which sections of each document changed, and why.

If a release genuinely changes nothing user-visible, say so in one line and
still add the README release-notes entry — that entry is never optional.

Claude must not start the release steps until both documents are updated, and
must never defer this to "after the release". A shipped version whose manual
describes the previous one is a defect.

**A screenshot is a spec check, not decoration.** Embedding one has twice caught
prose that had drifted from the app (the nav label in §10, the whole span /
slider set in §5.4). When a section gains an image, read the image against the
text before committing.

---

# 15. In-App Update Notifications — Full Releases Only (Non-Negotiable)

**The in-app update banner must only ever announce a full release. It must never
announce a pre-release or a draft.**

Pre-releases are opt-in: an operator who wants to test one goes to the GitHub
releases page and downloads it deliberately. Interrupting someone who is
operating the radio to push a less-tested build at them is the wrong trade.

Concretely, the update check (`wwwroot/js/ui/site.js`, `_checkForUpdate`) must:

- fetch `…/releases/latest` — **never** the `/releases` list endpoint, which
  includes pre-releases and drafts;
- bail out if the payload comes back with `prerelease` or `draft` set;
- never gain a setting, flag or "advanced" opt-in that surfaces pre-releases in
  the banner.

This applies to any future notification channel added to the app — a tray
balloon, a Settings "check now" button, an About-page version line. Same rule:
full releases only.

---

# 16. Voice Control Is Not Optional (Non-Negotiable)

Voice control exists so partially-sighted operators can use the radio. It is a
**required feature**, and that has consequences:

- A change that breaks recognition, the grammar, or the spoken feedback is a
  release blocker, not a nice-to-have.
- Every new radio control added to the touch UI should be considered for a
  voice intent at the same time. Say so if you skip it.
- **Never let the app announce success for something it did not do.** An intent
  that is recognised but inert must say so. This has been a real bug twice.
- Accessibility labels (`Pages/Labels.cshtml`) must gain an entry whenever a new
  control gets a `data-a11y-key`. A key with no registry entry is a control a
  screen-reader user cannot relabel.
