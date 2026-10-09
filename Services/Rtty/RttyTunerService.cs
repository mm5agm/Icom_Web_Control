using System.ComponentModel;
using Icom_Web_Control.Services.Audio;
using Icom_Web_Control.Services.Cw;
using RadioWebControl.Core.Services.Rtty;

namespace Icom_Web_Control.Services.Rtty
{
    /// <summary>
    /// The RTTY tuning scope as the application sees it: feeds received audio
    /// to Core's crossed-ellipse filters and hands the browser one sweep at a
    /// time. The filters, the limiter and the figure are all Core's and shared
    /// with Yaesu Web Control; what is local is where the audio comes from and
    /// where the two filters go.
    ///
    /// <para><b>The audio.</b> IWC has no audio bridge - no Remote Audio, no
    /// PortAudio, no network path for receive audio - so there is nothing to
    /// take a capture hold on the way YWC does. It listens to the same local
    /// WinMM recording device the CW reader uses, through
    /// <see cref="ReceiveAudioHold"/>, which exists so that the two can run at
    /// once. Which device that is, is the CW Reader's audio-device setting;
    /// there is only one, and it is the radio's USB codec either way.</para>
    ///
    /// <para><b>The tones.</b> The mark tone is the operator's, 2125 Hz unless
    /// they say otherwise. Which side of it the space tone lands in the audio
    /// depends on how the radio demodulates, and on the IC-7300 the mode name
    /// is no guide to that - <c>NameForMode</c> in CivRadioController calls
    /// RTTY normal "RTTY-L" and RTTY-R "RTTY-U" to match the UI's existing
    /// vocabulary, and says in as many words that the suffixes are names, not
    /// sidebands. The same inversion is already proven for CW on this radio:
    /// CW normal is displayed "CW-U" and is physically the lower-sideband case,
    /// which is why <c>CwReaderService.IsLowerSideband</c> returns true for it,
    /// and why the ZIN offset came out the right way round on the bench.
    ///
    /// Reading RTTY across by the same physics: in RTTY normal the BFO sits
    /// above the signal, so a tone lower in RF comes out higher in the audio.
    /// Space is 170 Hz below mark on the air, so space arrives ABOVE mark in
    /// the audio, and RTTY-R is the other way round. That happens to be the
    /// same answer YWC's rule gives for the same display strings, but it is
    /// reached from this radio's behaviour rather than borrowed from that one.
    ///
    /// <b>Measured on the bench, 2026-09-24, and the inference holds.</b> A
    /// broadcast carrier on 13710 kHz was tuned in RTTY normal with a 2700 Hz
    /// IF, and this scope's own mark filter was swept across the audio to find
    /// it: it peaked at 2125 Hz, which is also the radio's default RTTY mark
    /// pitch, so the dial in RTTY reads the mark frequency. Moving the dial UP
    /// 500 Hz moved the tone UP to 2600-2650 Hz. Audio rising with the dial is
    /// the lower-sideband case, so a tone lower in RF does arrive higher in the
    /// audio, and space - 170 Hz below mark on the air - does land above mark.
    /// RTTY normal therefore behaves exactly as CW normal does on this radio.
    /// <see cref="TonesFor"/> is right as written.</para>
    ///
    /// <para>Anything that is not an FSK mode - DATA-L, DATA-U, LSB, USB - is
    /// AFSK, where the software makes the tones and the radio is a plain SSB
    /// transceiver. RTTY software puts space above mark (2125 / 2295) by
    /// default, so those follow the same default.</para>
    /// </summary>
    public sealed class RttyTunerService : IDisposable
    {
        public const double DefaultMarkHz  = 2125.0;
        public const int    DefaultShiftHz = 170;

        /// <summary>
        /// Amateur RTTY, and what the radio's own decoder is fixed at.
        /// </summary>
        public const double DefaultBaud = 45.45;

        private readonly ReceiveAudioHold _audio;
        private readonly RadioStateService _state;
        private readonly RttyTunerModeService _mode;
        private readonly ILogger<RttyTunerService> _logger;
        private readonly object _gate = new();

