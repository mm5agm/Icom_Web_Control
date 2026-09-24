using Icom_Web_Control.Services.Cw;

namespace Icom_Web_Control.Services.Audio
{
    /// <summary>
    /// Reference-counted sharing of the one receive-audio capture device.
    ///
    /// IWC opens a single WinMM recording device, in
    /// <see cref="WaveInCwAudioSource"/>, and until the RTTY tuner arrived the
    /// CW reader was its only user, so it could own it outright: start on Start,
    /// stop on Stop. Two users cannot work that way. <c>StopAsync</c> is
    /// absolute - it tears the device down whoever else is listening - so a
    /// second feature that opened the tuner and closed it again would silently
    /// deafen a CW reader that was running at the time. There is no error in
    /// that: the reader keeps its panel, its transcript and its "running"
    /// status, and simply never copies another letter. That is the same failure
    /// the carry buffer in WaveInCwAudioSource exists to prevent, arriving by a
    /// different road.
    ///
    /// So nobody starts or stops the source directly any more. They take a hold
    /// and drop it, and the device lives from the first hold to the last.
    ///
    /// YWC solves the same problem with <c>RadioAudioBridgeService</c>'s
    /// Acquire/Release pair, and the shape here is deliberately the same so the
    /// two RTTY tuners read alike. It is not shared code, though, and could not
    /// be: YWC is counting holds on a PortAudio bridge carrying audio both ways
    /// over the network, and this is counting holds on one local WinMM handle.
    /// Only the counting is common, and the counting is six lines.
    /// </summary>
    public sealed class ReceiveAudioHold
    {
        private readonly WaveInCwAudioSource _source;
        private readonly ILogger<ReceiveAudioHold> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _holds;

        public ReceiveAudioHold(WaveInCwAudioSource source, ILogger<ReceiveAudioHold> logger)
        {
            _source = source;
            _logger = logger;
        }

        /// <summary>The shared source, for its frames and its status fields.</summary>
        public WaveInCwAudioSource Source => _source;

        /// <summary>Holds outstanding. Diagnostics only.</summary>
        public int Holds => Volatile.Read(ref _holds);

        /// <summary>
        /// Take a hold, opening the device if this is the first. Returns the
        /// capture error if the device would not open, else null.
        ///
        /// The hold is taken either way. A failed open is not a failed hold:
        /// the source reports itself running with a <c>CaptureError</c> set,
        /// precisely so the operator sees why there is no audio rather than a
        /// feature that silently refused to start, and releasing still has to
        /// balance.
        /// </summary>
        public async Task<string?> AcquireAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_holds == 0) await _source.StartAsync(ct);
                _holds++;
                _logger.LogDebug("Receive audio hold taken ({Holds} outstanding)", _holds);
                return _source.CaptureError;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Drop a hold, closing the device when the last one goes.
        ///
        /// Releasing more than was taken is ignored rather than thrown: this is
        /// called from stop paths and dispose paths, which run on the way out of
        /// features that may already have failed, and an exception there would
        /// replace a working radio with a stack trace.
        /// </summary>
        public async Task ReleaseAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_holds == 0)
                {
                    _logger.LogDebug("Receive audio release with no hold outstanding - ignored");
                    return;
                }
                _holds--;
                if (_holds == 0) await _source.StopAsync(ct);
                _logger.LogDebug("Receive audio hold dropped ({Holds} outstanding)", _holds);
            }
            finally { _gate.Release(); }
        }
    }
}
