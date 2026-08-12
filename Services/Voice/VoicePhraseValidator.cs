using Icom_Web_Control.Models;
using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Services.Voice
{
    /// <summary>Severity of a <see cref="ValidationIssue"/> — errors block save/install, warnings don't.</summary>
    public enum ValidationSeverity
    {
        Warning,
        Error,
    }

    /// <summary>
    /// One validation finding. <see cref="Path"/> is a dotted schema path
    /// (e.g. "macros[2].cat", "setBand.vocabulary.90") so a client can jump
    /// to and highlight the exact field.
    /// </summary>
    public sealed record ValidationIssue(ValidationSeverity Severity, string Path, string Message);

    /// <summary>
    /// Validates a <see cref="VoicePhrasesConfig"/> before it's saved or
    /// (in a later phase) installed from an imported pack. Two stages per
    /// docs/VoiceControl/language-pack-manager-design.md §1.5:
    ///
    ///   Stage A (structural) — cheap, cannot fail here since the config has
    ///   already been deserialised by the time this runs; kept as a single
    ///   method so a future JSON-string entry point (import) can call it
    ///   before deserialisation without duplicating the schema checks.
    ///
    ///   Stage B (semantic) — empty categories/phrases, duplicate phrases,
    ///   malformed macro CI-V payloads, unknown vocabulary keys for closed-set
    ///   commands (reported as warnings, not errors, so a pack authored
    ///   against a newer app version that added a value this app doesn't
    ///   know about yet still installs).
    ///
    /// The command allowlist / Advanced Mode check (§5.5): unless
    /// <c>advancedMode</c> is true, every CI-V command in a Custom Command
    /// must use a command byte one of the built-in Core Commands (or a
    /// shipped default macro) already sends — a Custom Command can only
    /// recombine primitives the app already trusts, not send anything new.
    /// This is what stands between an imported/shared voice pack and "can it
    /// damage my radio" — see ApplicationSettings.VoiceAdvancedModeEnabled.
    /// </summary>
    public static class VoicePhraseValidator
    {
        private static readonly HashSet<string> KnownBandMetres = ["160", "80", "60", "40", "30", "20", "17", "15", "12", "10", "6", "4"];
        // Attenuator is one 20 dB pad on/off (CI-V 11) and AGC is a
        // three-position time constant (16 12) — the Yaesu 6/12/18 dB steps and
        // the OFF/AUTO AGC positions this used to accept do not exist on the
        // IC-7300, so a pack still offering them is describing a radio the app
        // cannot drive.
        private static readonly HashSet<string> KnownAttenuatorLevels = ["off", "on"];
        private static readonly HashSet<string> KnownPreampLevels = ["off", "1", "2"];
        private static readonly HashSet<string> KnownAgcSpeeds = ["fast", "mid", "slow"];
        private static readonly HashSet<string> KnownNudgeSteps = ["10", "100", "1000", "10000", "100000"];
        private static readonly HashSet<string> KnownNotchModes = ["off", "auto", "manual"];
        private static readonly HashSet<string> KnownApfSettings = ["0", "1", "2", "3"];

        // The 0–100 level ladder shared by AF/RF gain, squelch, TX power, mic
        // gain and the level half of NR/NB/processor. Any whole number in range
        // is legal — the vocabulary is the user's to extend, so this is a range
        // check, not a fixed set.
        private static bool IsLevelKey(string key) =>
            int.TryParse(key, out var n) && n is >= 0 and <= 100;

        private static readonly HashSet<string> SwitchWords = ["off", "on"];

        // CI-V command bytes the app itself already sends through
        // IRadioController — set frequency (05/25), set mode (06/26), VFO
        // select/exchange/equalize (07), split (0F), attenuator (11), levels
        // (14: AF/RF gain, squelch, NR/NB level, PBT, power, CW speed/pitch),
        // functions (16: preamp, AGC, NB, NR, notch, break-in), CW memory send
        // (17), menu settings (1A: filter width, data mode, tone control),
        // tuner/PTT (1C) and the scope (27). A Custom Command may recombine
        // those; anything else needs Advanced Mode.
        //
        // 18 (power on/off) is deliberately NOT trusted even though the app
        // sends it: over the IC-7300's USB CI-V a power-off drops the serial
        // port with it, so a mistyped shared macro would take the radio off the
        // air with no way back short of walking to the rig.
        private static readonly HashSet<byte> TrustedCivCommands =
        [
            0x05, 0x06, 0x07, 0x0F, 0x11, 0x14, 0x16, 0x17, 0x1A, 0x1C, 0x25, 0x26, 0x27,
        ];

        public static List<ValidationIssue> Validate(VoicePhrasesConfig cfg, bool advancedMode = false)
        {
            var issues = new List<ValidationIssue>();

            ValidateStructural(cfg, issues);
            ValidateSemantic(cfg, issues, advancedMode);

            return issues;
        }

        // ── Stage A — structural ──────────────────────────────────────────

        private static void ValidateStructural(VoicePhrasesConfig cfg, List<ValidationIssue> issues)
        {
            // The gate stays at 9, not the current 10: v10 only added the
            // antenna-tuner commands, and VoicePhraseStore.MigrateToV10 fills
            // those in on load, so a v9 pack is still a valid import.
            if (cfg.Version < 9)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "version",
                    $"Schema version {cfg.Version} predates the supported format (9 or later; current is 10) and cannot be validated."));

            cfg.SimpleCommands ??= new();
            cfg.Macros ??= new();
        }

        // ── Stage B — semantic ────────────────────────────────────────────

        private static void ValidateSemantic(VoicePhrasesConfig cfg, List<ValidationIssue> issues, bool advancedMode)
        {
            var seenPhrases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // normalised phrase -> first path that used it

            foreach (var (key, phrases) in cfg.SimpleCommands)
            {
                if (phrases == null || phrases.Count == 0)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Warning, $"simpleCommands.{key}",
                        $"'{key}' has no phrases and will never trigger."));
                    continue;
                }
                CheckDuplicates(phrases, $"simpleCommands.{key}", seenPhrases, issues);
            }

            ValidateDecomposed(cfg.SetMode, "setMode", null, issues);
            ValidateDecomposed(cfg.SetBand, "setBand", KnownBandMetres, issues);
            ValidateDecomposed(cfg.SetNudgeStep, "setNudgeStep", KnownNudgeSteps, issues);
            ValidateDecomposed(cfg.SetAttenuator, "setAttenuator", KnownAttenuatorLevels, issues);
            ValidateDecomposed(cfg.SetPreamp, "setPreamp", KnownPreampLevels, issues);
            ValidateDecomposed(cfg.SetAgc, "setAgc", KnownAgcSpeeds, issues);
            ValidateDecomposed(cfg.SetAfGain, "setAfGain", null, issues);
            ValidateDecomposed(cfg.SetNoiseReduction, "setNoiseReduction", SwitchWords, issues, allowLevels: true);
            ValidateDecomposed(cfg.SetNoiseBlanker, "setNoiseBlanker", SwitchWords, issues, allowLevels: true);
            ValidateDecomposed(cfg.SetProcessor, "setProcessor", SwitchWords, issues, allowLevels: true);
            ValidateDecomposed(cfg.SetNotch, "setNotch", KnownNotchModes, issues);
            ValidateDecomposed(cfg.SetApf, "setApf", KnownApfSettings, issues);
            ValidateDecomposed(cfg.SetRfGain, "setRfGain", null, issues);
            ValidateDecomposed(cfg.SetSquelch, "setSquelch", null, issues);
            ValidateDecomposed(cfg.SetTxPower, "setTxPower", null, issues);
            ValidateDecomposed(cfg.SetMicGain, "setMicGain", null, issues);

            if (cfg.SetFrequency == null || cfg.SetFrequency.Triggers.Count == 0)
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, "setFrequency.triggers",
                    "SetFrequency has no trigger phrases and will never trigger."));

            for (int i = 0; i < cfg.Macros.Count; i++)
            {
                var m = cfg.Macros[i];
                var path = $"macros[{i}]";

                if (string.IsNullOrWhiteSpace(m.Name))
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.name", "Custom command has no name."));

                if (m.Phrases == null || m.Phrases.Count == 0)
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.phrases",
                        $"'{(string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name)}' has no phrases and will never trigger."));
                else
                    CheckDuplicates(m.Phrases, path, seenPhrases, issues);

                if (string.IsNullOrWhiteSpace(m.Cat))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.cat",
                        $"'{(string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name)}' has no CI-V command."));
                }
                else if (!CivMacroCodec.TryParse(m.Cat, out var commands, out var parseError))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.cat",
                        $"'{m.Name}': {parseError}. Expected CI-V bytes in hex, ';' between commands — e.g. \"16 40 01;\" (NR on) or \"16 40 01;16 22 01;\"."));
                }
                else if (!advancedMode && FirstUntrustedCommand(commands) is byte untrusted)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.cat",
                        $"'{m.Name}': CI-V command {untrusted:X2} is outside the trusted set. " +
                        "Enable Settings → Voice Control → Advanced Mode to allow any CI-V command in Custom Commands."));
                }
            }
        }

        /// <summary>The command byte of the first command outside <see cref="TrustedCivCommands"/>, or null if all are trusted.</summary>
        private static byte? FirstUntrustedCommand(List<byte[]> commands)
        {
            foreach (var command in commands)
            {
                if (command.Length > 0 && !TrustedCivCommands.Contains(command[0]))
                    return command[0];
            }
            return null;
        }

        /// <summary>
        /// <paramref name="allowLevels"/> widens the closed set to also accept
        /// any whole 0–100 level key, for the controls that are a switch and a
        /// level in one (NR, NB, speech processor) and for the pure level
        /// ladders. The level range is a rule rather than an enumerated set
        /// because the numbers in a pack are the user's to extend.
        /// </summary>
        private static void ValidateDecomposed(DecomposedCommand? cmd, string prefix, HashSet<string>? knownKeys,
            List<ValidationIssue> issues, bool allowLevels = false)
        {
            if (cmd == null) return;

            if (cmd.Vocabulary.Count > 0 && cmd.Triggers.Count == 0)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning, $"{prefix}.triggers",
                    $"'{prefix}' has vocabulary but no trigger phrases — it will be skipped entirely."));
            }

            foreach (var (key, words) in cmd.Vocabulary)
            {
                if (words == null || words.Count == 0)
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Warning, $"{prefix}.vocabulary.{key}",
                        $"'{prefix}' value '{key}' has no spoken words and will never trigger."));
                }

                // Closed-set commands: an unknown key is a warning, not an
                // error — it might be authored against a newer app version
                // that added a value this build doesn't recognise yet.
                if (knownKeys != null && !knownKeys.Contains(key) && !(allowLevels && IsLevelKey(key)))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Warning, $"{prefix}.vocabulary.{key}",
                        $"'{key}' isn't a value '{prefix}' currently understands — this app version will ignore it if spoken."));
                }
            }
        }

        private static void CheckDuplicates(List<string> phrases, string path, Dictionary<string, string> seen, List<ValidationIssue> issues)
        {
            foreach (var phrase in phrases)
            {
                if (string.IsNullOrWhiteSpace(phrase)) continue;
                var norm = phrase.Trim();
                if (seen.TryGetValue(norm, out var firstPath))
                {
                    if (firstPath != path)
                        issues.Add(new ValidationIssue(ValidationSeverity.Warning, path,
                            $"Phrase \"{norm}\" is also used by {firstPath} — whichever compiles first will win, so the other will never match."));
                }
                else
                {
                    seen[norm] = path;
                }
            }
        }

    }
}
