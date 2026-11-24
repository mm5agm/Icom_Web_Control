namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Lets one caller at a time drain the serial port (D#51).
    ///
    /// <para><b>Why.</b> .NET raises SerialPort.DataReceived by queueing a
    /// thread-pool job for each event, so on a busy PC several handlers run at
    /// once. Each read the byte count the port gave it, but an earlier handler
    /// had already taken those bytes, so the later ones waited in port.Read
    /// for up to the ReadTimeout with a pool thread each, while the scope's
    /// stream kept queueing more. Paul VK2AHB's laptop showed 398 busy pool
    /// threads, 3741 jobs waiting and IWC using under 11% CPU: the Radio page
    /// froze until the radio was switched off. Handlers running at once could
    /// also hand bytes to the frame buffer out of order.</para>
    ///
    /// <para>Now the first handler drains everything there is and the rest
    /// return straight away. Only the drainer reads, and only what the port
    /// says it holds, so a read never waits. Bytes that arrive just as the
    /// drainer finishes are picked up by its last check.</para>
    /// </summary>
    internal sealed class SingleDrain
    {
        private int _busy;
        private long _skipped;

        /// <summary>Events that found a drain already running and returned.</summary>
        public long Skipped => Interlocked.Read(ref _skipped);

        /// <summary>
        /// Drains <paramref name="available"/> bytes with <paramref name="read"/>,
        /// handing each chunk to <paramref name="deliver"/> in order, unless
        /// another caller is already draining. Returns whether this caller drained.
        /// </summary>
        public bool Run(Func<int> available, Func<int, byte[]?> read, Action<byte[]> deliver)
        {
            bool drained = false;
            while (true)
            {
                if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                {
                    if (!drained) Interlocked.Increment(ref _skipped);
                    return drained;
                }
                drained = true;
                try
                {
                    int n;
                    while ((n = available()) > 0)
                    {
                        var chunk = read(n);
                        if (chunk == null || chunk.Length == 0) break;
                        deliver(chunk);
                    }
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
                // A byte that arrived after the last check, whose own event
                // returned because this drain was still running.
                if (available() <= 0) return true;
            }
        }
    }
}
