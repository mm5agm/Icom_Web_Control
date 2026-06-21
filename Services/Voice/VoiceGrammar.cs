using System.Speech.Recognition;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Builds the en-GB voice command grammar programmatically. Mirrors
    /// the intents documented in Grammars/Commands.en-GB.srgs (kept as
    /// the human-readable spec). We can't load the SRGS file at runtime
    /// because System.Speech on .NET 6+ throws PlatformNotSupportedException
    /// from Grammar.LoadCfg -- the in-process SAPI 5 SRGS compiler isn't
    /// shipped with the modern System.Speech NuGet. The GrammarBuilder +
    /// Choices API uses a different code path that does work, but it is
    /// very fussy about nested optional repeats -- `Append(builder, 0, 1)`
    /// inside another `Append(builder, 0, 1)` triggers
    /// "FormatException: '' rule reference not defined in this grammar"
    /// from the underlying SAPI compiler. To avoid this we keep the
    /// structure FLAT: each variant of SetFrequency (no fraction / 1 frac
    /// digit / 3 frac digits) is its own GrammarBuilder alternative in
    /// the root Choices, instead of using nested optionals.
    ///
    /// Semantic keys produced (consumed by VoiceControlService.OnSpeechRecognized
    /// and IntentDispatcher):
    ///   SetFrequency:   intent="SetFrequency", mhz_whole, frac1?, frac2?, frac3?
    ///                   (VoiceControlService computes args["hz"] from these.)
    ///   SetBand:        intent="SetBand", metres
    ///   SetMode:        intent="SetMode", mode
    ///   SwapVFO:        intent="SwapVFO"
    ///   NudgeUp/Down:   intent="NudgeUp" | "NudgeDown"
    ///                   (VoiceControlService rewrites to intent="NudgeFrequency"
    ///                    with args["direction"]=±1 so IntentDispatcher's
    ///                    existing NudgeFrequency case stays untouched.)
    /// </summary>
    internal static class VoiceGrammar
    {
        public static Grammar BuildEnGb()
        {
            // Build each variant individually first so we can isolate which
            // sub-grammar (if any) triggers a compile error. The "rule
            // reference not defined" exception out of SAPI's compiler doesn't
            // tell us which rule, so per-variant compilation pin-points it.
            var variants = new List<GrammarBuilder>();
            void Try(string name, Func<GrammarBuilder> make)
            {
                try
                {
                    var gb = make();
                    _ = new Grammar(gb);   // sanity-compile alone
                    variants.Add(gb);
                    System.Diagnostics.Debug.WriteLine($"[VoiceGrammar] {name} OK");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"[VoiceGrammar] Variant '{name}' failed to compile: {ex.Message}", ex);
                }
            }

            Try("SwapVFO",                 BuildSwapVfo);
            Try("NudgeUp",                 BuildNudgeUp);
            Try("NudgeDown",               BuildNudgeDown);
            Try("SetBand",                 BuildSetBand);
            Try("SetMode",                 BuildSetMode);
            Try("SetFrequency_Whole",      BuildSetFrequency_Whole);
            Try("SetFrequency_OneDigit",   BuildSetFrequency_OneDigit);
            Try("SetFrequency_ThreeDigit", BuildSetFrequency_ThreeDigit);

            var root = new Choices(variants.ToArray());
            return new Grammar(new GrammarBuilder(root))
            {
                Name = "YwcCommands.en-GB"
            };
        }

        // ---- SetFrequency variants ---------------------------------------

        // "set frequency to fourteen [megahertz]"
        private static GrammarBuilder BuildSetFrequency_Whole()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", SetFrequencyVerbChoices()));
            gb.Append(new Choices("to", "tae"));
            gb.Append(new SemanticResultKey("mhz_whole", MhzWholeChoices()));
            gb.Append(new Choices("megahertz", "mega hertz"));
            return gb;
        }

        // "set frequency to fourteen point zero [megahertz]"
        // Fractional digit captured as plain string -- the SAPI compiler
        // rejects multiple consecutive SemanticResultKey appends in one
        // builder ("'' rule reference not defined"), so we parse the digit
        // out of e.Result.Text in VoiceControlService instead of capturing
        // it semantically.
        private static GrammarBuilder BuildSetFrequency_OneDigit()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", SetFrequencyVerbChoices()));
            gb.Append(new Choices("to", "tae"));
            gb.Append(new SemanticResultKey("mhz_whole", MhzWholeChoices()));
            gb.Append("point");
            gb.Append(FracDigitPhrases());
            gb.Append(new Choices("megahertz", "mega hertz"));
            return gb;
        }

        // "set frequency to fourteen point zero seven four [megahertz]"
        // Same plain-string approach as the one-digit variant; three
        // separate Choices instances (must be distinct objects -- reusing
        // one Choices across multiple Appends can also trigger the same
        // compile bug).
        private static GrammarBuilder BuildSetFrequency_ThreeDigit()
        {
            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", SetFrequencyVerbChoices()));
            gb.Append(new Choices("to", "tae"));
            gb.Append(new SemanticResultKey("mhz_whole", MhzWholeChoices()));
            gb.Append("point");
            gb.Append(FracDigitPhrases());
            gb.Append(FracDigitPhrases());
            gb.Append(FracDigitPhrases());
            gb.Append(new Choices("megahertz", "mega hertz"));
            return gb;
        }

        private static Choices FracDigitPhrases() =>
            new Choices("zero", "oh", "one", "two", "three", "four",
                        "five", "six", "seven", "eight", "nine");

        private static Choices SetFrequencyVerbChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("set frequency", "SetFrequency"));
            c.Add(new SemanticResultValue("set the frequency", "SetFrequency"));
            c.Add(new SemanticResultValue("tune", "SetFrequency"));
            return c;
        }

        // ---- SetBand -----------------------------------------------------
        // "go to twenty metres"
        private static GrammarBuilder BuildSetBand()
        {
            var verb = new Choices();
            verb.Add(new SemanticResultValue("go to", "SetBand"));
            verb.Add(new SemanticResultValue("go tae", "SetBand"));
            verb.Add(new SemanticResultValue("switch to", "SetBand"));
            verb.Add(new SemanticResultValue("switch tae", "SetBand"));

            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", verb));
            gb.Append(new SemanticResultKey("metres", BandMetresChoices()));
            gb.Append(new Choices("metres", "meters", "metre band", "meter band", "m band"));
            return gb;
        }

        // ---- SetMode -----------------------------------------------------
        private static GrammarBuilder BuildSetMode()
        {
            var verb = new Choices();
            verb.Add(new SemanticResultValue("set mode", "SetMode"));
            verb.Add(new SemanticResultValue("mode", "SetMode"));
            verb.Add(new SemanticResultValue("set the mode to", "SetMode"));

            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", verb));
            gb.Append(new SemanticResultKey("mode", ModeChoices()));
            return gb;
        }

        // ---- SwapVFO -----------------------------------------------------
        private static GrammarBuilder BuildSwapVfo()
        {
            var phrases = new Choices();
            phrases.Add(new SemanticResultValue("swap v f o", "SwapVFO"));
            phrases.Add(new SemanticResultValue("swap v f os", "SwapVFO"));
            phrases.Add(new SemanticResultValue("swap a and b", "SwapVFO"));
            phrases.Add(new SemanticResultValue("swap a b", "SwapVFO"));
            phrases.Add(new SemanticResultValue("switch v f o", "SwapVFO"));
            phrases.Add(new SemanticResultValue("switch a and b", "SwapVFO"));

            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", phrases));
            return gb;
        }

        // ---- NudgeUp -----------------------------------------------------
        private static GrammarBuilder BuildNudgeUp()
        {
            var phrases = new Choices();
            phrases.Add(new SemanticResultValue("tune up", "NudgeUp"));
            phrases.Add(new SemanticResultValue("step up", "NudgeUp"));
            phrases.Add(new SemanticResultValue("frequency up", "NudgeUp"));
            phrases.Add(new SemanticResultValue("up one step", "NudgeUp"));
            phrases.Add(new SemanticResultValue("tune higher", "NudgeUp"));

            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", phrases));
            return gb;
        }

        // ---- NudgeDown ---------------------------------------------------
        private static GrammarBuilder BuildNudgeDown()
        {
            var phrases = new Choices();
            phrases.Add(new SemanticResultValue("tune down", "NudgeDown"));
            phrases.Add(new SemanticResultValue("step down", "NudgeDown"));
            phrases.Add(new SemanticResultValue("frequency down", "NudgeDown"));
            phrases.Add(new SemanticResultValue("down one step", "NudgeDown"));
            phrases.Add(new SemanticResultValue("tune lower", "NudgeDown"));

            var gb = new GrammarBuilder();
            gb.Append(new SemanticResultKey("intent", phrases));
            return gb;
        }

        // ==== Helpers =====================================================
        // Each helper returns a fresh Choices on every call so callers can
        // append the same logical alternative set into multiple builders
        // without aliasing issues.

        private static Choices MhzWholeChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("one", 1));
            c.Add(new SemanticResultValue("two", 2));
            c.Add(new SemanticResultValue("three", 3));
            c.Add(new SemanticResultValue("four", 4));
            c.Add(new SemanticResultValue("five", 5));
            c.Add(new SemanticResultValue("six", 6));
            c.Add(new SemanticResultValue("seven", 7));
            c.Add(new SemanticResultValue("eight", 8));
            c.Add(new SemanticResultValue("nine", 9));
            c.Add(new SemanticResultValue("ten", 10));
            c.Add(new SemanticResultValue("eleven", 11));
            c.Add(new SemanticResultValue("twelve", 12));
            c.Add(new SemanticResultValue("thirteen", 13));
            c.Add(new SemanticResultValue("fourteen", 14));
            c.Add(new SemanticResultValue("fifteen", 15));
            c.Add(new SemanticResultValue("sixteen", 16));
            c.Add(new SemanticResultValue("seventeen", 17));
            c.Add(new SemanticResultValue("eighteen", 18));
            c.Add(new SemanticResultValue("nineteen", 19));
            c.Add(new SemanticResultValue("twenty", 20));
            c.Add(new SemanticResultValue("twenty one", 21));
            c.Add(new SemanticResultValue("twenty four", 24));
            c.Add(new SemanticResultValue("twenty eight", 28));
            c.Add(new SemanticResultValue("twenty nine", 29));
            c.Add(new SemanticResultValue("thirty", 30));
            c.Add(new SemanticResultValue("fifty", 50));
            c.Add(new SemanticResultValue("fifty one", 51));
            c.Add(new SemanticResultValue("fifty two", 52));
            c.Add(new SemanticResultValue("seventy", 70));
            c.Add(new SemanticResultValue("seventy one", 71));
            return c;
        }

        private static Choices BandMetresChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("one hundred and sixty", 160));
            c.Add(new SemanticResultValue("eighty", 80));
            c.Add(new SemanticResultValue("sixty", 60));
            c.Add(new SemanticResultValue("forty", 40));
            c.Add(new SemanticResultValue("thirty", 30));
            c.Add(new SemanticResultValue("twenty", 20));
            c.Add(new SemanticResultValue("seventeen", 17));
            c.Add(new SemanticResultValue("fifteen", 15));
            c.Add(new SemanticResultValue("twelve", 12));
            c.Add(new SemanticResultValue("ten", 10));
            c.Add(new SemanticResultValue("six", 6));
            c.Add(new SemanticResultValue("four", 4));
            return c;
        }

        private static Choices ModeChoices()
        {
            var c = new Choices();
            c.Add(new SemanticResultValue("u s b", "USB"));
            c.Add(new SemanticResultValue("upper sideband", "USB"));
            c.Add(new SemanticResultValue("l s b", "LSB"));
            c.Add(new SemanticResultValue("lower sideband", "LSB"));
            c.Add(new SemanticResultValue("c w", "CW-U"));
            c.Add(new SemanticResultValue("see double you", "CW-U"));
            c.Add(new SemanticResultValue("a m", "AM"));
            c.Add(new SemanticResultValue("f m", "FM"));
            c.Add(new SemanticResultValue("data", "DATA-U"));
            c.Add(new SemanticResultValue("data u", "DATA-U"));
            c.Add(new SemanticResultValue("data l", "DATA-L"));
            c.Add(new SemanticResultValue("digital", "DATA-U"));
            c.Add(new SemanticResultValue("r t t y", "RTTY-L"));
            return c;
        }
    }
}
