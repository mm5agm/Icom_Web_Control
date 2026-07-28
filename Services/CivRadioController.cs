using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Icom_Web_Control.Hubs;
using Icom_Web_Control.Models;
using Icom_Web_Control.Services.Civ;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Phase 2 — the first real radio link. This is the single class below the
    /// <see cref="IRadioController"/> seam that emits CI-V, replacing the Phase 1
    /// <see cref="StubRadioController"/>. It proves the whole spine end-to-end:
    /// real IC-7300 MkII → CI-V transport → <see cref="RadioStateService"/> →
    /// SignalR → the web page's VFO-A display.
    ///
    /// Scope is deliberately narrow (see docs/design/iwc-clone-split-plan.md,
    /// Phase 2): connect, learn the radio's address/ID (command 19 00, never
    /// hard-coded), and poll operating frequency (command 03). Mode, S-meter,
    /// PTT and the rest arrive one command per commit in Phase 3. The unimplemented
    /// seam members return safe values so nothing above the seam throws.
    ///
    /// Like the stub, it does NOT set <see cref="RadioStateService.IsInitialized"/>:
    /// broadcasting live values to the UI is the goal, but persisting a 4 Hz
    /// frequency stream to radio_state.json is needless disk churn this early.
    /// Persistence is turned on in a later phase alongside the full read-back.
    /// </summary>
    public sealed class CivRadioController : BackgroundService, IRadioController
    {
        private const int PollIntervalMs = 150;      // ~6–7 Hz — snappy meters/dial
        private const int ModePollEveryNLoops = 3;    // mode changes rarely; ~2 Hz is plenty
        private const int SplitPollEveryNLoops = 4;    // split rarely toggles; ~1.5 Hz is plenty
        private const int ReconnectDelayMs = 3000;
        private const int MaxConsecutiveReadMisses = 5;

        private readonly RadioStateService _state;
        private readonly ICivClient _bus;
        private readonly ISettingsService _settings;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<CivRadioController> _logger;

        // The radio's CI-V address. Starts at the IC-7300 MkII default and is
        // replaced with whatever actually answers (reply From byte) on connect.
        private byte _radioAddress = CivProtocol.DefaultRadioAddress;

        // Spectrum scope (block 6): reassembles the unsolicited 27 00 waveform
        // stream and counts broadcasts so we can re-assert "streaming" as a
        // heartbeat for clients that load after the stream is already flowing.
        private readonly CivScopeAssembler _scope = new();
        private int _scopeBroadcasts;

        public CivRadioController(
            RadioStateService state,
            ICivClient bus,
            ISettingsService settings,
            IHubContext<RadioHub> hubContext,
            ILogger<CivRadioController> logger)
        {
            _state = state;
            _bus = bus;
            _settings = settings;
            _hubContext = hubContext;
            _logger = logger;

            // The radio pushes 27 00 scope frames unsolicited (they never match a
            // pending transaction), so they arrive here rather than through the
            // request/reply path.
            _bus.UnsolicitedFrame += OnUnsolicitedFrame;
        }

        public bool IsConnected => _bus.IsOpen;
        public string? ModelId { get; private set; }

        // -- IRadioController ---------------------------------------------------

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var settings = await _settings.GetSettingsAsync();
            var port = settings.SerialPort;
            var baud = settings.BaudRate;

            _logger.LogInformation("[CivRadioController] Connecting to {Port} @ {Baud} 8N1…", port, baud);
            if (!await _bus.OpenAsync(port, baud))
                return false;

            // A port that opens is NOT proof the radio is usable: over USB the
            // COM port can stay enumerated while the radio is off, and the
            // IC-7300's CI-V circuit still answers 19 00 in soft-off/standby
            // (display dark). So neither "port open" nor "ID answered" means the
            // radio is operational. Gate the connection on the operating-frequency
            // read (03) — the same liveness signal the poll loop uses to hold the
            // link — so a powered-off radio reads as disconnected (steady red)
            // instead of flashing the power button green↔red as each reconnect
            // flags "connected" only to drop on the very next read miss.
            await IdentifyAsync(cancellationToken); // learn address/model if it answers; best-effort
            if (await ReadOperatingFrequencyAsync(cancellationToken) <= 0)
            {
                await _bus.CloseAsync();
                return false;
            }

            // Seed the RX controls so every slider/dropdown shows the radio's
            // real position on first paint rather than a default. All are
            // receiver-wide, so both panels mirror the one value. Best-effort —
            // a miss just leaves the existing default.
            await SeedRxControlsAsync(cancellationToken);

            // Turn on the CI-V spectrum scope and its waveform-to-controller
            // stream so the radio starts pushing 27 00 frames we reassemble into
            // the web spectrum. Best-effort — a miss just means no scope trace.
            await EnableScopeAsync(cancellationToken);

            return true;
        }

        // -- Spectrum scope (Phase 3 block 6) ----------------------------------

        /// <summary>
        /// Enable the spectrum scope in Center mode and switch on the waveform
        /// output to the controller. Center mode keeps the sweep centred on the
        /// operating frequency, which is the assumption the web SpectrumPanel's
        /// axis is built on. Sent once per connect; best-effort.
        /// </summary>
        private async Task EnableScopeAsync(CancellationToken ct)
        {
            await SendScopeSetAsync(CivProtocol.SubScopeMode, CivProtocol.ScopeModeCenter, "scope center mode", ct);
            await SendScopeSetAsync(CivProtocol.SubScopeOnOff, 0x01, "scope on", ct);
            await SendScopeSetAsync(CivProtocol.SubScopeOutput, 0x01, "scope waveform output", ct);
        }

        /// <summary>Send a 27-family scope set (27 &lt;sub&gt; &lt;val&gt;) and expect an ack.</summary>
        private async Task SendScopeSetAsync(byte sub, byte val, string what, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdScope, sub, val);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] {What} was not acknowledged", what);
        }

        /// <summary>
        /// Set the scope span (Center mode). <paramref name="spanHz"/> is the radio
        /// SPAN ± half-width in Hz (2500 … 500000) sent as 27 15 &lt;5-byte BCD&gt;.
        /// The waveform-info span field reads back the same value, which the
        /// assembler doubles for the displayed full width.
        /// </summary>
        public async Task SetScopeSpanAsync(int spanHz, CancellationToken cancellationToken = default)
        {
            // The radio stores the span field as the sent value ÷ 100 (verified
            // on the IC-7300 MkII: sending 250000 yields span field 2500). So to
            // set span field F (the ± half-width in Hz the waveform reports), send
            // F × 100 as the 5-byte BCD payload. Values that don't map to a valid
            // span field are NG-rejected by the radio.
            var body = new byte[2 + 5];
            body[0] = CivProtocol.CmdScope;
            body[1] = CivProtocol.SubScopeSpan;
            var bcd = CivProtocol.EncodeBcd((long)spanHz * 100, 5);
            Array.Copy(bcd, 0, body, 2, 5);
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] scope span ±{SpanHz} Hz was not acknowledged", spanHz);
        }

        /// <summary>
        /// Feed unsolicited frames to the scope assembler. Runs on the serial
        /// reader thread; the assembler is single-threaded by that contract, and
        /// the SignalR broadcast is fire-and-forget so we never block the reader.
        /// </summary>
        private void OnUnsolicitedFrame(object? sender, CivFrame frame)
        {
            if (frame.Cmd != CivProtocol.CmdScope)
                return;
            var sweep = _scope.Add(frame);
            if (sweep == null)
                return;

            // Re-assert "streaming" on the first sweep and every ~30 thereafter
            // so a client that loads mid-stream un-hides its spectrum panel.
            if (_scopeBroadcasts++ % 30 == 0)
                SendHub("SdrStatus", new { sdrId = "A", status = "streaming" });

            SendHub("SpectrumUpdate", new
            {
                sdrId = "A",
                bins = sweep.BinsDb,
                centreHz = sweep.CentreHz,
                spanHz = sweep.SpanHz,
            });
        }

        private void SendHub(string property, object value)
            => _ = _hubContext.Clients.All.SendAsync("RadioStateUpdate", new { property, value });

        /// <summary>
        /// One-shot read of the receiver-wide RX controls at connect, mirrored
        /// into both A/B state fields. Best-effort; a missed read leaves the
        /// existing value. The slow poll keeps AF gain live thereafter.
        /// </summary>
        private async Task SeedRxControlsAsync(CancellationToken ct)
        {
            var af = await GetAfGainAsync(ct);        if (af >= 0)  { _state.AfGainA = af;   _state.AfGainB = af; }
            var rf = await GetRfGainAsync(ct);        if (rf >= 0)  { _state.RfGainA = rf;   _state.RfGainB = rf; }
            var sq = await GetSquelchAsync(ct);       if (sq >= 0)  { _state.SquelchA = sq;  _state.SquelchB = sq; }
            var nrl = await GetNrLevelAsync(ct);      if (nrl >= 0) { _state.NrLevelA = nrl; _state.NrLevelB = nrl; }
            var nbl = await GetNbLevelAsync(ct);      if (nbl >= 0) { _state.NbLevelA = nbl; _state.NbLevelB = nbl; }
            var np = await GetNotchPositionAsync(ct); if (np >= 0)  { _state.ManualNotchFreqA = np; _state.ManualNotchFreqB = np; }
            var pa = await GetPreampAsync(ct);        if (pa >= 0)  { _state.IpoA = pa.ToString(); _state.IpoB = pa.ToString(); }
            var agc = await GetAgcAsync(ct);          if (agc >= 0) { _state.AgcA = agc.ToString(); _state.AgcB = agc.ToString(); }
            _state.NbA = await GetNoiseBlankerAsync(ct)   ? "1" : "0"; _state.NbB = _state.NbA;
            _state.NrA = await GetNoiseReductionAsync(ct) ? "1" : "0"; _state.NrB = _state.NrA;
            _state.AutoNotchA = await GetAutoNotchAsync(ct)   ? "1" : "0"; _state.AutoNotchB = _state.AutoNotchA;
            _state.ManualNotchA = await GetManualNotchAsync(ct) ? "1" : "0"; _state.ManualNotchB = _state.ManualNotchA;
            var mnw = await GetManualNotchWidthAsync(ct); if (mnw >= 0) { _state.ManualNotchWidthA = mnw.ToString(); _state.ManualNotchWidthB = _state.ManualNotchWidthA; }
            var ifs = await GetIfFilterShapeAsync(ct);    if (ifs >= 0) { _state.IfShapeA = ifs.ToString(); _state.IfShapeB = _state.IfShapeA; }
            _state.AttA = await GetAttenuatorAsync(ct) ? "20" : "00"; _state.AttB = _state.AttA;
        }

        // Rotating RX-control poll: mirror front-panel changes to AGC, preamp,
        // NB, NR, notch, attenuator and the levels back to the app. These move
        // rarely, so we read exactly one per poll loop and cycle through the
        // set — each control refreshes roughly every couple of seconds without
        // crowding out the S-meter / dial. AF gain has its own faster phase.
        //
        // Every branch reads the raw int (or command-11 byte) and only writes
        // state when the read succeeded (>= 0): a bus miss must never blink a
        // control to OFF. The state setters broadcast only on change, so a
        // steady radio produces no SignalR traffic here.
        private int _rxPollIndex;
        private const int RxControlCount = 15;
        private const int RxControlsPerLoop = 2;   // ~1.2 s to sweep all 15

        private async Task PollNextRxControlAsync(CancellationToken ct)
        {
            switch (_rxPollIndex % RxControlCount)
            {
                case 0:  { int v = await ReadFunc16Async(CivProtocol.SubAgc, ct);    if (v >= 0) { _state.AgcA = v.ToString(); _state.AgcB = _state.AgcA; } break; }
                case 1:  { int v = await ReadFunc16Async(CivProtocol.SubPreamp, ct); if (v >= 0) { _state.IpoA = v.ToString(); _state.IpoB = _state.IpoA; } break; }
                case 2:  { int v = await ReadFunc16Async(CivProtocol.SubNoiseBlanker, ct);   if (v >= 0) { _state.NbA = v == 1 ? "1" : "0"; _state.NbB = _state.NbA; } break; }
                case 3:  { int v = await ReadFunc16Async(CivProtocol.SubNoiseReduction, ct); if (v >= 0) { _state.NrA = v == 1 ? "1" : "0"; _state.NrB = _state.NrA; } break; }
                case 4:  { int v = await ReadFunc16Async(CivProtocol.SubAutoNotch, ct);   if (v >= 0) { _state.AutoNotchA = v == 1 ? "1" : "0"; _state.AutoNotchB = _state.AutoNotchA; } break; }
                case 5:  { int v = await ReadFunc16Async(CivProtocol.SubManualNotch, ct); if (v >= 0) { _state.ManualNotchA = v == 1 ? "1" : "0"; _state.ManualNotchB = _state.ManualNotchA; } break; }
                case 6:  { int v = await ReadAttenuatorRawAsync(ct); if (v >= 0) { _state.AttA = v == CivProtocol.AttOn20dB ? "20" : "00"; _state.AttB = _state.AttA; } break; }
                case 7:  { int v = await GetRfGainAsync(ct);        if (v >= 0) { _state.RfGainA = v;   _state.RfGainB = v; } break; }
                case 8:  { int v = await GetSquelchAsync(ct);       if (v >= 0) { _state.SquelchA = v;  _state.SquelchB = v; } break; }
                case 9:  { int v = await GetNrLevelAsync(ct);       if (v >= 0) { _state.NrLevelA = v;  _state.NrLevelB = v; } break; }
                case 10: { int v = await GetNbLevelAsync(ct);       if (v >= 0) { _state.NbLevelA = v;  _state.NbLevelB = v; } break; }
                case 11: { int v = await GetNotchPositionAsync(ct); if (v >= 0) { _state.ManualNotchFreqA = v; _state.ManualNotchFreqB = v; } break; }
                case 12: { int v = await ReadFunc16Async(CivProtocol.SubManualNotchWidth, ct); if (v >= 0) { _state.ManualNotchWidthA = v.ToString(); _state.ManualNotchWidthB = _state.ManualNotchWidthA; } break; }
                case 13: { int v = await ReadFunc16Async(CivProtocol.SubIfFilterShape, ct);    if (v >= 0) { _state.IfShapeA = v.ToString(); _state.IfShapeB = _state.IfShapeA; } break; }
                case 14: { int v = await GetTunerAsync(ct); if (v >= 0) { _state.AtuEnabled = v != CivProtocol.TunerOff; _state.AtuTuning = v == CivProtocol.TunerTune; } break; }
            }
            _rxPollIndex++;
        }

        /// <summary>Read the attenuator (command 11) as its raw byte. -1 on a miss; 0x20 = 20 dB.</summary>
        private async Task<int> ReadAttenuatorRawAsync(CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdAttenuator);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdAttenuator, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.CmdAttenuator || reply.Data.Length < 1)
                return -1;
            return reply.Data[0];
        }

        public Task DisconnectAsync() => _bus.CloseAsync();

        // Per-VFO addressing (block 5): the operating (active) VFO keeps the
        // hardware-proven operating commands 03/05 — frequency is also the poll
        // loop's liveness signal, so it must never regress to an unproven path.
        // The *other* VFO is reached without disturbing operation via 25 01
        // (unselected). If 25 is unsupported, only the watch VFO goes blank; the
        // link and the operating VFO are unaffected.

        public Task<long> GetFrequencyHzAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
            => vfo == ActiveVfo
                ? ReadOperatingFrequencyAsync(cancellationToken)                       // command 03
                : ReadFrequencyBySelectorAsync(CivProtocol.VfoUnselected, cancellationToken); // 25 01

        /// <summary>Read the operating VFO frequency (command 03). -1 on any miss.</summary>
        private async Task<long> ReadOperatingFrequencyAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdReadFrequency);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadFrequency, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.CmdReadFrequency || reply.Data.Length < 5)
                return -1;
            return CivProtocol.DecodeBcd(reply.Data.AsSpan(0, 5));
        }

        /// <summary>
        /// Read a VFO frequency by selected/unselected selector (command 25).
        /// Reply is <c>25 &lt;sel&gt; &lt;5 BCD LE&gt;</c>; -1 on any miss.
        /// </summary>
        private async Task<long> ReadFrequencyBySelectorAsync(byte sel, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoFrequency, sel);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdVfoFrequency, cancellationToken: cancellationToken);
            // Reply body: 25 <sel> <5 BCD> → Data = [sel, b0..b4].
            if (reply == null || reply.Cmd != CivProtocol.CmdVfoFrequency
                || reply.Data.Length < 6 || reply.Data[0] != sel)
                return -1;
            return CivProtocol.DecodeBcd(reply.Data.AsSpan(1, 5));
        }

        public async Task SetFrequencyHzAsync(RadioVfo vfo, long frequencyHz, CancellationToken cancellationToken = default)
        {
            var bcd = CivProtocol.EncodeBcd(frequencyHz, 5);
            byte[] body;
            if (vfo == ActiveVfo)
            {
                // Operating VFO — command 05 (proven).
                body = new byte[1 + bcd.Length];
                body[0] = CivProtocol.CmdSetFrequency;
                Array.Copy(bcd, 0, body, 1, bcd.Length);
            }
            else
            {
                // Other VFO — command 25 01, without switching the operating VFO.
                body = new byte[2 + bcd.Length];
                body[0] = CivProtocol.CmdVfoFrequency;
                body[1] = CivProtocol.VfoUnselected;
                Array.Copy(bcd, 0, body, 2, bcd.Length);
            }

            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, body);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                if (vfo == RadioVfo.A) _state.FrequencyA = frequencyHz;
                else _state.FrequencyB = frequencyHz;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set frequency {Hz} Hz was not acknowledged", frequencyHz);
            }
        }

        /// <summary>
        /// The VFO the radio is currently operating on, tracked from our own 07
        /// sends (there is no CI-V read for the active VFO). Maps
        /// <see cref="RadioStateService.ActiveVfo"/> (0=A/1=B) to <see cref="RadioVfo"/>.
        /// Front-panel A/B presses are the one known desync — a poll can't detect them.
        /// </summary>
        private RadioVfo ActiveVfo => _state.ActiveVfo == 1 ? RadioVfo.B : RadioVfo.A;

        private static int VfoIndex(RadioVfo vfo) => vfo == RadioVfo.B ? 1 : 0;

        private void SetVfoFrequency(RadioVfo vfo, long hz)
        {
            if (vfo == RadioVfo.A) _state.FrequencyA = hz; else _state.FrequencyB = hz;
        }

        private void SetVfoMode(RadioVfo vfo, string mode)
        {
            if (vfo == RadioVfo.A) _state.ModeA = mode; else _state.ModeB = mode;
        }

        // -- Mode (Phase 3 blocks 2 + 5) ---------------------------------------
        //
        // Block 5 folds the former two-frame 06 + 1A 06 dance into the single
        // atomic command 26, which carries <mode> <data> <filter> together and
        // addresses either VFO via the selected/unselected selector. Mode reads
        // are best-effort in the poll loop (a miss never drops the link), so
        // unlike frequency there's no need to keep the legacy 04 path.

        public async Task<string> GetModeAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            byte sel = SelectorFor(vfo);
            var m = await ReadVfoModeRawAsync(sel, cancellationToken);
            if (!m.ok)
                return vfo == RadioVfo.A ? (_state.ModeA ?? "") : (_state.ModeB ?? "");
            return NameForMode(m.mode, m.data != 0);
        }

        public async Task SetModeAsync(RadioVfo vfo, string mode, CancellationToken cancellationToken = default)
        {
            if (!ModeNameToIcom.TryGetValue(mode, out var target))
            {
                // Modes with no IC-7300 CI-V equivalent (PSK, FM-N, AM-N, …).
                _logger.LogWarning("[CivRadioController] Unsupported mode '{Mode}' — ignored", mode);
                return;
            }

            byte sel = SelectorFor(vfo);

            // Preserve the VFO's current filter (FIL1/2/3); default FIL1 if it
            // can't be read. In command 26 the filter is always 1–3 (unlike the
            // 1A 06 form where it was 00 when data was off).
            byte filter = 0x01;
            var cur = await ReadVfoModeRawAsync(sel, cancellationToken);
            if (cur.ok && cur.filter is >= 0x01 and <= 0x03) filter = cur.filter;

            byte dataByte = target.Data ? (byte)0x01 : (byte)0x00;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoMode, sel, target.BaseByte, dataByte, filter);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
            {
                _logger.LogWarning("[CivRadioController] Set mode '{Mode}' (26) was not acknowledged", mode);
                return;
            }

            var name = NameForMode(target.BaseByte, target.Data);
            if (vfo == RadioVfo.A) _state.ModeA = name; else _state.ModeB = name;
        }

        /// <summary>
        /// Read a VFO's mode/data/filter (command 26). Reply is
        /// <c>26 &lt;sel&gt; &lt;mode&gt; &lt;data&gt; &lt;filter&gt;</c>;
        /// <c>ok=false</c> on any miss.
        /// </summary>
        private async Task<(bool ok, byte mode, byte data, byte filter)> ReadVfoModeRawAsync(
            byte sel, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdVfoMode, sel);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdVfoMode, cancellationToken: cancellationToken);
            // Reply body: 26 <sel> <mode> <data> <filter> → Data = [sel, mode, data, filter].
            if (reply == null || reply.Cmd != CivProtocol.CmdVfoMode
                || reply.Data.Length < 4 || reply.Data[0] != sel)
                return (false, 0, 0, 0);
            return (true, reply.Data[1], reply.Data[2], reply.Data[3]);
        }

        /// <summary>Map an A/B target to the 25/26 selected(00)/unselected(01) byte.</summary>
        private byte SelectorFor(RadioVfo vfo)
            => vfo == ActiveVfo ? CivProtocol.VfoSelected : CivProtocol.VfoUnselected;

        // CI-V base-mode byte (+ DATA flag) → the display strings already spoken
        // by RadioStateService, the web mode dropdown, voice, and rigctld. The
        // "-U"/"-L" suffix follows the existing UI vocabulary — CW normal is the
        // USB side ("CW-U"), CW-R the LSB side ("CW-L"); RTTY normal is the LSB
        // side ("RTTY-L"), RTTY-R the USB side. Data variants: USB-D→"DATA-U",
        // LSB-D→"DATA-L", FM-D→"DATA-FM".
        private static string NameForMode(byte baseByte, bool data) => baseByte switch
        {
            0x00 => data ? "DATA-L" : "LSB",
            0x01 => data ? "DATA-U" : "USB",
            0x02 => "AM",   // AM-data has no distinct UI string; report as AM
            0x03 => "CW-U",
            0x04 => "RTTY-L",
            0x05 => data ? "DATA-FM" : "FM",
            0x07 => "CW-L",
            0x08 => "RTTY-U",
            _ => $"?{baseByte:X2}",
        };

        // Display string → (base-mode byte, DATA flag). Covers every mode the
        // IC-7300 exposes over CI-V; Yaesu-only strings (PSK, FM-N, AM-N,
        // DATA-FM-N) are intentionally absent and rejected by SetModeAsync.
        private static readonly Dictionary<string, (byte BaseByte, bool Data)> ModeNameToIcom =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["LSB"] = (0x00, false),
                ["USB"] = (0x01, false),
                ["AM"] = (0x02, false),
                ["CW-U"] = (0x03, false), ["CW"] = (0x03, false),
                ["RTTY-L"] = (0x04, false), ["RTTY"] = (0x04, false),
                ["FM"] = (0x05, false),
                ["CW-L"] = (0x07, false), ["CW-R"] = (0x07, false),
                ["RTTY-U"] = (0x08, false), ["RTTY-R"] = (0x08, false),
                ["DATA-L"] = (0x00, true),
                ["DATA-U"] = (0x01, true),
                ["DATA-FM"] = (0x05, true),
            };

        // -- Meters (Phase 3 blocks 3–4) ---------------------------------------

        public async Task<int> ReadSMeterAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // Command 15 02, level 0–255 as two big-endian BCD bytes
            // (00 00=S0, 01 20=S9, 02 41=S9+60 dB).
            int level = await ReadMeterAsync(CivProtocol.SubSMeter, cancellationToken);
            if (level < 0)
                return vfo == RadioVfo.A ? (_state.SMeterA ?? 0) : (_state.SMeterB ?? 0);
            return level;
        }

        /// <summary>
        /// Read one meter from the 15 family (S-meter 02, Po 11, SWR 12). All
        /// share the same reply shape — 15 &lt;sub&gt; &lt;d1&gt; &lt;d2&gt; with a
        /// 0–255 big-endian BCD level. Returns -1 on any miss (wrong sub, short
        /// frame, no reply) so callers can decide the fallback.
        /// </summary>
        private async Task<int> ReadMeterAsync(byte sub, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadMeter, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadMeter, cancellationToken: cancellationToken);
            // Reply body is 15 <sub> <d1> <d2>: Data = [sub, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdReadMeter
                || reply.Data.Length < 3 || reply.Data[0] != sub)
                return -1;
            return CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
        }

        // -- PTT / TX status + TX meters (Phase 3 block 4) ---------------------

        // The poll loop keeps _state.IsTransmitting live (via ReadTransmitAsync),
        // so on-demand callers (rigctld, voice) get the cached value cheaply
        // rather than firing another bus transaction at ~7 Hz.
        public Task<bool> GetTransmitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_state.IsTransmitting);

        public async Task SetTransmitAsync(bool transmit, CancellationToken cancellationToken = default)
        {
            // Software PTT: command 1C 00 with a data byte (01=TX, 00=RX).
            byte v = transmit ? (byte)0x01 : (byte)0x00;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTxStatus, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
                _state.IsTransmitting = transmit;
            else
                _logger.LogWarning("[CivRadioController] Set PTT {State} was not acknowledged",
                    transmit ? "TX" : "RX");
        }

        /// <summary>
        /// Read the transmit state (command 1C 00, no data byte). Returns the
        /// last known state on a miss so a dropped frame never spuriously
        /// flips TX/RX.
        /// </summary>
        private async Task<bool> ReadTransmitAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTxStatus);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdTransmit, cancellationToken: cancellationToken);
            // Reply body is 1C 00 <status>: Data = [00, status].
            if (reply != null && reply.Cmd == CivProtocol.CmdTransmit
                && reply.Data.Length >= 2 && reply.Data[0] == CivProtocol.SubTxStatus)
                return reply.Data[1] != 0;
            return _state.IsTransmitting;
        }

        // -- Antenna tuner (CI-V 1C 01) ----------------------------------------

        /// <summary>
        /// Read the antenna-tuner state (command 1C 01, no data byte):
        /// 0=OFF, 1=ON, 2=TUNING. -1 on any miss. The poll loop keeps
        /// _state.AtuEnabled / _state.AtuTuning live from this.
        /// </summary>
        public async Task<int> GetTunerAsync(CancellationToken cancellationToken = default)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTuner);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdTransmit, cancellationToken: cancellationToken);
            // Reply body is 1C 01 <status>: Data = [01, status].
            if (reply != null && reply.Cmd == CivProtocol.CmdTransmit
                && reply.Data.Length >= 2 && reply.Data[0] == CivProtocol.SubTuner)
                return reply.Data[1];
            return -1;
        }

        public async Task SetTunerAsync(int state, CancellationToken cancellationToken = default)
        {
            if (state < 0 || state > 2) return;
            byte v = (byte)state;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdTransmit, CivProtocol.SubTuner, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                // 00=OFF, 01=ON, 02=start tuning. A tuning cycle leaves the tuner
                // ON when it finishes; reflect "enabled" for both 01 and 02.
                _state.AtuEnabled = state != CivProtocol.TunerOff;
                _state.AtuTuning = state == CivProtocol.TunerTune;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set tuner {State} was not acknowledged", state);
            }
        }

        // -- RF output power set (CI-V 14 0A) ----------------------------------

        /// <summary>
        /// Read RF output power (command 14 0A). The reply carries a 0–255 level
        /// as two big-endian BCD bytes (same form as the 15-family meters); we
        /// return it as a 0–100 % value. -1 on any miss.
        /// </summary>
        public async Task<int> GetRfPowerPercentAsync(CancellationToken cancellationToken = default)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, CivProtocol.SubRfPower);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSetLevel, cancellationToken: cancellationToken);
            // Reply body is 14 0A <d1> <d2>: Data = [0A, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdSetLevel
                || reply.Data.Length < 3 || reply.Data[0] != CivProtocol.SubRfPower)
                return -1;
            int level = CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
            return (int)Math.Round(Math.Clamp(level, 0, 255) * 100.0 / 255.0);
        }

        /// <summary>
        /// Set RF output power (command 14 0A). The 0–100 % level is scaled to the
        /// radio's 0–255 range and sent as two big-endian BCD bytes.
        /// </summary>
        public async Task SetRfPowerPercentAsync(int percent, CancellationToken cancellationToken = default)
        {
            int level = (int)Math.Round(Math.Clamp(percent, 0, 100) * 255.0 / 100.0);
            byte d1 = (byte)(level / 100);                    // 0–2: a single BCD digit is its own value
            int rem = level % 100;
            byte d2 = (byte)(((rem / 10) << 4) | (rem % 10)); // packed BCD of the low two digits
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, CivProtocol.SubRfPower, d1, d2);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set RF power {Percent}% (level {Level}) was not acknowledged",
                    percent, level);
        }

        // -- AF (volume) level (CI-V 14 01) ------------------------------------
        // Receiver-wide 0–255 audio level; same 14-family form as RF power/PBT.

        public Task<int> GetAfGainAsync(CancellationToken cancellationToken = default)
            => ReadLevel14Async(CivProtocol.SubAfGain, cancellationToken);

        public Task SetAfGainAsync(int value, CancellationToken cancellationToken = default)
            => WriteLevel14Async(CivProtocol.SubAfGain, value, "AF gain", cancellationToken);

        // -- Twin PBT (CI-V 14 07 / 14 08) -------------------------------------
        // Same wire form as RF power (14 0A): two big-endian BCD bytes 00 00–02 55
        // where 01 28 (=128) is centre. Read and write share one helper each.

        public Task<int> GetPbtInnerAsync(CancellationToken cancellationToken = default)
            => ReadLevel14Async(CivProtocol.SubPbtInner, cancellationToken);

        public Task SetPbtInnerAsync(int value, CancellationToken cancellationToken = default)
            => WriteLevel14Async(CivProtocol.SubPbtInner, value, "PBT inner", cancellationToken);

        public Task<int> GetPbtOuterAsync(CancellationToken cancellationToken = default)
            => ReadLevel14Async(CivProtocol.SubPbtOuter, cancellationToken);

        public Task SetPbtOuterAsync(int value, CancellationToken cancellationToken = default)
            => WriteLevel14Async(CivProtocol.SubPbtOuter, value, "PBT outer", cancellationToken);

        /// <summary>
        /// Read a 14-family level (command 14 &lt;sub&gt;). The reply carries a
        /// 0–255 value as two big-endian BCD bytes. Returns -1 on any miss.
        /// </summary>
        private async Task<int> ReadLevel14Async(byte sub, CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSetLevel, cancellationToken: cancellationToken);
            // Reply body is 14 <sub> <d1> <d2>: Data = [sub, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdSetLevel
                || reply.Data.Length < 3 || reply.Data[0] != sub)
                return -1;
            return CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
        }

        /// <summary>
        /// Write a 14-family level (command 14 &lt;sub&gt;) as two big-endian BCD
        /// bytes. The value is clamped to 0–255.
        /// </summary>
        private async Task WriteLevel14Async(byte sub, int value, string what, CancellationToken cancellationToken)
        {
            int level = Math.Clamp(value, 0, 255);
            byte d1 = (byte)(level / 100);                    // 0–2: a single BCD digit is its own value
            int rem = level % 100;
            byte d2 = (byte)(((rem / 10) << 4) | (rem % 10)); // packed BCD of the low two digits
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetLevel, sub, d1, d2);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set {What} to {Level} was not acknowledged", what, level);
        }

        // -- RX controls (receiver-wide) ---------------------------------------
        // 14-family levels reuse the shared BCD helpers; the 16-family functions
        // and the attenuator get their own small read/write helpers below.

        public Task<int> GetRfGainAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubRfGain, ct);
        public Task SetRfGainAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubRfGain, value, "RF gain", ct);
        public Task<int> GetSquelchAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubSquelch, ct);
        public Task SetSquelchAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubSquelch, value, "squelch", ct);
        public Task<int> GetNrLevelAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubNrLevel, ct);
        public Task SetNrLevelAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubNrLevel, value, "NR level", ct);
        public Task<int> GetNbLevelAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubNbLevel, ct);
        public Task SetNbLevelAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubNbLevel, value, "NB level", ct);
        public Task<int> GetNotchPositionAsync(CancellationToken ct = default) => ReadLevel14Async(CivProtocol.SubNotchPos, ct);
        public Task SetNotchPositionAsync(int value, CancellationToken ct = default) => WriteLevel14Async(CivProtocol.SubNotchPos, value, "notch position", ct);

        public Task<int> GetPreampAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubPreamp, ct);
        public Task SetPreampAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubPreamp, Math.Clamp(value, 0, 2), "preamp", ct);
        public Task<int> GetAgcAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubAgc, ct);
        public Task SetAgcAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubAgc, Math.Clamp(value, 1, 3), "AGC", ct);

        public async Task<bool> GetNoiseBlankerAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubNoiseBlanker, ct) == 1;
        public Task SetNoiseBlankerAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubNoiseBlanker, on ? 1 : 0, "noise blanker", ct);
        public async Task<bool> GetNoiseReductionAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubNoiseReduction, ct) == 1;
        public Task SetNoiseReductionAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubNoiseReduction, on ? 1 : 0, "noise reduction", ct);
        public async Task<bool> GetAutoNotchAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubAutoNotch, ct) == 1;
        public Task SetAutoNotchAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubAutoNotch, on ? 1 : 0, "auto notch", ct);
        public async Task<bool> GetManualNotchAsync(CancellationToken ct = default) => await ReadFunc16Async(CivProtocol.SubManualNotch, ct) == 1;
        public Task SetManualNotchAsync(bool on, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubManualNotch, on ? 1 : 0, "manual notch", ct);
        public Task<int> GetManualNotchWidthAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubManualNotchWidth, ct);
        public Task SetManualNotchWidthAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubManualNotchWidth, Math.Clamp(value, 0, 2), "manual notch width", ct);
        public Task<int> GetIfFilterShapeAsync(CancellationToken ct = default) => ReadFunc16Async(CivProtocol.SubIfFilterShape, ct);
        public Task SetIfFilterShapeAsync(int value, CancellationToken ct = default) => WriteFunc16Async(CivProtocol.SubIfFilterShape, Math.Clamp(value, 0, 1), "IF filter shape", ct);

        /// <summary>Read a 16-family function (16 &lt;sub&gt;). Reply is 16 &lt;sub&gt; &lt;val&gt;. -1 on a miss.</summary>
        private async Task<int> ReadFunc16Async(byte sub, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetFunc, sub);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSetFunc, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.CmdSetFunc
                || reply.Data.Length < 2 || reply.Data[0] != sub)
                return -1;
            return reply.Data[1];
        }

        /// <summary>Write a 16-family function (16 &lt;sub&gt; &lt;val&gt;) and expect an ack.</summary>
        private async Task WriteFunc16Async(byte sub, int value, string what, CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetFunc, sub, (byte)value);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set {What} to {Value} was not acknowledged", what, value);
        }

        public async Task<bool> GetAttenuatorAsync(CancellationToken ct = default)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdAttenuator);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdAttenuator, cancellationToken: ct);
            // Reply body: 11 <val> → Data = [val]; 0x20 = 20 dB on.
            if (reply == null || reply.Cmd != CivProtocol.CmdAttenuator || reply.Data.Length < 1)
                return false;
            return reply.Data[0] == CivProtocol.AttOn20dB;
        }

        public async Task SetAttenuatorAsync(bool on, CancellationToken ct = default)
        {
            byte val = on ? CivProtocol.AttOn20dB : CivProtocol.AttOff;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdAttenuator, val);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            if (reply == null || reply.Cmd != CivProtocol.AckOk)
                _logger.LogWarning("[CivRadioController] Set attenuator {State} was not acknowledged", on ? "20 dB" : "off");
        }

        // -- VFO select / exchange / split (Phase 3 block 5) -------------------

        public async Task SelectVfoAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // Command 07 00/01 — set-only; no read of the active VFO exists, so
            // we mirror the change into RadioStateService.ActiveVfo, which is the
            // source of truth SelectorFor / ActiveVfo read back.
            byte v = vfo == RadioVfo.B ? CivProtocol.VfoSelectB : CivProtocol.VfoSelectA;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
                _state.ActiveVfo = vfo == RadioVfo.B ? 1 : 0;
            else
                _logger.LogWarning("[CivRadioController] Select VFO {Vfo} was not acknowledged", vfo);
        }

        public async Task ExchangeVfosAsync(CancellationToken cancellationToken = default)
        {
            // Command 07 B0 — swap A↔B; the selected VFO letter is unchanged, so
            // ActiveVfo stays put. Swap the cached freq/mode for a snappy UI; the
            // poll re-reads both within a couple of loops regardless.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, CivProtocol.VfoExchange);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                (_state.FrequencyA, _state.FrequencyB) = (_state.FrequencyB, _state.FrequencyA);
                (_state.ModeA, _state.ModeB) = (_state.ModeB, _state.ModeA);
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Exchange VFOs was not acknowledged");
            }
        }

        public async Task EqualizeVfosAsync(CancellationToken cancellationToken = default)
        {
            // Command 07 A0 — make both VFOs equal (the unselected takes the
            // selected VFO's contents). Mirror selected → other in cache.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSelectVfo, CivProtocol.VfoEqualize);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                if (ActiveVfo == RadioVfo.A) { _state.FrequencyB = _state.FrequencyA; _state.ModeB = _state.ModeA; }
                else                          { _state.FrequencyA = _state.FrequencyB; _state.ModeA = _state.ModeB; }
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Equalize VFOs was not acknowledged");
            }
        }

        public async Task<bool> GetSplitAsync(CancellationToken cancellationToken = default)
        {
            // Command 0F with no data reads split; reply 0F <00=off|01=on>.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdSplit);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdSplit, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.CmdSplit && reply.Data.Length >= 1)
                return reply.Data[0] != 0;
            return _state.SplitMode > 0; // keep last known on a miss
        }

        public async Task SetSplitAsync(bool on, CancellationToken cancellationToken = default)
        {
            byte v = on ? CivProtocol.SplitOn : CivProtocol.SplitOff;
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSplit, v);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                // Preserve a UI-set quick-split (2); only sync the on/off axis.
                if (on) { if (_state.SplitMode == 0) _state.SplitMode = 1; }
                else _state.SplitMode = 0;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set split {State} was not acknowledged", on ? "ON" : "OFF");
            }
        }

        // -- Power on/off (Phase 3 block 7, command 18) ------------------------

        public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default)
            => on ? PowerOnAsync(cancellationToken) : PowerOffAsync(cancellationToken);

        /// <summary>
        /// Power the radio down (18 00). The radio ACKs FB then powers off, so
        /// the ack is best-effort — a null reply here is expected, not an error.
        /// We flag RadioPowerOn false immediately; the poll loop's own
        /// miss-detection then drops the link naturally once the radio stops
        /// answering (no forced close from this thread, which would race the
        /// poll's in-flight transaction).
        /// </summary>
        private async Task PowerOffAsync(CancellationToken ct)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdPower, CivProtocol.PowerOff);
            await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: ct);
            _state.RadioPowerOn = false;
            _logger.LogInformation("[CivRadioController] Power OFF (18 00) sent");
        }

        /// <summary>
        /// Power the radio up (18 01). The asleep CI-V circuit is woken with a
        /// burst of 0xFE bytes sized to the baud rate, sent in one write with the
        /// frame appended so no inter-byte gap lets it doze off again. Over USB
        /// the port is unpowered while the radio is off, so this can only succeed
        /// over a separately-powered CI-V remote-jack link; on USB the front-panel
        /// switch is the only way up. On success the poll loop reconnects and
        /// re-identifies once the radio's bus starts answering.
        /// </summary>
        private async Task PowerOnAsync(CancellationToken ct)
        {
            var settings = await _settings.GetSettingsAsync();

            if (!_bus.IsOpen && !await _bus.OpenAsync(settings.SerialPort, settings.BaudRate))
            {
                _logger.LogWarning("[CivRadioController] Power ON: CI-V port unavailable — the USB port is unpowered while the radio is off");
                return;
            }

            int wake = CivProtocol.PowerOnWakePreambleCount(settings.BaudRate);
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdPower, CivProtocol.PowerOn);
            var burst = new byte[wake + frame.Length];
            for (int i = 0; i < wake; i++) burst[i] = CivProtocol.Preamble; // 0xFE
            Array.Copy(frame, 0, burst, wake, frame.Length);

            await _bus.SendRawAsync(burst, ct);
            _state.RadioPowerOn = true;
            _logger.LogInformation("[CivRadioController] Power ON (18 01) sent with {Wake}× FE wake preamble", wake);
        }

        // -- Connect / identify -------------------------------------------------

        /// <summary>
        /// Read the transceiver ID (19 00). The reply's From byte is the radio's
        /// real CI-V address; its data byte is the model's default address, which
        /// distinguishes IC-7300 MkII (B6) from the classic IC-7300 (94) and other
        /// Icoms — hence "read the ID, don't hard-code." Returns false when the
        /// radio doesn't answer at all: the port opened but nothing is on the bus
        /// (radio powered off or asleep), which is not a real connection.
        /// </summary>
        private async Task<bool> IdentifyAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadId, CivProtocol.SubReadId);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadId, cancellationToken: cancellationToken);
            if (reply == null)
                return false;

            if (reply.From != 0x00)
                _radioAddress = reply.From;

            byte idByte = _radioAddress;
            if (reply.Data.Length >= 2)
                idByte = reply.Data[^1]; // 19 00 <id>

            ModelId = MapModel(idByte);
            _state.RadioModel = ModelId;
            _logger.LogInformation("[CivRadioController] Radio identified: {Model} (CI-V address {Addr:X2})",
                ModelId, _radioAddress);
            return true;
        }

        private static string MapModel(byte idByte) => idByte switch
        {
            0xB6 => "IC-7300MK2",
            0x94 => "IC-7300",
            _ => $"Icom({idByte:X2})",
        };

        // -- Hosted poll loop ---------------------------------------------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[CivRadioController] Phase-3 CI-V link starting.");
            int misses = 0;
            long loop = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_bus.IsOpen)
                {
                    bool ok;
                    try
                    {
                        ok = await ConnectAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Connect touches the serial port (open, ID, freq read,
                        // close), any of which can throw while the radio is being
                        // powered off and its USB port vanishes. Swallow it here —
                        // this call sits outside the poll try/catch below, so an
                        // escape would fault the BackgroundService and take the
                        // whole host down. Treat it as a failed connect and retry.
                        _logger.LogWarning(ex, "[CivRadioController] Connect attempt threw — retrying");
                        ok = false;
                    }

                    if (!ok)
                    {
                        await SetConnectedAsync(false);
                        await DelayQuiet(ReconnectDelayMs, stoppingToken);
                        continue;
                    }

                    misses = 0;
                    await SetConnectedAsync(true);
                }

                try
                {
                    // Which VFO the radio is operating on decides how each panel
                    // is addressed: the active VFO rides the proven operating
                    // commands, the other is the "watch" VFO read via 25/26.
                    RadioVfo active = ActiveVfo;
                    RadioVfo other = active == RadioVfo.A ? RadioVfo.B : RadioVfo.A;

                    // Operating frequency (command 03) is the liveness signal —
                    // the only read that counts misses and can drop the link.
                    long hz = await ReadOperatingFrequencyAsync(stoppingToken);
                    if (hz > 0)
                    {
                        misses = 0;
                        // The operating-freq read is the liveness signal, so a good
                        // read means we're connected — regardless of how the port
                        // got opened. It can be opened out-of-band by PowerOnAsync
                        // (IWC's power button sends 18 01 on a port it opens itself),
                        // which skips the reconnect block above and its
                        // SetConnectedAsync(true); without this the init spinner
                        // would spin forever even though control works.
                        if (!_state.IsConnected)
                            await SetConnectedAsync(true);
                        SetVfoFrequency(active, hz); // broadcasts on change
                    }
                    else if (++misses >= MaxConsecutiveReadMisses)
                    {
                        _logger.LogWarning("[CivRadioController] {Misses} consecutive frequency-read misses — dropping link", misses);
                        await _bus.CloseAsync();
                        await SetConnectedAsync(false);
                        continue; // link is down — don't chase further reads
                    }

                    // Watch VFO frequency (command 25 01) — best-effort, every
                    // other loop. A miss (or an unsupported 25) just leaves the
                    // last value; it never affects liveness.
                    if (loop % 2 == 1)
                    {
                        long otherHz = await ReadFrequencyBySelectorAsync(CivProtocol.VfoUnselected, stoppingToken);
                        if (otherHz > 0) SetVfoFrequency(other, otherHz);
                    }

                    // TX/RX status (command 1C 00) every loop — cheap, and it
                    // decides which meters are worth reading. Best-effort: a miss
                    // keeps the last state and never drops the link.
                    bool transmitting = await ReadTransmitAsync(stoppingToken);
                    _state.IsTransmitting = transmitting; // broadcasts on change

                    if (transmitting)
                    {
                        // Transmitting: the S-meter is meaningless; the TX needles
                        // — Po (15 11), SWR (15 12), ALC (15 13), COMP (15 14) and
                        // Id (15 16) — are live. A -1 miss leaves the last value.
                        int po = await ReadMeterAsync(CivProtocol.SubPoMeter, stoppingToken);
                        if (po >= 0) _state.PowerMeter = po;
                        int swr = await ReadMeterAsync(CivProtocol.SubSwrMeter, stoppingToken);
                        if (swr >= 0) _state.SWRMeter = swr;
                        int alc = await ReadMeterAsync(CivProtocol.SubAlcMeter, stoppingToken);
                        if (alc >= 0) _state.ALCMeter = alc;
                        int comp = await ReadMeterAsync(CivProtocol.SubCompMeter, stoppingToken);
                        if (comp >= 0) _state.CompressionMeter = comp;
                        int id = await ReadMeterAsync(CivProtocol.SubIdMeter, stoppingToken);
                        if (id >= 0) _state.IDDMeter = id;
                    }
                    else
                    {
                        // Receiving: S-meter (command 15 02) is the fast-moving
                        // meter. Zero the TX-only needles once so they don't hang
                        // at their last transmit reading after unkey.
                        _state.SMeterA = await ReadSMeterAsync(RadioVfo.A, stoppingToken);
                        if (_state.PowerMeter is not (null or 0)) _state.PowerMeter = 0;
                        if (_state.SWRMeter is not (null or 0)) _state.SWRMeter = 0;
                        if (_state.ALCMeter is not (null or 0)) _state.ALCMeter = 0;
                        if (_state.CompressionMeter is not (null or 0)) _state.CompressionMeter = 0;
                        if (_state.IDDMeter is not (null or 0)) _state.IDDMeter = 0;
                    }

                    // Vd — the PA supply rail (15 15) — is present in both states
                    // (idle voltage on RX, sagging under load on TX), so read it
                    // independently of the TX/RX branch but slowly; it moves
                    // gently and shouldn't crowd out the S-meter.
                    if (loop % SplitPollEveryNLoops == 0)
                    {
                        int vd = await ReadMeterAsync(CivProtocol.SubVdMeter, stoppingToken);
                        if (vd >= 0) _state.VDDMeter = vd;
                    }

                    // Mode (command 26) changes rarely, so poll it less often to
                    // keep the bus free for the meter, and interleave the two
                    // VFOs onto different phases. Skip "?XX" (unmapped) values.
                    if (loop % ModePollEveryNLoops == 0)
                    {
                        var modeName = await GetModeAsync(active, stoppingToken);
                        if (!string.IsNullOrEmpty(modeName) && !modeName.StartsWith('?'))
                            SetVfoMode(active, modeName);
                    }
                    else if (loop % ModePollEveryNLoops == 1)
                    {
                        var modeName = await GetModeAsync(other, stoppingToken);
                        if (!string.IsNullOrEmpty(modeName) && !modeName.StartsWith('?'))
                            SetVfoMode(other, modeName);
                    }

                    // Split state (command 0F) — slow-moving; refresh a few times
                    // a second. Reading only distinguishes on/off, so preserve a
                    // UI-set quick-split (2). In split, the watch VFO transmits.
                    if (loop % SplitPollEveryNLoops == 2)
                    {
                        bool split = await GetSplitAsync(stoppingToken);
                        if (split) { if (_state.SplitMode == 0) _state.SplitMode = 1; }
                        else _state.SplitMode = 0;
                        _state.TxVfo = VfoIndex(split ? other : active);
                    }

                    // AF (volume) level (14 01) — mirror front-panel AF-knob
                    // turns back to the app slider. Receiver-wide and slow, so
                    // ride a spare split-poll phase (~1.5 Hz). A miss leaves the
                    // last value; the state setter only broadcasts on change, so
                    // this is quiet unless the knob actually moved.
                    if (loop % SplitPollEveryNLoops == 3)
                    {
                        int af = await GetAfGainAsync(stoppingToken);
                        if (af >= 0) { _state.AfGainA = af; _state.AfGainB = af; }
                    }

                    // A couple of RX controls per loop, cycling through AGC/
                    // preamp/NB/NR/notch/att/levels so front-panel changes to any
                    // of them make it back to the app within about a second.
                    for (int i = 0; i < RxControlsPerLoop; i++)
                        await PollNextRxControlAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CivRadioController] Radio poll failed");
                }

                loop++;
                await DelayQuiet(PollIntervalMs, stoppingToken);
            }

            await _bus.CloseAsync();
        }

        private async Task SetConnectedAsync(bool connected)
        {
            _state.IsConnected = connected;
            _state.RadioPowerOn = connected;

            // Leave the frontend's "connecting…" overlay once we have a link, in
            // the same way the Phase 1 stub did.
            AppStatus.InitializationStatus = connected ? "complete" : "initializing";
            try
            {
                await _hubContext.Clients.All.SendAsync("InitializationStatus", AppStatus.InitializationStatus);
            }
            catch
            {
                // No clients connected yet — the value is also polled via
                // /api/cat/status/init.
            }
        }

        private static async Task DelayQuiet(int ms, CancellationToken ct)
        {
            try { await Task.Delay(ms, ct); }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }
}
