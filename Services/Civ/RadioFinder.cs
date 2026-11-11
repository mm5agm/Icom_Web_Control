using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// One probe of the PC's serial ports for an Icom answering CI-V — the
    /// "Find my radio" button on Settings → Radio &amp; CAT (issue #43, where a
    /// first-time user had the wrong COM port, the wrong baud item on the
    /// radio, and no way to tell which of the two was the problem).
    ///
    /// Each port present is opened in turn, 8N1 with DTR and RTS de-asserted
    /// (the same rules as <see cref="CivBusService"/> — on Icom's USB port those
    /// lines can be PTT), and sent the transceiver-ID request on the CI-V
    /// broadcast address and at each known model's address, plus a frequency
    /// read at each (see <see cref="ProbeBurst"/>), so a classic IC-7300 at 94
    /// answers as readily as a MkII at B6, even one that ignores the broadcast.
    /// An ID reply wins; a frequency reply alone counts if no ID reply comes. Bauds are tried in the order they
    /// are likely to be right: 19200 (IWC's default, and what an original
    /// IC-7300 on "Auto" follows), then 115200 (what the original needs for
    /// its scope), then the rest of the radio's menu.
    ///
    /// This is IWC-local because it is CI-V from end to end. It is not on the
    /// <see cref="IRadioController"/> seam because it is not about *the*
    /// radio — it runs before there is one.
    /// </summary>
    public sealed class RadioFinder
    {
        /// <summary>Bauds probed, in order. Every rate the IC-7300's CI-V USB menu offers.</summary>
        public static readonly int[] BaudOrder = { 19200, 115200, 9600, 38400, 57600, 4800 };

        // Long enough for the radio's reply at 4800 baud (a 19 00 reply is 8
        // bytes ≈ 17 ms) plus the USB latency the MkII shows on the bench;
        // short enough that the first pass over five ports is under two seconds
        // and the full six-baud sweep of them stays inside ten.
        private const int ReplyTimeoutMs = 350;

        // Per question in the slow pass. A radio at 4800 baud replies inside 60 ms.
        private const int PacedReplyTimeoutMs = 150;

        private readonly ILogger<RadioFinder> _logger;

        public RadioFinder(ILogger<RadioFinder> logger)
        {
            _logger = logger;
        }

        public sealed record Result(
            bool Found,
            string? Port,
            int Baud,
            string? Model,
            byte Address,
            IReadOnlyList<string> PortsProbed,
            IReadOnlyList<string> PortsBusy);

        /// <summary>
        /// Probe every port the PC has right now. <paramref name="skipPort"/> is
        /// a port some other part of the app currently holds open (the live
        /// CI-V bus) and is reported as busy rather than probed.
        /// </summary>
        public async Task<Result> FindAsync(string? skipPort, CancellationToken ct)
        {
            // This opens every port it can see. Off Windows that isn't proven safe
            // yet: opening a port may raise DTR and RTS, and the list includes the
            // console port and anything else plugged in. The port is chosen by hand.
            if (!OperatingSystem.IsWindows())
                return new Result(false, null, 0, null, 0, Array.Empty<string>(), Array.Empty<string>());

            var ports = SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(PortNumber)
                .ToList();
            var busy = new List<string>();
            var probed = new List<string>();

            // Baud-major: every port at 19200 before any port at 115200. Most
            // radios answer on the first pass, so the usual case takes one
            // reply timeout per port rather than six.
            foreach (var baud in BaudOrder)
            {
                foreach (var port in ports)
                {
                    ct.ThrowIfCancellationRequested();
                    if (busy.Contains(port, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (skipPort != null && string.Equals(port, skipPort, StringComparison.OrdinalIgnoreCase))
                    {
                        busy.Add(port);
                        continue;
                    }

                    var (state, from, id) = await ProbeAsync(port, baud, ct);
                    if (state == ProbeState.Busy)
                    {
                        // A port we cannot open is not evidence either way;
                        // say so rather than silently reporting "not found".
                        busy.Add(port);
                        probed.Remove(port);
                        continue;
                    }
                    if (!probed.Contains(port))
                        probed.Add(port);
                    if (state == ProbeState.Answered)
                    {
                        var model = MapModel(id);
                        _logger.LogInformation("[RadioFinder] {Model} answered on {Port} at {Baud} (CI-V address {Addr:X2})",
                            model, port, baud, from);
                        return new Result(true, port, baud, model, from, probed, busy);
                    }
                }
            }

            _logger.LogInformation("[RadioFinder] No radio answered. Probed: {Probed}; busy: {Busy}",
                probed.Count > 0 ? string.Join(", ", probed) : "none",
                busy.Count > 0 ? string.Join(", ", busy) : "none");
            return new Result(false, null, 0, null, 0, probed, busy);
        }

        private enum ProbeState { Silent, Answered, Busy }

        private async Task<(ProbeState state, byte from, byte id)> ProbeAsync(string port, int baud, CancellationToken ct)
        {
            var buffer = new CivFrameBuffer();
            var tcs = new TaskCompletionSource<CivFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            CivFrame? frequencyReply = null;
            var received = new List<byte>();
            buffer.FrameReceived += (_, frame) =>
            {
                // The same two echo rules as CivBusService.OnFrameReceived: our
                // own bytes come back FROM the controller address, and the
                // broadcast query's echo carries To == 00, so a reply is
                // anything not from us whose command byte is one we asked for.
                if (frame.From == CivProtocol.ControllerAddress) return;
                if (frame.To != CivProtocol.ControllerAddress && frame.To != CivProtocol.BroadcastAddress) return;
                if (frame.Cmd == CivProtocol.CmdReadId)
                    tcs.TrySetResult(frame);
                else if (frame.Cmd == CivProtocol.CmdReadFrequency)
                    Interlocked.CompareExchange(ref frequencyReply, frame, null);
            };

            SerialPort? sp = null;
            try
            {
                sp = new SerialPort
                {
                    PortName = port,
                    BaudRate = baud,
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    Handshake = Handshake.None,
                    ReadTimeout = ReplyTimeoutMs,
                    WriteTimeout = ReplyTimeoutMs,
                    DtrEnable = false,
                    RtsEnable = false,
                };
                sp.DataReceived += (_, _) =>
                {
                    try
                    {
                        int n = sp.BytesToRead;
                        if (n <= 0) return;
                        var chunk = new byte[n];
                        int read = sp.Read(chunk, 0, n);
                        lock (received) received.AddRange(chunk.AsSpan(0, read).ToArray());
                        buffer.Append(chunk.AsSpan(0, read));
                    }
                    catch
                    {
                        // Port going away mid-probe; the timeout reports it.
                    }
                };
                sp.Open();
                sp.DiscardInBuffer();

                var burst = ProbeBurst();
                sp.Write(burst, 0, burst.Length);

                // The time the burst takes on the wire, so 4800 baud gets the same
                // listening time as 19200.
                int sendMs = burst.Length * 10 * 1000 / baud;
                await Task.WhenAny(tcs.Task, Task.Delay(ReplyTimeoutMs + sendMs, ct));
                var answer = Answer();
                if (answer != null) return Log(answer.Value, "answered");

                // Bytes came back but nothing answered: something is on this port,
                // perhaps a radio echoing our frames (CI-V USB Echo Back on), as Paul
                // VK2AHB's IC-7300 does (IWC #128). With echo on, an IC-7300MK2 drops
                // whatever arrives while it is sending a reply, so ask again, one
                // question at a time, waiting for each. A silent port skips this.
                int before;
                lock (received) before = received.Count;
                if (before == 0) return Log((ProbeState.Silent, 0, 0), "nothing came back");
                foreach (var frame in ProbeFrames())
                {
                    sp.Write(frame, 0, frame.Length);
                    var deadline = Environment.TickCount64 + PacedReplyTimeoutMs + frame.Length * 10 * 1000 / baud;
                    while (Environment.TickCount64 < deadline && !tcs.Task.IsCompleted && Volatile.Read(ref frequencyReply) == null)
                        await Task.Delay(10, ct);
                    answer = Answer();
                    if (answer != null) return Log(answer.Value, "answered one question at a time");
                }
                return Log((ProbeState.Silent, 0, 0), "no answer, also one question at a time");

                (ProbeState, byte, byte)? Answer()
                {
                    if (tcs.Task.IsCompleted)
                    {
                        var f = tcs.Task.Result;
                        byte id = f.Data.Length >= 2 ? f.Data[^1] : f.From;
                        return (ProbeState.Answered, f.From, id);
                    }
                    // No ID reply, but a frequency read was answered: a radio that
                    // won't identify itself. Its address is its model.
                    var freq = Volatile.Read(ref frequencyReply);
                    return freq != null ? (ProbeState.Answered, freq.From, freq.From) : null;
                }

                // One line per port and baud, with what came back, so a log from a
                // radio that isn't found shows whether it echoed, answered, or said nothing.
                (ProbeState, byte, byte) Log((ProbeState, byte, byte) result, string outcome)
                {
                    byte[] bytes;
                    lock (received) bytes = received.ToArray();
                    string hex = bytes.Length == 0 ? "" : ": " + BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace('-', ' ')
                                                          + (bytes.Length > 64 ? " …" : "");
                    _logger.LogInformation("[RadioFinder] {Port} at {Baud}: {Outcome}; {Count} bytes back{Hex}",
                        port, baud, outcome, bytes.Length, hex);
                    return result;
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogDebug("[RadioFinder] {Port} is in use by another program", port);
                return (ProbeState.Busy, 0, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A port that vanished between the listing and the open, a
                // driver that rejects the baud, a USB hiccup: none of them is a
                // radio, so treat it as silence and move on.
                _logger.LogDebug(ex, "[RadioFinder] {Port} at {Baud}: probe failed", port, baud);
                return (ProbeState.Silent, 0, 0);
            }
            finally
            {
                try { sp?.Close(); } catch { }
                sp?.Dispose();
            }
        }

        /// <summary>
        /// What each port is sent, in one write: the ID request on the broadcast
        /// address, then the ID request and a frequency read at every known model's
        /// address. An original IC-7300 has been seen ignoring the broadcast one but
        /// answering at 94 (IWC #128). A radio ignores frames for other addresses,
        /// and all of these only read. Sent together so a silent port still costs a
        /// single wait.
        /// </summary>
        internal static byte[] ProbeBurst() => ProbeFrames().SelectMany(f => f).ToArray();

        /// <summary>The questions in <see cref="ProbeBurst"/>, one frame each, in order.</summary>
        internal static IEnumerable<byte[]> ProbeFrames()
        {
            yield return CivProtocol.BuildFrame(CivProtocol.BroadcastAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadId, CivProtocol.SubReadId);
            var addresses = RadioProfile.AddressesToTry(null);
            foreach (var address in addresses)
                yield return CivProtocol.BuildFrame(address, CivProtocol.ControllerAddress,
                    CivProtocol.CmdReadId, CivProtocol.SubReadId);
            foreach (var address in addresses)
                yield return CivProtocol.BuildFrame(address, CivProtocol.ControllerAddress,
                    CivProtocol.CmdReadFrequency);
        }

        /// <summary>Same mapping as CivRadioController.MapModel, on the settings-page value.</summary>
        public static string MapModel(byte idByte) => RadioProfile.ModelNameForId(idByte);

        private static int PortNumber(string name)
            => int.TryParse(name.AsSpan(3), out var n) ? n : int.MaxValue;
    }
}
