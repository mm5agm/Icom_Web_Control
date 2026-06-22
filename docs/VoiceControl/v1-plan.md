# Voice Control v1 — Plan

**Status:** PARKED.
**Trigger to start:** Jacek SP3L confirms all his open #38-#47 bugs are fixed in v2.3.9-pre2 (or any subsequent pre-release).
**Branch:** `feature/voice-control` (created off develop, currently empty).
**Owner:** Colin (MM5AGM), implementation by Claude.
**Replaces:** the Alexa voice control work on `feature/alexa-voice-control`. Alexa branch stays parked while v1 voice is built; deleted when v1 voice is confirmed working end-to-end with at least Colin + one other tester.

---

## Goal

Replace cloud-routed Alexa voice control with a Windows-local, in-process system using `System.Speech.Recognition` (SAPI 5.x). User speaks commands via the PC microphone, YWC recognises them locally and executes against the connected radio. Zero external dependencies — no Cloudflare, no Amazon Developer account, no port forwarding, no recurring setup chores.

---

## Why this replaces Alexa

| Concern | Alexa | In-app SAPI |
|---|---|---|
| Setup time | 30-60 min (Cloudflare + Skill + DNS) | Click "Enable" in Settings |
| Ongoing cost | £8/year for domain (most users) | £0 |
| External services | Amazon, Cloudflare | None |
| Latency | Echo → Amazon → Cloudflare → PC | < 100 ms local |
| Privacy | Audio routed via Amazon | All on-device |
| Failure modes | Tunnel drop, cert expiry, Skill quarantine, Amazon outage | Mic disconnect (one cause) |

Three years from now if Amazon deprecates an API in the Alexa Skill Kit, the in-app implementation keeps working unchanged. That's the long-term argument.

---

## Architecture decisions (locked)

1. **In-process SAPI.** `System.Speech.Recognition` runs inside the YWC Windows process. No worker, no IPC. The browser is purely UI; mic is captured by YWC via `SetInputToDefaultAudioDevice()`.
2. **Push-to-talk on-screen button.** No keyboard binding in v1 (defer to v1.1 with user-configurable hotkey via a "press the key you want" capture).
3. **VFO A is the default target** for every voice command. FTdx10 / FT-710 are single-receiver anyway. VFO B targeting is a v2 concern.
4. **No TX commands in v1.** A live mic shouting "transmit" near speaker output is a feedback hazard. Defer with explicit confirm-step.
5. **Voice control OFF by default.** No surprise mic-permission grabs for users who didn't ask for it. Mic icon hidden in the navbar until they tick "Enable voice control" in Settings.
6. **Single language: en-GB.** User-grammars folder convention shipped (so the v1.1/v2 multi-language work has a place to drop files), but Settings only shows "English (UK)" as a non-interactive label.
7. **Lives on `feature/voice-control` branch off develop.** Bundled into a pre-release for Colin + Yuri + Jacek to test, then merged back when accepted.

---

## V1 command set — six intents

| Spoken (Scots / standard English variants accepted) | Intent | Notes |
|---|---|---|
| "set frequency to fourteen point zero seven four" / "tune to ..." | `SetFrequency` (Hz) | Numbers spoken as digits + "point" |
| "go to twenty metres" / "twenty metre band" | `SetBand` (metre band) | Uses existing band-switch infrastructure (respects per-band profile) |
| "set mode USB / LSB / CW / AM / FM / data / digital" | `SetMode` (mode string) | Letters spoken phonetically: "U S B" not "usb" |
| "swap VFO" / "swap A and B" / "switch VFO" | `SwapVFO` | Single CAT command |
| "tune up" / "step up" / "frequency up" | `NudgeFrequency` (+1 of selected digit) | Reuses the digit-step infrastructure from the keyboard arrow work |
| "tune down" / "step down" / "frequency down" | `NudgeFrequency` (-1 of selected digit) | Same |

Six intents. Anything outside this list is deferred to v2 or later.

---

## File layout

```
Services/Voice/
  VoiceControlService.cs            -- IHostedService, owns the SpeechRecognitionEngine
  IntentDispatcher.cs               -- semantic intent -> existing CatController action
  VoiceStatus.cs                    -- enum: Idle / Listening / Heard / Executing / Error

Controllers/VoiceController.cs       -- POST /api/voice/start, /api/voice/stop, GET /api/voice/status

Grammars/                            -- ships baked into the install dir
  Commands.en-GB.srgs                -- v1 grammar
  Commands.template.srgs             -- empty template with comments, for future contributors
  README.md                          -- "how to contribute a language" — placeholder for v2

Pages/Index.cshtml                   -- adds the PTT mic button in the navbar
Pages/Settings.cshtml                -- adds Voice Control collapsible section

%APPDATA%\MM5AGM\Yaesu Web Control\Grammars\
                                     -- user drop-in folder (created on first run, empty in v1)
                                     -- the multi-language picker code in v1.1/v2 reads from here
```

---

## Build steps (in order, dependencies respected)

