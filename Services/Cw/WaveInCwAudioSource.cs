using System.Threading.Channels;
using NAudio.Wave;
using RadioWebControl.Core.Services.Cw;

namespace Icom_Web_Control.Services.Cw
{
    /// <summary>
    /// Core's <see cref="ICwAudioSource"/> over a WinMM recording device.
    ///
    /// The radio's USB codec appears to Windows as an ordinary recording
    /// device, so the reader opens it directly. That is the whole audio stack
    /// IWC needs for this feature: one mono input at 48 kHz, no playback, no
    /// encoding, no session.
    ///
    /// It is opened <b>shared</b>, which WinMM does by default, so the
    /// operator's logging program or digital-mode software can have the same
    /// device open at the same time. The reader only listens; it never keys
    /// anything and never opens an output.
    ///
    /// Frames are moved off the capture thread through a bounded channel. NAudio
    /// raises <c>DataAvailable</c> on its own worker rather than a realtime
    /// callback, so this is less critical than it is in YWC, but the principle
    /// holds: an FFT running on the thread that has to come back for the next
    /// buffer is how capture starts dropping audio under load. If the decoder
    /// falls behind, the oldest frame is dropped rather than the capture
    /// stalled - a few lost characters beats a stuttering device.
    /// </summary>
    public sealed class WaveInCwAudioSource : ICwAudioSource, IDisposable
    {
        /// <summary>What the decoder expects, and what the codec provides.</summary>
        public const int Rate = 48_000;

        /// <summary>10 ms. Core's nominal frame.</summary>
        public const int FrameSamples = 480;

        // A second of audio. Long enough to ride out a GC pause, short enough
        // that a real backlog is discarded rather than decoded a minute late.
        private const int QueueCapacity = 100;

        private readonly ISettingsService _settings;
        private readonly ILogger<WaveInCwAudioSource> _logger;
        private readonly object _gate = new();

        private WaveInEvent? _wave;
        private Channel<ReadOnlyMemory<float>>? _queue;
        private CancellationTokenSource? _cts;
        private Task? _pump;
        private long _dropped;

        // Whatever is left of the last buffer after cutting whole frames out
        // of it. WinMM buffer lengths are not multiples of 480, so without
        // this the remainder would be dropped from every single buffer - a
        // steady, silent loss that looks like a decoder that cannot copy
        // rather than like missing audio.
        private float[] _carry = Array.Empty<float>();
        private int _carried;

