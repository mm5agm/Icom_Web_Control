using Icom_Web_Control.Services.Audio;
using Icom_Web_Control.Services.Cw;
using RadioWebControl.Core.Services.Rtty;

namespace Icom_Web_Control.Services.Rtty
{
    /// <summary>
    /// The Auto button: listen for a few seconds and work out what the station is
    /// sending - the two tones, the shift between them, which way round they are,
    /// and the speed.
    ///
    /// <para>All of the signal processing is Core's
    /// <see cref="RttySignalAnalyser"/>, which is radio-agnostic and shared with
    /// Yaesu Web Control. What is local, and all that is here, is getting four
    /// seconds of receive audio out of IWC's one WinMM device and turning the
    /// answer back into the three things the tuner is set with.</para>
    ///
    /// <para><b>It takes its own audio hold</b> rather than requiring the tuner to
    /// be running. The hold is reference counted, so pressing Auto with the tuner
    /// open costs nothing - the device is already streaming and this just listens in
    /// - and pressing it with the tuner closed opens the device for the four
    /// seconds and lets it go again.</para>
    ///
    /// <para><b>The answer is a measurement, not a menu choice.</b> The analyser
    /// reports the shift and the speed as it finds them, which may be a figure the
    /// radio's own RTTY menu has no rung for (450 Hz, say) or a speed nobody
    /// tabulated (56.9 baud on a press circuit). That is deliberate and it is the
    /// operator's instruction: the decoder works on what is actually on the air, and
    /// the radio's built-in decoder is not the authority on what a listener may
    /// copy. Whether a figure happens to have a name is reported separately, as
    /// <c>SnappedShiftHz</c> and <c>SnappedBaud</c>, so the dialog can fill its
    /// dropdown when there is a rung for it and show the measurement when there is
    /// not.</para>
    /// </summary>
    public sealed class RttyAutoService
    {
        /// <summary>
        /// How long to listen. Four seconds is about twenty-four characters at
        /// 45.45 baud, which is far more keying than the run-length estimate needs,
        /// and short enough that the operator does not think the button is broken.
        /// </summary>
        public const double ListenSeconds = 4.0;

        private readonly ReceiveAudioHold _audio;
        private readonly RadioStateService _state;
        private readonly ILogger<RttyAutoService> _logger;

        // One analysis at a time. Two at once would both be correct - they would
        // simply hear the same audio - but they would also both hold the device and
        // report separately, and the operator pressing Auto twice means "I want a
        // fresh answer", not "I want two".
        private readonly SemaphoreSlim _one = new(1, 1);

        public RttyAutoService(ReceiveAudioHold audio,
                               RadioStateService state,
                               ILogger<RttyAutoService> logger)
        {
            _audio = audio;
            _state = state;
            _logger = logger;
        }

        /// <summary>Whether an analysis is running, for the button's state.</summary>
        public bool IsBusy => _one.CurrentCount == 0;

