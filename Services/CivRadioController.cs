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

            await IdentifyAsync(cancellationToken);
            return true;
        }

        public Task DisconnectAsync() => _bus.CloseAsync();

        public async Task<long> GetFrequencyHzAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // The IC-7300 MkII is single-receiver: command 03 reads the current
            // operating VFO. Per-VFO addressing (25/26) arrives in Phase 3; for
            // now both A and B map to the operating frequency.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdReadFrequency);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadFrequency, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.CmdReadFrequency || reply.Data.Length < 5)
                return -1;

            return CivProtocol.DecodeBcd(reply.Data.AsSpan(0, 5));
        }

        public async Task SetFrequencyHzAsync(RadioVfo vfo, long frequencyHz, CancellationToken cancellationToken = default)
        {
            var bcd = CivProtocol.EncodeBcd(frequencyHz, 5);
            var body = new byte[1 + bcd.Length];
            body[0] = CivProtocol.CmdSetFrequency;
            Array.Copy(bcd, 0, body, 1, bcd.Length);

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

        // -- Mode (Phase 3 block 2) --------------------------------------------

        public async Task<string> GetModeAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // Single-receiver: command 04 reads the current operating mode. Both
            // A and B map to it until per-VFO addressing arrives.
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdReadMode);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadMode, cancellationToken: cancellationToken);
            if (reply == null || reply.Cmd != CivProtocol.CmdReadMode || reply.Data.Length < 1)
                return vfo == RadioVfo.A ? (_state.ModeA ?? "") : (_state.ModeB ?? "");

            byte baseByte = reply.Data[0];

            // On SSB/AM/FM the mode may additionally be a "data" mode (USB-D
            // etc.). CW/RTTY have no data variant, so skip the extra read.
            bool data = false;
            if (BaseSupportsData(baseByte))
                data = await ReadDataModeAsync(cancellationToken);

            return NameForMode(baseByte, data);
        }

        public async Task SetModeAsync(RadioVfo vfo, string mode, CancellationToken cancellationToken = default)
        {
            if (!ModeNameToIcom.TryGetValue(mode, out var target))
            {
                // Modes with no IC-7300 CI-V equivalent (PSK, FM-N, AM-N, …).
                _logger.LogWarning("[CivRadioController] Unsupported mode '{Mode}' — ignored", mode);
                return;
            }

            // 1) Base mode (command 06, mode byte only — radio keeps its filter).
            var baseFrame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdSetMode, target.BaseByte);
            var baseReply = await _bus.TransactAsync(baseFrame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (baseReply == null || baseReply.Cmd != CivProtocol.AckOk)
            {
                _logger.LogWarning("[CivRadioController] Set base mode for '{Mode}' was not acknowledged", mode);
                return;
            }

            // 2) DATA flag (command 1A 06), only where the base mode has one.
            //    Enabling data needs a filter (FIL1); disabling forces 00/00.
            if (BaseSupportsData(target.BaseByte))
            {
                byte dataOn = target.Data ? (byte)0x01 : (byte)0x00;
                byte filter = target.Data ? (byte)0x01 : (byte)0x00;
                var dataFrame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                    CivProtocol.CmdMenu, CivProtocol.SubDataMode, dataOn, filter);
                var dataReply = await _bus.TransactAsync(dataFrame, CivProtocol.AckOk, cancellationToken: cancellationToken);
                if (dataReply == null || dataReply.Cmd != CivProtocol.AckOk)
                    _logger.LogWarning("[CivRadioController] Set DATA flag for '{Mode}' was not acknowledged", mode);
            }

            var name = NameForMode(target.BaseByte, target.Data);
            if (vfo == RadioVfo.A) _state.ModeA = name; else _state.ModeB = name;
        }

        /// <summary>Read the DATA on/off flag (command 1A 06). False on any miss.</summary>
        private async Task<bool> ReadDataModeAsync(CancellationToken cancellationToken)
        {
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdMenu, CivProtocol.SubDataMode);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdMenu, cancellationToken: cancellationToken);
            // Reply body is 1A 06 <dataOn> <filter>: Data = [06, dataOn, filter].
            if (reply != null && reply.Cmd == CivProtocol.CmdMenu
                && reply.Data.Length >= 2 && reply.Data[0] == CivProtocol.SubDataMode)
                return reply.Data[1] != 0;
            return false;
        }

        private static bool BaseSupportsData(byte baseByte)
            => baseByte is 0x00 or 0x01 or 0x02 or 0x05; // LSB, USB, AM, FM

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

        // -- S-meter (Phase 3 block 3) -----------------------------------------

        public async Task<int> ReadSMeterAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            // Command 15 02 → 15 02 <d1> <d2>, level 0–255 as two big-endian
            // BCD bytes (00 00=S0, 01 20=S9, 02 41=S9+60 dB).
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadMeter, CivProtocol.SubSMeter);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadMeter, cancellationToken: cancellationToken);
            // Reply body is 15 02 <d1> <d2>: Data = [02, d1, d2].
            if (reply == null || reply.Cmd != CivProtocol.CmdReadMeter
                || reply.Data.Length < 3 || reply.Data[0] != CivProtocol.SubSMeter)
                return vfo == RadioVfo.A ? (_state.SMeterA ?? 0) : (_state.SMeterB ?? 0);

            return CivProtocol.BcdByte(reply.Data[1]) * 100 + CivProtocol.BcdByte(reply.Data[2]);
        }

        // Not yet implemented behind CI-V (later Phase 3 blocks). Safe
        // placeholders so the seam's other callers never throw.

        public Task<bool> GetTransmitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_state.IsTransmitting);

        public Task SetTransmitAsync(bool transmit, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // -- Connect / identify -------------------------------------------------

        private async Task IdentifyAsync(CancellationToken cancellationToken)
        {
            // Read the transceiver ID (19 00). The reply's From byte is the
            // radio's real CI-V address; its data byte is the model's default
            // address, which distinguishes IC-7300 MkII (B6) from the classic
            // IC-7300 (94) and other Icoms — hence "read the ID, don't hard-code."
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress,
                CivProtocol.CmdReadId, CivProtocol.SubReadId);
            var reply = await _bus.TransactAsync(frame, CivProtocol.CmdReadId, cancellationToken: cancellationToken);

            if (reply != null && reply.From != 0x00)
                _radioAddress = reply.From;

            byte idByte = _radioAddress;
            if (reply != null && reply.Data.Length >= 2)
                idByte = reply.Data[^1]; // 19 00 <id>

            ModelId = MapModel(idByte);
            _state.RadioModel = ModelId;
            _logger.LogInformation("[CivRadioController] Radio identified: {Model} (CI-V address {Addr:X2})",
                ModelId, _radioAddress);
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
                    var ok = await ConnectAsync(stoppingToken);
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
                    // Frequency (command 03) is the liveness signal — the only
                    // read that counts misses and can drop the link.
                    long hz = await GetFrequencyHzAsync(RadioVfo.A, stoppingToken);
                    if (hz > 0)
                    {
                        misses = 0;
                        _state.FrequencyA = hz; // broadcasts FrequencyA on change
                    }
                    else if (++misses >= MaxConsecutiveReadMisses)
                    {
                        _logger.LogWarning("[CivRadioController] {Misses} consecutive frequency-read misses — dropping link", misses);
                        await _bus.CloseAsync();
                        await SetConnectedAsync(false);
                        continue; // link is down — don't chase further reads
                    }

                    // S-meter (command 15 02) every loop — the fast-moving meter.
                    // Best-effort: a miss returns the last value and never drops
                    // the link.
                    _state.SMeterA = await ReadSMeterAsync(RadioVfo.A, stoppingToken);

                    // Mode (command 04, plus 1A 06 on SSB/AM/FM) is slower to
                    // change, so poll it less often to keep the bus free for the
                    // meter. Skip "?XX" (unmapped) values.
                    if (loop % ModePollEveryNLoops == 0)
                    {
                        var modeName = await GetModeAsync(RadioVfo.A, stoppingToken);
                        if (!string.IsNullOrEmpty(modeName) && !modeName.StartsWith('?'))
                            _state.ModeA = modeName; // broadcasts ModeA on change
                    }
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