        public WaveInCwAudioSource(ISettingsService settings, ILogger<WaveInCwAudioSource> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public int SampleRate => Rate;

        public bool IsRunning { get; private set; }

        public event Action<ReadOnlyMemory<float>>? FrameAvailable;

        /// <summary>Frames discarded because the decoder could not keep up.</summary>
        public long DroppedFrames => Interlocked.Read(ref _dropped);

        /// <summary>True while a device is open and delivering buffers.</summary>
        public bool DeviceOpen { get; private set; }

        /// <summary>
        /// Why the device could not be opened, or null. Surfaced so the reader
        /// can say "no recording device is chosen" rather than sitting there
        /// looking healthy and printing nothing.
        /// </summary>
        public string? CaptureError { get; private set; }

        /// <summary>The device being listened to, for the status line.</summary>
        public string? DeviceName { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            var settings = await _settings.GetSettingsAsync();

            lock (_gate)
            {
                if (IsRunning) return;

                CaptureError = null;
                DeviceOpen = false;
                DeviceName = null;
                _carried = 0;
                Interlocked.Exchange(ref _dropped, 0);

                // The itemDropped callback is the only way to know a frame
                // went. DropOldest discards silently, so without this the
                // reader would show a healthy status while quietly losing
                // audio - which looks exactly like a decoder that cannot copy.
                _queue = Channel.CreateBounded<ReadOnlyMemory<float>>(
                    new BoundedChannelOptions(QueueCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = true,
                    },
                    _ => Interlocked.Increment(ref _dropped));

                _cts = new CancellationTokenSource();
                _pump = Task.Run(() => PumpAsync(_cts.Token));

                // Running is set even when the device fails to open. The reader
                // is running - it simply has nothing to hear - and reporting it
                // as stopped would hide the error message that says why.
                IsRunning = true;

                try
                {
                    OpenLocked(settings.CwAudioDeviceName);
                }
                catch (Exception ex)
                {
                    CaptureError = ex.Message;
                    _logger.LogWarning(ex, "CW reader could not open the recording device");
                }
            }
        }

        /// <summary>Caller holds _gate.</summary>
        private void OpenLocked(string? deviceName)
        {
            if (WaveInEvent.DeviceCount == 0)
                throw new InvalidOperationException(
                    "Windows reports no recording devices at all.");

            int index = CwAudioDevices.IndexFor(deviceName);
            if (index < 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(deviceName)
                    ? "No CW audio device has been chosen. Pick the radio's USB "
                      + "codec under Settings, CW Reader."
                    : $"The chosen CW audio device (\"{deviceName}\") is not "
                      + "present. Plug it back in, or pick another under "
                      + "Settings, CW Reader.");
            }

            _wave = new WaveInEvent
            {
                DeviceNumber = index,

                // Mono. The codec is stereo on some radios, but the decoder
                // wants one channel and mixing two copies of the same audio
                // buys nothing but work.
                WaveFormat = new WaveFormat(Rate, 16, 1),

                // 50 ms buffers, three of them. Small enough that a stop is
                // prompt, large enough that WinMM is not interrupting
                // constantly on a busy machine. The decoder's own latency is
                // dominated by its 128 ms pitch FFT, so shaving this further
                // would not make the text appear sooner.
                BufferMilliseconds = 50,
                NumberOfBuffers = 3,
            };

            _wave.DataAvailable += OnData;
            _wave.RecordingStopped += OnRecordingStopped;
            _wave.StartRecording();

            DeviceOpen = true;
            DeviceName = CwAudioDevices.List().FirstOrDefault(d => d.Index == index)?.Name;
            _logger.LogInformation("CW reader listening to \"{Device}\" (index {Index})",
                                   DeviceName, index);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            WaveInEvent? wave;
            CancellationTokenSource? cts;
            Task? pump;

            lock (_gate)
            {
                if (!IsRunning) return Task.CompletedTask;
                IsRunning = false;
                DeviceOpen = false;

                wave = _wave;
                _wave = null;
                cts = _cts;
                _cts = null;
                pump = _pump;
                _pump = null;

                _queue?.Writer.TryComplete();
            }

            if (wave is not null)
            {
                wave.DataAvailable -= OnData;
                wave.RecordingStopped -= OnRecordingStopped;
                try { wave.StopRecording(); } catch { /* the device may already be gone */ }
                try { wave.Dispose(); } catch { /* likewise */ }
            }

            cts?.Cancel();
            try { pump?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
            cts?.Dispose();

            _logger.LogInformation("CW reader stopped listening");
            return Task.CompletedTask;
        }

        // ---- capture -------------------------------------------------------

        private void OnData(object? sender, WaveInEventArgs e)
        {
            var queue = _queue;
            if (queue is null) return;

            int samples = e.BytesRecorded / 2;
            if (samples <= 0) return;

            // Grow once and keep it. This runs on every buffer, so allocating
            // a fresh array each time would hand the GC 20 arrays a second for
            // as long as the reader is open.
            int needed = _carried + samples;
            if (_carry.Length < needed + FrameSamples)
                Array.Resize(ref _carry, needed + FrameSamples);

            for (int i = 0; i < samples; i++)
            {
                short pcm = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                _carry[_carried + i] = pcm / 32768f;
            }
            _carried += samples;

            int offset = 0;
            while (_carried - offset >= FrameSamples)
            {
                var frame = new float[FrameSamples];
                Array.Copy(_carry, offset, frame, 0, FrameSamples);
                offset += FrameSamples;

                // DropOldest, so this never returns false in practice and
                // never blocks; anything discarded to make room is counted by
                // the itemDropped callback above.
                queue.Writer.TryWrite(frame);
            }

            // Shuffle the remainder down rather than reallocating.
            int left = _carried - offset;
            if (left > 0) Array.Copy(_carry, offset, _carry, 0, left);
            _carried = left;
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is null) return;

            // The device went away under us - unplugged, or claimed
            // exclusively by something else. Say so; the reader shows it.
            DeviceOpen = false;
            CaptureError = e.Exception.Message;
            _logger.LogWarning(e.Exception, "CW reader capture stopped unexpectedly");
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            var queue = _queue;
            if (queue is null) return;

            long seen = 0;
            try
            {
                await foreach (var frame in queue.Reader.ReadAllAsync(ct))
                {
                    seen++;
                    try
                    {
                        FrameAvailable?.Invoke(frame);
                    }
                    catch (Exception ex)
                    {
                        // A decoder fault must not kill the pump: the next
                        // frame may well decode, and a dead pump is a reader
                        // that goes quiet with no explanation.
                        _logger.LogWarning(ex, "CW decode threw on a frame");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }

            _logger.LogDebug("CW audio pump ended after {Frames} frames", seen);
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogDebug(ex, "CW audio source disposal"); }
        }
    }
}