        private RttyTuningScope? _scope;
        private bool _holdsCapture;
        private bool _acquiring;
        private string? _captureError;
        private double _markHz = DefaultMarkHz;
        private int _shiftHz = DefaultShiftHz;
        private bool _reverse;
        // What RttyTunerModeService changed about the radio, for the status line.
        private string? _modeNote;
        // The scope does not use this - it is two filters and speed means nothing
        // to it. It is held here because this is the server-side record of what the
        // operator is listening to, and the reader that decodes it will want it. On
        // the server rather than in the page for the reason Reader Mode's state is:
        // a reload must not lose the one figure the operator cannot re-derive by
        // eye. See the note on Baud in StartAsync.
        private double _baud = DefaultBaud;
        // One per window showing the figure; the audio is held while any is.
        private readonly RttyTunerLeases _leases = new();
        private System.Threading.Timer? _timer;

        public RttyTunerService(ReceiveAudioHold audio,
                                RadioStateService state,
                                RttyTunerModeService mode,
                                ILogger<RttyTunerService> logger)
        {
            _audio = audio;
            _state = state;
            _mode = mode;
            _logger = logger;
        }

        public bool IsRunning { get { lock (_gate) return _scope != null; } }

        /// <summary>
        /// Where the mark and space filters go, in audio Hz. Pure and static,
        /// so the rule can be exercised without a radio - though the rule
        /// itself has now been checked against one: see the bench note on the
        /// class.
        /// </summary>
        /// <param name="mode">The display mode string, e.g. "RTTY-L".</param>
        public static (double MarkHz, double SpaceHz) TonesFor(string? mode, double markHz, int shiftHz, bool reverse)
        {
            // "RTTY-U" is what the UI calls RTTY-R (CI-V mode byte 0x08).
            bool spaceAbove = mode != "RTTY-U";
            if (reverse) spaceAbove = !spaceAbove;
            return (markHz, spaceAbove ? markHz + shiftHz : markHz - shiftHz);
        }

