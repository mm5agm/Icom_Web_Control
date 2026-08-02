# Yaesu Web Control — Strict Architectural Rules (Authoritative Specification)
# Claude must follow these rules for ALL code in this repository.

---

# 1. Subsystem Boundaries (Non‑Negotiable)

The Yaesu Web Control is composed of strict, isolated subsystems.
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
- WsConnection handles transport only.
- WsUpdatePipeline handles message routing only.
- No DOM access.
- No gauge updates.
- No formatting.
- No calibration logic.
- No UI logic.
- No global state.

## 3. Meter Subsystem
- MeterPanel owns all UI meter rendering.
- gaugeFactory creates all gauges.
- update-engine performs gauge updates.
- meter-formatters handles UI text formatting.
- DOM access allowed ONLY inside UI modules.
- No calibration logic.
- No WebSocket logic.

## 4. Orchestrator Subsystem
- FTdx101Meters is the only orchestrator.
- It wires together WebSocket → pipeline → MeterPanel.
- It must not contain:
  - calibration logic
  - gauge creation logic
  - formatting logic
  - DOM manipulation
- It may call MeterPanel.update() only.

## 5. CAT / Serial / Queue Subsystem
- Serial timing lives ONLY in the serial layer.
- Queueing logic lives ONLY in the queue layer.
- Decoding logic lives ONLY in the decoding layer.
- No UI logic.
- No DOM access.
- No WebSocket logic.
- No calibration logic.

## 6. UI/State Subsystem
- The ONLY subsystem allowed to touch the DOM.
- All DOM access must be isolated to UI modules.
- No calibration logic.
- No decoding logic.
- No WebSocket logic.
- No serial/queue logic.

---

# 2. Value Flow Rules (Strict)

All meter values must follow this exact pipeline:

WebSocket raw payload
    ↓
WsUpdatePipeline (routing only)
    ↓
calibration-engine (pure functions)
    ↓
FTdx101Meters orchestrator
    ↓
MeterPanel.update()
    ↓
gaugeFactory + update-engine
    ↓
Canvas rendering

Claude must never generate code that bypasses or rearranges this flow.

---

# 3. DOM Access Rules (Strict)

## Allowed
- Only inside UI/state subsystem modules:
  - MeterPanel
  - gaugeFactory
  - overlays
  - UI helpers

## Forbidden Everywhere Else
- calibration engine
- WebSocket subsystem
- decoding subsystem
- serial/queue subsystem
- orchestrator
- helpers
- logic modules

Claude must refuse to generate DOM access in forbidden layers.

---

# 4. Gauge Rules (Strict)

## Allowed
- Gauges must be created ONLY through gaugeFactory.
- Gauge updates must go through update-engine.
- MeterPanel owns all gauge instances.

## Forbidden
- Direct RadialGauge creation anywhere else.
- Inline gauge configuration.
- Gauge logic inside calibration, WebSocket, or orchestrator layers.

---

# 5. Formatting Rules (Strict)

## Allowed
- All UI text formatting must live in meter-formatters.js.

## Forbidden
- Formatting logic inside calibration engine.
- Formatting logic inside WebSocket pipeline.
- Formatting logic inside orchestrator.
- Formatting logic inside gaugeFactory.

---

# 6. Naming Rules (Strict)

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

# 7. Empirical Behaviour Rules (Strict)

- Empirical findings (timing, scaling, decoding quirks) must be preserved.
- They must live in the correct subsystem.
- UI/state must never contain empirical logic.
- Calibration tables must remain the single source of truth.
- Decoding quirks must remain in the decoding layer.

---

# 8. Folder Structure Rules (Strict)

Claude must maintain this structure:

/websocket
    ws-connection.js
    ws-update-pipeline.js

/calibration
    calibration-engine.js
    calibration-tables.js

/ui
    meter-panel.js
    gaugeFactory.js
    update-engine.js
    meter-formatters.js
    overlays.js

/orchestrators
    FTdx101Meters.js

/serial
/queue
/decoding

Claude must:
- create new modules in the correct folder
- refuse to place logic in the wrong folder
- reorganise helpers when boundaries evolve

---

# 9. Global State Rules (Strict)

## Forbidden
- Global variables
- Global FTdx101Meters instance
- Global gauge instances
- Global calibration tables
- Global WebSocket references

## Allowed
- Local orchestrator instance created on page load.

---

# 10. Refactoring Rules (Strict)

When Claude refactors code, it must:
- preserve subsystem boundaries
- eliminate duplication
- correct drift
- update comments immediately
- maintain architectural purity
- never introduce cross‑layer leakage
- never weaken the architecture

---

# 11. Output Style Rules

Claude must:
- use clear, natural language
- avoid boilerplate
- explain architectural intent when rewriting
- maintain consistency with these rules

---

# 12. Scope

These rules apply to:
- all code
- all refactors
- all comments
- all documentation
- all UI/state logic
- all WebSocket logic
- all calibration logic
- all decoding logic
- all serial/queue logic
- all architectural decisions

Claude must follow these rules for every change in this repository.

---

# 13. Release Documentation (Non-Negotiable)

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
   **full** release only — never a pre-release. That is the same call rule 14
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

---

# 14. In-App Update Notifications — Full Releases Only (Non-Negotiable)

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