### Step 1 — backend skeleton (~3 h)
- `VoiceControlService` as `IHostedService`. On start: check SAPI installed for en-GB via `SpeechRecognitionEngine.InstalledRecognizers()`, load `Grammars/Commands.en-GB.srgs`, create `SpeechRecognitionEngine`, hook `SpeechRecognized` event. **Do not call `RecognizeAsync`** — that's gated by the PTT button.
- API endpoints under `/api/voice/`:
  - `POST /api/voice/start` — begins `RecognizeAsync(RecognizeMode.Multiple)`
  - `POST /api/voice/stop` — calls `RecognizeAsyncStop()`
  - `GET /api/voice/status` — current state
- SignalR broadcast: `VoiceStatus { state, lastHeard, lastIntent, executedOk }`.

### Step 2 — grammar file (~3 h)
- Write `Commands.en-GB.srgs` covering the six intents with Scots variants alongside standard EN.
- Number rule: digits 0-9 + "point" + repeat patterns rather than enumerating 14,074. Test that "fourteen point zero seven four" recognises and `out.hz = 14074000`.
- Mode rule: alphabet-style spelled-out letters ("U S B", "L S B") — SAPI struggles with "USB" as one syllable.

### Step 3 — intent dispatcher (~2 h)
- `IntentDispatcher.Dispatch(string intent, IDictionary<string,object> args)` — maps to existing methods on `CatController` or directly to `CatMultiplexerService`. Reuse, don't reimplement.
- `NudgeFrequency`: read the currently-selected digit from the same state as the keyboard arrow-key work.
- `SetBand`: reuse the existing band-switch endpoint so per-band profile prefs are respected.

### Step 4 — frontend mic button (~2 h)
- Mic icon button in the navbar, right of the brand.
- `mousedown` / `touchstart` → POST `/api/voice/start`; `mouseup` / `touchend` → POST `/api/voice/stop`.
- Visual states: grey/idle, blue/listening, green/heard intent (briefly), red/error.
- Last-recognised phrase shown as small grey text below the button — sanity check on what SAPI heard.
- Hidden entirely when voice control disabled in Settings.

### Step 5 — Settings section (~1 h)
- New collapsible "Voice Control" section.
- Master enable/disable toggle (off by default).
- Language: "English (UK)" greyed dropdown for v1; hint "more languages coming in v2".
- Diagnostics: whether `en-GB` SAPI is installed, mic device name, last error.
- "Open user grammars folder" button — opens `%APPDATA%\MM5AGM\Yaesu Web Control\Grammars\` in Explorer (no-op in v1 since folder is empty, but discoverable for v2 contributors).

### Step 6 — end-to-end testing (~2 h)
- Test each of the six intents with the USB mic Colin has now.
- Test error paths: no en-GB SAPI installed, no mic, recogniser mid-utterance when stop pressed, grammar file missing or malformed.
- PTT button responsiveness — no hangs on rapid press-release.

**Total estimate: ~13 h spread across 1.5 working days.**

---

## Out of scope for v1 (deliberate)

- Multi-language UI (folder convention shipped, picker UX defers to v2)
- Translation wizard
- TX / RX voice commands
- Hotword / always-listening
- TTS feedback ("frequency set to 14.074")
- Memory recall / store commands
- Status query commands ("what's the frequency?")
- Mic device picker (use Windows default)
- Confidence threshold tuning UI
- Keyboard PTT shortcut (v1.1)

---

## v1.1 candidates (post-v1, in priority order)

1. **Keyboard PTT hotkey** with capture-the-key UI in Settings. User picks whatever feels right on their keyboard.
2. **Mic device picker** in Settings. List from `NAudio.CoreAudioApi.MMDeviceEnumerator`, store the choice as a Windows endpoint ID.
3. **Confidence threshold slider** + "show recogniser hypotheses" diagnostic toggle.

## v2 candidates

1. **Multi-language support.** Settings dropdown populated from the bundled `Grammars/` folder + the user-drop-in folder. Cross-checked against installed SAPI recognisers; un-installable ones greyed out with deep-link to `ms-settings:speech`.
2. **TX commands with confirm-step.** "Transmit" → mic enters confirm mode for 3 seconds, second "transmit" within window actually keys up. Cancel with anything else.
3. **TTS feedback.** Confirmation phrases via `SpeechSynthesizer`. Configurable per-command (some users prefer silent execution).
4. **Status queries.** "What's the frequency", "what's the mode", "rig status" — TTS responses.
5. **Memory recall / store.** "Recall memory five", "store to memory five".

---

## When work starts

When Jacek closes (or otherwise marks as fixed) all his open issues — i.e. `gh issue list --assignee SP3L-Jacek --state open` returns empty or only contains v2-deferred items:

1. Read this plan again, confirm nothing has drifted.
2. Set up the todo list from the six build steps.
3. `git checkout feature/voice-control` (the branch already exists, parked at the same commit as develop).
4. Step 1 first. Build steps are dependency-ordered; don't parallelise across steps within v1 — the wedge is small enough.
5. Pre-release as `v2.4.0-pre1` when steps 1-6 are done, post in Discussions for Colin / Yuri / Jacek to test.

---

## Open questions for when work starts

- Final wording for the mic button tooltip ("Hold to speak" / "Press and hold to speak" / "Voice command")?
- Mic button icon — Bootstrap's `bi-mic-fill` or a custom one? (Bootstrap default is fine.)
- Should the "last heard" text persist after the button releases, or fade after a few seconds? (Lean: persist until next utterance — gives the user a record of misrecognitions.)

Not blocking the plan, but worth deciding before Step 4 of the build.
