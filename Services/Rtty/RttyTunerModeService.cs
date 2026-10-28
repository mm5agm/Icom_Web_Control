using Icom_Web_Control.Models;
using RadioWebControl.Core.Services.Rtty;

namespace Icom_Web_Control.Services.Rtty
{
    /// <summary>
    /// Puts the radio into RTTY when the tuner starts, sets it up to copy the
    /// signal Auto finds, and puts it all back when the tuner stops.
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
    /// <para><b>What it sets, and in what order.</b></para>
    ///
    /// <para>1. <i>The mode: the operator's RTTY mode</i>
    /// (<see cref="ApplicationSettings.RttyTunerMode"/>), from anything that is
    /// not already of the same kind - see <see cref="NeedsSwitching"/>. Until
    /// 2026-10-13 DATA, LSB and USB were left alone for AFSK operators; I decided
    /// then that opening RTTY means RTTY, the same as opening the CW reader means
    /// CW. Since 2026-10-28 an AFSK operator can choose DATA-L or DATA-U
    /// instead, as on YWC since 2026-10-26: the tuner then puts the radio in
    /// DATA and holds it there. Either sideband of the chosen kind is left as
    /// it is - Reverse is the tuner's business.</para>
    ///
    /// <para>2. <i>Twin PBT, centred.</i> PBT slides the passband about 100 Hz
    /// for 30 steps, and every width decision below assumes it is where the
    /// noise sweep measured it. Put back on stop, before the mode, because it was
    /// changed in RTTY.</para>
    ///
    /// <para>3. <i>The IF filter, to the narrowest rung that carries the pair</i>
    /// - but only once Auto has found the signal and moved the dial so the pair
    /// straddles the passband centre (<see cref="CentreForAsync"/>). Before that
    /// the filter is only ever widened, because a filter sized for where the
    /// dialog thinks the tones are can hide the station Auto is about to look
    /// for. While Auto listens the filter is opened to <see cref="ListenWidthHz"/>
    /// for the same reason.</para>
    ///
    /// <para><b>In DATA the filter is only ever widened.</b> The RTTY passband
    /// centre below was swept; the DATA one (<see cref="DataCentreHz"/>) has not
    /// been, and a filter narrowed round a centre that is wrong hides the signal
    /// it was sized for.</para>
    /// </summary>
    public sealed class RttyTunerModeService
    {
        /// <summary>
        /// The mode the switch goes to unless the operator has chosen another:
        /// RTTY normal, the one the UI calls "RTTY-L".
        ///
        /// Not RTTY-R. Which of the two is right depends on the station, and the
        /// tuner already has a Reverse control for that; starting from normal
        /// means Reverse means what the operator expects it to mean, and the mark
        /// and shift defaults are the normal-mode ones.
        /// </summary>
        public const string RttyMode = "RTTY-L";

        /// <summary>
        /// The modes an operator can choose for RTTY: the radio's two FSK modes,
        /// and the two DATA modes AFSK uses.
        /// </summary>
        public static readonly IReadOnlyList<string> Modes = ["RTTY-L", "RTTY-U", "DATA-L", "DATA-U"];

