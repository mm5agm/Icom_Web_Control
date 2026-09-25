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
        /// Nobody has asked for a sweep in this long: the page has gone, so
        /// stop holding the audio device open for it.
        /// </summary>
        private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Closing and reopening the dialog must not close and reopen the
        /// capture device. A stop only takes effect once it has stood this
        /// long, so flipping the dialog shut and open again is free.
        /// </summary>
        private static readonly TimeSpan StopDebounce = TimeSpan.FromSeconds(2);

        private readonly ReceiveAudioHold _audio;
        private readonly RadioStateService _state;
        private readonly ILogger<RttyTunerService> _logger;
        private readonly object _gate = new();

        private RttyTuningScope? _scope;
        private bool _holdsCapture;
        private bool _acquiring;
        private string? _captureError;
        private double _markHz = DefaultMarkHz;
        private int _shiftHz = DefaultShiftHz;
        private bool _reverse;
        private DateTime _lastPollUtc;
        private DateTime? _stopRequestedUtc;
        private System.Threading.Timer? _timer;

        public RttyTunerService(ReceiveAudioHold audio,
                                RadioStateService state,
                                ILogger<RttyTunerService> logger)
        {
            _audio = audio;
            _state = state;
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

        /// <summary>Start, or re-tone if already running. Returns an error for bad settings.</summary>
        public async Task<string?> StartAsync(double markHz, int shiftHz, bool reverse)
        {
            if (markHz < 300 || markHz > 3000) return "Mark must be between 300 and 3000 Hz.";
            // The three the IC-7300's SET > Function > RTTY Shift Width menu offers
            // (CI-V 00 40: 00=170, 01=200, 02=425). The tuner used to accept 450 and
            // 850 as receive-only rungs the radio could not be told about; they are
            // gone, so every shift the tuner will run on is one the radio can follow.
            if (shiftHz is not (170 or 200 or 425)) return "Shift must be 170, 200 or 425 Hz.";
            // Checked both ways round, so a later mode change cannot move space out of range.
            if (markHz + shiftHz > 3500 || markHz - shiftHz < 150)
                return "That mark and shift put the space tone outside the audio passband.";

            bool acquire;
            lock (_gate)
            {
                _markHz = markHz;
                _shiftHz = shiftHz;
                _reverse = reverse;
                _lastPollUtc = DateTime.UtcNow;
                _stopRequestedUtc = null;

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

        /// <summary>The dialog has closed. Takes effect after <see cref="StopDebounce"/> unless restarted.</summary>
        public void RequestStop()
        {
            lock (_gate)
            {
                if (_scope != null) _stopRequestedUtc ??= DateTime.UtcNow;
            }
        }

        private void OnTimer()
        {
            string? why = null;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_stopRequestedUtc is { } at && now - at >= StopDebounce) why = "dialog closed";
                else if (now - _lastPollUtc > IdleStop) why = "no page polling";
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
                _stopRequestedUtc = null;
                _captureError = null;
                // An acquire still in flight releases its own hold when it
                // finds the scope gone, so only a completed hold is ours.
                release = _holdsCapture;
                _holdsCapture = false;
            }

            if (release) await _audio.ReleaseAsync();
            _logger.LogInformation("RTTY tuner stopped ({Why})", why);
        }

        /// <summary>
        /// The latest <paramref name="points"/> points of the figure, scaled to
        /// whole numbers against this sweep's own peak so the reply stays small.
        /// The peak is sent too, for the display's gain control.
        /// </summary>
        public RttyTunerFrame Frame(int points)
        {
            RttyScopeFrame? f;
            lock (_gate)
            {
                _lastPollUtc = DateTime.UtcNow;
                f = _scope?.Snapshot(points);
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
                CaptureError     = _captureError,
                AudioDevicesOpen = _audio.Source.DeviceOpen,
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
        public string? CaptureError     { get; set; }
        public bool    AudioDevicesOpen { get; set; }

        /// <summary>Interleaved x (mark filter), y (space filter), -1000..1000 of Peak.</summary>
        public int[]   Points           { get; set; } = Array.Empty<int>();
        public float   Peak             { get; set; }
        public double  MarkDb           { get; set; }
        public double  SpaceDb          { get; set; }
        public double  InputDb          { get; set; }
    }
}
