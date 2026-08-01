using System.Text.Json.Serialization;

namespace Icom_Web_Control.Models
{
    /// <summary>
    /// User-editable voice phrase configuration persisted to
    /// %APPDATA%\MM5AGM\Icom Web Control\voice_phrases.json.
    /// </summary>
    public sealed class VoicePhrasesConfig
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 3;

        /// <summary>Key = intent name (e.g. "SwapVFO"), value = complete trigger phrases.</summary>
        [JsonPropertyName("simpleCommands")]
        public Dictionary<string, List<string>> SimpleCommands { get; set; } = new();

        /// <summary>
        /// SetMode command: user says one of the triggers followed by a vocabulary word.
        /// e.g. "set mode" + "u s b" → SetMode:USB.
        /// </summary>
        [JsonPropertyName("setMode")]
        public DecomposedCommand SetMode { get; set; } = new();

        /// <summary>
        /// SetBand command: user says one of the triggers followed by a vocabulary word.
        /// e.g. "go to" + "eighty metres" → SetBand:80.
        /// </summary>
        [JsonPropertyName("setBand")]
        public DecomposedCommand SetBand { get; set; } = new();

        /// <summary>
        /// SetNudgeStep command: user says one of the triggers followed by a step-size word.
        /// e.g. "set step" + "ten kilohertz" → SetNudgeStep:10000.
        /// </summary>
        [JsonPropertyName("setNudgeStep")]
        public DecomposedCommand SetNudgeStep { get; set; } = new();

        [JsonPropertyName("setAttenuator")]
        public DecomposedCommand SetAttenuator { get; set; } = new();

        [JsonPropertyName("setPreamp")]
        public DecomposedCommand SetPreamp { get; set; } = new();

        [JsonPropertyName("setAgc")]
        public DecomposedCommand SetAgc { get; set; } = new();

        /// <summary>
        /// SetAfGain command: user says a trigger then a level (0–100).
        /// e.g. "a f gain fifty" → sets AF gain to 50 % of maximum.
        /// </summary>
        [JsonPropertyName("setAfGain")]
        public DecomposedCommand SetAfGain { get; set; } = new();

        /// <summary>
        /// SetFrequency still needs structured decomposition because MHz values
        /// are a large enumerated set with optional fractional digits.
        /// Only the trigger phrases and number words need translating.
        /// </summary>
        [JsonPropertyName("setFrequency")]
        public SetFrequencyPhrases SetFrequency { get; set; } = new();

        /// <summary>
        /// User-defined macros. Each sends one or more CAT commands verbatim.
        /// Power users can add their own; the defaults cover common shortcuts.
        /// </summary>
        [JsonPropertyName("macros")]
        public List<MacroDefinition> Macros { get; set; } = new();
    }

    /// <summary>
    /// A command decomposed into a shared trigger and a per-value vocabulary.
    /// User says: [trigger] [vocabulary word] — e.g. "mode u s b" or "go to eighty metres".
    /// The trigger is consumed (not semantic); the vocabulary word encodes the value.
    /// </summary>
    public sealed class DecomposedCommand
    {
        /// <summary>Phrases that start the command. e.g. ["mode", "set mode"].</summary>
        [JsonPropertyName("triggers")]
        public List<string> Triggers { get; set; } = new();

        /// <summary>
        /// Key = parameter value (e.g. "USB", "80"), value = spoken words for that value.
        /// e.g. "USB" → ["u s b", "upper sideband"].
        /// </summary>
        [JsonPropertyName("vocabulary")]
        public Dictionary<string, List<string>> Vocabulary { get; set; } = new();
    }

    /// <summary>
    /// A user-defined voice command that sends one or more CAT strings.
    /// Multi-command macros: chain commands back-to-back e.g. "NR01;NB01;".
    /// The dispatcher splits on ';' and sends each segment.
    /// </summary>
    public sealed class MacroDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("phrases")]
        public List<string> Phrases { get; set; } = new();

        /// <summary>One or more CAT commands, e.g. "NR01;" or "NR01;NB01;".</summary>
        [JsonPropertyName("cat")]
        public string Cat { get; set; } = "";

        /// <summary>
        /// Free-text grouping shown in the Settings "Custom Commands" editor
        /// (e.g. "Noise Reduction", "Custom CAT commands"). Defaults to
        /// "Macros" for anything created before this field existed. Purely
        /// presentational — has no effect on recognition or dispatch.
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = "Macros";
    }

    public sealed class SetFrequencyPhrases
    {
        /// <summary>
        /// Complete trigger phrases including any connector word.
        /// e.g. ["tune to", "set frequency to", "set the frequency to"].
        /// Include the connector ("to") in the phrase so the user only edits one field.
        /// </summary>
        [JsonPropertyName("triggers")]
        public List<string> Triggers { get; set; } = new();

        /// <summary>Optional suffix word(s) after the MHz number. e.g. ["megahertz"].</summary>
        [JsonPropertyName("megahertz")]
        public List<string> Megahertz { get; set; } = new();

        [JsonPropertyName("point")]
        public List<string> Point { get; set; } = new();

        /// <summary>Key = integer MHz value as string (e.g. "14"), value = spoken word(s).</summary>
        [JsonPropertyName("mhz")]
        public Dictionary<string, List<string>> Mhz { get; set; } = new();

        /// <summary>Digit words used for fractional MHz (0-9).</summary>
        [JsonPropertyName("fracDigits")]
        public List<string> FracDigits { get; set; } = new();
    }
}
