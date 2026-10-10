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

        // The FSK mode the tuner is operating in and will put back if something
        // else moves the radio out of it - null when the tuner is not holding
        // the mode at all, which is every AFSK session: there the mode is the
        // operator's software's business and nothing here may touch it.
        private string? _heldMode;

        // The shift and speed the filter was last sized for. Every poll of the
        // figure is preceded by a start, and a start that read the mode and the
        // filter width would put two CI-V round trips in front of every one of
        // them - on a 19200-baud bus the poll loop and the scope are already
        // sharing. Nothing about the radio can need looking at twice for the same
        // signal, so a repeat start asks it nothing.
        private (double Mark, int Shift, double Baud)? _sizedFor;

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
        /// The FSK mode being held for as long as the tuner runs, or null if
        /// none is. Read without the gate on purpose: it is a single reference
        /// and the only caller is deciding whether a re-assert is worth a task
        /// at all, which <see cref="ReassertAsync"/> then re-checks properly.
        /// </summary>
        public string? HeldMode => _heldMode;

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
        /// <param name="markHz">
        /// The mark tone, in Hz of audio. Needed as well as the shift because this
        /// radio centres its IF passband on the mark, so the mark is what decides
        /// where the passband sits - see <see cref="PassbandCentreHz"/>.
        /// </param>
        /// <param name="shiftHz">The shift the tuner's filters are set to.</param>
        /// <param name="baud">The speed the tuner's filters are set for.</param>
        public async Task<string?> EnsureAsync(double markHz, int shiftHz, double baud,
                                               CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.RttyTunerSetMode) return null;

            await _gate.WaitAsync(ct);
            try
            {
                if (!_radio.IsConnected) return null;
                if (_saved is not null && _sizedFor == (markHz, shiftHz, baud)) return null;

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
                var widened = await WidenIfNeededAsync(markHz, shiftHz, baud, ct);
                _sizedFor = (markHz, shiftHz, baud);

                // What to hold. The mode the radio is in now, if it is one of
                // the radio's own FSK modes - which is either the one just
                // written or one it was already in. An AFSK session holds
                // nothing: RTTY-L would be the wrong answer there, and the
                // operator did not ask for it.
                var effective = RttyMarkCentre.SidebandForFskMode(_state.ModeA) is not null
                    ? _state.ModeA
                    : null;
                _heldMode = effective;

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
                _heldMode = null;
                _logger.LogInformation("RTTY tuner: restored mode {Mode}, IF width {Width} Hz",
                                       saved.Mode, saved.IfWidthHz);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Put the held mode back when something else has moved the radio out of
        /// it while the tuner is running. Returns a line for the status bar when
        /// it had to act, null when there was nothing to do.
        ///
        /// <para>The operator's own front panel is the obvious cause, but not the
        /// common one. A band change recalls that band's stacking register, which
        /// carries the mode the band was last used in, so simply moving from 30 m
        /// to 20 m can land the radio in CW with nobody having asked for it. The
        /// page's mode guard then closes the panel two seconds later and the stop
        /// restores the pre-tuner mode, so an incidental mode change ends the
        /// session - which is what the bench reported on 2026-10-09.</para>
        ///
        /// <para><b>While the tuner is open the mode is the tuner's.</b> Leaving
        /// RTTY is done by closing it, which is also the only thing that puts
        /// the operator's own mode back. Nothing is held in an AFSK session: see
        /// <see cref="_heldMode"/>.</para>
        /// </summary>
        public async Task<string?> ReassertAsync(CancellationToken ct = default)
        {
            if (_heldMode is null) return null;
            if (RttyMarkCentre.SidebandForFskMode(_state.ModeA) is not null) return null;

            await _gate.WaitAsync(ct);
            try
            {
                // Re-read everything inside the gate. A restore may have run
                // while this was waiting for it, in which case the mode is
                // deliberately not RTTY any more and writing one back would
                // undo the operator's own settings a moment after returning
                // them.
                if (_heldMode is not { } hold) return null;
                var now = _state.ModeA;
                if (RttyMarkCentre.SidebandForFskMode(now) is not null) return null;
                if (!_radio.IsConnected) return null;

                await _radio.SetModeAsync(RadioVfo.A, hold, ct);
                _state.ModeA = hold;
                _logger.LogInformation(
                    "RTTY tuner: mode went to {From} with the tuner open, held at {To}", now, hold);

                return $"Mode went to {now} - held at {hold}. Close the tuner to leave RTTY.";
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

        /// <summary>
        /// Where this radio centres its RTTY IF passband, in Hz of audio: on the
        /// mark tone.
        ///
        /// <para>Bench-measured on 2026-10-09 and not a guess. At 450 Hz shift and
        /// 50 baud the old shift-plus-twice-baud sum asked for 550 Hz, and a 550 Hz
        /// filter put the space tone 33 dB down and decoded <i>zero</i> characters;
        /// 1200 Hz decoded cleanly. That is the signature of a passband pinned to
        /// the mark rather than straddling the pair.</para>
        ///
        /// <para>It is a method rather than a constant because it is a fact about
        /// the radio, and the FTdx101MP - measured the same day - answers
        /// differently: it holds the passband at a fixed ~1800 Hz regardless of the
        /// tones. Yaesu Web Control's port of this service therefore returns that
        /// fixed figure here instead, which is exactly why
        /// <see cref="RttyIfWidth"/> takes the centre as a parameter and does not
        /// assume either behaviour.</para>
        /// </summary>
        private static double PassbandCentreHz(double markHz) => markHz;

        private async Task<string?> WidenIfNeededAsync(
            double markHz, int shiftHz, double baud, CancellationToken ct)
        {
            int current = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);

            double centre = PassbandCentreHz(markHz);

            // The worse of the two places the space tone can sit, not the one the
            // current mode puts it in. Reverse is a toggle the operator can flip at
            // any moment, and the mode can change under us too, while this widen
            // runs once at the start of a session; sizing for the nearer placement
            // would mean a filter that silently clips the moment they press
            // Reverse. On this radio the two are equidistant from the mark anyway,
            // so the Max costs nothing here and keeps the sum honest for a radio
            // where they are not.
            double worstSpace =
                Math.Abs((markHz + shiftHz) - centre) >= Math.Abs((markHz - shiftHz) - centre)
                    ? markHz + shiftHz
                    : markHz - shiftHz;

            if (RttyIfWidth.WidenToHz(current, markHz, worstSpace, baud, centre) is not { } want)
                return null;

            await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, want, ct);
            int actual = await ReadWidthBackAsync(ct);
            _logger.LogInformation(
                "RTTY tuner: IF width {Current} Hz is too narrow for a {Shift} Hz shift at {Baud} baud "
                + "with the mark at {Mark} Hz, widened to {Actual} Hz",
                current, shiftHz, baud, markHz, actual > 0 ? actual : want);

            int got = actual > 0 ? actual : want;

            // The widest filter the radio has may still not reach, if the pair sits
            // far enough off the centre of the passband. Saying "widened to 500 Hz"
            // and stopping there would read as success while the screen stayed
            // empty, so the operator is told the dial is the thing to move.
            if (!RttyIfWidth.Passes(got, markHz, worstSpace, baud, centre))
                return $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz, "
                     + "which is still not enough for this tone pair - move the dial to bring the tones "
                     + "closer to the middle of the passband.";

            return $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz.";
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