        public async Task<RttyAutoResult> AnalyseAsync(CancellationToken ct = default)
        {
            if (!await _one.WaitAsync(0, ct))
                return RttyAutoResult.Failed("Already listening - wait for that to finish.");

            try
            {
                var (audio, captureError) = await ListenAsync(ct);
                if (captureError != null)
                    return RttyAutoResult.Failed(captureError);
                if (audio == null)
                    return RttyAutoResult.Failed(
                        "No receive audio arrived. Check the audio device on the Settings page.");

                var got = RttySignalAnalyser.Analyse(audio, WaveInCwAudioSource.Rate);
                if (got == null)
                    return RttyAutoResult.Failed(
                        "No RTTY keying found. Tune the signal in and try again.");

                return Describe(got);
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>
        /// Four seconds of receive audio, or null if none arrived.
        ///
        /// The frames land on the WinMM callback thread and are copied straight
        /// into one pre-allocated buffer, which is the only writer, so no locking is
        /// needed beyond the volatile write that publishes how much is in it.
        /// </summary>
        private async Task<(float[]? audio, string? error)> ListenAsync(CancellationToken ct)
        {
            var wanted = (int)(WaveInCwAudioSource.Rate * ListenSeconds);
            var buffer = new float[wanted];
            var filled = 0;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnFrame(ReadOnlyMemory<float> frame)
            {
                var room = wanted - filled;
                if (room <= 0) return;

                var take = Math.Min(room, frame.Length);
                frame.Span[..take].CopyTo(buffer.AsSpan(filled));
                Volatile.Write(ref filled, filled + take);

                if (filled >= wanted) done.TrySetResult();
            }

            var error = await _audio.AcquireAsync(ct);
            _audio.Source.FrameAvailable += OnFrame;
            try
            {
                if (error != null) return (null, error);

                // A second of slack. A full buffer finishes on the frame that fills
                // it; the timeout is for the device that opened but is not
                // delivering, which must say so rather than hang the request.
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(ListenSeconds + 1));
                using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                using (both.Token.Register(() => done.TrySetResult()))
                    await done.Task;

                ct.ThrowIfCancellationRequested();

                var got = Volatile.Read(ref filled);

                // Enough to work with even if the device was slow: the analyser
                // needs two FFT windows and a page of keying, and two seconds is
                // both. Below that, say so rather than report a guess made from a
                // handful of frames.
                if (got < WaveInCwAudioSource.Rate * 2)
                {
                    _logger.LogWarning("RTTY Auto heard only {Samples} samples in {Seconds} s",
                        got, ListenSeconds + 1);
                    return (null, null);
                }

                return (got == wanted ? buffer : buffer[..got], null);
            }
            finally
            {
                _audio.Source.FrameAvailable -= OnFrame;
                await _audio.ReleaseAsync(CancellationToken.None);
            }
        }

        /// <summary>
        /// The estimate as the tuner's three settings. The analyser reports two
        /// audio frequencies; the tuner is set with a mark, a shift and a Rev flag,
        /// and which Rev means depends on the mode - so this is
        /// <see cref="RttyTunerService.TonesFor"/> run backwards, and the two have
        /// to stay in step.
        /// </summary>
        private RttyAutoResult Describe(RttySignalEstimate got)
        {
            var mode = _state.ModeA ?? "";

            // Where the space tone sits with Rev unticked, which is what the mode
            // alone decides. Measured the other way round means Rev.
            var (_, defaultSpace) = RttyTunerService.TonesFor(mode, got.MarkHz, 1, false);
            var defaultSpaceAbove = defaultSpace > got.MarkHz;
            var reverse = (got.SpaceHz > got.MarkHz) != defaultSpaceAbove;

            var shift = (int)Math.Round(got.ShiftHz);

            _logger.LogInformation(
                "RTTY Auto: mark {Mark:F0} Hz, space {Space:F0} Hz, shift {Shift} Hz, " +
                "{Baud:F2} baud, {Rev}, confidence {Conf:F2}, tone margin {Margin:F2}",
                got.MarkHz, got.SpaceHz, shift, got.Baud,
                reverse ? "reversed" : "normal", got.Confidence, got.ToneMargin);

            return new RttyAutoResult
            {
                Ok              = true,
                Mode            = mode,
                MarkHz          = Math.Round(got.MarkHz, 1),
                SpaceHz         = Math.Round(got.SpaceHz, 1),
                ShiftHz         = shift,
                SnappedShiftHz  = RttySignalAnalyser.SnapShift(got.ShiftHz),
                Baud            = Math.Round(got.Baud, 2),
                SnappedBaud     = RttySignalAnalyser.SnapBaud(got.Baud),
                Reverse         = reverse,
                Confidence      = Math.Round(got.Confidence, 2),
                ToneMargin      = Math.Round(got.ToneMargin, 2),
            };
        }
    }

    public sealed class RttyAutoResult
    {
        public bool    Ok      { get; set; }
        public string? Reason  { get; set; }
        public string  Mode    { get; set; } = "";

        public double MarkHz  { get; set; }
        public double SpaceHz { get; set; }

        /// <summary>The measured shift, rounded to the nearest hertz.</summary>
        public int ShiftHz { get; set; }

        /// <summary>
        /// The standard shift the measurement matches, or null when it matches
        /// none - which is the dialog's cue to show <see cref="ShiftHz"/> as a
        /// measurement rather than select a rung.
        /// </summary>
        public int? SnappedShiftHz { get; set; }

        /// <summary>The measured speed. Not rounded to a standard one.</summary>
        public double Baud { get; set; }

        /// <summary>As <see cref="SnappedShiftHz"/>, for the speed.</summary>
        public double? SnappedBaud { get; set; }

        public bool Reverse { get; set; }

        /// <summary>
        /// How sure the analyser is that this is RTTY with these tones at this
        /// speed. Below about 0.4 it is a guess.
        /// </summary>
        public double Confidence { get; set; }

        /// <summary>
        /// How sure it is about <see cref="Reverse"/>, separately - a station
        /// using a one-bit stop element can be identified perfectly except for
        /// which tone is mark. Near zero means "try Rev if it does not copy".
        /// </summary>
        public double ToneMargin { get; set; }

        public static RttyAutoResult Failed(string reason) => new() { Ok = false, Reason = reason };
    }
}
