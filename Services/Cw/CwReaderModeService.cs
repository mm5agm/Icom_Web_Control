namespace Icom_Web_Control.Services.Cw
{
    /// <summary>
    /// Reader Mode: one button that sets the radio up the way the decoder needs
    /// it, and puts it back afterwards.
    ///
    /// The measured case for this came off the Yaesu bench: that radio's own
    /// built-in decoder failed on a signal the operator could copy by ear with
    /// the filters at 3 kHz, and was still poor after they were narrowed to
    /// 600 Hz. What a decoder is fed matters more than how it decodes, and a
    /// 2.4 kHz passband full of adjacent signals will defeat any decoder there
    /// is. So: CW mode, a narrow filter, APF on.
    ///
    /// <b>The restore is the reason this lives on the server.</b> The obvious
    /// implementation is three fetch calls from the browser, and it works right
    /// up until the operator reloads the page - at which point the record of
    /// what their filter used to be is gone with the tab, and they are left
    /// with a 250 Hz filter and APF ringing and nothing to press to undo it.
    /// Holding the previous settings here means the button still works after a
    /// reload, from a second browser, or from the voice control.
    ///
    /// <para>
    /// This is the same feature as Yaesu Web Control's, and deliberately not
    /// shared. On that side it is a page of CAT frames and per-model SH code
    /// tables; here it is six calls through <see cref="IRadioController"/>,
    /// because the seam already speaks Hz and already snaps to the ladder the
    /// radio actually has. Two implementations of one idea, which is what a
    /// clean seam buys - and what the width tables' absence from Core proves.
    /// </para>
    /// </summary>
    public sealed class CwReaderModeService
    {
        /// <summary>
        /// The APF width Reader Mode asks for: 2, MID.
        ///
        /// Not NAR. With the APF TYPE menu on SHARP, NAR is about 80 Hz wide,
        /// and Morse keying puts real energy either side of the tone - at
        /// 20 wpm a dit is 60 ms, so the sidebands run to roughly +/-17 Hz, and
        /// faster sending spreads them further. A filter that narrow starts
        /// rounding the edges of the very transitions the decoder is timing.
        /// MID keeps most of the rejection without cutting into the keying.
        /// </summary>
        private const int ApfMid = 2;

        private readonly IRadioController _radio;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<CwReaderModeService> _logger;

        // One at a time. Enabling and restoring both read the radio, decide
        // from what came back, and write - so two of them interleaved could
        // save the settings Reader Mode had just applied as if they were the
        // operator's own, and leave the radio at 250 Hz and APF for ever.
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Saved? _saved;

        public CwReaderModeService(IRadioController radio,
                                   RadioStateService state,
                                   ISettingsService settings,
                                   ILogger<CwReaderModeService> logger)
        {
            _radio    = radio;
            _state    = state;
            _settings = settings;
            _logger   = logger;
        }

        /// <summary>What the radio was set to before Reader Mode touched it.</summary>
        /// <param name="IfWidthHz">-1 when the radio did not report one - FM has none.</param>
        /// <param name="ApfWidth">0-3, where 0 is off.</param>
        private sealed record Saved(string? Mode, int IfWidthHz, int ApfWidth);

        public bool IsOn => _saved is not null;

        /// <summary>
        /// Sets CW mode, a narrow filter and APF, remembering what was there
        /// before. Enabling twice is a no-op rather than an error - it would
        /// otherwise save Reader Mode's own settings over the operator's.
        /// </summary>
        public async Task<CwReaderModeStatus> EnableAsync(int? filterHz = null, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is not null) return Describe("Already on.");
                if (!_radio.IsConnected) return Describe("The radio is not connected.");

                var settings = await _settings.GetSettingsAsync();

                // Read the radio rather than trusting the cached state. Mode
                // and filter width are not on the fast poll, so if the operator
                // has just turned the filter knob the cached values can be a
                // second or two stale - and a stale value here is not a stale
                // display, it is what gets put back afterwards.
                string mode  = await _radio.GetModeAsync(RadioVfo.A, ct);
                int    width = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
                int    apf   = await _radio.GetApfAsync(ct);

                _saved = new Saved(string.IsNullOrWhiteSpace(mode) ? _state.ModeA : mode,
                                   width,
                                   apf < 0 ? 0 : apf);

                _logger.LogInformation(
                    "Reader Mode on: saving mode {Mode}, IF width {Width}, APF {Apf}",
                    _saved.Mode,
                    _saved.IfWidthHz < 0 ? "unknown" : _saved.IfWidthHz + " Hz",
                    _saved.ApfWidth);

                await ApplyAsync(mode:     IsCw(_saved.Mode) ? _saved.Mode : "CW-U",
                                 widthHz:  filterHz ?? settings.CwReaderFilterHz,
                                 apfWidth: settings.CwReaderUseApf ? ApfMid : 0,
                                 ct: ct);

                return Describe("Reader Mode on.");
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Puts back what was there before. Restoring when it was never enabled
        /// does nothing, deliberately: the reader panel calls this when it
        /// stops, and stopping a reader the operator never put into Reader Mode
        /// must not change their radio.
        /// </summary>
        public async Task<CwReaderModeStatus> RestoreAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var saved = _saved;
                if (saved is null) return Describe("Reader Mode was not on.");
                if (!_radio.IsConnected) return Describe("The radio is not connected.");

                await ApplyAsync(mode:     saved.Mode,
                                 widthHz:  saved.IfWidthHz > 0 ? saved.IfWidthHz : null,
                                 apfWidth: saved.ApfWidth,
                                 ct: ct);

                // Cleared last. If a write threw half way through, the operator
                // still has a button that will try the restore again, which is
                // more use than a service that believes it already has.
                _saved = null;
                _logger.LogInformation("Reader Mode off: restored mode {Mode}, IF width {Width} Hz",
                                       saved.Mode, saved.IfWidthHz);

                return Describe("Reader Mode off. Your settings are back.");
            }
            finally { _gate.Release(); }
        }

        public CwReaderModeStatus Status() => Describe(IsOn ? "Reader Mode on." : "Reader Mode off.");

        // ---- the radio ----------------------------------------------------

        /// <summary>
        /// Mode, then width, then APF - and the order is not cosmetic.
        ///
        /// The width applies to the mode the radio is in, so sending it first
        /// would set a width for the mode the radio is about to leave. APF goes
        /// last for the same reason: it is a per-mode setting, and a mode change
        /// after it would put the radio's own stored value back over ours.
        ///
        /// <para>
        /// The width is requested in Hz and the controller snaps it to the
        /// nearest rung of the radio's own ladder, so there is no table here to
        /// get wrong. One difference from Yaesu Web Control worth knowing: that
        /// side breaks an exact tie towards the <i>wider</i> filter, whereas the
        /// codec here keeps the first match and so breaks a tie narrower. It
        /// only bites on a value exactly between two rungs - 275 Hz, say - and
        /// the 250 Hz default is a rung, so it does not arise in practice.
        /// Changing the codec to suit this one caller would move the CAT API's
        /// snapping and the width nudge with it, which is not a trade worth
        /// making for a tie nobody has hit.
        /// </para>
        /// </summary>
        private async Task ApplyAsync(string? mode, int? widthHz, int apfWidth, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(mode))
            {
                await _radio.SetModeAsync(RadioVfo.A, mode, ct);
                _state.ModeA = mode;
            }

            if (widthHz is { } hz)
            {
                await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, hz, ct);

                // Read back rather than recording what was asked for. The
                // controller snapped it, and the operator is about to be shown
                // the number - showing them the request rather than the result
                // would be a quiet lie the moment 250 is not a rung.
                int actual = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
                if (actual > 0)
                {
                    _state.IfWidthA = actual.ToString();
                    _state.IfWidthB = actual.ToString();
                }
            }

            await _radio.SetApfAsync(apfWidth, ct);

            // One receiver, so the setting is not per-VFO - mirrored into both
            // the way the CAT endpoint does it.
            _state.ApfWidthA = apfWidth;
            _state.ApfWidthB = apfWidth;
            _state.ApfOnA = apfWidth != 0;
            _state.ApfOnB = apfWidth != 0;
        }

        private static bool IsCw(string? mode) => mode is "CW-U" or "CW-L";

        private CwReaderModeStatus Describe(string message) => new()
        {
            On            = _saved is not null,
            Message       = message,
            Mode          = _state.ModeA,
            IfWidthHz     = int.TryParse(_state.IfWidthA, out int hz) ? hz : null,
            ApfWidth      = _state.ApfWidthA,
            RestoresMode  = _saved?.Mode,
            RestoresWidth = _saved is null || _saved.IfWidthHz <= 0 ? null : _saved.IfWidthHz,
            RestoresApf   = _saved?.ApfWidth,
        };
    }

    /// <summary>
    /// What Reader Mode did, and what it will put back. The restore values are
    /// reported because an operator who can see what the button is holding for
    /// them is much more likely to trust it with their filter settings.
    /// </summary>
    public sealed class CwReaderModeStatus
    {
        public bool On { get; init; }
        public string Message { get; init; } = "";
        public string? Mode { get; init; }

        /// <summary>The width now in effect, or null if the radio has not reported one.</summary>
        public int? IfWidthHz { get; init; }

        /// <summary>0=off, 1=wide, 2=mid, 3=narrow.</summary>
        public int ApfWidth { get; init; }

        public string? RestoresMode { get; init; }
        public int? RestoresWidth { get; init; }
        public int? RestoresApf { get; init; }
    }
}
