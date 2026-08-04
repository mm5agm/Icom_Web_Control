# Claude configuration for Icom Web Control

This directory holds the AI-assistant configuration for IWC.

## Files

| File | What it is |
|---|---|
| `rules.md` | **The enforceable specification.** Strict subsystem boundaries, the radio seam, and the non-negotiable release / voice / update-banner rules. |
| `project-overview.md` | Intent and domain background — what the app is for, why the architecture is shaped this way, what CI-V concepts matter. |
| `settings.json` | Claude Code permissions for this project. |

The repository-root **`CLAUDE.md`** is the working map: build and run, service
layout, SignalR envelope, frontend module map, domain facts, and a list of known
dead/stale code. Claude Code loads it automatically; it points at the two files
above.

## Reading order

1. `CLAUDE.md` — where everything is.
2. `.claude/rules.md` — what you may and may not do.
3. `.claude/project-overview.md` — why.
4. `docs/design/iwc-clone-split-plan.md` — the roadmap, including the phases
   still open (CW decode, skins, settings backup over CI-V).

## Architecture in one screen

**One seam.** `Services/IRadioController.cs` separates radio *semantics* from
the CI-V *wire protocol*. Above it: controllers, voice, rigctld, Razor pages —
all speaking Hz and mode names. Below it: exactly one class emits bytes
(`CivRadioController`, or `StubRadioController` which emits none).

**Strict frontend boundaries:**

- Calibration engine — pure functions, no DOM
- WebSocket subsystem — transport and routing only
- Meter subsystem — the only place gauges and meter DOM live
- Orchestrator — wires them together, contains no logic of its own
- `SpectrumPanel` — owns its canvas; DOM access intentional there

**The value pipeline, never bypassed or reordered:**

```
SignalR RadioStateUpdate → WsUpdatePipeline → calibration-engine →
Ic7300Meters → MeterPanel.update() → gaugeFactory/update-engine → canvas
```

## Maintenance

These files are version controlled, and they go stale silently — nothing fails
when a rule names a file that has been renamed. If you rename a module, move a
responsibility, or drop a subsystem, update `rules.md` and `CLAUDE.md` in the
same commit. A rule that cannot be satisfied is worse than no rule.
