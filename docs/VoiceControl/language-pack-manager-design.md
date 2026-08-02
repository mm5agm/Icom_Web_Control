# Voice Control — Language Pack Manager: Architecture & UX Specification

**Status:** design spec, not yet implemented.
**Scope:** extends the existing SAPI-based voice control (`docs/VoiceControl/v1-plan.md`, shipped in v2.4.0-pre1) from a single hardcoded `en-GB` grammar into a user-manageable, multi-language, extensible command system.
**Format:** architecture + UX only. No C# in this document — see the "Suggested build order" appendix for how it maps to real files, but no code is prescribed.

---

## 0. Grounding — what already exists today

This spec extends real, working code. Getting the extension points right depends on these facts holding:

| Fact | Where |
|---|---|
| Runtime grammar is built **in-memory from JSON** via `GrammarBuilder`/`Choices`/`SemanticResultValue` — **not** loaded from the `.srgs` XML file. `System.Speech` on .NET 6+ throws `PlatformNotSupportedException` from the SRGS→CFG compiler path, so SRGS can't be the live source. | `VoiceGrammar.cs`, `VoiceControlService.TryInitialiseEngine` |
| The JSON schema (`VoicePhrasesConfig`) already has: flat `SimpleCommands` (intent → phrase list), several `DecomposedCommand` fields (trigger phrases + value vocabulary) for `SetMode`/`SetBand`/`SetNudgeStep`/`SetAttenuator`/`SetPreamp`/`SetAgc`/`SetAfGain`, a bespoke `SetFrequencyPhrases`, and a fully user-extensible `Macros` list (name, phrases, raw CI-V command hex). | `Models/VoicePhrasesConfig.cs` |
| Every built-in command's *behaviour* is a hardcoded `switch` in `IntentDispatcher` — translatable, not addable. **Macros are the only fully data-driven extension point today** — arbitrary phrases → an arbitrary CI-V command body in hex, no code change needed. | `Services/Voice/IntentDispatcher.cs` |
| Value-space openness varies per built-in command: `SetFrequency`/`SetAfGain` accept any numeric value; `SetMode` accepts any string `CatCommands.FormatMode` understands; `SetBand`/`SetAttenuator`/`SetPreamp`/`SetAgc` are constrained to fixed dictionaries in `IntentDispatcher`. | same |
| Single locale today: `en-GB` hardcoded in `TryInitialiseEngine`. Multi-language was explicitly deferred to v2 in the v1 plan. | `docs/VoiceControl/v1-plan.md` |
| Settings → Voice Control already has: enable toggle, spoken-confirmation toggle, nudge-step dropdown, a flat phrase-table editor (`buildPhrasesEditor` in `Settings.cshtml`), a dynamic macros table (add/delete rows), a diagnostics panel (`/api/voice/status`), and an "open user grammars folder" button (currently a no-op placeholder pointing at `%APPDATA%\...\Grammars\`). | `Pages/Settings.cshtml` |
| `VoiceGrammar.Build()` already isolates one bad variant per intent via a `Try(name, make)` wrapper — one malformed command can't take down the whole grammar. | `VoiceGrammar.cs` |
| Hot-reload already works: `POST /api/voice/phrases` saves JSON and calls `VoiceControlService.ReloadGrammar()` — stop, swap grammar, resume — no app restart. | `VoiceController.cs`, `VoiceControlService.cs` |

Everything below is written to **reuse** these mechanisms rather than replace them. The single biggest new architectural decision is formalising **JSON as canonical, SRGS as a generated/derived portable artifact** — see §1.2.

---

## 1. Language Pack Architecture

### 1.1 Pack contents & naming

A language pack is a folder (on disk) or a ZIP (in transit) containing:

```
Commands.<culture>.json     required — canonical, editable, hot-reloadable
Commands.<culture>.srgs     required — generated from the JSON, for portability/interop/review
Commands.<culture>.meta.json  optional — author, version, description, locale, dateCreated, dateModified
```

`<culture>` is a BCP-47 tag (`en-GB`, `de-DE`, `pl-PL`, …), matching the `Culture.Name` reported by `SpeechRecognitionEngine.InstalledRecognizers()` when a matching Windows speech pack is installed. A pack can exist, be edited, and be shared without the matching Windows recognizer installed — it just can't be *activated for listening* until it is (see §4.4).

### 1.2 JSON is canonical; SRGS is derived — and why

This is the load-bearing decision for the whole design. Because SRGS can't be loaded at runtime on .NET 6+ (see §0), the two files cannot be peers:

- **JSON is the only file the app ever reads to build behaviour.** Every edit, import, and locale switch flows through the JSON schema.
- **SRGS is always generated from the JSON**, by the same data-walk `VoiceGrammar.Build()` already performs — one generator, two outputs (an in-memory `GrammarBuilder` for the live engine, and SRGS/SISR XML text for the portable file). Because both come from one walk over one data structure, they cannot drift apart.
- SRGS's job is: human/tool readability (SRGS is a W3C standard other speech tools understand), a structural-validation target for hand-authored or foreign packs (via `SrgsDocument` parsing — this **does** work for parse-checking, per the existing `Grammars/README.md` "testing without a microphone" note; it's only *loading into a live recognizer* that's unsupported), and a durable snapshot that survives a future engine swap (if YWC ever moves off `System.Speech`, the SRGS files are the migration asset).
- **If a user hand-edits or imports a foreign SRGS file that disagrees with the JSON, JSON wins.** The importer regenerates SRGS from JSON immediately post-import (§3.2). A foreign SRGS with structure YWC can't map to its schema is kept alongside as a read-only reference file, clearly labelled "not live," never parsed for behaviour.

### 1.3 Schema evolution: categories & custom commands

`VoicePhrasesConfig` gains two additive, non-breaking fields:

- Every built-in `DecomposedCommand` / `SimpleCommands` entry gets an implicit **category** tag, defaulted in code (not stored per-user-file) so existing files don't need migration:

  | Category | Commands |
  |---|---|
  | Tuning | SetFrequency, SetBand, SwapVFO, NudgeFrequency, BandUp, BandDown, SetNudgeStep |
  | Radio Controls | SetMode, SetAfGain, SetAttenuator, SetPreamp, SetAgc, NudgeIfWidth |
  | Transmit | TxOn, TxOff, SplitOn, SplitOff |
  | Status & Help | StatusFrequency, StatusMode, StatusBand, Help |

- `Macros[]` (presented in the UI as **Custom Commands**) gains a `category` string field — free text, defaults to `"Macros"`, autocompletes from categories already present in the pack. This is how a user creates a "Noise Reduction" or "Custom CI-V commands" grouping: they just type that category name once and every custom command tagged with it groups together. No schema change needed beyond the one new field — the category *system* is entirely data, not a fixed enum.

This directly resolves the "add new categories" requirement without inventing a second command-type hierarchy: **categories are a tag on commands, not a container users create separately.** A category with zero commands in it simply doesn't render.

### 1.4 Metadata block

`Commands.<culture>.meta.json`:

| Field | Notes |
|---|---|
| `author` | free text, remembered locally so it doesn't need retyping per export |
| `version` | integer, auto-incremented on export/publish |
| `description` | free text |
| `locale` | must match the culture encoded in the JSON/SRGS filenames — mismatch is a hard validation error (§1.5) |
| `dateCreated` / `dateModified` | ISO 8601, set automatically |
| `sourcePackId` | optional — set when a pack was created via "Duplicate & translate" (§6.2), points at the pack it was translated from, purely informational |

Absent metadata is not an error — a pack with just JSON+SRGS and no meta.json is valid, just displayed as "Unknown author / no description" in the UI.

### 1.5 Validation pipeline & error surfacing

Two stages, mirroring the app's existing `ModelState`-driven validation UX (Settings.cshtml.cs pattern: block on errors, still show the form, highlight the field) rather than a raw JSON dump:

**Stage A — structural** (on file open, on every import, on every save):
- JSON: deserialises against `VoicePhrasesConfig`; required top-level keys present; recognised `Version`.
- SRGS (import-time only, for foreign/hand-authored packs): well-formed XML, parses via `SrgsDocument` (structural check only — never loaded into a live engine). Parse failures report line/column from the XML parser.
- meta.json `locale` must match the filenames' `<culture>` — mismatch is an **error**, not a warning (§3.3 explains why).

**Stage B — semantic** (before install/activate, and on-demand "Validate" button in the editor):
- Every category has ≥1 command; every command has ≥1 phrase.
- No duplicate phrases *within the pack* — SAPI grammar ambiguity means a duplicate silently favours whichever variant compiled first; this is exactly the kind of bug the existing `MinConfidence` threshold was added to guard against, so it's caught here instead.
- Macro/Custom Command payloads: parsed as CI-V command bodies by `CivMacroCodec` (hex byte pairs, `;` between commands, ≤16 bytes each) and, unless **Advanced Mode** is on, checked against the trusted CI-V command set (§5.5).
- Built-in command vocabulary keys: open-set commands (`SetFrequency`, `SetAfGain`) skip this check; closed-set commands (`SetBand`, `SetAttenuator`, `SetPreamp`, `SetAgc`) report unknown keys as **warnings** (forward-compatible — a future app version might add a value this pack predates), never hard errors.

Every check returns `{ severity: error | warning, path, message }` — `path` is a dotted schema path (e.g. `macros[2].cat`, `setBand.vocabulary.90`) so the editor can jump to and highlight the exact field. **Errors block save/install. Warnings show inline with an explicit "proceed anyway" affordance** — never silently swallowed, never silently blocking.

### 1.6 Storage locations

| Tier | Location | Mutable? |
|---|---|---|
| Built-in | `Grammars/<culture>/` next to the exe | Read-only; overwritten on app update |
| Installed | `%APPDATA%\MM5AGM\Yaesu Web Control\Grammars\<culture>\` | Yes — this is what the running app actually loads for any locale (including a user-customised copy of the built-in `en-GB`) |
| Draft (in-editor) | `%APPDATA%\...\Grammars\<culture>\.draft.json` | Autosaved working copy, not yet published (§2.5, §6.1) |
| Version history | `%APPDATA%\...\Grammars\<culture>\history\<timestamp>-v<version>\` | Read-only snapshots, capped at last 5 (§3.3) |

This generalises the existing `Grammars/` (shipped) + `%APPDATA%\...\Grammars\` (user drop-in, currently an inert placeholder) split into a real per-locale structure, and finally gives that "open grammars folder" button something meaningful to show.

---

## 2. User Interface for Editing Commands

### 2.1 Two-tier command model

The editor must be honest about what's actually editable, or "add a new command" becomes a promise the app can't keep:

- **Core Commands** — the built-in intents wired into `IntentDispatcher`. Fixed set; cannot be added or deleted (their behaviour is compiled into the app). Every phrase is editable; open-set values (`SetFrequency`, `SetAfGain`) accept any new value key; closed-set values (`SetBand`, `SetAttenuator`, `SetPreamp`, `SetAgc`) can have synonyms added to an *existing* key but not a brand-new key (Stage B would warn, and `IntentDispatcher` wouldn't know what to do with it anyway).
- **Custom Commands** — the generalised Macro system. Fully user-owned: name, phrases, one-or-more CI-V commands, category. This is the actual answer to "add new commands" and "add new categories."

This distinction is surfaced in the UI as two visually different row styles within the same category groups (§2.2), not as two separate tabs — a user thinking "I want a Noise Reduction section" shouldn't have to know or care which tier NR-on/NR-off (Core? No — they're already Custom Commands today) happen to be.

### 2.2 Layout: category-grouped accordion

Replaces today's one long flat page (`buildPhrasesEditor`) with a collapsible group per category:

```
▾ Tuning
    [Core] Set frequency         triggers: tune to, set frequency to, tune tae      [reset]
    [Core] Set band               triggers: go to, switch to   →  12 band values…    [reset]
    [Core] Swap VFO                phrases: swap v f o, swap a and b, …             [reset]
    ...
▾ Radio Controls
    [Core] Set mode                triggers: mode, set mode  →  8 mode values…       [reset]
    ...
▾ Noise Reduction                                                    + Add command
    [Custom] NR on      phrases: noise reduction on, n r on      cat: 16 40 01;     [✕]
    [Custom] NR off     phrases: noise reduction off, n r off    cat: 16 40 00;     [✕]
▾ Custom CI-V commands                                               + Add command
    [Custom] Preamp 1   phrases: preamp one                      cat: 16 02 01;     [✕]
▸ Macros (collapsed)                                                 + Add command
```

- Core rows keep today's inline comma-separated phrase input (proven, low-friction) plus a per-value vocabulary sub-table for decomposed commands — same interaction, just grouped.
- Custom rows keep today's macro-row pattern (name / phrases / CI-V / delete), with a new **category** field (autocomplete dropdown + free-text "new category…").
- A category with zero commands doesn't render. Typing a brand-new category name into any "+ Add command" form and saving a command into it is how a category is created — there is no separate "create category" action, matching §1.3's "category is a tag, not a container."
- Search/filter box above the accordion (new) — filters visible rows by phrase or command name text match, auto-expanding matching categories. Necessary once the page holds an open-ended number of custom commands rather than today's fixed ~15 rows.

### 2.3 Add / Delete / Modify

| Action | UI |
|---|---|
| Add command | "+ Add command" in any category header → inline form: Name, Phrases (chip/tag input — upgrade from today's comma-separated text field for clearer editing and better accessibility with screen readers), CI-V command(s) (monospace textarea, hex bytes, `;`-joined or one-per-line, live trusted-command check as they type), Category (prefilled to the section they clicked from, changeable) |
| Delete command | Trash icon on Custom rows only (matches today's macro delete). Core rows show "reset to default phrases" instead — deleting a Core command would leave its intent permanently silent, which is a confusing dead-end for a user, so it's reframed as a revert |
| Modify phrases | Inline chip input, same interaction as today, now consistent across Core and Custom rows |
| Modify values | Vocabulary sub-table under decomposed Core commands (today's pattern, unchanged); for Custom Commands, "value" *is* the CI-V command hex, edited directly |
| Add new category | Implicit — type a new name into any "+ Add command" form's Category field |

### 2.4 Auto-generation of JSON + SRGS

Unchanged transport shape from today (`POST /api/voice/phrases` with a `VoicePhrasesConfig`-shaped body), extended to be locale-scoped (target `<culture>` in the URL or body) and to include the new `category` fields. Server-side, saving:

1. Runs Stage A + Stage B validation (§1.5); blocks on errors, returns the report.
2. Writes `Commands.<culture>.json`.
3. Regenerates `Commands.<culture>.srgs` from the same data-walk `VoiceGrammar.Build()` already does for the live `GrammarBuilder` (§1.2) — one generator, two outputs, so there is no separate "SRGS writer" to keep in sync by hand.
4. Calls the existing `ReloadGrammar()` hot-swap if `<culture>` is the active locale — unchanged mechanism.

### 2.5 Live validation & test-before-save

- Inline field-level validation as the user types (duplicate-phrase warning appears under the field the moment it becomes a duplicate, not just on save).
- Per-row **"Try it"** button — a scoped 3-second one-shot recognition against just that command's grammar fragment, showing `heard: "..." → matched ✓` or `no match ✗` inline. New relative to today's all-or-nothing navbar mic button; the single highest-value addition for a translator who needs to know *immediately* whether SAPI's phonetic model actually recognises the phrase they just typed, without leaving the editor or memorising the whole grammar's other phrases first.
- Autosave to the `.draft.json` tier (§1.6) on every change, independent of the explicit "Save"/"Publish" action — protects against losing translation work to a browser refresh, which today's editor has no defence against.

---

## 3. Import / Export Workflow

### 3.1 Export

"Export" on a pack card (Language Manager, §7) → fill/confirm metadata (author remembered from last time, description free text) → server bundles the three files (§1.1, SRGS freshly regenerated so it's never stale) into `YWC-VoicePack-<culture>-v<version>.zip`, streamed as a browser download.

### 3.2 Import

1. "Import" → file picker (`.zip` only).
2. Server extracts to a temp directory, runs Stage A + Stage B (§1.5) **without installing anything**, returns a structured report.
3. **Preview screen**: metadata card (author/version/description/locale/date) + a category tree (category → command count → phrase count) + expandable "sample phrases" per category, so the user can eyeball translation quality before committing anything to disk.
4. If the locale has no matching Windows SAPI recognizer installed, a non-blocking banner appears with a deep link to `ms-settings:speech` — the pack can still be installed and edited, just can't be activated for listening yet (§4.4).
5. "Install" is enabled once Stage B has zero blocking errors; warnings show an explicit "Install with N warnings" confirmation, never a silent pass-through.
6. On install: JSON is written, SRGS is **regenerated from the imported JSON** (not the imported SRGS file verbatim — §1.2), and any foreign SRGS with a structure YWC can't map is kept alongside as a clearly-labelled read-only reference file.

### 3.3 Versioning & rollback

- Every install snapshots the *previous* copy of that locale (if any) into `history/<timestamp>-v<version>/` before overwriting — capped at the last 5 snapshots per locale, oldest pruned. Mirrors the app's existing local-backup instinct (`radio_state.json`, `calibration.user.json`) rather than introducing real version control.
- Language Manager → pack card → "Version history" → list of past installs with metadata → **"Restore this version"** runs the *same* validate → preview → install pipeline as a normal import, using the archived snapshot as the source. Rollback is not a special code path; it's an import from a local ZIP-equivalent.

### 3.4 Conflicts & locale mismatches

- **Re-importing the same locale**: default action is **Replace** (predictable, matches the existing "Reset to defaults" pattern elsewhere in Settings). **Merge** is an opt-in secondary choice on the preview screen: adds only commands that don't already exist by name; any name collision is listed individually for the user to pick "keep mine" or "take theirs" — never silently overwritten.
- **Locale mismatch** (meta.json says one locale, the JSON/SRGS filenames encode another): Stage A **error**, blocks install outright. This one is a hard error rather than a warning because a silently-mis-tagged pack would install under the wrong language and quietly break recognition with no obvious cause — exactly the kind of bug that's expensive to diagnose after the fact and cheap to catch at import time.

---

## 4. Multi-Locale Support

### 4.1 Detecting available languages

`GET /api/voice/locales` returns three lists, cross-referenced:
- **Installed packs** (from `%APPDATA%\...\Grammars\`)
- **Built-in packs** (from `Grammars/` next to the exe)
- **Windows SAPI recognizers actually available** (`SpeechRecognitionEngine.InstalledRecognizers()`)

Each installed/built-in pack is annotated ✓ (matching Windows recognizer present) or ⚠ (pack exists, but Windows can't listen in that language until its speech pack is installed).

### 4.2 Language switcher

Replaces today's static "English (UK)" label in Settings → Voice Control with a real dropdown: every installed pack, display name from `meta.json` (falling back to `CultureInfo(<culture>).NativeName`), ✓/⚠ badge per §4.1. Switching sets the active locale and calls the existing `ReloadGrammar()` hot-swap against the newly-selected locale's JSON — no restart, extending the same mechanism already used for phrase edits.

### 4.3 Installing multiple languages

Nothing prevents multiple packs being installed side by side (§1.6 — separate `<culture>` folders). Only **one is active** at a time: `SpeechRecognitionEngine` is constructed for a single culture (the existing `TryInitialiseEngine` constraint, a real platform limitation, not a design choice). Listening in two languages simultaneously would need two engines and a mic-arbitration story — explicitly out of scope here.

### 4.4 Preferred recognition language

The active locale is a new persisted field in `ApplicationSettings` (parallel to `VoiceControlEnabled` / `VoiceNudgeStepHz`), distinct from "installed languages" — a user can have five packs installed and one active. Switching is instant (§4.2); it does not require re-import.

### 4.5 Windows-locale mismatch warning

On engine init and on every language switch: compare the active locale against `SpeechRecognitionEngine.InstalledRecognizers()`. Today, a missing recognizer produces a generic error state. This spec makes it specific: **"Windows has no <locale display name> speech recognizer installed. Voice control for this language pack won't work until you install it."** with the `ms-settings:speech` deep link, and the mic button is greyed out (not left clickable-but-silently-broken) until resolved.

---

## 5. Safe Dynamic Loading

### 5.1 Runtime pipeline

`Load JSON (active locale's installed folder) → Stage A/B validate → VoiceGrammar.Build() walks data into GrammarBuilder (per-variant try/catch, unchanged from today) → UnloadAllGrammars() + LoadGrammar() swap into the live engine (unchanged ReloadGrammar() mechanism)`.

The only structural change from today is that "the active locale's installed folder" replaces the single hardcoded `voice_phrases.json` path — everything downstream is the proven existing mechanism.

### 5.2 SRGS is never fed to the live engine

Per §1.2, the runtime never parses XML from a pack at all — only JSON, only through the fixed, enumerable `GrammarBuilder`/`Choices`/`SemanticResultValue` surface. This closes off an entire class of "malformed or malicious SRGS" concern for free: there is no XML parser, no XSLT, no external-entity resolution anywhere in the runtime load path, because SRGS in a pack is a read/export artifact only.

### 5.3 JSON definition loading safety

- `System.Text.Json` deserialisation only, always into the strongly-typed `VoicePhrasesConfig` model (or its per-culture equivalent) — never into `object`/`dynamic`. An imported pack cannot inject an arbitrary object graph; unknown JSON properties are simply ignored by the deserialiser.
- Default `System.Text.Json` depth limits (64) are more than sufficient for this schema's actual depth and are not relaxed — confirm-don't-bypass, not new work.

### 5.4 Intent registration — no reflection

The mapping from intent name to behaviour stays the fixed `switch` in `IntentDispatcher` for Core Commands — deliberately **not** made "pluggable via reflection or attribute scanning," even though that's a tempting generalisation once packs become dynamic, because a reflection-loaded handler sourced from a downloaded pack is arbitrary code execution. Custom Commands never execute code: they only ever produce a CI-V *command body*, handed to `IRadioController.SendRawCommandAsync`, which is the one deliberate escape hatch in an otherwise semantic seam. The macro supplies the body only — `CivRadioController` still applies framing and addressing itself, so a pack can neither forge a radio address nor split/join frames. **A voice pack is data, never code, at every layer** — this is the single sentence that should gate any future feature request that tries to make commands "more powerful."

### 5.5 CI-V allowlist & Advanced Mode

A small static set of CI-V command bytes considered safe for voice-invoked Custom Commands in normal mode — in practice, the same commands the app itself already sends through `IRadioController` (`05`/`06`/`07`, `0F`, `11`, `14`, `16`, `17`, `1A`, `1C`, `25`/`26`, `27`). Framed precisely: **Advanced-Mode-off means custom commands can only recombine the primitives the built-in commands already trust** — not a new trust boundary, just an extension of the existing one. One deliberate exception: `18` (power on/off) is *not* trusted even though the app sends it, because over the IC-7300's USB CI-V link a power-off takes the serial port down with it, leaving nothing to power the radio back on with.

- Stage B validation rejects any Custom Command whose command byte is outside the trusted set, unless the user has ticked **Settings → Voice Control → "Advanced mode: allow any CI-V command in Custom Commands"** (off by default).
- First-time enable shows an interstitial confirm: *"A malformed or malicious voice pack could send commands that alter your radio's configuration. Only enable this if you trust the source of packs you import, or you're hand-authoring your own."*
- This directly answers the question that will come up the moment community pack-sharing exists: *"I imported someone else's pack — can it damage my radio?"* Answer: not unless they explicitly opted into Advanced Mode.

---

## 6. User Experience Flows

### 6.1 Create a new language from scratch

1. Settings → Voice Control → Language Manager → **"+ New language"**.
2. Modal: pick a base locale (searchable BCP-47 list, cross-referenced against installed Windows recognizers) + starting point — **"Start from English (UK) structure, empty phrases"** (recommended: keeps every category/command placeholder, blanks all phrase lists) or **"Start completely empty"** (advanced).
3. Lands in the category-grouped editor (§2.2) with a persistent **"0 of 47 phrases translated"** progress indicator — gives translators a visible finish line.
4. Autosaves to `.draft.json` on every change (§2.5) — a refresh doesn't lose translation work, unlike today's editor.
5. Per-row **"Try it"** lets them validate phonetics as they go (§2.5).
6. **"Publish"** — runs Stage B, fills `meta.json` (author defaults to last-used name), moves the draft into the installed tier (§1.6).

### 6.2 Translate an existing language

1. Language Manager → pick an installed pack (or import one first) → **"Duplicate & translate"**.
2. Prompt for the target locale (must differ from source; sets `sourcePackId` in the new pack's metadata, §1.4).
3. Opens the category editor with the *source* pack's phrases shown read-only alongside each empty target-phrase field — a side-by-side original/translation layout, the one meaningfully new UI element this flow needs beyond 6.1's editor.
4. Same progress indicator, autosave, Try-it, Publish as 6.1.

### 6.3 Share a language pack

1. Language Manager → pack card → **"Export"**.
2. Confirm metadata → ZIP downloads.
3. UI copy suggests next steps ("share this file, or attach it to a GitHub Discussion post") — mirrors the existing `Grammars/README.md` contribution convention, generalised for non-technical users who can't open a PR.

### 6.4 Install a language pack

1. Language Manager → **"Import"** → pick ZIP.
2. Validation report + preview screen (§3.2) — metadata, category tree, sample phrases, warnings.
3. **"Install"** (or "Install with N warnings") → pack appears in the installed list, not yet active.
4. Prompt: **"Make this your active recognition language now?"** → Yes switches immediately, firing the Windows-recognizer mismatch check if relevant (§4.5).

### 6.5 Test commands

1. From the category editor, or a pack card in Language Manager: **"Test this pack"** opens a modal reusing the existing navbar mic button's plumbing (`/api/voice/start` / `/api/voice/stop` / `VoiceStatusUpdate` over SignalR — unchanged) so the user doesn't have to leave Settings.
2. Live trace: `Listening… → Heard: "..." → Matched: SetMode:USB → Sent to radio: MD01;` — the diagnostics data that already exists (§6.6) surfaced live during a test session instead of only after the fact.
3. **"Dry run"** toggle — test recognition + intent matching **without** sending the CAT command to the radio. Valuable for testing a pack without retuning a live radio mid-session. *(Note: this needs a dry-run flag threaded through `IntentDispatcher.DispatchAsync`, since today it always calls `SendCommandAsync` inline — a small, scoped addition, not a redesign.)*

### 6.6 View diagnostics

1. The existing diagnostics panel (state / last heard / last intent / last error, from `/api/voice/status`) gains: active locale + pack version, installed-recognizer match status, and the **confidence score of the last recognition** — today's `MinConfidence = 0.6f` threshold is enforced but never shown to the user, so a translator has no way to tell *how close* a near-miss was. Surfacing the number turns "why didn't that work" into a self-serve tuning tool.
2. The existing `/api/voice/log` tail (already filtered to `[Voice]`/`[IntentDispatcher]` lines, already the thing Colin asks bug reporters to paste into GitHub issues) gains a locale/pack-version prefix per line, so a pasted log capture always identifies which pack was active at the time — small, but directly useful given how that log is already used in practice.

---

## 7. Settings information architecture

Two viable placements; recommending the cheaper one for a first landing:

- **(a) Recommended for v1 of this feature.** Keep everything under the existing Settings → Voice Control section, but split its single `<details>` block into two nested ones: **"Command Phrases"** (today's editor, scoped to the active pack, restructured per §2) and **"Language Manager"** (new: install / export / switch / version history, §3–§4). Additive to the existing pattern; consistent with the app's deliberate "everything lives in Settings, not a new page" convention (the same reasoning that kept the SP3L layout discussion from spawning a second full Razor page casually).
- **(b) Defer.** A standalone `Pages/VoiceLanguages.cshtml`, mirroring how `Diagnostics.cshtml` is its own page rather than a Settings subsection. Only worth it if the category editor genuinely doesn't fit in a collapsible section once built — revisit after (a) ships and gets real use.

---

## Appendix: suggested build order (non-binding, no code)

For whoever implements this — dependency-ordered, each step independently shippable/testable:

1. **Schema + storage**: add `category` fields, per-locale folder layout (§1.6), metadata file — migrate today's single `voice_phrases.json` into `Grammars/en-GB/Commands.en-GB.json` as the first "installed pack."
2. **Validation pipeline** (§1.5) as a standalone service, callable from both the editor's save path and the import path — write it once, use it twice.
3. **Category-grouped editor** (§2) — replaces `buildPhrasesEditor`, still single-locale, still no import/export. This alone is a real usability win over today's flat page.
4. **SRGS generator** — extend `VoiceGrammar`'s data-walk to also emit SRGS/SISR text, not just a `GrammarBuilder`. Needed before export can produce a truthful SRGS file.
5. **Export** (§3.1) — lowest-risk half of import/export; ship it first, let it soak.
6. **Import + preview + validation report** (§3.2) — the highest-value, highest-risk piece; build on top of the now-proven validation pipeline from step 2.
7. **Multi-locale runtime switch** (§4) — active-locale setting, `ReloadGrammar()` generalised to take a locale, the Windows-recognizer mismatch banner.
8. **Version history / rollback** (§3.3) — thin wrapper around the import pipeline from step 6.
9. **CI-V allowlist + Advanced Mode gate** (§5.5) — should land no later than step 6, since import is the first point untrusted CI-V commands enter the system.
10. **Per-row "Try it" + dry-run testing** (§2.5, §6.5) — polish, but the single most-requested-once-people-see-it feature based on how translators actually work; don't leave it for "later" indefinitely.