        /// <summary>One of <see cref="Modes"/>, spelled as listed there, or null.</summary>
        public static string? Normalise(string? mode) =>
            Modes.FirstOrDefault(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The mode the tuner switches to and holds.</summary>
        public static string TargetMode(ApplicationSettings settings) =>
            Normalise(settings.RttyTunerMode) ?? RttyMode;

        private static bool IsData(string? mode) =>
            string.Equals(mode, "DATA-L", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "DATA-U", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="mode"/> is the same kind as
        /// <paramref name="target"/>: both FSK, or both DATA. Either sideband
        /// will do, as it always has for FSK: which one is right depends on the
        /// station, and the tuner's Reverse is for that.
        /// </summary>
        public static bool SameKind(string? mode, string target) =>
            IsData(target)
                ? IsData(mode)
                : RttyMarkCentre.SidebandForFskMode(mode) is not null;

        /// <summary>
        /// Where the IC-7300 centres its DATA passband, in Hz of audio, with
        /// Twin PBT centred. Noise sweep, 2026-10-28, 10.106 MHz: 1494..1541 Hz
        /// at -6 dB in DATA-U at 2400, 3000 and 3600 and DATA-L at 2400, and
        /// DDK9 tuned to 1500 by the carrier came out centred at 1500. The
        /// passband measured about 2.26 kHz at -6 dB whatever the width was
        /// set to, so it is used only to judge whether the filter is wide
        /// enough, never to narrow it.
        /// </summary>
        public const double DataCentreHz = 1500.0;

        /// <summary>
        /// The filter Auto listens through: wide enough for any pair within a few
        /// hundred Hz of where the radio expects it, and inside the RTTY ladder
        /// (which stops at 2700).
        /// </summary>
        public const int ListenWidthHz = 2400;

        // Twin PBT centre on the 0-255 level scale. Both knobs here is "no shift,
        // no narrowing", which is the setting the noise sweep was done at.
        private const int PbtCentre = 128;

        // The shifts the IC-7300's RTTY menu offers (CI-V 1A 05 00 40).
        private static readonly int[] MenuShiftsHz = { 170, 200, 425 };

        private readonly IRadioController _radio;
        private readonly RadioStateService _state;
        private readonly ISettingsService _settings;
        private readonly ILogger<RttyTunerModeService> _logger;

        // One at a time. Ensuring and restoring both read the radio, decide from
        // what came back and write, so two of them interleaved could save this
        // service's own settings over the operator's.
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Saved? _saved;

        // The mode the tuner is operating in and will put back if something
        // else moves the radio out of its kind - null when the tuner is not
        // holding the mode at all.
        private string? _heldMode;

        // The tones the filter was last sized for. Every poll of the figure is
        // preceded by a start, and a start that read the mode and the filter width
        // would put two CI-V round trips in front of every one of them - on a
        // 19200-baud bus the poll loop and the scope are already sharing. Nothing
        // about the radio can need looking at twice for the same signal, so a
        // repeat start asks it nothing. Cleared whenever something else has
        // touched the filter, so the next start sizes it again.
        private (double Mark, int Shift, double Baud, bool Reverse)? _sizedFor;

        // Whether the filter may be narrowed - true once Auto has confidently
        // found the signal and centred it. See the class notes, point 3.
        private bool _narrowOk;

        // The radio's RTTY menu tones, which set where its passband sits. Read on
        // the first start and kept in step with what this service writes.
        private RttyToneSettings? _menu;

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

        private sealed record Saved(string? Mode, int IfWidthHz, int PbtInner, int PbtOuter);

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
        /// <param name="markHz">The tuner's mark tone, in Hz of audio.</param>
        /// <param name="shiftHz">The shift the tuner's filters are set to.</param>
        /// <param name="baud">The speed the tuner's filters are set for.</param>
        /// <param name="reverse">The tuner's Reverse, which decides the space side.</param>
        public async Task<string?> EnsureAsync(double markHz, int shiftHz, double baud, bool reverse,
                                               CancellationToken ct = default)
        {
            var settings = await _settings.GetSettingsAsync();
            if (!settings.RttyTunerSetMode) return null;

            await _gate.WaitAsync(ct);
            try
            {
                if (!_radio.IsConnected) return null;
                if (_saved is not null && _sizedFor == (markHz, shiftHz, baud, reverse)) return null;

                string target = TargetMode(settings);
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

                    if (NeedsSwitching(mode, target))
                    {
                        await _radio.SetModeAsync(RadioVfo.A, target, ct);
                        _state.ModeA = target;
                        switched = $"Mode set to {target}.";
                        _logger.LogInformation("RTTY tuner: mode {From} set to {To}", mode, target);
                    }

                    // PBT read after the mode change, in RTTY or DATA, because that is
                    // where it is about to be changed and where it is put back.
                    int inner = await _radio.GetPbtInnerAsync(ct);
                    int outer = await _radio.GetPbtOuterAsync(ct);

                    _saved = new Saved(mode, width, inner, outer);
                    _narrowOk = false;
                    _logger.LogInformation(
                        "RTTY tuner: saving mode {Mode}, IF width {Width}, PBT {Inner}/{Outer}",
                        mode, width > 0 ? width + " Hz" : "unknown", inner, outer);

                    switched = Join(switched, await CentrePbtAsync(inner, outer, ct));
                    _menu = await _radio.GetRttyToneSettingsAsync(ct);
                }

                // After the mode, always - the width belongs to the mode it was
                // set in, so a width written before a mode change is a width set
                // for the mode the radio is about to leave. The mode change also
                // brings the radio's own stored RTTY width with it, which is the
                // width this then judges.
                var sized = await SizeFilterAsync(markHz, shiftHz, baud, reverse, ct);
                _sizedFor = (markHz, shiftHz, baud, reverse);

                // What to hold. The mode the radio is in now, if it is the
                // kind the operator chose - which is either the one just
                // written or one it was already in. Anything else, which only
                // a mode the switch could not write leaves, holds nothing.
                _heldMode = SameKind(_state.ModeA, target) ? _state.ModeA : null;

                return Join(switched, sized);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Open the filter for Auto to listen through. Returns the width it was
        /// at, for <see cref="EndListeningAsync"/>, or null when nothing was
        /// changed - the tuner is not running, the radio is not in RTTY or DATA,
        /// or the filter is already at least <see cref="ListenWidthHz"/>.
        /// </summary>
        public async Task<int?> BeginListeningAsync(CancellationToken ct = default)
        {
            if (_saved is null) return null;
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is null || !_radio.IsConnected) return null;
                if (RttyMarkCentre.SidebandForMode(_state.ModeA) is null) return null;

                int current = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
                if (current <= 0 || current >= ListenWidthHz) return null;

                await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, ListenWidthHz, ct);
                await ReadWidthBackAsync(ct);
                _sizedFor = null;
                _logger.LogInformation("RTTY Auto: IF width {From} Hz opened to {To} Hz to listen",
                                       current, ListenWidthHz);
                return current;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Put back the width <see cref="BeginListeningAsync"/> replaced, when
        /// Auto found nothing it could size a filter for.
        /// </summary>
        public async Task EndListeningAsync(int widthHz, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is null || !_radio.IsConnected || widthHz <= 0) return;
                await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, widthHz, ct);
                await ReadWidthBackAsync(ct);
                _sizedFor = null;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Get the radio ready for a pair Auto has just measured, and say where
        /// its passband is now centred - or null when there is no centre to give
        /// (tuner not running, radio not in RTTY or DATA, menu unreadable).
        ///
        /// <para>In DATA there is no menu to set: PBT is centred and
        /// <see cref="DataCentreHz"/> given, and the filter is still not allowed
        /// to narrow, because that centre is not measured.</para>
        ///
        /// <para>Sets the menu shift to the nearest the radio offers, because the
        /// passband centre follows the menu pair and a 425 menu shift under a 170
        /// signal would leave the centre 128 Hz to one side of it. Leaves the
        /// menu mark alone: it is the operator's choice, and any mark works once
        /// the dial is moved to suit. Re-centres PBT if it has been moved since
        /// the start. Then allows the next start to narrow the filter.</para>
        /// </summary>
        public async Task<double?> CentreForAsync(int signalShiftHz, CancellationToken ct = default)
        {
            if (_saved is null) return null;
            await _gate.WaitAsync(ct);
            try
            {
                if (_saved is null || !_radio.IsConnected) return null;
                string? mode = _state.ModeA;
                if (IsData(mode))
                {
                    if (_heldMode is null) return null;
                    await CentrePbtAsync(await _radio.GetPbtInnerAsync(ct), await _radio.GetPbtOuterAsync(ct), ct);
                    _sizedFor = null;
                    return DataCentreHz;
                }
                if (RttyMarkCentre.SidebandForFskMode(mode) is null) return null;

                _menu ??= await _radio.GetRttyToneSettingsAsync(ct);
                if (_menu is not { } menu) return null;

                int shift = MenuShiftsHz.MinBy(s => Math.Abs(s - signalShiftHz));
                if (shift != menu.ShiftHz)
                {
                    var wrote = await _radio.SetRttyToneSettingsAsync(menu.MarkHz, shift, ct);
                    if (wrote.ShiftHz is { } took)
                    {
                        _logger.LogInformation("RTTY Auto: radio menu shift {From} Hz set to {To} Hz for a {Signal} Hz signal",
                                               menu.ShiftHz, took, signalShiftHz);
                        menu = new RttyToneSettings(menu.MarkHz, took);
                        _menu = menu;
                    }
                }

                await CentrePbtAsync(await _radio.GetPbtInnerAsync(ct), await _radio.GetPbtOuterAsync(ct), ct);

                _narrowOk = true;
                _sizedFor = null;
                return PassbandCentreHz(menu);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Put back the mode, width and PBT that were there before. Restoring when
        /// nothing was saved does nothing, deliberately: the tuner calls this
        /// whenever it stops, and stopping a tuner that never changed the
        /// operator's radio must not change it now.
        ///
        /// <para>The RTTY menu tones are not put back. The tuner's own Mark and
        /// Shift controls write them too and never have, and the menu shift Auto
        /// chose is the right one for the radio's own decoder on that station.</para>
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

                // PBT first, while the radio is still in the mode it was
                // changed in. A read that failed (-1) is not written back.
                if (saved.PbtInner >= 0) await _radio.SetPbtInnerAsync(saved.PbtInner, ct);
                if (saved.PbtOuter >= 0) await _radio.SetPbtOuterAsync(saved.PbtOuter, ct);

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
                _narrowOk = false;
                _menu = null;
                _logger.LogInformation("RTTY tuner: restored mode {Mode}, IF width {Width} Hz, PBT {Inner}/{Outer}",
                                       saved.Mode, saved.IfWidthHz, saved.PbtInner, saved.PbtOuter);
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
        /// the operator's own mode back. A move within the kind - RTTY to RTTY-R,
        /// DATA-L to DATA-U - is left alone: see <see cref="SameKind"/>.</para>
        /// </summary>
        public async Task<string?> ReassertAsync(CancellationToken ct = default)
        {
            if (_heldMode is not { } held || SameKind(_state.ModeA, held)) return null;

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
                if (SameKind(now, hold)) return null;
                if (!_radio.IsConnected) return null;

                await _radio.SetModeAsync(RadioVfo.A, hold, ct);
                _state.ModeA = hold;
                _sizedFor = null;   // the mode change brought its own width with it
                _logger.LogInformation(
                    "RTTY tuner: mode went to {From} with the tuner open, held at {To}", now, hold);

                return $"Mode went to {now} - held at {hold}. Close the tuner to leave RTTY.";
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// The operator has picked <paramref name="mode"/> for RTTY, and is about
        /// to put the radio in it. While the tuner is holding a mode, the hold
        /// moves to the new one - otherwise the change would be undone as soon
        /// as the radio reported it - and the filter is sized again on the next
        /// start, because it belongs to the mode it was set in.
        /// </summary>
        public async Task ChooseAsync(string mode, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_heldMode is null) return;
                _heldMode = mode;
                _sizedFor = null;
                // Earned in the old kind; DATA never narrows (class notes).
                if (!SameKind(mode, RttyMode)) _narrowOk = false;
                _logger.LogInformation("RTTY tuner: operator chose {Mode}, now held", mode);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Whether the tuner has to change the radio's mode to
        /// <paramref name="target"/>: when it is not already of the same kind
        /// (<see cref="SameKind"/>).
        /// </summary>
        public static bool NeedsSwitching(string? mode, string target) =>
            !SameKind(mode, target);

        // ---- the passband ----------------------------------------------------

        /// <summary>
        /// Where the IC-7300 MkII centres its RTTY IF passband, in Hz of audio,
        /// with Twin PBT centred: on the middle of the tone pair set in its own
        /// RTTY menu.
        ///
        /// <para>Noise sweep, 2026-10-13, RTTY-L, 10.116 MHz: six seconds of
        /// receiver noise per setting, Welch FFT, edges at -6 dB below the in-band
        /// shoulder at each wall (this radio's noise tilts 10-14 dB across the
        /// passband, so edges taken from the peak lie).</para>
        ///
        /// <code>
        ///   menu mark/shift   width              centre measured   mark + shift/2
        ///   2125/425          500..1800 (4)      2332..2350        2337
        ///   2125/170          800                2191              2210
        ///   1615/200          800                1717              1715
        ///   1275/425          800                1482              1487
        /// </code>
        ///
        /// <para>The earlier "mark-centred" reading came from a station sitting
        /// the other way round from the menu pair - its space was below the
        /// menu mark, so the passband looked as if it hugged the mark.</para>
        ///
        /// <para>RTTY-R, 2026-10-28, 10.106 MHz, 500 Hz: the same place. 2125/425
        /// centred at 2326 in both modes, 2125/170 at 2197 in both. The menu
        /// pair is menu mark and menu mark + shift in either mode; what RTTY-R
        /// changes is which end the higher RF lands on (see
        /// <see cref="TunerMarkFromMenu"/>).</para>
        /// </summary>
        private static double PassbandCentreHz(RttyToneSettings menu) =>
            menu.MarkHz + menu.ShiftHz / 2.0;

        /// <summary>
        /// The tuner's Mark for the radio's menu mark. The tuner puts space
        /// below mark in RTTY-R (RTTY-U), as that sideband puts the higher RF
        /// higher in the audio; the radio's menu mark is the lower end of the
        /// pair in both modes, so in RTTY-R the tuner's mark is the menu mark
        /// plus the shift. DDK9, 2026-10-28: dial on a tone, menu 2125/425,
        /// that tone came out at 2552 Hz in RTTY-R and 2126 Hz in RTTY.
        /// </summary>
        public static int TunerMarkFromMenu(string? mode, int menuMarkHz, int shiftHz) =>
            mode == "RTTY-U" ? menuMarkHz + shiftHz : menuMarkHz;

        /// <summary>The other way: the menu mark for the tuner's Mark.</summary>
        public static int MenuMarkFromTuner(string? mode, int tunerMarkHz, int shiftHz) =>
            mode == "RTTY-U" ? tunerMarkHz - shiftHz : tunerMarkHz;

        private async Task<string?> CentrePbtAsync(int inner, int outer, CancellationToken ct)
        {
            if (inner == PbtCentre && outer == PbtCentre) return null;
            // A failed read (-1) is still centred: the centre is the only setting
            // the passband has been measured at.
            await _radio.SetPbtInnerAsync(PbtCentre, ct);
            await _radio.SetPbtOuterAsync(PbtCentre, ct);
            _logger.LogInformation("RTTY tuner: Twin PBT {Inner}/{Outer} centred", inner, outer);
            return "Twin PBT centred.";
        }

        // ---- the filter ----------------------------------------------------

        private async Task<string?> SizeFilterAsync(
            double markHz, int shiftHz, double baud, bool reverse, CancellationToken ct)
        {
            string? mode = _state.ModeA;
            bool data = IsData(mode);

            // Outside RTTY and DATA there is no centre to size against - only
            // reached with the mode switch turned off - so the filter is the
            // operator's.
            double centre;
            if (data)
                centre = DataCentreHz;
            else if (RttyMarkCentre.SidebandForFskMode(mode) is null)
                return null;
            else
            {
                _menu ??= await _radio.GetRttyToneSettingsAsync(ct);
                if (_menu is not { } menu) return null;
                centre = PassbandCentreHz(menu);
            }

            int current = await _radio.GetIfFilterWidthHzAsync(RadioVfo.A, ct);
            if (current <= 0) return null;

            var (mark, space) = RttyTunerService.TonesFor(mode, markHz, shiftHz, reverse);

            if (RttyIfWidth.MinimumHz(mark, space, baud, centre) is not { } floor) return null;
            int rung = RungAtOrAbove(floor, data ? SsbWidestHz : RttyWidestHz);

            // Already right, or wide enough and not yet allowed to narrow: write
            // nothing. A no-op write still costs a CI-V exchange on a bus the
            // scope and the meters are sharing. DATA never narrows (class notes).
            if (rung == current) return null;
            bool narrowing = rung < current;
            if (narrowing && (data || !_narrowOk)) return null;

            await _radio.SetIfFilterWidthHzAsync(RadioVfo.A, rung, ct);
            int got = await ReadWidthBackAsync(ct);
            if (got <= 0) got = rung;

            _logger.LogInformation(
                "RTTY tuner: IF width {Current} Hz {Verb} to {Actual} Hz for mark {Mark:F0} / space {Space:F0} "
                + "at {Baud} baud, passband centre {Centre:F0} Hz, floor {Floor} Hz",
                current, narrowing ? "narrowed" : "widened", got, mark, space, baud, centre, floor);

            // The widest filter the radio has may still not reach, if the pair sits
            // far enough off the centre of the passband. Saying "widened" and
            // stopping there would read as success while the screen stayed empty,
            // so the operator is told the dial is the thing to move.
            if (!RttyIfWidth.Passes(got, mark, space, baud, centre))
                return $"IF width widened from {current} Hz to {got} Hz, which is still not enough "
                     + "for this tone pair - press Auto, or move the dial to bring the tones "
                     + "closer to the middle of the passband.";

            return narrowing
                ? $"IF width narrowed from {current} Hz to {got} Hz, the narrowest that carries a "
                  + $"{shiftHz} Hz shift at {baud:0.##} baud with the pair centred."
                : $"IF width {current} Hz was too narrow for {shiftHz} Hz shift; widened to {got} Hz.";
        }

        // The widest filters the IC-7300 has in RTTY and in SSB (and so DATA).
        internal const int RttyWidestHz = 2700;
        internal const int SsbWidestHz = 3600;

        /// <summary>
        /// The narrowest IC-7300 filter at least <paramref name="wantedHz"/>
        /// wide: 50 Hz steps to 500, 100 Hz steps to <paramref name="widestHz"/>
        /// (2700 in RTTY, 3600 in SSB and DATA). Rounded up, not to
        /// the nearest - a floor is a floor, and the controller's own snapping
        /// picks the nearest, which can be below it. The answer is always a rung,
        /// so the controller's snap leaves it alone.
        /// </summary>
        internal static int RungAtOrAbove(int wantedHz, int widestHz = RttyWidestHz)
        {
            if (wantedHz <= 50) return 50;
            if (wantedHz <= 500) return (wantedHz + 49) / 50 * 50;
            return Math.Min(widestHz, (wantedHz + 99) / 100 * 100);
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