        /// <summary>
        /// Start for <paramref name="client"/>, or re-tone if already running.
        /// The filters are shared, so a re-tone from one window moves them for
        /// every window. Returns an error for bad settings.
        /// </summary>
        public async Task<string?> StartAsync(double markHz, int shiftHz, bool reverse,
                                              double baud = DefaultBaud, string? client = null)
        {
            if (markHz < 300 || markHz > 3000) return "Mark must be between 300 and 3000 Hz.";
            // Any shift that fits in the audio, not just the three the radio's
            // SET > Function > RTTY Shift Width menu offers (CI-V 00 40: 00=170,
            // 01=200, 02=425).
            //
            // This was briefly restricted to those three, on the reasoning that a
            // shift the radio cannot be told about is a shift the operator cannot
            // use. That is the wrong way round, and Colin settled it on 2026-10-08:
            // the decoder is the authority, not the radio's own. A listener meets
            // 450 Hz on the DWD weather stations and 850 Hz on aviation circuits
            // every day, and IWC can copy both - it is only the radio's built-in
            // decoder that cannot, and nothing here depends on that decoder. Writing
            // the menu is a separate request and already reports honestly when there
            // is no rung for a figure; see RttyController.SetRadioTones.
            if (shiftHz < 20 || shiftHz > 1200) return "Shift must be between 20 and 1200 Hz.";
            // Checked both ways round, so a later mode change cannot move space out of range.
            if (markHz + shiftHz > 3500 || markHz - shiftHz < 150)
                return "That mark and shift put the space tone outside the audio passband.";
            // Not used by the scope, only recorded - but recorded wrong is worse than
            // not recorded, so it is checked like anything else. The range covers
            // every speed a listener meets, from 45.45 to the 100 and 200 baud
            // military and aviation circuits.
            if (baud < 20 || baud > 300) return "Speed must be between 20 and 300 baud.";

            // Before the lock, and before the tones are worked out, because the
            // mode is an input to both: TonesFor reads _state.ModeA to decide
            // which side of the mark the space tone sits on, so a mode change
            // after the filters were placed would place them on the wrong sides.
            // Only the first start of a run changes anything - see EnsureAsync.
            var modeNote = await _mode.EnsureAsync(shiftHz, baud);

            bool acquire;
            lock (_gate)
            {
                _markHz = markHz;
                _shiftHz = shiftHz;
                _reverse = reverse;
                _baud = baud;
                // Kept rather than returned: StartAsync's return value is an
                // error, and what the mode service did is not an error. It rides
                // out on the next frame so every window showing the figure says
                // the same thing, including one that opened afterwards.
                if (modeNote != null) _modeNote = modeNote;
                _leases.Start(client, DateTime.UtcNow);

                var (m, s) = TonesFor(_state.ModeA, _markHz, _shiftHz, _reverse);
                if (_scope == null)
                {
                    _scope = new RttyTuningScope(WaveInCwAudioSource.Rate, m, s);
                    _state.PropertyChanged += OnRadioStateChanged;
                    _audio.Source.FrameAvailable += OnRxFrame;
                    _timer = new System.Threading.Timer(_ => OnTimer(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
                else
                {
                    _scope.SetTones(m, s);
                }
                // Retry a failed open on every start: the operator may have
                // fixed the device setting since.
                acquire = !_holdsCapture && !_acquiring;
                if (acquire) _acquiring = true;
            }

            if (acquire)
            {
                var error = await _audio.AcquireAsync();
                bool stoppedMeanwhile;
                lock (_gate)
                {
                    _acquiring = false;
                    // A stop that landed while the device was opening left the
                    // release to us. The hold is taken even when the open
                    // failed, so it is ours to drop either way.
                    stoppedMeanwhile = _scope == null;
                    _holdsCapture = !stoppedMeanwhile;
                    if (!stoppedMeanwhile) _captureError = error;
                }
                if (stoppedMeanwhile)
                {
                    await _audio.ReleaseAsync();
                    return null;
                }
                if (error != null)
                    _logger.LogWarning("RTTY tuner running but capture could not open: {Error}", error);
                else
                    _logger.LogInformation("RTTY tuner started: mark {Mark} Hz, shift {Shift} Hz, {Pol}, mode {Mode}",
                        markHz, shiftHz, reverse ? "reverse" : "normal", _state.ModeA);
            }
            return null;
        }

        /// <summary>
        /// <paramref name="client"/>'s dialog has closed. The audio is let go
        /// once no other window holds it, after
        /// <see cref="RttyTunerLeases.StopDebounce"/> unless restarted.
        /// </summary>
        public void RequestStop(string? client = null)
        {
            lock (_gate)
            {
                if (_scope != null) _leases.Stop(client, DateTime.UtcNow);
            }
        }

        private void OnTimer()
        {
            string? why = null;
            lock (_gate)
            {
                why = _leases.Expire(DateTime.UtcNow);
            }
            if (why != null) _ = StopNowAsync(why);
        }

        private async Task StopNowAsync(string why)
        {
            bool release;
            lock (_gate)
            {
                if (_scope == null) return;
                _audio.Source.FrameAvailable -= OnRxFrame;
                _state.PropertyChanged -= OnRadioStateChanged;
                _timer?.Dispose();
                _timer = null;
                _scope = null;
                _leases.Clear();
                _captureError = null;
                // An acquire still in flight releases its own hold when it
                // finds the scope gone, so only a completed hold is ours.
                release = _holdsCapture;
                _holdsCapture = false;
                _modeNote = null;
            }

            if (release) await _audio.ReleaseAsync();

            // After the audio, because the restore talks to the radio over the
            // same 19200-baud bus the scope was polling, and outside the lock
            // because it awaits. A no-op unless the start changed something.
            await _mode.RestoreAsync();
            _logger.LogInformation("RTTY tuner stopped ({Why})", why);
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, scaled to
        /// whole numbers against this sweep's own peak so the reply stays small.
        /// The peak is sent too, for the display's gain control.
        ///
        /// Running means running for <paramref name="client"/>: a window
        /// whose lease lapsed while another kept the tuner going is told it
        /// is stopped, so it starts again and is counted.
        /// </summary>
        public RttyTunerFrame Frame(int points, string? client = null)
        {
            RttyScopeFrame? f;
            lock (_gate)
            {
                f = _scope != null && _leases.Poll(client, DateTime.UtcNow) ? _scope.Snapshot(points) : null;
            }

            var (mark, space) = TonesFor(_state.ModeA, _markHz, _shiftHz, _reverse);
            var frame = new RttyTunerFrame
            {
                Running          = f != null,
                Mode             = _state.ModeA ?? "",
                MarkHz           = f?.MarkHz ?? mark,
                SpaceHz          = f?.SpaceHz ?? space,
                ShiftHz          = _shiftHz,
                Reverse          = _reverse,
                Baud             = _baud,
                CaptureError     = _captureError,
                AudioDevicesOpen = _audio.Source.DeviceOpen,
                ModeNote         = _modeNote,
            };
            if (f == null) return frame;

            float peak = 0f;
            foreach (var v in f.Points) peak = Math.Max(peak, Math.Abs(v));
            var xy = new int[f.Points.Length];
            if (peak > 0)
                for (int i = 0; i < xy.Length; i++)
                    xy[i] = (int)Math.Round(f.Points[i] / peak * 1000f);

            frame.Points  = xy;
            frame.Peak    = peak;
            frame.MarkDb  = Math.Round(f.MarkDb, 1);
            frame.SpaceDb = Math.Round(f.SpaceDb, 1);
            frame.InputDb = Math.Round(f.InputDb, 1);
            return frame;
        }

        /// <summary>
        /// On the WinMM callback thread, by way of the source's pump. Nine
        /// biquad sections a sample is a few tens of microseconds a frame, so
        /// unlike the CW decoder this runs in place rather than through a
        /// second queue.
        /// </summary>
        private void OnRxFrame(ReadOnlyMemory<float> frame)
        {
            try
            {
                _scope?.Process(frame.Span);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTTY tuner frame threw - ignoring");
            }
        }

        private void OnRadioStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RadioStateService.ModeA)) return;
            lock (_gate)
            {
                if (_scope == null) return;
                var (m, s) = TonesFor(_state.ModeA, _markHz, _shiftHz, _reverse);
                if (m != _scope.MarkHz || s != _scope.SpaceHz) _scope.SetTones(m, s);
            }

            // Outside the lock, because it writes to the radio, and only when
            // there is something to write: the mode arrives on every third poll
            // loop and the usual answer is "still RTTY, nothing to do".
            if (_mode.HeldMode is not null
                && RttyMarkCentre.SidebandForFskMode(_state.ModeA) is null)
            {
                _ = HoldModeAsync();
            }
        }

        /// <summary>
        /// Put the mode back when something outside the tuner has moved it. Any
        /// failure is logged and dropped: the figure is still worth drawing, and
        /// a mode that could not be written will be tried again on the next poll
        /// that reports it.
        /// </summary>
        private async Task HoldModeAsync()
        {
            try
            {
                var note = await _mode.ReassertAsync();
                if (note is null) return;
                lock (_gate)
                {
                    if (_scope != null) _modeNote = note;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RTTY tuner: holding the mode threw");
            }
        }

        public void Dispose() => StopNowAsync("shutting down").GetAwaiter().GetResult();
    }

    public sealed class RttyTunerFrame
    {
        public bool    Running          { get; set; }
        public string  Mode             { get; set; } = "";
        public double  MarkHz           { get; set; }
        public double  SpaceHz          { get; set; }
        public int     ShiftHz          { get; set; }
        public bool    Reverse          { get; set; }

        /// <summary>
        /// The speed the operator has set, which the scope does not use. Here so
        /// that the dialog shows the same figure after a reload, and for the reader
        /// to pick up. See the field it comes from in RttyTunerService.
        /// </summary>
        public double  Baud             { get; set; }
        public string? CaptureError     { get; set; }
        /// <summary>
        /// What was changed about the radio so that the figure could be trusted -
        /// a mode switch, a widened filter, or both. Null when nothing was, which
        /// is the usual case: an operator already in RTTY with a sensible filter
        /// is told nothing, because nothing happened to them.
        /// </summary>
        public string? ModeNote        { get; set; }
        public bool    AudioDevicesOpen { get; set; }

        /// <summary>Interleaved x (mark filter), y (space filter), -1000..1000 of Peak.</summary>
        public int[]   Points           { get; set; } = Array.Empty<int>();
        public float   Peak             { get; set; }
        public double  MarkDb           { get; set; }
        public double  SpaceDb          { get; set; }
        public double  InputDb          { get; set; }
    }
}
