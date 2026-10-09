using RadioWebControl.Core.Services.Rtty;

namespace Icom_Web_Control.Services.Rtty
{
    /// <summary>
    /// Puts the radio into RTTY when the tuner starts, and puts it back when the
    /// tuner stops.
    ///
    /// <para>The case for it came off the bench on 2026-10-08. The tuner opens on
    /// whatever the radio was last doing, and what the radio was last doing is
    /// often CW with a 250 Hz filter - a passband that cannot carry a 170 Hz
    /// shift's keying sidebands, never mind DDK9's 450. The figure then draws a
    /// perfectly convincing ellipse built out of one tone and the skirt of the
    /// other, and the operator has no way to tell that from a correctly tuned
    /// signal. <b>A tuning indicator that lies is worse than no tuning indicator</b>,
    /// so the settings it needs are set rather than hoped for.</para>
    ///
    /// <para><b>The restore is why this lives on the server</b>, for exactly the
    /// reason <see cref="Cw.CwReaderModeService"/> gives: the obvious
    /// implementation is a couple of fetch calls from the dialog, and it works
    /// right up until the operator reloads the page, at which point the record of
    /// what mode they were in has gone with the tab. This must stay a singleton
    /// for the same reason.</para>
    ///
    /// <para><b>Two deliberate restraints</b>, both narrower than the CW
    /// equivalent:</para>
    ///
    /// <para>1. <i>Only from the modes RTTY cannot be copied in at all</i> - see
    /// <see cref="NeedsSwitching"/>. The tuner's own documentation supports AFSK:
    /// in DATA-L, DATA-U, LSB or USB the tones are made by the operator's software
    /// and the radio is a plain SSB transceiver, which is a working arrangement
    /// that this switching would break. So the switch fires from CW, AM, FM and
    /// DATA-FM, and leaves every mode that can actually pass an audio tone pair
    /// where it is.</para>
    ///
    /// <para>2. <i>The filter is only ever widened.</i> Changing to RTTY brings
    /// the radio's own stored RTTY filter width with it, which is whatever that
    /// operator normally uses for RTTY and is better evidence about their band
    /// than any formula. The width is touched only when it is too narrow to have
    /// carried the shift the tuner is set to, and then only up to the floor
    /// <see cref="RttyIfWidth"/> computes. Narrowing is left to the operator: see
    /// that class for why - the short of it is that nobody has yet measured where
    /// this radio centres its RTTY passband, and narrowing on a wrong assumption
    /// about that would attenuate one tone, which is the very fault this service
    /// exists to prevent.</para>
    /// </summary>
    public sealed class RttyTunerModeService
    {
        /// <summary>
        /// The mode the switch goes to: RTTY normal, the one the UI calls
        /// "RTTY-L".
        ///
        /// Not RTTY-R. Which of the two is right depends on the station, and the
        /// tuner already has a Reverse control for that; starting from normal
        /// means Reverse means what the operator expects it to mean, and the mark
        /// and shift defaults are the normal-mode ones.
        /// </summary>
        public const string RttyMode = "RTTY-L";

        private readonly IRadioController _radio;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<RttyTunerModeService> _logger;

        // One at a time. Ensuring and restoring both read the radio, decide from
        // what came back and write, so two of them interleaved could save this
        // service's own settings over the operator's.
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Saved? _saved;

        // The shift and speed the filter was last sized for. Every poll of the
        // figure is preceded by a start, and a start that read the mode and the
        // filter width would put two CI-V round trips in front of every one of
        // them - on a 19200-baud bus the poll loop and the scope are already
        // sharing. Nothing about the radio can need looking at twice for the same
        // signal, so a repeat start asks it nothing.
        private (int Shift, double Baud)? _sizedFor;

        public RttyTunerModeService(IRadioController radio,
                                    RadioStateService state,
                                    ISettingsService settings,
                                    ILogger<RttyTunerModeService> logger)
        {
            _radio = radio;
            _state = state;
            _settings = settings;
            _logger = logger;
        }

        private sealed record Saved(string? Mode, int IfWidthHz);

        public bool IsOn => _saved is not null;

        /// <summary>
        /// Make sure the radio can carry this signal, saving what was there the
        /// first time. Returns a line for the tuner's status bar when something
        /// was changed, null when nothing was.
        ///
        /// <para>Called on every start, including a re-tone, because a re-tone can
        /// change the shift - an operator moving from a 170 Hz amateur signal to
        /// an 850 Hz aviation one needs the filter looked at again, and the mode
        /// not looked at again.</para>
        /// </summary>
        /// <param name="shiftHz">The shift the tuner's filters are set to.</param>
        /// <param name="baud">The speed the tuner's filters are set for.</param>
        public async Task<string?> EnsureAsync(int shiftHz, double baud, CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.RttyTunerSetMode) return null;

