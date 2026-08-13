using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Icom_Web_Control.Hubs;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Services.Voice
{
    /// <summary>
    /// Maps a recognised semantic intent + parameter dictionary (from the
    /// SRGS grammar's <c>out.intent</c> tags) to radio operations sent through
    /// the <see cref="IRadioController"/> seam. Voice commands target whichever
    /// VFO's mic button was pressed (v2, per-VFO voice) — see <see cref="_vfo"/>.
    /// The IC-7300 is single-receiver, so the target always collapses to A
    /// (<see cref="RadioCapabilities.VfoIsB"/> already enforces this).
    /// </summary>
    public sealed class IntentDispatcher
    {
        private readonly ILogger<IntentDispatcher> _logger;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly IHubContext<RadioHub> _hub;
        // The semantic seam. Every intent that reaches the radio goes through
        // it: frequency, band, mode, TX, split, VFO-swap, the antenna tuner,
        // and the whole receive/transmit control chain (AF/RF gain, squelch,
        // preamp, attenuator, AGC, NR, NB, notch, APF, TX power, mic gain,
        // processor).
        // Every intent now reaches the radio through it. The last two that did
        // not — BandUp/Down and NudgeIfWidth — were wired up after the carve, and
        // with them went the legacy Yaesu-CAT SendCommand stub they were the only
        // callers of. There is no longer any path out of this class but the seam.
        private readonly IRadioController _radio;

        public IntentDispatcher(
            ILogger<IntentDispatcher> logger,
            RadioStateService state,
            ISettingsService settings,
            IHubContext<RadioHub> hub,
            IRadioController radio)
        {
            _logger = logger;
            _state = state;
            _settings = settings;
            _hub = hub;
            _radio = radio;
        }

        // §6.5 dry-run testing: when set, the seam wrappers below log what they
        // would send instead of sending it. AsyncLocal rather than a field/parameter
        // threaded through every intent handler -- it scopes cleanly to the
        // single DispatchAsync call tree without touching ~20 method
        // signatures, and doesn't leak into unrelated concurrent dispatches.
        private static readonly AsyncLocal<bool> _dryRun = new();

        // Which VFO this DispatchAsync call tree targets ("A" or "B") -- set
        // by whichever mic button the operator pressed (VoiceControlService
        // passes it through from StartListeningAsync). Same AsyncLocal
        // pattern as _dryRun, for the same reason: scopes to one dispatch
        // without threading a parameter through ~15 handler methods.
        private static readonly AsyncLocal<string?> _vfo = new();

        /// <summary>The targeted VFO for the in-flight dispatch ("A" or "B"); defaults to "A".</summary>
        private static string CurrentVfo => _vfo.Value ?? "A";


        /// <summary>True when the targeted VFO's state should be written to the *B fields. See <see cref="RadioCapabilities.VfoIsB"/>.</summary>
        private bool VfoIsB => RadioCapabilities.VfoIsB(_state.IsSingleReceiver, _state.ActiveVfo, CurrentVfo);

        /// <summary>
        /// Dispatch a recognised intent. Returns true if the intent was
        /// known AND successfully sent to the radio; false if the intent
        /// name is unknown or arguments are invalid. When <paramref name="dryRun"/>
        /// is true, intent matching and confirmation-phrase generation run
        /// exactly as normal but no CAT command is actually sent to the
        /// radio (§6.5 "Test this pack" dry run). Read-modify-write intents
        /// (SwapVFO on single-receiver radios, NudgeIfWidth) need a real CAT
        /// readback to compute their result and will report unsuccessful in
        /// dry-run mode -- a known, acceptable limitation of testing without
        /// a connected radio. <paramref name="vfo"/> is "A" or "B" -- which
        /// mic button was pressed; ignored (collapses to "A") on
        /// single-receiver radios.
        /// </summary>
        public async Task<DispatchResult> DispatchAsync(
            string intent,
            IReadOnlyDictionary<string, object> parameters,
            CancellationToken cancellationToken = default,
            bool dryRun = false,
            string vfo = "A")
        {
            _dryRun.Value = dryRun;
            _vfo.Value = vfo;
            _logger.LogInformation(
                "[IntentDispatcher] intent={Intent} params={@Params} dryRun={DryRun} vfo={Vfo}",
                intent, parameters, dryRun, vfo);

            try
            {
                switch (intent)
                {
                    case "SetFrequency":   return await SetFrequencyAsync(parameters, cancellationToken);
                    case "SetBand":        return await SetBandAsync(parameters, cancellationToken);
                    case "SetMode":        return await SetModeAsync(parameters, cancellationToken);
                    case "SetNudgeStep":      return await SetNudgeStepAsync(parameters, cancellationToken);
                    case "SwapVFO":           return await SwapVfoAsync(cancellationToken);
                    case "NudgeFrequency":    return await NudgeFrequencyAsync(parameters, cancellationToken);
                    case "BandUp":            return await BandStepAsync(up: true, cancellationToken);
                    case "BandDown":          return await BandStepAsync(up: false, cancellationToken);
                    case "Macro":             return await MacroAsync(parameters, cancellationToken);
                    case "StatusFrequency":   return StatusFrequency();
                    case "StatusMode":        return StatusMode();
                    case "StatusBand":        return StatusBand();
                    case "TxOn":              return await TxOnAsync(cancellationToken);
                    case "TxOff":             return await TxOffAsync(cancellationToken);
                    case "SplitOn":           return await SplitAsync(true, cancellationToken);
                    case "SplitOff":          return await SplitAsync(false, cancellationToken);
                    case "AtuOn":             return await AtuAsync(true, cancellationToken);
                    case "AtuOff":            return await AtuAsync(false, cancellationToken);
                    case "AtuTune":           return await AtuTuneAsync(cancellationToken);
                    case "Help":              return Help();
                    case "NudgeIfWidth":      return await NudgeIfWidthAsync(parameters, cancellationToken);
                    case "SetAfGain":          return await SetAfGainAsync(parameters, cancellationToken);
                    case "SetAttenuator":     return await SetAttenuatorAsync(parameters, cancellationToken);
                    case "SetPreamp":         return await SetPreampAsync(parameters, cancellationToken);
                    case "SetAgc":            return await SetAgcAsync(parameters, cancellationToken);
                    case "SetNoiseReduction": return await SetNoiseReductionAsync(parameters, cancellationToken);
                    case "SetNoiseBlanker":   return await SetNoiseBlankerAsync(parameters, cancellationToken);
                    case "SetNotch":          return await SetNotchAsync(parameters, cancellationToken);
                    case "SetRfGain":         return await SetRfGainAsync(parameters, cancellationToken);
                    case "SetSquelch":        return await SetSquelchAsync(parameters, cancellationToken);
                    case "SetTxPower":        return await SetTxPowerAsync(parameters, cancellationToken);
                    case "SetMicGain":        return await SetMicGainAsync(parameters, cancellationToken);
                    case "SetProcessor":      return await SetProcessorAsync(parameters, cancellationToken);
                    case "SetApf":            return await SetApfAsync(parameters, cancellationToken);
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
            await SetRadioFrequency(VfoIsB ? RadioVfo.B : RadioVfo.A, hz, ct);
            _logger.LogInformation("[Voice] SetFrequency -> {Hz} Hz (VFO {Vfo})", hz, CurrentVfo);
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
            // Icom: set the band-default frequency through the CI-V seam, exactly
            // like SetFrequency. On the single-receiver IC-7300 a VFO-B set uses
            // CI-V 25 01 (unselected) so it never swaps the operating VFO.
            await SetRadioFrequency(VfoIsB ? RadioVfo.B : RadioVfo.A, hz, ct);
            _logger.LogInformation("[Voice] SetBand -> {Metres}m -> {Hz} Hz (VFO {Vfo})", metres, hz, CurrentVfo);
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
            // Phase 3 block 2: set mode through the CI-V seam (command 06). The
            // recognised `mode` is already a display string ("USB", "CW-U", …),
            // which is exactly what the seam speaks.
            await SetRadioMode(VfoIsB ? RadioVfo.B : RadioVfo.A, mode, ct);
            _logger.LogInformation("[Voice] SetMode -> {Mode} (VFO {Vfo})", mode, CurrentVfo);
            return new DispatchResult(true, $"Mode {ModeForSpeech(mode)}");
        }

        // -- SwapVFO -------------------------------------------------------

        private async Task<DispatchResult> SwapVfoAsync(CancellationToken ct)
        {
            const string phrase = "Swap V F O";
            // Icom: CI-V 07 B0 atomically exchanges A↔B on the radio (the seam
            // swaps the cached freq/mode and the poll re-reads both). No need for
            // the Yaesu single-vs-dual FA/FB read-write-back dance.
            await ExchangeRadioVfos(ct);
            _logger.LogInformation("[Voice] SwapVFO (07 B0 exchange)");
            return new DispatchResult(true, phrase);
        }

        // -- SetNudgeStep --------------------------------------------------

        private static readonly long[] _validNudgeSteps = [10, 100, 1_000, 10_000, 100_000];

        private async Task<DispatchResult> SetNudgeStepAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "step", out var step) || !_validNudgeSteps.Contains(step))
                return new DispatchResult(false, "Set step size");

            var settings = await _settings.GetSettingsAsync();
            var isB = VfoIsB;
            if (isB) settings.VoiceNudgeStepHzB = step; else settings.VoiceNudgeStepHzA = step;
            await _settings.SaveSettingsAsync(settings);
            await _hub.Clients.All.SendAsync("RadioStateUpdate",
                new { property = isB ? "VoiceNudgeStepHzB" : "VoiceNudgeStepHzA", value = step }, ct);

            var label = step switch
            {
                10      => "ten hertz",
                100     => "one hundred hertz",
                1_000   => "one kilohertz",
                10_000  => "ten kilohertz",
                100_000 => "one hundred kilohertz",
                _       => $"{step} hertz",
            };
            _logger.LogInformation("[Voice] SetNudgeStep -> {Step} Hz", step);
            return new DispatchResult(true, $"Step size {label}");
        }

        // -- NudgeFrequency ------------------------------------------------

        private async Task<DispatchResult> NudgeFrequencyAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "direction", out var direction) || (direction != 1 && direction != -1))
            {
                _logger.LogWarning("[Voice] NudgeFrequency invalid direction");
                return new DispatchResult(false, "Tune");
            }
            var phrase = direction > 0 ? "Tune up" : "Tune down";
            var settings = await _settings.GetSettingsAsync();
            var isB = VfoIsB;
            var stepHzSetting = isB ? settings.VoiceNudgeStepHzB : settings.VoiceNudgeStepHzA;
            var stepHz = stepHzSetting > 0 ? stepHzSetting : 10_000;
            var current = isB ? _state.FrequencyB : _state.FrequencyA;
            var next = current + direction * stepHz;
            if (next < 30_000 || next > 75_000_000)
            {
                _logger.LogWarning("[Voice] NudgeFrequency would go out of range ({Next})", next);
                return new DispatchResult(false, phrase);
            }
            await SetRadioFrequency(isB ? RadioVfo.B : RadioVfo.A, next, ct);
            _logger.LogInformation("[Voice] NudgeFrequency {Dir} -> {Hz} Hz (VFO {Vfo})",
                direction > 0 ? "up" : "down", next, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        // -- BandUp / BandDown ---------------------------------------------

        /// <summary>
        /// Step to the next amateur band up or down, landing on the same
        /// band-default frequency "go to &lt;band&gt; metres" would have used.
        /// <para>
        /// This is a step through <see cref="BandDefaultsHz"/>, not through the
        /// radio's band-stacking registers. Band stacking would remember where the
        /// operator last was on each band, which is nicer — but it is also
        /// per-register state that voice cannot inspect before committing to it,
        /// and this intent exists for operators who cannot see where they landed.
        /// A known, announced frequency beats a remembered one they have to read
        /// back off the screen. Bands outside the configured band plan are skipped,
        /// so 4 m does not appear on a Region 2 setup.
        /// </para>
        /// </summary>
        private async Task<DispatchResult> BandStepAsync(bool up, CancellationToken ct)
        {
            var phrase = up ? "Band up" : "Band down";

            // Ascending by frequency, so "up" is +1 in this list and metres go down.
            var ladder = BandDefaultsHz
                .OrderBy(kv => kv.Value)
                .Where(kv => _state.GetBandFromFrequency(kv.Value) != BandPlanService.UnknownBand)
                .ToList();
            if (ladder.Count == 0)
            {
                _logger.LogWarning("[Voice] Band {Dir}: no bands in the configured band plan", up ? "up" : "down");
                return new DispatchResult(false, phrase);
            }

            var isB = VfoIsB;
            var current = isB ? _state.FrequencyB : _state.FrequencyA;

            // Where are we now? Index by the band we are actually in, so tuning
            // 40 kHz off the band default still steps from the band we can hear.
            // Off-band (out-of-band listening, or a frequency we have no plan for)
            // falls back to the nearest band default, which is the only sensible
            // reading of "one band up" from somewhere that is not a band.
            var band = _state.GetBandFromFrequency(current);
            int index = ladder.FindIndex(kv => $"{kv.Key}m" == band);
            if (index < 0)
            {
                index = 0;
                for (int i = 1; i < ladder.Count; i++)
                    if (Math.Abs(ladder[i].Value - current) < Math.Abs(ladder[index].Value - current))
                        index = i;
            }

            int next = index + (up ? 1 : -1);
            if (next < 0 || next >= ladder.Count)
            {
                // Clamped, not wrapped: 10 m → "band up" landing on 160 m would be
                // a nasty surprise for an operator who cannot see the dial.
                var edge = up ? "highest" : "lowest";
                _logger.LogInformation("[Voice] Band {Dir} refused — already on the {Edge} band ({Band})",
                    up ? "up" : "down", edge, band);
                return new DispatchResult(false, $"Already on the {edge} band", IsReadBack: true);
            }

            var (metres, hz) = (ladder[next].Key, ladder[next].Value);
            await SetRadioFrequency(isB ? RadioVfo.B : RadioVfo.A, hz, ct);
            _logger.LogInformation("[Voice] Band {Dir} -> {Metres}m -> {Hz} Hz (VFO {Vfo})",
                up ? "up" : "down", metres, hz, CurrentVfo);
            return new DispatchResult(true, $"Move to {metres} metres");
        }

        // -- Macro ---------------------------------------------------------

        private async Task<DispatchResult> MacroAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            var name = args.TryGetValue("macroName", out var n) ? n?.ToString() ?? "Macro" : "Macro";
            if (!args.TryGetValue("macroCat", out var c) || c is not string cat || string.IsNullOrWhiteSpace(cat))
            {
                _logger.LogWarning("[Voice] Macro '{Name}' has no CI-V command", name);
                return new DispatchResult(false, name);
            }

            // The macro payload is CI-V command bodies in hex, ';'-separated —
            // see CivMacroCodec. A malformed one never reaches the radio (the
            // Settings validator rejects it at save time too): report it
            // unsuccessful rather than sending a guess.
            if (!CivMacroCodec.TryParse(cat, out var commands, out var error))
            {
                _logger.LogWarning("[Voice] Macro '{Name}' payload '{Cat}' isn't valid CI-V: {Error}", name, cat, error);
                return new DispatchResult(false, name);
            }

            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would send macro '{Name}': {Commands}",
                    name, string.Join(" | ", commands.Select(CivMacroCodec.Describe)));
                return new DispatchResult(true, name);
            }

            // Chained commands go in order and stop at the first rejection. A
            // half-applied macro is untidy, but pressing on past a command the
            // radio refused is worse — and the spoken "unsuccessful" tells the
            // operator to look, which is the whole point of the confirmation.
            foreach (var command in commands)
            {
                if (!await _radio.SendRawCommandAsync(command, ct))
                {
                    _logger.LogWarning("[Voice] Macro '{Name}': command {Command} was not acknowledged",
                        name, CivMacroCodec.Describe(command));
                    return new DispatchResult(false, name);
                }
            }
            _logger.LogInformation("[Voice] Macro '{Name}' sent {Count} CI-V command(s)", name, commands.Count);
            return new DispatchResult(true, name);
        }

        // -- Status read-back (IsReadBack=true → no ", successful" appended) -----

        private DispatchResult StatusFrequency()
        {
            var hz = VfoIsB ? _state.FrequencyB : _state.FrequencyA;
            var phrase = FormatFrequencyForSpeech(hz);
            return new DispatchResult(true, phrase, IsReadBack: true);
        }

        private DispatchResult StatusMode()
        {
            var mode = VfoIsB ? _state.ModeB : _state.ModeA;
            var phrase = $"Mode {ModeForSpeech(mode ?? string.Empty)}";
            return new DispatchResult(true, phrase, IsReadBack: true);
        }

        private DispatchResult StatusBand()
        {
            var hz = VfoIsB ? _state.FrequencyB : _state.FrequencyA;
            var phrase = FrequencyToBandName(hz) is string band
                ? $"{band} metres"
                : "frequency not on a standard amateur band";
            return new DispatchResult(true, phrase, IsReadBack: true);
        }

        private static string? FrequencyToBandName(long hz) => hz switch
        {
            >= 1_800_000  and <= 2_000_000  => "one six zero",
            >= 3_500_000  and <= 4_000_000  => "eighty",
            >= 5_250_000  and <= 5_450_000  => "sixty",
            >= 7_000_000  and <= 7_300_000  => "forty",
            >= 10_100_000 and <= 10_150_000 => "thirty",
            >= 14_000_000 and <= 14_350_000 => "twenty",
            >= 18_068_000 and <= 18_168_000 => "seventeen",
            >= 21_000_000 and <= 21_450_000 => "fifteen",
            >= 24_890_000 and <= 24_990_000 => "twelve",
            >= 28_000_000 and <= 29_700_000 => "ten",
            >= 50_000_000 and <= 54_000_000 => "six",
            >= 70_000_000 and <= 71_000_000 => "four",
            _ => null,
        };

        private static DispatchResult Help() =>
            new(true,
                "You can say: set frequency, set mode, set band, band up or down, " +
                "tune up or down, swap V F O, or ask what frequency, what mode, " +
                "what band. Full list in the user manual.",
                IsReadBack: true);

        // -- TX / Split ----------------------------------------------------

        private async Task<DispatchResult> TxOnAsync(CancellationToken ct)
        {
            await SetRadioTransmit(true, ct);
            _logger.LogInformation("[Voice] TxOn");
            return new DispatchResult(true, "Transmitting");
        }

        private async Task<DispatchResult> TxOffAsync(CancellationToken ct)
        {
            await SetRadioTransmit(false, ct);
            _logger.LogInformation("[Voice] TxOff");
            return new DispatchResult(true, "Receive");
        }

        private async Task<DispatchResult> SplitAsync(bool on, CancellationToken ct)
        {
            await SetRadioSplit(on, ct);
            _logger.LogInformation("[Voice] Split {State}", on ? "on" : "off");
            return new DispatchResult(true, on ? "Split on" : "Split off");
        }

        // -- Antenna tuner -------------------------------------------------
        // The touch UI reaches the tuner through a long press on the ATU
        // button, which a partially-sighted operator can neither discover nor
        // perform. These three intents are that operator's only route to it,
        // so they go through the seam rather than the HTTP endpoints.

        private async Task<DispatchResult> AtuAsync(bool on, CancellationToken ct)
        {
            await SetRadioTuner(on ? 1 : 0, ct);
            _logger.LogInformation("[Voice] ATU {State}", on ? "on" : "off");
            return new DispatchResult(true, on ? "Antenna tuner on" : "Antenna tuner off");
        }

        /// <summary>
        /// Start an auto-tune cycle, or stop the one that is already running.
        /// CI-V 1C 01 02 starts a cycle and 1C 01 01 stops it and leaves the
        /// tuner in line, so this is the same toggle the button performs —
        /// and the spoken phrase says which of the two it did, because the
        /// operator relying on voice has no red "Tuning…" button to look at.
        /// </summary>
        private async Task<DispatchResult> AtuTuneAsync(CancellationToken ct)
        {
            bool tuning = _state.AtuTuning;
            await SetRadioTuner(tuning ? 1 : 2, ct);
            _logger.LogInformation("[Voice] ATU auto-tune {Action}", tuning ? "stopped" : "started");
            return new DispatchResult(true, tuning ? "Stopping antenna tuner" : "Tuning antenna");
        }

        // -- IF width / AF gain nudges (query → adjust → set) --------------

        private async Task<DispatchResult> NudgeIfWidthAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLong(args, "direction", out var direction) || (direction != 1 && direction != -1))
                return new DispatchResult(false, "Filter width");

            // One step along the radio's own filter ladder. The step sizes are
            // uneven and mode-dependent (50 Hz below 500, 100 Hz above, 200 Hz flat
            // in AM), so the seam takes a step count and keeps the table below it.
            var vfo = VfoIsB ? RadioVfo.B : RadioVfo.A;
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would nudge IF width {Dir}", direction > 0 ? "wider" : "narrower");
                return new DispatchResult(true, direction > 0 ? "Filter wider" : "Filter narrower");
            }

            var hz = await _radio.NudgeIfFilterWidthAsync(vfo, (int)direction, ct);
            if (hz < 0)
            {
                // FM has no adjustable width, and saying so is more use than a
                // generic failure to an operator who cannot see the greyed-out
                // control.
                _logger.LogInformation("[Voice] NudgeIfWidth: no adjustable width in the current mode (VFO {Vfo})", CurrentVfo);
                return new DispatchResult(false, "Filter width can't be changed in this mode", IsReadBack: true);
            }

            _logger.LogInformation("[Voice] NudgeIfWidth {Dir} -> {Hz} Hz (VFO {Vfo})",
                direction > 0 ? "wider" : "narrower", hz, CurrentVfo);
            // Speak the resulting width, not just the direction: it is the only
            // feedback there is when the on-screen control cannot be seen, and at
            // the end of the ladder the number simply stops changing.
            return new DispatchResult(true,
                $"{(direction > 0 ? "Filter wider" : "Filter narrower")}, {hz} hertz");
        }

        private async Task<DispatchResult> SetAfGainAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            // Vocabulary keys are 0-100 (percentage). Map to 0-255 for the CAT command.
            if (!TryGetLong(args, "level", out var pct))
                return new DispatchResult(false, "A F gain");

            pct = Math.Clamp(pct, 0, 100);
            var catValue = (int)Math.Round(pct * 255.0 / 100.0);
            await SetRadioAfGain(catValue, ct);
            if (VfoIsB) _state.AfGainB = catValue; else _state.AfGainA = catValue;
            _logger.LogInformation("[Voice] SetAfGain {Pct}% -> CAT {CatValue} (VFO {Vfo})", pct, catValue, CurrentVfo);
            return new DispatchResult(true, $"Audio gain {pct}");
        }

        // -- Attenuator / Preamp / AGC -------------------------------------

        // The IC-7300's attenuator is a single ~20 dB pad, switched on or off
        // (CI-V 11) — the vocabulary is "off"/"on", not the Yaesu off/6/12/18 dB
        // ladder this used to expect and could never send.
        private async Task<DispatchResult> SetAttenuatorAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("level", out var lvlObj) || lvlObj is not string level)
                return new DispatchResult(false, "Attenuator");

            bool? on = level switch { "off" => false, "on" => true, _ => null };
            if (on == null) return new DispatchResult(false, "Attenuator");

            await SetRadioSwitch(_radio.SetAttenuatorAsync, on.Value, "attenuator", ct);
            _logger.LogInformation("[Voice] SetAttenuator -> {Level} (VFO {Vfo})", level, CurrentVfo);
            return new DispatchResult(true, on.Value ? "Attenuator on" : "Attenuator off");
        }

        private async Task<DispatchResult> SetPreampAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("level", out var lvlObj) || lvlObj is not string level)
                return new DispatchResult(false, "Preamp");

            var code = level switch
            {
                "off" => "0",
                "1"   => "1",
                "2"   => "2",
                _     => null,
            };
            if (code == null) return new DispatchResult(false, "Preamp");

            // code is "0"/"1"/"2" — IC-7300 preamp off / amp 1 / amp 2 (CI-V 16 02).
            await SetRadioPreamp(int.Parse(code), ct);
            if (VfoIsB) _state.IpoB = code; else _state.IpoA = code;
            var phrase = level switch
            {
                "off" => "Preamp off",
                "1"   => "Preamp amp one",
                "2"   => "Preamp amp two",
                _     => "Preamp",
            };
            _logger.LogInformation("[Voice] SetPreamp -> {Level} (VFO {Vfo})", level, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        // IC-7300 AGC is a three-position time constant (CI-V 16 12 → 1/2/3).
        // The Yaesu "off" and "auto" positions the old vocabulary offered do not
        // exist on this radio and are gone from the pack; a v8 pack that still
        // has them is reset by VoicePhraseStore.Load, so "off"/"auto" can only
        // reach here from a hand-edited file — hence the plain rejection.
        private async Task<DispatchResult> SetAgcAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!args.TryGetValue("speed", out var spObj) || spObj is not string speed)
                return new DispatchResult(false, "A G C");

            var code = speed switch
            {
                "fast" => 1,
                "mid"  => 2,
                "slow" => 3,
                _      => 0,
            };
            if (code == 0) return new DispatchResult(false, "A G C");

            await SetRadioLevel(_radio.SetAgcAsync, code, "AGC", ct);
            if (VfoIsB) _state.AgcB = code.ToString(); else _state.AgcA = code.ToString();
            _logger.LogInformation("[Voice] SetAgc -> {Speed} (VFO {Vfo})", speed, CurrentVfo);
            return new DispatchResult(true, $"A G C {speed}");
        }

        // -- Receive/transmit chain (v9 intents) ---------------------------
        //
        // One handler per control that carries a data-a11y-key on the main
        // page, so anything a screen reader can name is also sayable. They all
        // read a single "value" argument: either a 0–100 level or a position
        // word. Unlike the older handlers these do NOT write RadioStateService
        // optimistically — CivRadioController's setters don't either, and the
        // poll loop reads the real value back within a couple of hundred ms.

        /// <summary>0–100 level words map to the radio's raw 0–255 range.</summary>
        private static int PercentToRaw(long pct) =>
            (int)Math.Round(Math.Clamp(pct, 0, 100) * 255.0 / 100.0);

        /// <summary>Reads the shared "value" argument as a 0–100 level; false if it isn't one.</summary>
        private static bool TryGetLevel(IReadOnlyDictionary<string, object> args, out long pct)
        {
            pct = 0;
            return args.TryGetValue("value", out var v) && v is string s &&
                   long.TryParse(s, out pct);
        }

        /// <summary>Reads the shared "value" argument as a position word ("off", "auto", …).</summary>
        private static string? GetWord(IReadOnlyDictionary<string, object> args) =>
            args.TryGetValue("value", out var v) && v is string s ? s : null;

        // NR and NB are a switch and a depth in one command: a bare "off"/"on"
        // toggles the function, a number sets its level AND switches it on, so
        // the operator never has to say two commands to hear the difference.

        private async Task<DispatchResult> SetNoiseReductionAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            var word = GetWord(args);
            if (word == "off" || word == "on")
            {
                await SetRadioSwitch(_radio.SetNoiseReductionAsync, word == "on", "noise reduction", ct);
                _logger.LogInformation("[Voice] SetNoiseReduction -> {State} (VFO {Vfo})", word, CurrentVfo);
                return new DispatchResult(true, word == "on" ? "Noise reduction on" : "Noise reduction off");
            }
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "Noise reduction");

            await SetRadioSwitch(_radio.SetNoiseReductionAsync, true, "noise reduction", ct);
            await SetRadioLevel(_radio.SetNrLevelAsync, PercentToRaw(pct), "NR level", ct);
            _logger.LogInformation("[Voice] SetNoiseReduction level -> {Pct}% (VFO {Vfo})", pct, CurrentVfo);
            return new DispatchResult(true, $"Noise reduction {pct}");
        }

        private async Task<DispatchResult> SetNoiseBlankerAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            var word = GetWord(args);
            if (word == "off" || word == "on")
            {
                await SetRadioSwitch(_radio.SetNoiseBlankerAsync, word == "on", "noise blanker", ct);
                _logger.LogInformation("[Voice] SetNoiseBlanker -> {State} (VFO {Vfo})", word, CurrentVfo);
                return new DispatchResult(true, word == "on" ? "Noise blanker on" : "Noise blanker off");
            }
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "Noise blanker");

            await SetRadioSwitch(_radio.SetNoiseBlankerAsync, true, "noise blanker", ct);
            await SetRadioLevel(_radio.SetNbLevelAsync, PercentToRaw(pct), "NB level", ct);
            _logger.LogInformation("[Voice] SetNoiseBlanker level -> {Pct}% (VFO {Vfo})", pct, CurrentVfo);
            return new DispatchResult(true, $"Noise blanker {pct}");
        }

        // The panel's notch control is one three-position selector, but the
        // radio has two independent filters (auto 16 41, manual 16 48). Each
        // position therefore sets both, so "notch auto" can't leave the manual
        // notch quietly running underneath it.
        private async Task<DispatchResult> SetNotchAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            var word = GetWord(args);
            (bool auto, bool manual)? want = word switch
            {
                "off"    => (false, false),
                "auto"   => (true, false),
                "manual" => (false, true),
                _        => null,
            };
            if (want == null) return new DispatchResult(false, "Notch");

            await SetRadioSwitch(_radio.SetAutoNotchAsync, want.Value.auto, "auto notch", ct);
            await SetRadioSwitch(_radio.SetManualNotchAsync, want.Value.manual, "manual notch", ct);
            _logger.LogInformation("[Voice] SetNotch -> {Word} (VFO {Vfo})", word, CurrentVfo);
            return new DispatchResult(true, word == "off" ? "Notch off" : $"Notch {word}");
        }

        private async Task<DispatchResult> SetRfGainAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "R F gain");
            await SetRadioLevel(_radio.SetRfGainAsync, PercentToRaw(pct), "RF gain", ct);
            _logger.LogInformation("[Voice] SetRfGain -> {Pct}% (VFO {Vfo})", pct, CurrentVfo);
            return new DispatchResult(true, $"R F gain {pct}");
        }

        private async Task<DispatchResult> SetSquelchAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "Squelch");
            await SetRadioLevel(_radio.SetSquelchAsync, PercentToRaw(pct), "squelch", ct);
            _logger.LogInformation("[Voice] SetSquelch -> {Pct}% (VFO {Vfo})", pct, CurrentVfo);
            return new DispatchResult(true, $"Squelch {pct}");
        }

        // TX power and mic gain are already percentages at the seam (the
        // controller does the 0–255 scaling), so they pass the spoken number
        // through unchanged rather than via PercentToRaw.
        private async Task<DispatchResult> SetTxPowerAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "Transmit power");
            await SetRadioLevel(_radio.SetRfPowerPercentAsync, (int)Math.Clamp(pct, 0, 100), "TX power", ct);
            _logger.LogInformation("[Voice] SetTxPower -> {Pct}%", pct);
            return new DispatchResult(true, $"Transmit power {pct}");
        }

        private async Task<DispatchResult> SetMicGainAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "Mic gain");
            await SetRadioLevel(_radio.SetMicGainPercentAsync, (int)Math.Clamp(pct, 0, 100), "mic gain", ct);
            _logger.LogInformation("[Voice] SetMicGain -> {Pct}%", pct);
            return new DispatchResult(true, $"Mic gain {pct}");
        }

        private async Task<DispatchResult> SetProcessorAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            var word = GetWord(args);
            if (word == "off" || word == "on")
            {
                await SetRadioSwitch(_radio.SetSpeechCompressorAsync, word == "on", "speech processor", ct);
                _logger.LogInformation("[Voice] SetProcessor -> {State}", word);
                return new DispatchResult(true, word == "on" ? "Processor on" : "Processor off");
            }
            if (!TryGetLevel(args, out var pct)) return new DispatchResult(false, "Processor");

            await SetRadioSwitch(_radio.SetSpeechCompressorAsync, true, "speech processor", ct);
            await SetRadioLevel(_radio.SetCompressorLevelPercentAsync, (int)Math.Clamp(pct, 0, 100), "processor level", ct);
            _logger.LogInformation("[Voice] SetProcessor level -> {Pct}%", pct);
            return new DispatchResult(true, $"Processor {pct}");
        }

        // APF is CW-only and OFF is simply its fourth position, so this is one
        // set, not a switch plus a width (CI-V 16 32 → 0=OFF, 1=WIDE, 2=MID,
        // 3=NAR). Sent in any mode; the radio ignores it outside CW.
        private async Task<DispatchResult> SetApfAsync(
            IReadOnlyDictionary<string, object> args, CancellationToken ct)
        {
            if (!TryGetLevel(args, out var setting) || setting is < 0 or > 3)
                return new DispatchResult(false, "A P F");

            await SetRadioLevel(_radio.SetApfAsync, (int)setting, "APF", ct);
            var phrase = setting switch
            {
                0 => "A P F off",
                1 => "A P F wide",
                2 => "A P F medium",
                _ => "A P F narrow",
            };
            _logger.LogInformation("[Voice] SetApf -> {Setting} (VFO {Vfo})", setting, CurrentVfo);
            return new DispatchResult(true, phrase);
        }

        /// <summary>
        /// Set a VFO's frequency through the CI-V seam, honouring the §6.5
        /// dry-run flag (no radio traffic during a pack test). The seam updates
        /// RadioStateService on the radio's ACK; in dry-run we set state
        /// optimistically so the confirmation phrase still reflects the move.
        /// </summary>
        private async Task SetRadioFrequency(RadioVfo vfo, long hz, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set VFO {Vfo} to {Hz} Hz", vfo, hz);
                if (vfo == RadioVfo.B) _state.FrequencyB = hz; else _state.FrequencyA = hz;
                return;
            }
            await _radio.SetFrequencyHzAsync(vfo, hz, ct);
        }

        /// <summary>
        /// Set a VFO's mode through the CI-V seam, honouring the §6.5 dry-run
        /// flag. As with <see cref="SetRadioFrequency"/>, dry-run sets state
        /// optimistically so the spoken confirmation still reflects the change.
        /// </summary>
        private async Task SetRadioMode(RadioVfo vfo, string mode, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set VFO {Vfo} to mode {Mode}", vfo, mode);
                if (vfo == RadioVfo.B) _state.ModeB = mode; else _state.ModeA = mode;
                return;
            }
            await _radio.SetModeAsync(vfo, mode, ct);
        }

        /// <summary>
        /// Key/unkey the radio through the CI-V seam (software PTT), honouring the
        /// §6.5 dry-run flag. As with the frequency/mode helpers, dry-run sets
        /// state optimistically so the spoken confirmation still reflects it —
        /// and, importantly, dry-run never actually transmits.
        /// </summary>
        private async Task SetRadioTransmit(bool transmit, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set PTT {State}", transmit ? "TX" : "RX");
                _state.IsTransmitting = transmit;
                return;
            }
            await _radio.SetTransmitAsync(transmit, ct);
        }

        /// <summary>Exchange VFO A↔B through the CI-V seam (07 B0), dry-run-aware.</summary>
        private async Task ExchangeRadioVfos(CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would exchange VFO A<->B");
                (_state.FrequencyA, _state.FrequencyB) = (_state.FrequencyB, _state.FrequencyA);
                (_state.ModeA, _state.ModeB) = (_state.ModeB, _state.ModeA);
                return;
            }
            await _radio.ExchangeVfosAsync(ct);
        }

        /// <summary>Set split on/off through the CI-V seam (0F), dry-run-aware.</summary>
        private async Task SetRadioSplit(bool on, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set split {State}", on ? "on" : "off");
                _state.SplitMode = on ? 1 : 0;
                return;
            }
            await _radio.SetSplitAsync(on, ct);
        }

        /// <summary>
        /// Set the antenna tuner through the CI-V seam (1C 01), dry-run-aware.
        /// <paramref name="state"/> is 0 = bypassed, 1 = in line, 2 = start an
        /// auto-tune cycle. The seam updates RadioStateService itself on the
        /// real path, so only the dry run sets state here — and it never
        /// pretends a cycle is running, because nothing was sent to run one.
        /// </summary>
        private async Task SetRadioTuner(int state, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set ATU state {State}", state);
                if (state != 2) _state.AtuEnabled = state == 1;
                return;
            }
            await _radio.SetTunerAsync(state, ct);
        }

        /// <summary>Set AF gain (0–255) through the CI-V seam (14 01), dry-run-aware.</summary>
        private async Task SetRadioAfGain(int value, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set AF gain {Value}", value);
                return;
            }
            await _radio.SetAfGainAsync(value, ct);
        }

        /// <summary>Set preamp (0/1/2) through the CI-V seam (16 02), dry-run-aware.</summary>
        private async Task SetRadioPreamp(int value, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set preamp {Value}", value);
                return;
            }
            await _radio.SetPreampAsync(value, ct);
        }

        // The v9 receive/transmit-chain intents all reach the seam through one
        // of these two, so §6.5 dry-run stays a single choke point rather than
        // a copy of the same three lines in each of nine handlers. The seam
        // member goes in as a method group (_radio.SetSquelchAsync), which
        // keeps each call site readable about which command it is sending.

        /// <summary>Set a seam on/off function, dry-run-aware.</summary>
        private async Task SetRadioSwitch(
            Func<bool, CancellationToken, Task> set, bool on, string what, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set {What} {State}", what, on ? "on" : "off");
                return;
            }
            await set(on, ct);
        }

        /// <summary>Set a seam integer level (raw 0–255, a percentage, or a position code), dry-run-aware.</summary>
        private async Task SetRadioLevel(
            Func<int, CancellationToken, Task> set, int value, string what, CancellationToken ct)
        {
            if (_dryRun.Value)
            {
                _logger.LogInformation("[Voice] DRY RUN -- would set {What} {Value}", what, value);
                return;
            }
            await set(value, ct);
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

        // ── Speech-formatting helpers for the spoken confirmation ─────────
        //
        // TTS handles raw numbers ("14.074 megahertz") inconsistently across
        // installed voices -- one voice says "fourteen point oh seven four",
        // another says "fourteen point seventy-four". We spell the value out
        // digit-by-digit so the spoken confirmation matches what the user
        // said (digit-by-digit fractional MHz is the IWC v1 grammar).

        private static string FormatFrequencyForSpeech(long hz)
        {
            var mhz     = hz / 1_000_000;
            var rem     = hz % 1_000_000;
            var fracKhz = rem / 1000;   // 0-999: kHz digits
            var fracHz  = rem % 1000;   // 0-999: Hz digits (often 0)

            if (fracKhz == 0 && fracHz == 0)
                return $"{NumberWord(mhz)} megahertz";

            var khzSpelled = string.Join(" ", fracKhz.ToString("D3").Select(c => DigitWord(c - '0')));
            if (fracHz == 0)
                return $"{NumberWord(mhz)} point {khzSpelled} megahertz";

            // Include the sub-kHz digits so "18.128010" reads as
            // "eighteen point one two eight zero one zero megahertz".
            var hzSpelled = string.Join(" ", fracHz.ToString("D3").Select(c => DigitWord(c - '0')));
            return $"{NumberWord(mhz)} point {khzSpelled} {hzSpelled} megahertz";
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
    /// <summary>
    /// <c>IsReadBack</c> = true for status queries and help — the phrase is spoken directly
    /// without appending ", successful". Also bypasses the VoiceSpokenConfirmationEnabled
    /// gate so status queries always get a spoken answer.
    /// </summary>
    public record DispatchResult(bool Success, string ConfirmationPhrase, bool IsReadBack = false);
}
