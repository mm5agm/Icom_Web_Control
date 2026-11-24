using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Owns the CI-V serial port and turns it into a request/reply channel.
    /// Icom replacement for the Yaesu <c>CatMultiplexerService</c> — but far
    /// smaller in Phase 2: one transaction at a time, matched by command byte.
    ///
    /// Two CI-V realities this handles that the Yaesu code never had to:
    ///  * <b>Binary, not ASCII.</b> Bytes are read with <c>SerialPort.Read</c>,
    ///    never <c>ReadExisting</c> (which would decode the 0x00–0xFF payload as
    ///    text and corrupt it).
    ///  * <b>Bus echo.</b> Icom's CI-V (and its USB emulation of it) echoes the
    ///    controller's own transmission back before the radio replies. Echo
    ///    frames carry To == the radio's address; genuine replies carry
    ///    To == our controller address, so we simply drop the former.
    ///
    /// Serial parameters are the verified IC-7300 MkII values: 8 data bits, no
    /// parity, 1 stop bit (8N1). DTR and RTS are left de-asserted — on Icom's
    /// USB "Serial A" port those lines can be assigned to PTT/keying, and we
    /// must never risk keying the transmitter just by opening the port.
    /// </summary>
    public sealed class CivBusService : ICivClient, IDisposable
    {
        private readonly ILogger<CivBusService> _logger;
        private readonly CivFrameBuffer _buffer = new();
        private readonly SingleDrain _drain = new();
        private readonly object _portLock = new();
        private readonly CivBusGate _gate;

        private SerialPort? _port;
        private Pending? _pending;

        // The close of a port this bus has let go of, still running if a
        // driver is slow to give the handle back. See CloseDetachedAsync.
        private Task _closing = Task.CompletedTask;
        private string? _closingPortName;

        // An open still running because the driver hasn't answered. See OpenAsync.
        private Task _opening = Task.CompletedTask;
        private string? _openingPortName;

        /// <summary>
        /// How long opening the port may take before the bus gives up on it.
        /// A healthy open takes milliseconds. See <see cref="OpenAsync"/>.
        /// </summary>
        internal const int OpenTimeoutMs = 3000;

        /// <summary>
        /// How long a close may take before the bus stops waiting for it.
        /// See <see cref="CloseDetachedAsync"/>.
        /// </summary>
        internal const int CloseTimeoutMs = 2000;

        /// <summary>
        /// How long a write may take before the bus decides the driver has
        /// stopped taking data. The port's own WriteTimeout is 500 ms, so a
        /// driver that honours it throws long before this. See
        /// <see cref="WriteWithinAsync"/>.
        /// </summary>
        internal const int WriteStuckMs = 2000;
        // The last bytes received, so a connect that gets no answer can log what did come
        // back: nothing, only our echo, or something else (IWC #128).
        private const int RecentCapacity = 96;
        private readonly Queue<byte> _recent = new();

        public CivBusService(ILogger<CivBusService> logger)
        {
            _logger = logger;
            _gate = new CivBusGate(logger);
            _buffer.FrameReceived += OnFrameReceived;
        }

        public string RecentReceivedHex()
        {
            lock (_recent)
                return _recent.Count == 0 ? "nothing" : BitConverter.ToString(_recent.ToArray()).Replace('-', ' ');
        }

        public bool IsOpen
        {
            // No lock: this is asked by every transaction, from the poll loop,
            // every browser request and every rigctld client. Waiting on the
            // port lock here let one stuck serial call hold up all of them
            // (D#51, see WriteWithinAsync).
            get { return Volatile.Read(ref _port)?.IsOpen ?? false; }
        }

        public string? PortName
        {
            get { var port = Volatile.Read(ref _port); return port?.IsOpen == true ? port.PortName : null; }
        }

        // Only the CW Paddle's key line calls this, and only once the operator
        // has chosen this port as the key line (CwKeyingService). Reopening the
        // port starts with DTR low again.
        public void SetDtr(bool on)
        {
            // Not proven safe off Windows: the lines haven't been measured there yet.
            if (on && !OperatingSystem.IsWindows())
                throw new InvalidOperationException("Keying by DTR isn't available on this system yet.");
            lock (_portLock)
            {
                if (_port?.IsOpen != true) throw new InvalidOperationException("The CI-V port is not open.");
                _port.DtrEnable = on;
            }
        }

        public event EventHandler<CivFrame>? UnsolicitedFrame;

        public async Task<bool> OpenAsync(string portName, int baudRate)
        {
            // Already open on another port or speed: let that go first, outside
            // the lock (see CloseDetachedAsync).
            SerialPort? previous = null;
            lock (_portLock)
            {
                if (_port?.IsOpen == true)
                {
                    if (string.Equals(_port.PortName, portName, StringComparison.OrdinalIgnoreCase)
                        && _port.BaudRate == baudRate)
                    {
                        return true;
                    }
                    previous = Detach();
                }
            }
            if (previous != null)
                await CloseDetachedAsync(previous);

            // A close of this same port that hasn't finished still holds its
            // handle, so opening it now would only fail. Try again next time.
            if (!_closing.IsCompleted
                && string.Equals(_closingPortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("CI-V port {Port} is still closing; not reopening it yet", portName);
                return false;
            }

            // An open of this port that never came back still has the driver's
            // attention; don't pile another on top of it.
            if (!_opening.IsCompleted
                && string.Equals(_openingPortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("CI-V port {Port} is still opening from an earlier try; not trying again yet", portName);
                return false;
            }

            var available = SerialPort.GetPortNames();
            if (!RadioWebControl.Core.Services.SerialPortPaths.IsPresent(portName, available))
            {
                _logger.LogWarning("CI-V port {Port} not present. Available: {Ports}",
                    portName, string.Join(", ", available));
                return false;
            }

            var port = new SerialPort
            {
                PortName = portName,
                BaudRate = baudRate,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = false,
                RtsEnable = false,
            };

            // The open runs on its own thread, with no lock held and a time
            // limit (D#51). It used to run inside the port lock, which the
            // reader, SetDtr and every close also take, so a USB driver that
            // never answered the open would have held all of them, and a pool
            // thread, for good. An open that doesn't come back in time is left
            // to finish on its own thread, and the port is let go when it does.
            var opening = RunOnOwnThread(port.Open);
            _openingPortName = portName;
            _opening = opening;
            bool finished;
            try
            {
                finished = await FinishesWithinAsync(opening, OpenTimeoutMs);
                if (finished) await opening;   // rethrows a failed open
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open CI-V port {Port}", portName);
                CloseQuietly(port);
                return false;
            }
            if (!finished)
            {
                _logger.LogWarning(
                    "CI-V port {Port} didn't open within {Ms} ms: the USB serial driver isn't answering. Trying again later",
                    portName, OpenTimeoutMs);
                _ = opening.ContinueWith(t => { _ = t.Exception; CloseQuietly(port); }, TaskScheduler.Default);
                return false;
            }

            SerialPort? extra = null;
            lock (_portLock)
            {
                if (_port != null)
                {
                    extra = port;   // another open got there first: keep that one
                }
                else
                {
                    lock (_recent) _recent.Clear();
                    _buffer.Clear();
                    port.DataReceived += OnDataReceived;
                    _port = port;
                    _gate.Reset();
                }
            }
            if (extra != null)
            {
                await CloseDetachedAsync(extra);
                return IsOpen;
            }

            _logger.LogInformation("✓ CI-V port {Port} open at {Baud} 8N1", portName, baudRate);
            return true;
        }

        public async Task CloseAsync()
        {
            SerialPort? port;
            lock (_portLock)
            {
                port = Detach();
            }
            if (port != null)
                await CloseDetachedAsync(port);
        }

        /// <summary>
        /// Lets go of the port: from here on nothing reads from it, writes to it
        /// or waits on it. Called under <see cref="_portLock"/>; the port itself
        /// is closed afterwards, outside the lock, by
        /// <see cref="CloseDetachedAsync"/>.
        /// </summary>
        private SerialPort? Detach()
        {
            var port = _port;
            if (port == null) return null;
            port.DataReceived -= OnDataReceived;
            _port = null;
            _pending?.Tcs.TrySetResult(null);
            _pending = null;
            return port;
        }

        /// <summary>
        /// Closes a port the bus has already let go of, without ever holding up
        /// the caller for more than <see cref="CloseTimeoutMs"/>.
        ///
        /// <para><b>Why (IWC #128, D#51).</b> The close used to run inside
        /// <see cref="_portLock"/>, which the DataReceived handler also takes, and
        /// <c>SerialPort.Close</c> can wait on the serial event thread, or on a
        /// USB driver that has stopped answering. When the radio went quiet and
        /// the poll loop dropped the link, that close never came back. The loop
        /// stopped there: no reconnect attempt ever logged, 0% CPU, and every exit
        /// forced after 10 s. The close now runs with no lock held, on a worker,
        /// and the loop moves on after the timeout whether or not it has finished.
        /// A close still running keeps the port's handle, so
        /// <see cref="OpenAsync"/> won't try that port again until it ends.</para>
        /// </summary>
        private async Task CloseDetachedAsync(SerialPort port)
        {
            string name;
            try { name = port.PortName; } catch { name = "?"; }

            var closing = RunOnOwnThread(() => CloseQuietly(port));
            _closingPortName = name;
            _closing = closing;

            if (!await FinishesWithinAsync(closing, CloseTimeoutMs))
                _logger.LogWarning(
                    "CI-V port {Port} didn't close within {Ms} ms; carrying on and leaving it to finish in the background",
                    name, CloseTimeoutMs);
        }

        private void CloseQuietly(SerialPort port)
        {
            try
            {
                if (port.IsOpen)
                    port.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while closing CI-V port");
            }
            finally
            {
                try { port.Dispose(); } catch { /* already reported above */ }
            }
        }

        /// <summary>
        /// Writes to the port, giving up after <see cref="WriteStuckMs"/>. Returns
        /// whether the write finished; a write that fails throws as before.
        ///
        /// <para><b>Why (D#51).</b> The write used to run inside the port lock,
        /// which <see cref="IsOpen"/>, the reader and every transaction also
        /// took. A USB serial driver that stops taking data and ignores the
        /// port's WriteTimeout left that write, and the lock, held for good.
        /// Every browser request and rigctld command that touched the radio then
        /// waited on the lock with a thread each, until the thread pool ran dry:
        /// the page froze, the browsers timed out, and only pulling the USB
        /// cable (which makes the write fail) brought IWC back. Now the write
        /// holds no lock, and one that has not finished in time lets go of the
        /// port. The radio shows as disconnected and IWC keeps answering; the
        /// poll loop reconnects once the stuck port has let go of its handle.</para>
        /// </summary>
        private async Task<bool> WriteWithinAsync(SerialPort port, byte[] bytes)
        {
            if (await WriteFinishesWithinAsync(() => port.Write(bytes, 0, bytes.Length), WriteStuckMs))
                return true;

            string name;
            try { name = port.PortName; } catch { name = "?"; }
            _logger.LogWarning(
                "CI-V write to {Port} didn't finish within {Ms} ms: the USB serial driver has stopped taking data. Letting go of the port so IWC keeps answering; it reconnects when the port comes back",
                name, WriteStuckMs);

            SerialPort? stuck;
            lock (_portLock)
            {
                stuck = ReferenceEquals(_port, port) ? Detach() : null;
            }
            if (stuck != null)
                _ = CloseDetachedAsync(stuck);
            return false;
        }

        /// <summary>
        /// Runs <paramref name="write"/> on a worker and waits at most
        /// <paramref name="timeoutMs"/> for it. True if it finished; an exception
        /// it threw comes back out. A write still running after the timeout is
        /// left to finish (or fail) on its own, its error observed.
        /// </summary>
        internal static async Task<bool> WriteFinishesWithinAsync(Action write, int timeoutMs)
        {
            var work = Task.Run(write);
            if (await FinishesWithinAsync(work, timeoutMs))
            {
                await work; // rethrows a failed write to the caller
                return true;
            }
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return false;
        }

        /// <summary>
        /// Runs a serial call that a stuck USB driver can hold forever (open,
        /// close) on a thread of its own, not the thread pool's. A call that
        /// never comes back then costs one idle thread, never a pool thread
        /// that the web pages, rigctld and the poll loop need (D#51).
        /// </summary>
        internal static Task RunOnOwnThread(Action call)
            => Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        /// <summary>Whether <paramref name="work"/> ends within <paramref name="timeoutMs"/>.</summary>
        internal static async Task<bool> FinishesWithinAsync(Task work, int timeoutMs)
        {
            var winner = await Task.WhenAny(work, Task.Delay(timeoutMs));
            return winner == work;
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                // Only the current port: one already let go of may still raise
                // an event or two while it closes. Read with no lock held, so a
                // write the driver is slow over never holds up the reader (and
                // a port closed under us just throws, caught below).
                var port = Volatile.Read(ref _port);
                if (!ReferenceEquals(sender, port) || port?.IsOpen != true) return;
                // One handler drains at a time; the others return at once
                // (see SingleDrain, D#51).
                _drain.Run(
                    () => port.IsOpen ? port.BytesToRead : 0,
                    n =>
                    {
                        byte[] chunk = new byte[n];
                        int read = port.Read(chunk, 0, n);
                        return read <= 0 ? null : read < n ? chunk[..read] : chunk;
                    },
                    chunk =>
                    {
                        lock (_recent)
                        {
                            foreach (var b in chunk)
                            {
                                _recent.Enqueue(b);
                                if (_recent.Count > RecentCapacity) _recent.Dequeue();
                            }
                        }
                        _buffer.Append(chunk);
                    });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading CI-V bytes");
            }
        }

        private void OnFrameReceived(object? sender, CivFrame frame)
        {
            // Drop our own bus echo. Echoes come in two shapes and the To check
            // below only catches one of them:
            //
            //  * Ordinary commands are addressed TO the radio, so their echo has
            //    To == the radio address and is rejected below.
            //  * The identify query (19 00) is deliberately addressed to the
            //    BROADCAST address so a classic IC-7300 at 94 answers as well as
            //    a MkII at B6 — see CivRadioController.IdentifyAsync. Its echo
            //    therefore carries To == 00, which the check below has to let
            //    through, because a radio answering the broadcast is the whole
            //    point of sending it.
            //
            // So the identify echo has to be caught by From instead. No genuine
            // radio frame is FROM the controller address; only our own bytes
            // coming back at us are. Left unfiltered it satisfies the pending
            // 19-command transaction before the radio can reply, IdentifyAsync
            // reads From off it, and the radio address becomes E0 — after which
            // every command is addressed to ourselves and nothing ever answers.
            // The symptom is "port opened, but the radio isn't responding",
            // reported against radios that were working fine (issues #2, #5).
            //
            // Only bites when the radio echoes on the USB path — MENU > SET >
            // Connectors > CI-V > "CI-V USB (A) Echo Back", which defaults to
            // OFF. Reproduced on an IC-7300MK2 by switching it ON.
            if (!IsForController(frame))
                return;

            var pending = _pending;
            if (pending != null && (frame.Cmd == pending.ExpectedCmd || frame.Cmd == CivProtocol.AckNg))
            {
                pending.Tcs.TrySetResult(frame);
                return;
            }

            UnsolicitedFrame?.Invoke(this, frame);
        }

        /// <summary>
        /// Whether a frame off the wire is for us: not our own echo (From is never
        /// the controller address on a genuine radio frame) and addressed to the
        /// controller or to broadcast. See <see cref="OnFrameReceived"/> for why
        /// both checks are needed.
        /// </summary>
        internal static bool IsForController(CivFrame frame)
            => frame.From != CivProtocol.ControllerAddress
               && (frame.To == CivProtocol.ControllerAddress || frame.To == CivProtocol.BroadcastAddress);

        public async Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
        {
            if (!IsOpen) return null;

            // The poll loop waits for the bus; the browser and rigctld give up
            // after 1.5 s, or straight away while the radio isn't answering (D#51).
            if (!await _gate.EnterAsync(mayBeSkipped: true, cancellationToken)) return null;
            try
            {
                var tcs = new TaskCompletionSource<CivFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = new Pending { ExpectedCmd = expectedCmd, Tcs = tcs };

                var port = Volatile.Read(ref _port);
                if (port?.IsOpen != true) return null;
                if (!await WriteWithinAsync(port, frame)) return null;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, timeoutCts.Token));
                timeoutCts.Cancel(); // stop the delay task if the reply won

                if (completed == tcs.Task)
                {
                    _gate.Answered();
                    return await tcs.Task;
                }

                if (!cancellationToken.IsCancellationRequested) _gate.TimedOut();
                return null; // timed out
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                // The serial device can be pulled out from under us — powering
                // the radio off drops the USB CDC port, so a write/read here
                // throws IOException / InvalidOperationException / Unauthorized
                // AccessException. Treat any such failure as a transaction miss
                // (null), never let it escape: an unhandled throw from the poll
                // loop's BackgroundService would stop the whole host.
                _logger.LogDebug(ex, "CI-V transaction failed (port error?)");
                return null;
            }
            finally
            {
                _pending = null;
                _gate.Release();
            }
        }

        public async Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
        {
            if (!IsOpen) return;

            // Never skipped: the power-on wake-up is for a radio that isn't answering.
            if (!await _gate.EnterAsync(mayBeSkipped: false, cancellationToken)) return;
            try
            {
                var port = Volatile.Read(ref _port);
                if (port?.IsOpen != true) return;
                await WriteWithinAsync(port, bytes);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error writing raw CI-V bytes");
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            SerialPort? port;
            lock (_portLock)
            {
                port = Detach();
            }
            if (port != null)
                CloseDetachedAsync(port).GetAwaiter().GetResult();
            _gate.Dispose();
        }

        private sealed class Pending
        {
            public byte ExpectedCmd { get; init; }
            public TaskCompletionSource<CivFrame?> Tcs { get; init; } = null!;
        }
    }
}
