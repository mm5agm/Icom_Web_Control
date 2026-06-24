using Microsoft.Extensions.Logging;
using Yaesu_Web_Control.Services;

namespace Yaesu_Web_Control.Services.Voice
{
    /// <summary>
    /// Maps a recognised semantic intent + parameter dictionary (from the
    /// SRGS grammar's <c>out.intent</c> tags) to CAT commands sent via
    /// <see cref="ICatClient"/>. Voice commands always target VFO A per
    /// the v1 plan — FTdx10 / FT-710 are single-receiver anyway, and the
    /// dual-receiver case (FTdx101) can be extended in v2 if needed.
    /// </summary>
    public sealed class IntentDispatcher
    {
        private readonly ILogger<IntentDispatcher> _logger;
        private readonly ICatClient _catClient;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;

        public IntentDispatcher(
            ILogger<IntentDispatcher> logger,
            ICatClient catClient,
            RadioStateService state,
            ISettingsService settings)
        {
            _logger = logger;
            _catClient = catClient;
            _state = state;
            _settings = settings;
        }

        /// <summary>
        /// Dispatch a recognised intent. Returns true if the intent was
        /// known AND successfully sent to the radio; false if the intent
        /// name is unknown or arguments are invalid.
        /// </summary>
        public async Task<DispatchResult> DispatchAsync(
            string intent,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation(
                "[IntentDispatcher] intent={Intent} params={@Params}",
                intent, parameters);

            try
            {
                switch (intent)
                {
                    case "SetFrequency":  return await SetFrequencyAsync(parameters, cancellationToken);
                    case "SetBand":       return await SetBandAsync(parameters, cancellationToken);
                    case "SetMode":       return await SetModeAsync(parameters, cancellationToken);
                    case "SwapVFO":       return await SwapVfoAsync(cancellationToken);
                    case "NudgeFrequency": return await NudgeFrequencyAsync(parameters, cancellationToken);
                    default:
                        _logger.LogWarning("[IntentDispatcher] Unknown intent: {Intent}", intent);
                        return new DispatchResult(false, "Unknown command");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IntentDispatcher] Failed to dispatch intent {Intent}", intent);
                return new DispatchResult(false, intent);
            }
        }

        // -- SetFrequency --------------------------------------------------

