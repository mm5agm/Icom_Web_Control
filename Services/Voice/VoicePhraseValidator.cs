using Icom_Web_Control.Models;

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
    ///   malformed macro CAT strings, unknown vocabulary keys for closed-set
    ///   commands (reported as warnings, not errors, so a pack authored
    ///   against a newer app version that added a value this app doesn't
    ///   know about yet still installs).
    ///
    /// The CAT allowlist / Advanced Mode check (§5.5): unless
    /// <c>advancedMode</c> is true, a Custom Command's CAT string must start
    /// with a prefix one of the built-in Core Commands (or a shipped
    /// default macro) already sends — a Custom Command can only recombine
    /// primitives the app already trusts, not send anything new. This is
    /// what stands between an imported/shared voice pack and "can it damage
    /// my radio" — see ApplicationSettings.VoiceAdvancedModeEnabled.
    /// </summary>
    public static class VoicePhraseValidator
    {
        private static readonly HashSet<string> KnownBandMetres = ["160", "80", "60", "40", "30", "20", "17", "15", "12", "10", "6", "4"];
        private static readonly HashSet<string> KnownAttenuatorLevels = ["off", "6", "12", "18"];
        private static readonly HashSet<string> KnownPreampLevels = ["off", "1", "2"];
        private static readonly HashSet<string> KnownAgcSpeeds = ["off", "fast", "mid", "slow", "auto"];
        private static readonly HashSet<string> KnownNudgeSteps = ["10", "100", "1000", "10000", "100000"];

        // Prefixes IntentDispatcher's own Core Commands already send
        // (FA/FB, MD, SV, TX0/RX, FT0/FT1, SH, AG0, RA, PA, GT), plus the
        // prefixes used by the shipped default Custom Commands (NR, NB, AB,
        // BA, UP, DN, RF) — those ship trusted by default, so Advanced-Mode-
        // off treats them the same as Core Command primitives.
        private static readonly string[] TrustedCatPrefixes =
        [
            "FA", "FB", "MD", "SV", "TX", "RX", "FT", "SH", "AG",
            "RA", "PA", "GT", "NR", "NB", "AB", "BA", "UP", "DN", "RF",
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
            if (cfg.Version < 6)
                issues.Add(new ValidationIssue(ValidationSeverity.Error, "version",
                    $"Schema version {cfg.Version} predates the current format (6) and cannot be validated."));

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
                        $"'{(string.IsNullOrWhiteSpace(m.Name) ? "(unnamed)" : m.Name)}' has no CAT string."));
                }
                else if (!LooksLikeCatCommand(m.Cat))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.cat",
                        $"'{m.Name}': CAT string '{m.Cat}' doesn't look valid — expected letters/digits ending in ';', e.g. \"NR01;\" or \"NR01;NB01;\"."));
                }
                else if (!advancedMode && !HasOnlyTrustedPrefixes(m.Cat))
                {
                    issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{path}.cat",
                        $"'{m.Name}': CAT string '{m.Cat}' uses a command prefix outside the trusted set. " +
                        "Enable Settings → Voice Control → Advanced Mode to allow any CAT command in Custom Commands."));
                }
            }
        }

        private static bool HasOnlyTrustedPrefixes(string cat)
        {
            foreach (var segment in cat.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TrustedCatPrefixes.Any(p => segment.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
            return true;
        }

        private static void ValidateDecomposed(DecomposedCommand? cmd, string prefix, HashSet<string>? knownKeys, List<ValidationIssue> issues)
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
                if (knownKeys != null && !knownKeys.Contains(key))
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

        private static bool LooksLikeCatCommand(string cat)
        {
            // Conservative shape check only (no allowlist yet — see class
            // remarks): letters, digits and semicolons, must end with ';'.
            if (!cat.EndsWith(';')) return false;
            foreach (var c in cat)
            {
                if (!char.IsLetterOrDigit(c) && c != ';')
                    return false;
            }
            return true;
        }
    }
}
