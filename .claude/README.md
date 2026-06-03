# Claude AI Configuration for Yaesu Web Control

This directory contains AI assistant configuration files for Claude when working with the Yaesu Web Control project.

## Files

- **`boot-prompt.md`** - Initial prompt that Claude should follow when starting a session
- **`rules.md`** - Strict architectural rules and subsystem boundaries
- **`project-overview.md`** - High-level project description and domain concepts

## Usage

### For Claude Extensions in VS Code

Depending on which Claude extension you're using, you may need to configure it to read these files:

#### Cline / Roo-Cline
1. Open extension settings
2. Add `.claude/boot-prompt.md` to custom instructions
3. Optionally reference `.claude/rules.md` and `.claude/project-overview.md`

#### Continue
1. Open Continue settings
2. Add custom system message pointing to these files
3. Or paste contents into system message

### Manual Loading

If your extension doesn't auto-load, you can manually tell Claude:

```
Please read and follow the instructions in:
- .claude/boot-prompt.md
- .claude/rules.md
- .claude/project-overview.md
```

## Architecture Summary

This project uses strict subsystem boundaries:

- **Calibration Engine** - Pure functions only
- **WebSocket Subsystem** - Transport/routing only
- **Meter Subsystem** - UI/DOM access ONLY here
- **Orchestrator** - Wires subsystems, no subsystem logic
- **CAT/Serial/Queue** - Isolated, no UI/WebSocket

All code must follow the value pipeline:
```
WebSocket → WsUpdatePipeline → calibration-engine → FTdx101Meters → 
MeterPanel.update() → gaugeFactory/update-engine → canvas
```

## Maintenance

These files are version controlled. Edit them as needed to keep Claude aligned with project architecture.
