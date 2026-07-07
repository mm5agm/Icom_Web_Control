using System.Speech.Recognition;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Builds a SAPI grammar from a <see cref="VoicePhrasesConfig"/>. All
    /// phrase lists are user-configurable; this class only knows about
    /// structure (which semantic keys exist, how parameters compose), not
    /// specific words. That means a German user can replace "set mode" with
    /// "Betriebsart" and "eighty metres" with "achtzig Meter" without any
    /// code changes.
    ///
    /// Grammar patterns used:
    ///
    ///   SimpleCommands   — flat Choices of complete phrases; each phrase maps
    ///                      directly to an intent string via SemanticResultValue.
    ///
    ///   SetMode/SetBand  — DecomposedCommand: a non-semantic trigger prefix
    ///                      ("mode", "set mode") followed by a value Choices
    ///                      ("u s b" → "SetMode:USB"). The trigger is consumed
    ///                      but carries no semantic output; the value word
    ///                      encodes the full "IntentPrefix:Value" in one key.
    ///
    ///   Macros           — flat Choices; each phrase maps to
    ///                      "Macro:{name}|{cat}" encoding both spoken name
    ///                      (for the TTS confirmation) and CAT string to send.
    ///
    ///   SetFrequency     — three flat variants (whole / one frac digit /
    ///                      three frac digits) to avoid the SAPI nested-
    ///                      optional compile bug ("'' rule reference not
    ///                      defined"). Uses GrammarBuilder + Choices throughout
    ///                      — Grammar.LoadCfg throws PlatformNotSupportedException
    ///                      on .NET 6+ so SRGS XML loading is not available.
    /// </summary>
    internal static class VoiceGrammar
    {
        public static Grammar Build(VoicePhrasesConfig cfg)
        {
            var variants = new List<GrammarBuilder>();

            void Try(string name, Func<GrammarBuilder?> make)
            {
                try
                {
                    var gb = make();
                    if (gb == null) return;
                    _ = new Grammar(gb);   // compile-check before adding
                    variants.Add(gb);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"[VoiceGrammar] Variant '{name}' failed to compile: {ex.Message}", ex);
                }
            }

            // Simple commands — each is a flat Choices of complete trigger phrases.
            foreach (var (key, cmd) in cfg.SimpleCommands)
            {
                var k = key;
                Try(k, () => BuildSimple(k, cmd));
            }

            Try("SetMode",                () => BuildDecomposed("SetMode",        cfg.SetMode));
            Try("SetBand",                () => BuildDecomposed("SetBand",        cfg.SetBand));
            Try("SetNudgeStep",           () => BuildDecomposed("SetNudgeStep",   cfg.SetNudgeStep));
            Try("SetAttenuator",          () => BuildDecomposed("SetAttenuator",  cfg.SetAttenuator));
            Try("SetPreamp",              () => BuildDecomposed("SetPreamp",      cfg.SetPreamp));
            Try("SetAgc",                 () => BuildDecomposed("SetAgc",         cfg.SetAgc));
            Try("SetAfGain",              () => BuildDecomposed("SetAfGain",      cfg.SetAfGain));
            if (cfg.Macros?.Count > 0)
                Try("Macros",             () => BuildMacros(cfg.Macros));
            Try("SetFrequency_Whole",     () => BuildSetFrequency_Whole(cfg.SetFrequency));
            Try("SetFrequency_OneDigit",  () => BuildSetFrequency_OneDigit(cfg.SetFrequency));
            Try("SetFrequency_ThreeDigit",() => BuildSetFrequency_ThreeDigit(cfg.SetFrequency));

            if (variants.Count == 0)
                throw new InvalidOperationException("[VoiceGrammar] No grammar variants compiled — check phrase lists.");

            var root = new Choices(variants.ToArray());
            return new Grammar(new GrammarBuilder(root)) { Name = "YwcCommands" };
        }

        // ── Simple commands ───────────────────────────────────────────────

        private static GrammarBuilder? BuildSimple(string intent, IReadOnlyList<string> phrases)
        {
            if (phrases.Count == 0) return null;
            var c = new Choices();
            foreach (var p in phrases)
                c.Add(new SemanticResultValue(p, intent));
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", c));
            return gb;
        }

        // ── DecomposedCommand: SetMode / SetBand ──────────────────────────
        // User says: [trigger] [vocabulary word]
        //   "set mode"  + "u s b"          → intent = "SetMode:USB"
        //   "go to"     + "eighty metres"  → intent = "SetBand:80"
        //
        // The trigger is a non-semantic prefix (just consumed). The vocabulary
        // word carries the full encoded intent via SemanticResultValue.
        // NormaliseIntent in VoiceControlService splits on ':' to recover
        // the intent name and parameter value.

        private static GrammarBuilder? BuildDecomposed(string intentPrefix, DecomposedCommand cmd)
        {
            if (cmd.Triggers.Count == 0 || cmd.Vocabulary.Count == 0) return null;

            var valueChoices = new Choices();
            bool any = false;
            foreach (var (key, words) in cmd.Vocabulary)
            {
                foreach (var word in words)
                {
                    valueChoices.Add(new SemanticResultValue(word, $"{intentPrefix}:{key}"));
                    any = true;
                }
            }
            if (!any) return null;

            var gb = new GrammarBuilder();
            gb.Append(new Choices(cmd.Triggers.ToArray()));          // non-semantic trigger
            gb.Append(new SemanticResultKey("intent", valueChoices)); // value encodes intent
            return gb;
        }

        // ── Macros (user-defined CAT shortcuts) ──────────────────────────
        // Intent encoding: "Macro:{name}|{cat}" — name is for the spoken
        // confirmation, cat is sent verbatim to the radio (split on ';').
        // The '|' separator is safe because CAT strings never contain '|'.

        private static GrammarBuilder? BuildMacros(List<MacroDefinition> macros)
        {
            var c = new Choices();
            bool any = false;
            foreach (var macro in macros)
            {
                if (macro.Phrases == null || macro.Phrases.Count == 0) continue;
                if (string.IsNullOrWhiteSpace(macro.Cat)) continue;
                var intent = $"Macro:{macro.Name}|{macro.Cat}";
                foreach (var phrase in macro.Phrases)
                    c.Add(new SemanticResultValue(phrase, intent));
                any = true;
            }
            if (!any) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", c));
            return gb;
        }

        // ── SetFrequency variants ─────────────────────────────────────────
        // Three flat variants to avoid the nested-optional SAPI compile bug.
        // Triggers now include the connector word ("tune to", "set frequency to")
        // so there is no separate Connectors list — the user edits one field.

        private static GrammarBuilder? BuildSetFrequency_Whole(SetFrequencyPhrases cfg)
        {
            if (!HasFrequencyBase(cfg)) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", FrequencyTriggerChoices(cfg)));
            gb.Append(new SemanticResultKey("mhz_whole", MhzChoices(cfg)));
            gb.Append(new GrammarBuilder(new Choices(cfg.Megahertz.ToArray())), 0, 1);
            return gb;
        }

        private static GrammarBuilder? BuildSetFrequency_OneDigit(SetFrequencyPhrases cfg)
        {
            if (!HasFrequencyBase(cfg) || cfg.FracDigits.Count == 0 || cfg.Point.Count == 0) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", FrequencyTriggerChoices(cfg)));
            gb.Append(new SemanticResultKey("mhz_whole", MhzChoices(cfg)));
            gb.Append(new Choices(cfg.Point.ToArray()));
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new GrammarBuilder(new Choices(cfg.Megahertz.ToArray())), 0, 1);
            return gb;
        }

        private static GrammarBuilder? BuildSetFrequency_ThreeDigit(SetFrequencyPhrases cfg)
        {
            if (!HasFrequencyBase(cfg) || cfg.FracDigits.Count == 0 || cfg.Point.Count == 0) return null;
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", FrequencyTriggerChoices(cfg)));
            gb.Append(new SemanticResultKey("mhz_whole", MhzChoices(cfg)));
            gb.Append(new Choices(cfg.Point.ToArray()));
            // Three separate Choices instances — reusing one triggers SAPI compile bug.
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new Choices(cfg.FracDigits.ToArray()));
            gb.Append(new GrammarBuilder(new Choices(cfg.Megahertz.ToArray())), 0, 1);
            return gb;
        }

        private static bool HasFrequencyBase(SetFrequencyPhrases cfg) =>
            cfg.Triggers.Count > 0 && cfg.Mhz.Count > 0;

        private static Choices FrequencyTriggerChoices(SetFrequencyPhrases cfg)
        {
            var c = new Choices();
            foreach (var t in cfg.Triggers)
                c.Add(new SemanticResultValue(t, "SetFrequency"));
            return c;
        }

        private static Choices MhzChoices(SetFrequencyPhrases cfg)
        {
            var c = new Choices();
            foreach (var (mhzKey, words) in cfg.Mhz)
            {
                if (!int.TryParse(mhzKey, out var mhz)) continue;
                foreach (var w in words)
                    c.Add(new SemanticResultValue(w, mhz));
            }
            return c;
        }
    }
}
