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
        private const int PollIntervalMs = 300;
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

            return ModeByteToName.TryGetValue(reply.Data[0], out var name) ? name : $"?{reply.Data[0]:X2}";
        }

        public async Task SetModeAsync(RadioVfo vfo, string mode, CancellationToken cancellationToken = default)
        {
            if (!ModeNameToByte.TryGetValue(mode, out var modeByte))
            {
                // Icom data modes (USB-D etc.) need 1A 06 and land in a later
                // block; core modes are handled here.
                _logger.LogWarning("[CivRadioController] Unsupported mode '{Mode}' — ignored", mode);
                return;
            }

            // Mode byte only: the radio keeps its current filter (FIL1/2/3).
            var frame = CivProtocol.BuildFrame(_radioAddress, CivProtocol.ControllerAddress, CivProtocol.CmdSetMode, modeByte);
            var reply = await _bus.TransactAsync(frame, CivProtocol.AckOk, cancellationToken: cancellationToken);
            if (reply != null && reply.Cmd == CivProtocol.AckOk)
            {
                var name = ModeByteToName.TryGetValue(modeByte, out var n) ? n : mode;
                if (vfo == RadioVfo.A) _state.ModeA = name; else _state.ModeB = name;
            }
            else
            {
                _logger.LogWarning("[CivRadioController] Set mode '{Mode}' was not acknowledged", mode);
            }
        }

        // CI-V mode byte (command 04/06 data 1) ↔ the display strings already
        // spoken by RadioStateService, the web mode dropdown, voice, and
        // rigctld. The IC-7300's native modes; "-U"/"-L" follows the existing
        // UI vocabulary — CW normal is the USB side ("CW-U"), CW-R the LSB side
        // ("CW-L"); RTTY normal is the LSB side ("RTTY-L"), RTTY-R the USB side.
        private static readonly Dictionary<byte, string> ModeByteToName = new()
        {
            [0x00] = "LSB", [0x01] = "USB", [0x02] = "AM",
            [0x03] = "CW-U", [0x04] = "RTTY-L", [0x05] = "FM",
            [0x07] = "CW-L", [0x08] = "RTTY-U",
        };

        private static readonly Dictionary<string, byte> ModeNameToByte = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LSB"] = 0x00, ["USB"] = 0x01, ["AM"] = 0x02,
            ["CW-U"] = 0x03, ["CW"] = 0x03,
            ["RTTY-L"] = 0x04, ["RTTY"] = 0x04,
            ["FM"] = 0x05,
            ["CW-L"] = 0x07, ["CW-R"] = 0x07,
            ["RTTY-U"] = 0x08, ["RTTY-R"] = 0x08,
        };

        // Not yet implemented behind CI-V (later Phase 3 blocks). Safe
        // placeholders so the seam's other callers never throw.

        public Task<int> ReadSMeterAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
            => Task.FromResult(vfo == RadioVfo.A ? (_state.SMeterA ?? 0) : (_state.SMeterB ?? 0));

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
            _logger.LogInformation("[CivRadioController] Phase-2 CI-V link starting.");
            int misses = 0;

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
                        continue; // link is down — don't chase a mode read
                    }

                    // Mode (command 04) is best-effort: the frequency read above
                    // is the liveness signal, so a mode miss must not inflate
                    // `misses` or drop the link. Skip "?XX" (unmapped) values.
                    var modeName = await GetModeAsync(RadioVfo.A, stoppingToken);
                    if (!string.IsNullOrEmpty(modeName) && !modeName.StartsWith('?'))
                        _state.ModeA = modeName; // broadcasts ModeA on change
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[CivRadioController] Radio poll failed");
                }

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
