using System;
using System.IO;
using System.Linq;
using RadioWebControl.Core.Services.Cw;
using System.Collections.Generic;
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

                // AnalyseAgreed, not Analyse: it measures the first and second
                // halves of this same capture separately and refuses to answer
                // unless they agree, which costs the operator no extra waiting and
                // is the only thing that catches a spurious speed. See the method's
                // own notes for the bench readings that put it there.
                var checkd = RttySignalAnalyser.AnalyseAgreed(
                    audio, WaveInCwAudioSource.Rate);

                // Every press keeps its four seconds, named after what Auto made of
                // them, so a surprising answer can be examined instead of argued
                // about. On the bench a properly tuned DDK9 gave 50.05, 105.17,
                // 46.78 and 51.13 baud on consecutive presses, and there was no way
                // to ask which of those the audio actually supported - the audio was
                // gone. These files are the answer to that question, and they are
                // also ready-made decoder fixtures.
                SaveCapture(audio, checkd);

                if (checkd.Estimate == null)
                {
                    LogRefusal(checkd);

                    // The advisory goes on the refusals too, and it matters more
                    // here than on an answer: a filter too narrow to pass the
                    // station is one of the likeliest reasons the two halves could
                    // not agree, and a bare "try again when it steadies" would send
                    // the operator to wait out a fade that was never the problem.
                    return RttyAutoResult.Failed(
                        checkd.Outcome == RttyAgreement.DidNotRepeat
                            ? "The signal did not measure the same twice - it is "
                              + "probably fading. Nothing was changed; try again "
                              + "when it steadies."
                            : "No RTTY keying found. Tune the signal in and try again.",
                        Advisories());
                }

                return Describe(checkd.Estimate);
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>
        /// A long, continuous recording of the receive audio, straight to a WAV.
        ///
        /// <para>Auto's own captures are four seconds each, which is the right length
        /// for the thing Auto does and the wrong length for working out why it keeps
        /// changing its mind. A minute of the same station lets the same audio be cut
        /// up and re-measured as often as the question needs, and the answers compared
        /// against each other rather than against a signal that has since gone.</para>
        ///
        /// <para>Streamed to the file frame by frame, not buffered: a minute at 48 kHz
        /// is 11 MB of float in memory, ten minutes is 115 MB, and there is no reason
        /// to hold any of it.</para>
        ///
        /// <para>This changes nothing on the radio. It listens to whatever the receiver
        /// is already doing.</para>
        /// </summary>
        /// <param name="seconds">How long to record. Clamped to 5 s - 30 min.</param>
        /// <param name="name">Optional base name; a timestamp if omitted.</param>
        public async Task<(string? path, string? error)> RecordAsync(
            double seconds, string? name = null, CancellationToken ct = default)
        {
            seconds = Math.Clamp(seconds, 5, 30 * 60);

            // Shares the one-at-a-time lock with Auto, because they share the device
            // and because a press of Auto during a recording would otherwise take the
            // audio away from it halfway through.
            if (!await _one.WaitAsync(0, ct))
                return (null, "Already listening - wait for that to finish.");

            var dir = CaptureDirectory();
            Directory.CreateDirectory(dir);

            var stem = string.IsNullOrWhiteSpace(name)
                ? $"rtty-long-{DateTime.Now:yyyyMMdd-HHmmss}"
                : string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, stem + ".wav");

            try
            {
                var error = await _audio.AcquireAsync(ct);
                if (error != null) return (null, error);

                try
                {
                    using var wav = new CwWavRecorder(path, WaveInCwAudioSource.Rate);

                    // The frames arrive on the WinMM callback thread, so the writer is
                    // the one thing two threads touch. CwWavRecorder locks internally -
                    // the CW reader has been writing to it from that same thread since
                    // it was built - so the handler only has to not throw.
                    void OnFrame(ReadOnlyMemory<float> frame)
                    {
                        try { wav.Write(frame.Span); } catch { /* disposed mid-flight */ }
                    }

                    _audio.Source.FrameAvailable += OnFrame;
                    try
                    {
                        _logger.LogInformation(
                            "RTTY long capture started, {Seconds:F0} s: {Path}", seconds, path);

                        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                    }
                    finally
                    {
                        _audio.Source.FrameAvailable -= OnFrame;
                    }

                    _logger.LogInformation(
                        "RTTY long capture finished: {Seconds:F1} s in {Path}",
                        wav.DurationSeconds, path);

                    return (path, null);
                }
                finally
                {
                    await _audio.ReleaseAsync(CancellationToken.None);
                }
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>
        /// How many captures to keep. Four seconds of 48 kHz mono is about 384 KB,
        /// so sixty of them is a little over 20 MB - enough to cover a whole bench
        /// session and small enough that nobody has to think about it.
        /// </summary>
        private const int KeepCaptures = 60;

        /// <summary>
        /// Where the captures go: beside the CW bench captures, under the same user
        /// data directory as everything else the app writes.
        /// </summary>
        public static string CaptureDirectory() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Icom Web Control", "RTTY Captures");

        /// <summary>
        /// The audio Auto just judged, written to a WAV named after the judgement.
        ///
        /// <para>Reuses <see cref="CwWavRecorder"/> rather than writing a second RIFF
        /// header: a WAV writer has nothing to do with any radio, which is why that
        /// class is in core already, and the CW reader's bench captures have been
        /// written by it since the decoder was built. The name carries the verdict so
        /// that a directory listing is itself the bench log - a run of files reading
        /// 50baud, 105baud, refused, 46baud is the instability in one glance.</para>
        ///
        /// <para>Never allowed to break Auto. The operator pressed a button to
        /// measure a signal, not to write a file, so a full disk or a locked
        /// directory is logged and swallowed.</para>
        /// </summary>
        private void SaveCapture(float[] audio, RttyAgreementResult r)
        {
            try
            {
                var dir = CaptureDirectory();
                Directory.CreateDirectory(dir);

                var verdict = r.Estimate is { } e
                    ? $"{e.ShiftHz:F0}Hz-{e.Baud:F2}baud-conf{e.Confidence:F2}"
                    : $"refused-{r.Outcome}";

                var path = Path.Combine(
                    dir, $"rtty-{DateTime.Now:yyyyMMdd-HHmmss}-{verdict}.wav");

                using (var wav = new CwWavRecorder(path, WaveInCwAudioSource.Rate))
                    wav.Write(audio);

                _logger.LogInformation("RTTY Auto capture saved: {Path}", path);
                PruneCaptures(dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RTTY Auto capture could not be saved");
            }
        }

        /// <summary>Oldest captures beyond <see cref="KeepCaptures"/>, deleted.</summary>
        private void PruneCaptures(string dir)
        {
            // "rtty-2026...", not "rtty-long-2026...": the long captures are made
            // deliberately, one at a time, and are the ones somebody will come back
            // for. Only Auto's automatic four-second files are pruned.
            var old = new DirectoryInfo(dir)
                .GetFiles("rtty-2*.wav")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepCaptures);

            foreach (var file in old)
            {
                try { file.Delete(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not prune {File}", file.Name);
                }
            }
        }

        /// <summary>
        /// A refusal, in the log, with the halves that caused it.
        ///
        /// Successes were logged from the start and refusals were not, which left
        /// the one outcome that needs explaining as the one that wrote nothing at
        /// all - so a bench report of "Auto did not find it" could not be told from
        /// the operator having pressed at the wrong moment. The half figures are
        /// what distinguishes the cases: a speed that moved while the tones held is
        /// a fade, two different tone pairs are two stations, and one half finding
        /// nothing is a signal that came and went inside the four seconds.
        /// </summary>
        private void LogRefusal(RttyAgreementResult r)
        {
            static string Half(RttySignalEstimate? e) => e == null
                ? "nothing"
                : $"{e.MarkHz:F0}/{e.SpaceHz:F0} Hz shift {e.ShiftHz:F0} {e.Baud:F2} baud";

            _logger.LogInformation(
                "RTTY Auto refused ({Outcome}): first half {Early}, second half {Late}",
                r.Outcome, Half(r.Early), Half(r.Late));
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
                "{Baud:F2} baud, {Rev}, confidence {Conf:F2}, tone margin {Margin:F2}, " +
                "halves agreed {Agreement:F2}",
                got.MarkHz, got.SpaceHz, shift, got.Baud,
                reverse ? "reversed" : "normal", got.Confidence, got.ToneMargin,
                got.Agreement);

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
                Agreement       = Math.Round(got.Agreement, 2),
                Advice          = Advisories(),
            };
        }

        /// <summary>
        /// Any shift wider than the IF filter is simply not in the audio, so a
        /// narrow filter makes Auto confidently right about a pair of tones that is
        /// the wrong pair. On the bench a 250 Hz CW filter left open gave a shift of
        /// 104 Hz at good confidence, which is what the filter skirts were passing -
        /// the analyser was not wrong, it was answering about what reached it.
        ///
        /// <para>This is a note and not a refusal, and that distinction was argued
        /// out: a refusal would have to guess at what the operator is tuned to, and
        /// a legitimate 170 Hz station through a 250 Hz filter would be refused for
        /// no reason. It would not even have caught the case above, whose measured
        /// shift fits a narrow station perfectly. Only the operator knows whether
        /// they expected something wider, so only the operator can judge it - this
        /// tells them what they need in order to.</para>
        /// </summary>
        private string? Advisories()
        {
            var notes = new List<string>();

            // The one that cost a bench session. Auto in CW-U on a 250 Hz filter
            // measured a shift of 93 to 108 Hz off DDK9 - the filter skirts, not the
            // station - and said so with a straight face. Nothing in the audio can
            // reveal that the receiver is in the wrong mode, because the audio is
            // all the analyser gets.
            var mode = _state.ModeA ?? "";
            if (!mode.StartsWith("RTTY", StringComparison.OrdinalIgnoreCase))
                notes.Add($"The radio is in {mode}, not RTTY, so the tones you are "
                        + "hearing are not where RTTY would put them. Switch to RTTY "
                        + "and press Auto again.");

            var filter = NarrowFilterNote();
            if (filter != null) notes.Add(filter);

            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        private string? NarrowFilterNote()
        {
            // 500 Hz is chosen to stay silent for every filter anyone receives RTTY
            // through - the usual 500 to 2400 - and speak up for the CW filters,
            // which is where the problem lives.
            const int NarrowHz = 500;

            if (!int.TryParse(_state.IfWidthA, out var hz) || hz <= 0 || hz >= NarrowHz)
                return null;

            return $"Your IF filter is {hz} Hz, so no shift wider than that could "
                 + "have been seen. Open it and press Auto again if you expected one.";
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

        /// <summary>
        /// How nearly the first and second halves of the capture measured the same
        /// speed, 0 to 1. Separate from <see cref="Confidence"/> on purpose: a
        /// result that is here at all already passed the agreement gate, so folding
        /// one into the other would charge a signal twice for the same thing.
        /// </summary>
        public double Agreement { get; set; } = 1;

        /// <summary>
        /// A note for the operator about something the analyser could not have
        /// known, or null when there is nothing to say. Shown after the figures, not
        /// instead of them: the answer is still the answer. Today it only ever
        /// reports a narrow IF filter.
        /// </summary>
        public string? Advice { get; set; }

        public static RttyAutoResult Failed(string reason, string? advice = null) =>
            new() { Ok = false, Reason = reason, Advice = advice };
    }
}