        private async Task<DispatchResult> SetFrequencyAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "hz", out var hz))
            {
                _logger.LogWarning("[Voice] SetFrequency missing/invalid hz parameter");
                return new DispatchResult(false, "Set frequency");
            }
            var phrase = $"Move to {FormatFrequencyForSpeech(hz)}";
            // 30 kHz - 75 MHz: same bounds as the HTTP SetFrequencyA endpoint.
            if (hz < 30_000 || hz > 75_000_000)
            {
                _logger.LogWarning("[Voice] SetFrequency {Hz} out of range", hz);
                return new DispatchResult(false, phrase);
            }
            var command = $"FA{hz:D9};";
            await _catClient.SendCommandAsync(command, "Voice", ct);
            _state.FrequencyA = hz;
            _logger.LogInformation("[Voice] SetFrequency -> {Hz} Hz", hz);
            return new DispatchResult(true, phrase);
        }

        // -- SetBand -------------------------------------------------------

        // Band-default frequencies: typical FT8/data hangout in each amateur
        // band, plus standard SSB calling frequencies for bands where FT8
        // isn't the obvious centre. These are intentional "good starting
        // point" values; user can fine-tune with NudgeFrequency or
        // SetFrequency afterwards.
        private static readonly Dictionary<long, long> BandDefaultsHz = new()
        {
            [160] =  1_840_000,
            [80]  =  3_573_000,
            [60]  =  5_357_000,
            [40]  =  7_074_000,
            [30]  = 10_136_000,
            [20]  = 14_074_000,
            [17]  = 18_100_000,
            [15]  = 21_074_000,
            [12]  = 24_915_000,
            [10]  = 28_074_000,
            [6]   = 50_313_000,
            [4]   = 70_154_000,
        };

        private async Task<DispatchResult> SetBandAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "metres", out var metres))
            {
                _logger.LogWarning("[Voice] SetBand missing/invalid metres parameter");
                return new DispatchResult(false, "Change band");
            }
            var phrase = $"Move to {metres} metres";
            if (!BandDefaultsHz.TryGetValue(metres, out var hz))
            {
                _logger.LogWarning("[Voice] SetBand: no default frequency for {Metres}m", metres);
                return new DispatchResult(false, phrase);
            }
            var command = $"FA{hz:D9};";
            await _catClient.SendCommandAsync(command, "Voice", ct);
            _state.FrequencyA = hz;
            _logger.LogInformation("[Voice] SetBand -> {Metres}m -> {Hz} Hz", metres, hz);
            return new DispatchResult(true, phrase);
        }

        // -- SetMode -------------------------------------------------------

        private async Task<DispatchResult> SetModeAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("mode", out var modeObj) || modeObj is not string mode)
            {
                _logger.LogWarning("[Voice] SetMode missing/invalid mode parameter");
                return new DispatchResult(false, "Set mode");
            }
            // CatCommands.FormatMode handles the Yaesu-mode-string -> MD code
            // translation. Voice always targets VFO A in v1.
            var command = CatCommands.FormatMode(mode, isSubVfo: false);
            await _catClient.SendCommandAsync(command, "Voice", ct);
            _state.ModeA = mode;
            _logger.LogInformation("[Voice] SetMode -> {Mode}", mode);
            return new DispatchResult(true, $"Mode {ModeForSpeech(mode)}");
        }

        // -- SwapVFO -------------------------------------------------------

        private async Task<DispatchResult> SwapVfoAsync(CancellationToken ct)
        {
            var settings = await _settings.GetSettingsAsync();
            var isSingleReceiver = RadioCapabilities.IsSingleReceiver(settings.RadioModel);
            const string phrase = "Swap V F O";

            if (isSingleReceiver)
            {
                // Single-receiver radios (FTdx10/FT-710): no atomic SV;
                // command. Fake the swap by reading both stored frequencies
                // and writing them back swapped. Simpler than toggling VS,
                // and matches what the radio actually does when the user
                // presses A/B on the front panel.
                var faRaw = await _catClient.SendCommandAsync("FA;", "Voice", ct);
                var fbRaw = await _catClient.SendCommandAsync("FB;", "Voice", ct);
                if (!TryParseFreqResponse(faRaw, "FA", out var fa) ||
                    !TryParseFreqResponse(fbRaw, "FB", out var fb))
                {
                    _logger.LogWarning("[Voice] SwapVFO: couldn't parse FA/FB readback");
                    return new DispatchResult(false, phrase);
                }
                await _catClient.SendCommandAsync($"FA{fb:D9};", "Voice", ct);
                await _catClient.SendCommandAsync($"FB{fa:D9};", "Voice", ct);
                _state.FrequencyA = fb;
                _state.FrequencyB = fa;
                _logger.LogInformation("[Voice] SwapVFO (fake) -> A={A}, B={B}", fb, fa);
                return new DispatchResult(true, phrase);
            }
            else
            {
                // Dual-receiver (FTdx101): atomic SV; — the radio handles the
                // swap and broadcasts new FA/FB via auto-info.
                await _catClient.SendCommandAsync("SV;", "Voice", ct);
                _logger.LogInformation("[Voice] SwapVFO (SV;)");
                return new DispatchResult(true, phrase);
            }
        }

        // -- NudgeFrequency ------------------------------------------------

        // Fixed step for v1. Future: read the frontend's currently-selected
        // digit and use that step. 10 kHz was picked over 1 kHz so each
        // "tune up" / "nudge up" press produces a visible movement on the
        // display -- 1 kHz only changes the rightmost-but-three digit and
        // is easy to miss when you're driving by voice rather than watching
        // the screen. If 10 kHz turns out to be too coarse for SSB use, the
        // v2 "step by selected digit" plan is the proper fix.
        private const long NudgeStepHz = 10_000;

        private async Task<DispatchResult> NudgeFrequencyAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "direction", out var direction) || (direction != 1 && direction != -1))
            {
                _logger.LogWarning("[Voice] NudgeFrequency invalid direction");
                return new DispatchResult(false, "Tune");
            }
            var phrase = direction > 0 ? "Tune up" : "Tune down";
            var current = _state.FrequencyA;
            var next = current + direction * NudgeStepHz;
            if (next < 30_000 || next > 75_000_000)
            {
                _logger.LogWarning("[Voice] NudgeFrequency would go out of range ({Next})", next);
                return new DispatchResult(false, phrase);
            }
            await _catClient.SendCommandAsync($"FA{next:D9};", "Voice", ct);
            _state.FrequencyA = next;
            _logger.LogInformation("[Voice] NudgeFrequency {Dir} -> {Hz} Hz",
                direction > 0 ? "up" : "down", next);
            return new DispatchResult(true, phrase);
        }

        // -- helpers -------------------------------------------------------

        /// <summary>
        /// SRGS semantic values come back as boxed primitives — int when the
        /// grammar tag does integer math, double if any decimal slipped in,
        /// string for explicit string assignments. Normalise to long.
        /// </summary>
        private static bool TryGetLong(
            IReadOnlyDictionary<string, object> args, string key, out long value)
        {
            value = 0;
            if (!args.TryGetValue(key, out var obj) || obj == null) return false;
            try
            {
                value = Convert.ToInt64(obj);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Parses "FA01407400000;" or "FA01407400000" into a long Hz value.</summary>
        private static bool TryParseFreqResponse(string? raw, string prefix, out long hz)
        {
            hz = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var trimmed = raw.Trim().TrimEnd(';');
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return long.TryParse(trimmed.AsSpan(prefix.Length), out hz);
        }

        // ── Speech-formatting helpers for the spoken confirmation ─────────
        //
        // TTS handles raw numbers ("14.074 megahertz") inconsistently across
        // installed voices -- one voice says "fourteen point oh seven four",
        // another says "fourteen point seventy-four". We spell the value out
        // digit-by-digit so the spoken confirmation matches what the user
        // said (digit-by-digit fractional MHz is the YWC v1 grammar).

        private static string FormatFrequencyForSpeech(long hz)
        {
            var mhz = hz / 1_000_000;
            var fracKhz = (hz % 1_000_000) / 1000;  // 0-999
            if (fracKhz == 0)
                return $"{NumberWord(mhz)} megahertz";
            // Pad to 3 digits and speak each digit separately so "074" reads
            // as "zero seven four", matching the grammar's digit-by-digit
            // form ("point zero seven four").
            var fracDigits = fracKhz.ToString("D3");
            var spelled = string.Join(" ", fracDigits.Select(c => DigitWord(c - '0')));
            return $"{NumberWord(mhz)} point {spelled} megahertz";
        }

        private static string DigitWord(int d) => d switch
        {
            0 => "zero", 1 => "one", 2 => "two", 3 => "three", 4 => "four",
            5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine",
            _ => d.ToString()
        };

        // Whole-MHz number names, matching VoiceGrammar.MhzWholeChoices so
        // the confirmation echoes the same words the user said. Numbers not
        // in the grammar (e.g. 23, 25) just speak as themselves.
        private static string NumberWord(long n) => n switch
        {
            0  => "zero",     1  => "one",      2  => "two",      3  => "three",
            4  => "four",     5  => "five",     6  => "six",      7  => "seven",
            8  => "eight",    9  => "nine",     10 => "ten",      11 => "eleven",
            12 => "twelve",   13 => "thirteen", 14 => "fourteen", 15 => "fifteen",
            16 => "sixteen",  17 => "seventeen",18 => "eighteen", 19 => "nineteen",
            20 => "twenty",   21 => "twenty one", 24 => "twenty four",
            28 => "twenty eight", 29 => "twenty nine", 30 => "thirty",
            50 => "fifty",    51 => "fifty one", 52 => "fifty two",
            70 => "seventy",  71 => "seventy one",
            _  => n.ToString()
        };

        // Letter modes ("USB", "LSB", "AM", "FM", "CW-U" etc.) read more
        // clearly when spelled out letter-by-letter, same as the grammar
        // input form. "DATA-U" -> "data U", "RTTY-L" -> "R T T Y L".
        private static string ModeForSpeech(string mode) => mode switch
        {
            "USB"      => "U S B",
            "LSB"      => "L S B",
            "AM"       => "A M",
            "FM"       => "F M",
            "AM-N"     => "A M narrow",
            "FM-N"     => "F M narrow",
            "CW-U"     => "C W upper",
            "CW-L"     => "C W lower",
            "RTTY-U"   => "R T T Y upper",
            "RTTY-L"   => "R T T Y lower",
            "DATA-U"   => "data upper",
            "DATA-L"   => "data lower",
            "DATA-FM"  => "data F M",
            "DATA-FM-N"=> "data F M narrow",
            "PSK"      => "P S K",
            _          => mode
        };
    }

    /// <summary>
    /// Result of dispatching a voice intent. <c>Success</c> drives the
    /// spoken-confirmation status ("successful" vs "unsuccessful");
    /// <c>ConfirmationPhrase</c> is the human-readable description of what
    /// the command tried to do, with parameter values folded in
    /// (e.g. "Move to fourteen point zero seven four megahertz", "Mode U S B").
    /// </summary>
    public record DispatchResult(bool Success, string ConfirmationPhrase);
}
