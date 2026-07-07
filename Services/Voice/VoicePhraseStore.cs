using System.Text.Json;
using System.Text.Json.Serialization;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Reads and writes voice_phrases.json from the user's AppData folder.
    /// Falls back to built-in defaults when no file exists or the version is older
    /// than the current schema (version 3 introduced DecomposedCommand for SetMode/SetBand
    /// and merged Verbs+Connectors into Triggers for SetFrequency).
    /// </summary>
    public sealed class VoicePhraseStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control", "voice_phrases.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public VoicePhrasesConfig Load()
        {
            if (!File.Exists(FilePath))
                return BuildDefaults();

            try
            {
                var json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize<VoicePhrasesConfig>(json, _jsonOpts);
                // Version 3 changed SetMode/SetBand to DecomposedCommand and merged
                // Verbs+Connectors into Triggers for SetFrequency. Older files are
                // reset to defaults rather than partially migrated.
                if (config == null || config.Version < 6)
                    return BuildDefaults();
                return config;
            }
            catch
            {
                return BuildDefaults();
            }
        }

        public void Save(VoicePhrasesConfig config)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, _jsonOpts));
        }

        public static VoicePhrasesConfig BuildDefaults() => new()
        {
            Version = 6,
            SimpleCommands = new()
            {
                ["SwapVFO"]          = ["swap v f o", "swap v f os", "swap a and b", "swap a b", "switch v f o", "switch a and b"],
                ["NudgeUp"]          = ["tune up", "step up", "frequency up", "up one step", "tune higher", "nudge up", "nudge higher"],
                ["NudgeDown"]        = ["tune down", "step down", "frequency down", "down one step", "tune lower", "nudge down", "nudge lower"],
                ["BandUp"]           = ["band up", "next band", "up one band"],
                ["BandDown"]         = ["band down", "previous band", "down one band"],
                ["StatusFrequency"]  = ["what frequency", "what is my frequency", "read frequency", "current frequency"],
                ["StatusMode"]       = ["what mode", "what is my mode", "read mode", "current mode"],
                ["StatusBand"]       = ["what band", "what band am I on", "current band"],
                ["TxOn"]             = ["key transmitter", "start transmitting", "transmit now"],
                ["TxOff"]            = ["stop transmitting", "go to receive", "transmit off"],
                ["SplitOn"]          = ["split on", "enable split", "split transmit"],
                ["SplitOff"]         = ["split off", "disable split", "simplex"],
                ["Help"]             = ["help", "command help", "what can I say", "list commands"],
                ["NudgeIfWidthUp"]   = ["filter wider", "wider filter", "increase bandwidth", "i f width up"],
                ["NudgeIfWidthDown"] = ["filter narrower", "narrower filter", "decrease bandwidth", "i f width down"],
            },
            SetMode = new()
            {
                Triggers   = ["mode", "set mode"],
                Vocabulary = new()
                {
                    ["USB"]    = ["u s b", "upper sideband"],
                    ["LSB"]    = ["l s b", "lower sideband"],
                    ["CW-U"]   = ["c w", "see double you"],
                    ["AM"]     = ["a m"],
                    ["FM"]     = ["f m"],
                    ["DATA-U"] = ["data", "digital"],
                    ["DATA-L"] = ["data l"],
                    ["RTTY-L"] = ["r t t y"],
                },
            },
            SetBand = new()
            {
                Triggers   = ["go to", "switch to"],
                Vocabulary = new()
                {
                    ["160"] = ["one sixty metres"],
                    ["80"]  = ["eighty metres"],
                    ["60"]  = ["sixty metres"],
                    ["40"]  = ["forty metres"],
                    ["30"]  = ["thirty metres"],
                    ["20"]  = ["twenty metres"],
                    ["17"]  = ["seventeen metres"],
                    ["15"]  = ["fifteen metres"],
                    ["12"]  = ["twelve metres"],
                    ["10"]  = ["ten metres"],
                    ["6"]   = ["six metres"],
                    ["4"]   = ["four metres"],
                },
            },
            Macros = new()
            {
                new() { Name = "Copy A to B",    Phrases = ["copy a to b", "a to b"],         Cat = "AB;" },
                new() { Name = "Copy B to A",    Phrases = ["copy b to a", "b to a"],         Cat = "BA;" },
                new() { Name = "Fine step up",   Phrases = ["fine up", "fine step up"],       Cat = "UP;" },
                new() { Name = "Fine step down", Phrases = ["fine down", "fine step down"],   Cat = "DN;" },
                new() { Name = "NR on",           Phrases = ["noise reduction on", "n r on"],           Cat = "NR01;" },
                new() { Name = "NR off",          Phrases = ["noise reduction off", "n r off"],          Cat = "NR00;" },
                new() { Name = "NB on",           Phrases = ["noise blanker on", "n b on"],              Cat = "NB01;" },
                new() { Name = "NB off",          Phrases = ["noise blanker off", "n b off"],            Cat = "NB00;" },
                // Roofing filter — CAT strings are FTdx101 specific; verify and adjust if needed.
                new() { Name = "Roofing 3 kHz",   Phrases = ["roofing three kilohertz", "roofing three k"],   Cat = "RF03;" },
                new() { Name = "Roofing 6 kHz",   Phrases = ["roofing six kilohertz", "roofing six k"],       Cat = "RF04;" },
                new() { Name = "Roofing 12 kHz",  Phrases = ["roofing twelve kilohertz", "roofing twelve k"], Cat = "RF05;" },
            },
            SetAttenuator = new()
            {
                Triggers   = ["attenuator", "set attenuator", "a t t"],
                Vocabulary = new()
                {
                    ["off"] = ["off"],
                    ["6"]   = ["six d b"],
                    ["12"]  = ["twelve d b"],
                    ["18"]  = ["eighteen d b"],
                },
            },
            SetPreamp = new()
            {
                Triggers   = ["preamp", "set preamp", "preamplifier"],
                Vocabulary = new()
                {
                    ["off"] = ["off", "i p o"],
                    ["1"]   = ["one", "amp one"],
                    ["2"]   = ["two", "amp two"],
                },
            },
            SetAgc = new()
            {
                Triggers   = ["a g c", "set a g c"],
                Vocabulary = new()
                {
                    ["off"]  = ["off"],
                    ["fast"] = ["fast"],
                    ["mid"]  = ["medium", "mid"],
                    ["slow"] = ["slow"],
                    ["auto"] = ["auto", "automatic"],
                },
            },
            SetAfGain = new()
            {
                Triggers   = ["a f gain", "audio gain", "set a f gain"],
                Vocabulary = new()
                {
                    ["0"]   = ["zero", "mute", "silent"],
                    ["10"]  = ["ten"],
                    ["20"]  = ["twenty"],
                    ["25"]  = ["twenty five"],
                    ["30"]  = ["thirty"],
                    ["40"]  = ["forty"],
                    ["50"]  = ["fifty"],
                    ["60"]  = ["sixty"],
                    ["70"]  = ["seventy"],
                    ["75"]  = ["seventy five"],
                    ["80"]  = ["eighty"],
                    ["90"]  = ["ninety"],
                    ["100"] = ["one hundred", "maximum", "full"],
                },
            },
            SetNudgeStep = new()
            {
                Triggers   = ["set step", "step size", "nudge step"],
                Vocabulary = new()
                {
                    ["10"]     = ["ten hertz"],
                    ["100"]    = ["one hundred hertz", "hundred hertz"],
                    ["1000"]   = ["one kilohertz", "one k"],
                    ["10000"]  = ["ten kilohertz", "ten k"],
                    ["100000"] = ["one hundred kilohertz", "hundred k"],
                },
            },
            SetFrequency = new()
            {
                Triggers   = ["tune to", "set frequency to", "set the frequency to", "tune tae"],
                Megahertz  = ["megahertz", "mega hertz"],
                Point      = ["point"],
                FracDigits = ["zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"],
                Mhz        = new()
                {
                    ["1"]  = ["one"],   ["2"]  = ["two"],   ["3"]  = ["three"],
                    ["4"]  = ["four"],  ["5"]  = ["five"],  ["6"]  = ["six"],
                    ["7"]  = ["seven"], ["8"]  = ["eight"], ["9"]  = ["nine"],
                    ["10"] = ["ten"],   ["11"] = ["eleven"],["12"] = ["twelve"],
                    ["13"] = ["thirteen"], ["14"] = ["fourteen"], ["15"] = ["fifteen"],
                    ["16"] = ["sixteen"],  ["17"] = ["seventeen"],["18"] = ["eighteen"],
                    ["19"] = ["nineteen"], ["20"] = ["twenty"],
                    ["21"] = ["twenty one"],  ["24"] = ["twenty four"],
                    ["28"] = ["twenty eight"],["29"] = ["twenty nine"],
                    ["30"] = ["thirty"],
                    ["50"] = ["fifty"],  ["51"] = ["fifty one"], ["52"] = ["fifty two"],
                    ["70"] = ["seventy"],["71"] = ["seventy one"],
                },
            },
        };
    }
}
