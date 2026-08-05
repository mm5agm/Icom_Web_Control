# Voice Control Grammars

Speech recognition grammar files (`.srgs`), one per locale, named
`Commands.<culture>.srgs`. Only the en-GB grammar ships; the multi-language
picker is a later phase.

**These files are not loaded at runtime.** `System.Speech` on .NET 6+ throws
`PlatformNotSupportedException` when compiling SRGS/CFG live, so
`VoiceControlService` builds the actual recognition grammar in memory from
`Commands.<culture>.json` via `VoiceGrammar.Build()` (see
`Services/Voice/VoiceGrammar.cs`). The `.srgs` files are a human-readable,
standards-based reference copy of the same command set — useful for review,
offline validation, and sharing with other SAPI-based tools.

`VoiceGrammar.GenerateSrgs()` produces them from the same config object
`Build()` compiles, and `VoicePhraseStore.Save()` rewrites the installed
pack's copy on every save, so a *user's* pack can't drift from its JSON. The
copy checked in here is a snapshot of the shipped defaults
(`VoicePhraseStore.BuildDefaults()`) and does have to be refreshed by hand
when those defaults change — save the defaults once through the editor and
copy `%APPDATA%\MM5AGM\Icom Web Control\Grammars\en-GB\Commands.en-GB.srgs`
over the file here. See `docs/VoiceControl/language-pack-manager-design.md`.

## Files

| File | Locale | Notes |
|---|---|---|
| `Commands.en-GB.srgs` | English (UK) | Snapshot of the schema-9 defaults: the simple commands plus 16 decomposed ones (frequency, band, mode, step, AF/RF gain, squelch, attenuator, preamp, AGC, NR, NB, notch, APF, TX power, mic gain, processor). |

## Format

Standard W3C SRGS 1.0 with SISR (semantic interpretation) tags, namespace
`http://www.w3.org/2001/06/grammar`. This is the format the live grammar is
*structurally equivalent to* — the actual runtime object is built from JSON
via `GrammarBuilder`/`Choices`/`SemanticResultValue`, not by parsing this XML
(see note above).

Each command emits a `out.intent` string and zero or more parameters. The
intent name + parameter dictionary are handed to `IntentDispatcher` which
maps them onto the `IRadioController` seam (CI-V). The equivalent live encoding (in
`VoiceGrammar.cs`) uses a single `"intent"` semantic key carrying strings like
`"SetMode:USB"` or `"Macro:{name}|{cat}"`, parsed back apart in
`VoiceControlService.NormaliseIntent()`.

## Adding a new locale

Voice control is single-locale today — only `en-GB` is read. The multi-
language picker (with both shipped `Grammars/` and user-drop-in
`%APPDATA%\MM5AGM\Icom Web Control\Grammars\`) is a later concern documented
in `docs/VoiceControl/language-pack-manager-design.md`. If you're keen to contribute a grammar
for another language now, copy `Commands.en-GB.srgs` to `Commands.<your-
locale>.srgs`, translate the spoken phrases, **keep the `out.intent` and
parameter names identical**, and open a GitHub PR.

The semantic tags must remain in English (`SetFrequency`, `SetBand`, etc.)
even when the spoken phrases are in another language — the IntentDispatcher
keys on those names and is language-agnostic by design.

## Testing without a microphone

`System.Speech.Recognition.SrgsDocument` can validate an `.srgs` file
without any audio device:

```csharp
var doc = new SrgsDocument("Grammars/Commands.en-GB.srgs");
var grammar = new Grammar(doc);
```

If the XML is malformed or the SISR semantics don't compile, this throws
at load time, surfacing the problem clearly.
