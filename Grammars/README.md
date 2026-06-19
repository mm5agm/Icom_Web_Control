# Voice Control Grammars

Speech recognition grammar files (`.srgs`) loaded by `VoiceControlService` at
startup. One file per locale, named `Commands.<culture>.srgs`. Only the
en-GB grammar ships in v1; the v1 plan reserves the multi-language picker
for v2.

## Files

| File | Locale | Notes |
|---|---|---|
| `Commands.en-GB.srgs` | English (UK) | v1 baseline. Six intents, Scots variants where the construction changes. |

## Format

Standard W3C SRGS 1.0 with SISR (semantic interpretation) tags, namespace
`http://www.w3.org/2001/06/grammar`. Microsoft's SAPI 5 engine (consumed via
`System.Speech.Recognition.Grammar`) loads these directly.

Each command emits a `out.intent` string and zero or more parameters. The
intent name + parameter dictionary are handed to `IntentDispatcher` which
maps to the existing CAT helpers.

## Adding a new locale

Voice control is single-locale in v1 — only `en-GB` is read. The multi-
language picker (with both shipped `Grammars/` and user-drop-in
`%APPDATA%\MM5AGM\Yaesu Web Control\Grammars\`) is a v2 concern documented
in `docs/VoiceControl/v1-plan.md`. If you're keen to contribute a grammar
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
