namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Reads the CI-V port on a thread of its own (D#51).
    ///
    /// <para><b>Why.</b> IWC used to read in SerialPort.DataReceived. .NET
    /// raises that event by queueing a thread-pool job for each burst of bytes,
    /// and every job waits on one lock inside the port before it runs the
    /// handler. While one handler was reading and passing scope frames on, each
    /// new burst parked another pool thread on that lock. On Paul VK2AHB's busy
    /// laptop this grew to 1,282 blocked pool threads and 40,000 waiting jobs,
    /// with IWC using no CPU: the poll loop's work sat in that queue, the Radio
    /// and Diagnostics pages froze, and only switching the radio off (no more
    /// bytes, so no more jobs) let it catch up. Reading here instead puts
    /// nothing on the pool for incoming bytes.</para>
    ///
    /// <para><b>How.</b> <c>read</c> is SerialPort.Read with a ReadTimeout: it
    /// returns as soon as any bytes are there, or throws TimeoutException when
    /// none came in time, which just means read again. The loop ends when the
    /// port is no longer the bus's port, or when a read fails (port closed or
    /// unplugged); the poll loop notices the silence and reconnects.</para>
    /// </summary>
    internal static class CivPortReader
    {
        public const int BufferSize = 4096;

        /// <summary>
        /// Reads until <paramref name="current"/> says stop or a read fails,
        /// handing each chunk to <paramref name="deliver"/> in order. A fault in
        /// <paramref name="deliver"/> is reported and reading carries on.
        /// </summary>
        public static void Run(
            Func<byte[], int, int, int> read,
            Func<bool> current,
            Action<byte[]> deliver,
            Action<Exception> deliverFailed,
            Action<Exception> readFailed)
        {
            var buf = new byte[BufferSize];
            while (current())
            {
                int n;
                try
                {
                    n = read(buf, 0, buf.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (current()) readFailed(ex);
                    return;
                }
                if (n <= 0 || !current()) continue;
                try
                {
                    deliver(buf[..n]);
                }
                catch (Exception ex)
                {
                    deliverFailed(ex);
                }
            }
        }
    }
}
