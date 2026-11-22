using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Who gets on the CI-V bus, and how long they wait (IWC Discussion #51).
    ///
    /// The bus takes one transaction at a time. When the radio half-answers,
    /// each transaction can hold it for its whole timeout, and requests from
    /// the browser and rigctld queue behind each other. A browser allows only
    /// a few connections to one host, so a queue of radio calls can leave pages
    /// that don't load at all. Three rules here:
    ///  * The poll loop waits as long as it takes, as before. It is the caller
    ///    that has to get through, and it marks itself with <see cref="MarkPollLoop"/>.
    ///  * Everyone else gives up after <see cref="BusWaitMs"/> and reads it as
    ///    a radio that didn't answer, which every caller already handles.
    ///  * While the radio isn't answering at all (the last
    ///    <see cref="SilentAfterTimeouts"/> transactions timed out and nothing
    ///    has come back for <see cref="SilentAfterMs"/>), everyone but the poll
    ///    loop is told "no reply" straight away. The poll loop's first answer ends it.
    /// Long waits are logged with the queue's depth, so a log shows whether
    /// this is what happened.
    /// </summary>
    internal sealed class CivBusGate : IDisposable
    {
        internal const int BusWaitMs = 1500;
        internal const int SlowWaitMs = 1000;
        internal const int SilentAfterTimeouts = 4;
        internal const int SilentAfterMs = 2000;
        internal const int LogEveryMs = 10_000;

        private static readonly AsyncLocal<bool> PollLoop = new();

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ILogger _logger;
        private readonly Func<long> _now;
        private readonly object _stats = new();

        private int _waiting;
        private int _timeouts;
        private long _lastAnswer;
        private bool _silent;

        // Since the last warning: the longest wait, the most waiting, and how many gave up or were skipped.
        private long _longestWait;
        private int _mostWaiting;
        private int _gaveUp;
        private int _skipped;
        private long _lastLog = long.MinValue;

        public CivBusGate(ILogger logger, Func<long>? now = null)
        {
            _logger = logger;
            _now = now ?? (() => Environment.TickCount64);
            _lastAnswer = _now();
        }

        /// <summary>
        /// Marks the calling async flow, and everything it awaits, as the poll
        /// loop: it waits for the bus without a limit and is never skipped.
        /// Set once at the top of the poll loop; only that flow carries it.
        /// </summary>
        public static void MarkPollLoop() => PollLoop.Value = true;

        internal static bool IsPollLoop => PollLoop.Value;

        /// <summary>How many callers are waiting for the bus now.</summary>
        public int Waiting => Volatile.Read(ref _waiting);

        /// <summary>Whether the radio has stopped answering; see the class summary.</summary>
        public bool RadioSilent
        {
            get { lock (_stats) return _silent; }
        }

        /// <summary>
        /// Wait for the bus. True once this caller holds it; it must then call
        /// <see cref="Release"/>. False if it gave up or was skipped, and it
        /// doesn't hold the bus. <paramref name="mayBeSkipped"/> is false for a
        /// raw write: the power-on wake-up has to reach a radio that isn't answering.
        /// Throws if <paramref name="ct"/> is cancelled, as the bare lock did.
        /// </summary>
        public async Task<bool> EnterAsync(bool mayBeSkipped, CancellationToken ct)
        {
            bool poll = IsPollLoop;
            if (!poll && mayBeSkipped && RadioSilent)
            {
                lock (_stats) _skipped++;
                Report(0, 0);
                return false;
            }

            int waiting = Interlocked.Increment(ref _waiting);
            long start = _now();
            bool entered = false;
            try
            {
                if (poll) { await _lock.WaitAsync(ct); entered = true; }
                else entered = await _lock.WaitAsync(BusWaitMs, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }

            long waited = _now() - start;
            lock (_stats)
            {
                if (waited > _longestWait) _longestWait = waited;
                if (waiting > _mostWaiting) _mostWaiting = waiting;
                if (!entered) _gaveUp++;
            }
            if (!entered || waited >= SlowWaitMs) Report(waited, waiting);
            return entered;
        }

        public void Release() => _lock.Release();

        /// <summary>The radio answered a transaction (an NG counts).</summary>
        public void Answered()
        {
            bool was;
            lock (_stats)
            {
                was = _silent;
                _silent = false;
                _timeouts = 0;
                _lastAnswer = _now();
            }
            if (was)
                _logger.LogInformation("[CivBus] The radio is answering again; calls from the browser and rigctld go through again.");
        }

        /// <summary>A transaction got no answer in its time.</summary>
        public void TimedOut()
        {
            bool began;
            lock (_stats)
            {
                _timeouts++;
                began = !_silent && _timeouts >= SilentAfterTimeouts && _now() - _lastAnswer >= SilentAfterMs;
                if (began) _silent = true;
            }
            if (began)
                _logger.LogWarning("[CivBus] The radio hasn't answered the last {Count} requests, or anything for {Ms} ms. " +
                                   "Calls from the browser and rigctld get \"no reply\" straight away until it does; " +
                                   "the poll loop keeps asking.", SilentAfterTimeouts, SilentAfterMs);
        }

        /// <summary>A new port: forget the last one's silence.</summary>
        public void Reset()
        {
            lock (_stats)
            {
                _silent = false;
                _timeouts = 0;
                _lastAnswer = _now();
            }
        }

        // One warning per LogEveryMs at most, carrying what happened since the last.
        private void Report(long waited, int waiting)
        {
            long longest; int most, gaveUp, skipped;
            lock (_stats)
            {
                long now = _now();
                if (_lastLog != long.MinValue && now - _lastLog < LogEveryMs) return;
                _lastLog = now;
                longest = _longestWait; most = _mostWaiting; gaveUp = _gaveUp; skipped = _skipped;
                _longestWait = 0; _mostWaiting = 0; _gaveUp = 0; _skipped = 0;
            }
            _logger.LogWarning("[CivBus] Slow bus: this call waited {Waited} ms with {Waiting} in the queue. " +
                               "Since the last report: longest wait {Longest} ms, up to {Most} waiting, " +
                               "{GaveUp} gave up after {Limit} ms, {Skipped} skipped while the radio wasn't answering.",
                waited, waiting, longest, most, gaveUp, BusWaitMs, skipped);
        }

        public void Dispose() => _lock.Dispose();
    }
}
