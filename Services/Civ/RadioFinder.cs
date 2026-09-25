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
    /// broadcast address, so a classic IC-7300 at 94 answers as readily as a
    /// MkII at B6. The first reply wins. Bauds are tried in the order they
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
            buffer.FrameReceived += (_, frame) =>
            {
                // The same two echo rules as CivBusService.OnFrameReceived: our
                // own bytes come back FROM the controller address, and the
                // broadcast query's echo carries To == 00, so a reply is
                // anything not from us whose command byte is the ID we asked for.
                if (frame.From == CivProtocol.ControllerAddress) return;
                if (frame.To != CivProtocol.ControllerAddress && frame.To != CivProtocol.BroadcastAddress) return;
                if (frame.Cmd != CivProtocol.CmdReadId) return;
                tcs.TrySetResult(frame);
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
                        buffer.Append(chunk.AsSpan(0, read));
                    }
                    catch
                    {
                        // Port going away mid-probe; the timeout reports it.
                    }
                };
                sp.Open();
                sp.DiscardInBuffer();

                var frame = CivProtocol.BuildFrame(CivProtocol.BroadcastAddress, CivProtocol.ControllerAddress,
                    CivProtocol.CmdReadId, CivProtocol.SubReadId);
                sp.Write(frame, 0, frame.Length);

                var reply = await Task.WhenAny(tcs.Task, Task.Delay(ReplyTimeoutMs, ct));
                if (reply == tcs.Task)
                {
                    var f = tcs.Task.Result;
                    byte id = f.Data.Length >= 2 ? f.Data[^1] : f.From;
                    return (ProbeState.Answered, f.From, id);
                }
                return (ProbeState.Silent, 0, 0);
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

        /// <summary>Same mapping as CivRadioController.MapModel, on the settings-page value.</summary>
        public static string MapModel(byte idByte) => idByte switch
        {
            0xB6 => "IC-7300MK2",
            0x94 => "IC-7300",
            _ => $"Icom({idByte:X2})",
        };

        private static int PortNumber(string name)
            => int.TryParse(name.AsSpan(3), out var n) ? n : int.MaxValue;
    }
}