            await _gate.WaitAsync(ct);
            try
            {
                if (!_radio.IsConnected) return null;
                if (_saved is not null && _sizedFor == (shiftHz, baud)) return null;

                string? switched = null;
                if (_saved is null)
                {
                    // Read the radio rather than trusting the cached state. Mode
                    // is on the poll only every third loop and width is not on it
                    // at all, so a value that is a second or two stale here is not
                    // a stale display - it is what gets written back afterwards.
                    string mode = await _radio.GetModeAsync(RadioVfo.A, ct);
                    if (string.IsNullOrWhiteSpace(mode)) mode = _state.ModeA ?? "";

                    int width = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
                    _saved = new Saved(mode, width);
                    _logger.LogInformation(
                        "RTTY tuner: saving mode {Mode}, IF width {Width}",
                        mode, width > 0 ? width + " Hz" : "unknown");

                    if (NeedsSwitching(mode))
                    {
                        await _radio.SetModeAsync(RadioVfo.A, RttyMode, ct);
                        _state.ModeA = RttyMode;
                        switched = $"Mode set to {RttyMode}.";
                        _logger.LogInformation("RTTY tuner: mode {From} cannot carry RTTY, set to {To}",
                                               mode, RttyMode);
                    }
                }

                // After the mode, always - the width belongs to the mode it was
                // set in, so a width written before a mode change is a width set
                // for the mode the radio is about to leave. The mode change also
                // brings the radio's own stored RTTY width with it, which is the
                // width this then judges.
                var widened = await WidenIfNeededAsync(shiftHz, baud, ct);
                _sizedFor = (shiftHz, baud);

                return Join(switched, widened);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Put back the mode and width that were there before. Restoring when
        /// nothing was saved does nothing, deliberately: the tuner calls this
        /// whenever it stops, and stopping a tuner that never changed the
        /// operator's radio must not change it now.
        /// </summary>
        public async Task RestoreAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var saved = _saved;
                if (saved is null) return;
                if (!_radio.IsConnected)
                {
                    // Left saved on purpose. A radio that is not there cannot be
                    // put back, and dropping the record would mean it never is.
                    _logger.LogInformation("RTTY tuner: radio not connected, mode {Mode} still owed",
                                           saved.Mode);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(saved.Mode))
                {
                    await _radio.SetModeAsync(RadioVfo.A, saved.Mode, ct);
                    _state.ModeA = saved.Mode;
                }

                // Width after mode, and only when it was read: a width of zero is
                // "could not read it", and writing a zero would be inventing one.
                if (saved.IfWidthHz > 0)
                {
                    await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, saved.IfWidthHz, ct);
                    await ReadWidthBackAsync(ct);
                }

                // Cleared last. If a write threw half way through, the operator
                // still has a tuner that will try the restore again next time,
                // which is more use than a service that believes it already has.
                _saved = null;
                _sizedFor = null;
                _logger.LogInformation("RTTY tuner: restored mode {Mode}, IF width {Width} Hz",
                                       saved.Mode, saved.IfWidthHz);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Whether the radio is in a mode that cannot carry an RTTY tone pair at
        /// all - and so the only modes this service will take the radio out of.
        ///
        /// <para>CW, because its filters go down to 50 Hz and an operator who was
        /// just using CW is very likely sitting behind one of them. AM and FM
        /// because their detectors destroy the phase relationship the figure is
        /// drawn from. DATA-FM for the same reason as FM. Everything else -
        /// RTTY, RTTY-R, SSB, DATA - either is FSK already or is a perfectly good
        /// way to receive an audio tone pair, and is left alone.</para>
        /// </summary>
        public static bool NeedsSwitching(string? mode) =>
            mode is "CW-U" or "CW-L" or "AM" or "FM" or "DATA-FM";

        // ---- the filter ----------------------------------------------------

        private async Task<string?> WidenIfNeededAsync(int shiftHz, double baud, CancellationToken ct)
        {
            int current = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
            if (RttyIfWidth.WidenToHz(current, shiftHz, baud) is not { } want) return null;

            await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, want, ct);
            int actual = await ReadWidthBackAsync(ct);
            _logger.LogInformation(
                "RTTY tuner: IF width {Current} Hz is too narrow for a {Shift} Hz shift at {Baud} baud, widened to {Actual} Hz",
                current, shiftHz, baud, actual > 0 ? actual : want);

            return $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {(actual > 0 ? actual : want)} Hz.";
        }

        /// <summary>
        /// Read the width back rather than recording what was asked for. The
        /// controller snaps the request to a rung of the radio's own ladder, and
        /// the operator is about to be shown the number - showing them the request
        /// instead of the result would be a quiet lie the moment the two differ.
        /// </summary>
        private async Task<int> ReadWidthBackAsync(CancellationToken ct)
        {
            int actual = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
            if (actual > 0)
            {
                _state.IfWidthA = actual.ToString();
                _state.IfWidthB = actual.ToString();
            }
            return actual;
        }

        private static string? Join(string? a, string? b) =>
            (a, b) switch
            {
                (null, null) => null,
                (null, _)    => b,
                (_, null)    => a,
                _            => a + " " + b,
            };
    }
}
