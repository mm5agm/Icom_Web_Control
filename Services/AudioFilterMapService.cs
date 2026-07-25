using System.Text.Json;
using System.Text.Json.Serialization;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Loads the per-radio audio-filter EX address map from
    /// wwwroot/data/audio-filter-ex-map.json and exposes lookups for the
    /// AudioFilter popout controller. The radio stores LCUT/HCUT/slopes
    /// per mode class (SSB, AM, FM, DATA, RTTY, CW), not per VFO, so the
    /// {VFO} disambiguator the controller takes only selects which VFO's
    /// current mode to look up; the EX address is per-mode-class.
    /// </summary>
    public class AudioFilterMapService
    {
        private readonly ILogger<AudioFilterMapService> _logger;
        private readonly AudioFilterMap _map;

        public AudioFilterMapService(IWebHostEnvironment env, ILogger<AudioFilterMapService> logger)
        {
            _logger = logger;
            var path = Path.Combine(env.ContentRootPath, "wwwroot", "data", "audio-filter-ex-map.json");
            try
            {
                var json = File.ReadAllText(path);
                _map = JsonSerializer.Deserialize<AudioFilterMap>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })!;
                _logger.LogInformation("Audio filter EX map loaded: {Count} radios", _map.Radios.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load audio filter EX map from {Path}", path);
                _map = new AudioFilterMap();
            }
        }

        /// <summary>
        /// Maps YWC's friendly mode-name string (as stored in RadioStateService.ModeA/B)
        /// to one of the six mode-class keys used in the JSON map.
        /// Covers the modes reported by every supported radio's MD command.
        /// Returns null if the mode isn't recognised.
        /// </summary>
        public static string? ModeClassFor(string? friendlyMode)
        {
            if (string.IsNullOrEmpty(friendlyMode)) return null;
            return friendlyMode.ToUpperInvariant() switch
            {
                "LSB" or "USB"
                    => "SSB",
                "CW" or "CW-U" or "CW-L" or "CW-R"
                    => "CW",
                "AM" or "AM-N"
                    => "AM",
                "FM" or "FM-N"
                    => "FM",
                "RTTY-L" or "RTTY-U" or "FSK" or "FSK-R" or "RTTY-LSB" or "RTTY-USB"
                    => "RTTY",
                "DATA-L" or "DATA-U" or "DATA-FM" or "DATA-FM-N" or "DATA-LSB" or "DATA-USB"
                    or "PSK" or "PKT-L" or "PKT-U" or "PKT-FM"
                    => "DATA",
                _ => null,
            };
        }

        /// <summary>
        /// Whether YWC has an EX address map for the given radio model.
        /// </summary>
        public bool IsRadioSupported(string radioModel)
            => !string.IsNullOrEmpty(radioModel) && _map.Radios.ContainsKey(radioModel);

        /// <summary>
        /// Returns the EX-format type for a radio ("hierarchical" for FTdx101/FTdx10/FT-710,
        /// "flat" for FTDX3000/FT-991A). Returns null if the radio isn't in the map.
        /// </summary>
        public string? GetExFormat(string radioModel)
            => _map.Radios.TryGetValue(radioModel, out var r) ? r.ExFormat : null;

        /// <summary>
        /// Returns the EX address for a specific cell (radio, mode class, setting),
        /// or null if that cell isn't supported on the given radio (e.g. FM on
        /// FT-991A, RTTY LCUT SLOPE on FTDX3000).
        /// </summary>
        public AudioFilterAddress? GetAddress(string radioModel, string modeClass, string setting)
        {
            if (!_map.Radios.TryGetValue(radioModel, out var radio)) return null;
            if (!radio.Modes.TryGetValue(modeClass, out var modeMap)) return null;
            return setting switch
            {
                "lcutFreq" => modeMap.LcutFreq,
                "lcutSlope" => modeMap.LcutSlope,
                "hcutFreq" => modeMap.HcutFreq,
                "hcutSlope" => modeMap.HcutSlope,
                _ => null,
            };
        }

        /// <summary>
        /// Returns the global value-range definitions (off code, min/max Hz, codes, etc).
        /// </summary>
        public AudioFilterValueRanges ValueRanges => _map.ValueRanges;

        /// <summary>
        /// Builds the CAT *read* command for a given EX address.
        /// Hierarchical: "EX{P1}{P2}{P3};". Flat: "EX{ID};".
        /// </summary>
        public string BuildReadCommand(string radioModel, AudioFilterAddress addr)
        {
            if (GetExFormat(radioModel) == "flat")
                return $"EX{addr.Id};";
            return $"EX{addr.P1}{addr.P2}{addr.P3};";
        }

        /// <summary>
        /// Builds the CAT *set* command for a given EX address and value code.
        /// Hierarchical: "EX{P1}{P2}{P3}{value};". Flat: "EX{ID}{value};".
        /// Caller is responsible for formatting the value code to the right
        /// number of digits (see ValueRanges).
        /// </summary>
        public string BuildSetCommand(string radioModel, AudioFilterAddress addr, string valueCode)
        {
            if (GetExFormat(radioModel) == "flat")
                return $"EX{addr.Id}{valueCode};";
            return $"EX{addr.P1}{addr.P2}{addr.P3}{valueCode};";
        }

        /// <summary>
        /// Parses the value code (the P4 / parameter portion) from an EX answer
        /// string. Returns null if the response is malformed or doesn't match
        /// the expected address. Strips the EX prefix, the address, and the
        /// trailing semicolon, returning what's left.
        /// </summary>
        public string? ParseAnswerValueCode(string radioModel, AudioFilterAddress addr, string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;
            // Strip trailing semicolon if present (multiplexer usually removes
            // it, but defensively handle either).
            var body = response.TrimEnd(';');
            // Strip EX prefix.
            if (!body.StartsWith("EX", StringComparison.OrdinalIgnoreCase)) return null;
            body = body.Substring(2);
            // Strip the address that should follow the prefix.
            string expectedAddr = GetExFormat(radioModel) == "flat"
                ? addr.Id ?? ""
                : (addr.P1 ?? "") + (addr.P2 ?? "") + (addr.P3 ?? "");
            if (string.IsNullOrEmpty(expectedAddr)) return null;
            if (!body.StartsWith(expectedAddr, StringComparison.OrdinalIgnoreCase)) return null;
            // The rest is the value code.
            return body.Substring(expectedAddr.Length);
        }
    }

    // ── JSON shape ───────────────────────────────────────────────────────────

    public class AudioFilterMap
    {
        [JsonPropertyName("valueRanges")]
        public AudioFilterValueRanges ValueRanges { get; set; } = new();

        [JsonPropertyName("radios")]
        public Dictionary<string, AudioFilterRadio> Radios { get; set; } = new();
    }

    public class AudioFilterValueRanges
    {
        [JsonPropertyName("lcutFreq")]
        public AudioFilterFreqRange LcutFreq { get; set; } = new();

        [JsonPropertyName("hcutFreq")]
        public AudioFilterFreqRange HcutFreq { get; set; } = new();

        [JsonPropertyName("slope")]
        public AudioFilterSlopeRange Slope { get; set; } = new();
    }

    public class AudioFilterFreqRange
    {
        [JsonPropertyName("off")]
        public string Off { get; set; } = "00";

        [JsonPropertyName("min")]
        public AudioFilterFreqPoint Min { get; set; } = new();

        [JsonPropertyName("max")]
        public AudioFilterFreqPoint Max { get; set; } = new();

        [JsonPropertyName("stepHz")]
        public int StepHz { get; set; }

        [JsonPropertyName("digits")]
        public int Digits { get; set; }
    }

    public class AudioFilterFreqPoint
    {
        [JsonPropertyName("hz")]
        public int Hz { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = "";
    }

    public class AudioFilterSlopeRange
    {
        [JsonPropertyName("digits")]
        public int Digits { get; set; }

        [JsonPropertyName("options")]
        public List<AudioFilterSlopeOption> Options { get; set; } = new();
    }

    public class AudioFilterSlopeOption
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }

    public class AudioFilterRadio
    {
        [JsonPropertyName("exFormat")]
        public string ExFormat { get; set; } = "hierarchical";

        [JsonPropertyName("modes")]
        public Dictionary<string, AudioFilterMode> Modes { get; set; } = new();
    }

    public class AudioFilterMode
    {
        [JsonPropertyName("lcutFreq")]
        public AudioFilterAddress? LcutFreq { get; set; }

        [JsonPropertyName("lcutSlope")]
        public AudioFilterAddress? LcutSlope { get; set; }

        [JsonPropertyName("hcutFreq")]
        public AudioFilterAddress? HcutFreq { get; set; }

        [JsonPropertyName("hcutSlope")]
        public AudioFilterAddress? HcutSlope { get; set; }
    }

    public class AudioFilterAddress
    {
        [JsonPropertyName("p1")]
        public string? P1 { get; set; }

        [JsonPropertyName("p2")]
        public string? P2 { get; set; }

        [JsonPropertyName("p3")]
        public string? P3 { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
