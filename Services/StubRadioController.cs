using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;
using Icom_Web_Control.Hubs;
using Icom_Web_Control.Models;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Phase 1 stand-in for the real radio link. It implements the semantic
    /// <see cref="IRadioController"/> seam with canned values (no serial port,
    /// no CI-V) and, as a hosted service, gently animates a few
    /// <see cref="RadioStateService"/> properties so the LIFTed SignalR →
    /// calibration → gauge pipeline can be exercised end-to-end with fake data.
    ///
    /// It exists only to prove the plumbing survived the clone-and-carve. Phase 2
    /// replaces it with CivRadioController talking real CI-V to the IC-7300 MkII.
    /// See docs/design/iwc-clone-split-plan.md.
    ///
    /// Deliberately does NOT set RadioStateService.IsInitialized, so its canned
    /// values are broadcast to the UI but never persisted over the user's real
    /// radio_state.json.
    /// </summary>
    public class StubRadioController : BackgroundService, IRadioController
    {
        private readonly RadioStateService _state;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<StubRadioController> _logger;

        // Canned starting point: a plausible dual-VFO HF setup.
        private long _freqA = 14_074_000; // 20 m FT8
        private long _freqB = 7_100_000;  // 40 m
        private string _modeA = "USB";
        private string _modeB = "LSB";
        private bool _transmit;

        public StubRadioController(
            RadioStateService state,
            IHubContext<RadioHub> hubContext,
            ILogger<StubRadioController> logger)
        {
            _state = state;
            _hubContext = hubContext;
            _logger = logger;
        }

        // -- IRadioController (canned) -----------------------------------------

        public bool IsConnected { get; private set; }
        public string? ModelId { get; private set; }

        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            ModelId = "STUB";
            return Task.FromResult(true);
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<long> GetFrequencyHzAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
            => Task.FromResult(vfo == RadioVfo.A ? _freqA : _freqB);

        public Task SetFrequencyHzAsync(RadioVfo vfo, long frequencyHz, CancellationToken cancellationToken = default)
        {
            if (vfo == RadioVfo.A) { _freqA = frequencyHz; _state.FrequencyA = frequencyHz; }
            else                   { _freqB = frequencyHz; _state.FrequencyB = frequencyHz; }
            return Task.CompletedTask;
        }

        public Task<string> GetModeAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
            => Task.FromResult(vfo == RadioVfo.A ? _modeA : _modeB);

        public Task SetModeAsync(RadioVfo vfo, string mode, CancellationToken cancellationToken = default)
        {
            if (vfo == RadioVfo.A) { _modeA = mode; _state.ModeA = mode; }
            else                   { _modeB = mode; _state.ModeB = mode; }
            return Task.CompletedTask;
        }

        public Task<int> ReadSMeterAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
            => Task.FromResult(vfo == RadioVfo.A ? (_state.SMeterA ?? 0) : (_state.SMeterB ?? 0));

        public Task<bool> GetTransmitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_transmit);

        public Task SetTransmitAsync(bool transmit, CancellationToken cancellationToken = default)
        {
            _transmit = transmit;
            _state.IsTransmitting = transmit;
            return Task.CompletedTask;
        }

        // -- VFO / split (Phase 3 block 5, canned) -----------------------------

        private bool _split;

        public Task SelectVfoAsync(RadioVfo vfo, CancellationToken cancellationToken = default)
        {
            _state.ActiveVfo = vfo == RadioVfo.B ? 1 : 0;
            return Task.CompletedTask;
        }

        public Task ExchangeVfosAsync(CancellationToken cancellationToken = default)
        {
            (_freqA, _freqB) = (_freqB, _freqA);
            (_modeA, _modeB) = (_modeB, _modeA);
            _state.FrequencyA = _freqA; _state.FrequencyB = _freqB;
            _state.ModeA = _modeA; _state.ModeB = _modeB;
            return Task.CompletedTask;
        }

        public Task EqualizeVfosAsync(CancellationToken cancellationToken = default)
        {
            if (_state.ActiveVfo == 1) { _freqA = _freqB; _modeA = _modeB; }
            else                       { _freqB = _freqA; _modeB = _modeA; }
            _state.FrequencyA = _freqA; _state.FrequencyB = _freqB;
            _state.ModeA = _modeA; _state.ModeB = _modeB;
            return Task.CompletedTask;
        }

        public Task<bool> GetSplitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_split);

        public Task SetSplitAsync(bool on, CancellationToken cancellationToken = default)
        {
            _split = on;
            _state.SplitMode = on ? 1 : 0;
            return Task.CompletedTask;
        }

        // -- Hosted canned-data feeder -----------------------------------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "[StubRadioController] Phase-1 stub active — no radio hardware. Feeding canned gauge data.");

            // Present as a connected, powered radio so the UI leaves its
            // "connecting…" overlay and renders the panels.
            await ConnectAsync(stoppingToken);
            _state.IsConnected = true;
            _state.RadioPowerOn = true;
            _state.FrequencyA = _freqA;
            _state.FrequencyB = _freqB;
            _state.ModeA = _modeA;
            _state.ModeB = _modeB;

            AppStatus.InitializationStatus = "complete";
            try
            {
                await _hubContext.Clients.All.SendAsync("InitializationStatus", "complete", stoppingToken);
            }
            catch { /* no clients connected yet — the value is polled via /api/cat/status/init too */ }

            // Slow sine so the S-meter needle drifts instead of sitting dead —
            // enough to see the whole SignalR→calibration→gauge path move.
            double phase = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                phase += 0.15;
                int sMeter = (int)(120 + 60 * Math.Sin(phase)); // ~S5–S9 sweep
                _state.SMeterA = sMeter;
                if (_state.IsSingleReceiver)
                    _state.SMeterB = sMeter;
                else
                    _state.SMeterB = (int)(120 + 60 * Math.Sin(phase + 1.7));

                // Not transmitting in the idle stub → TX-only meters read zero.
                _state.PowerMeter = 0;
                _state.SWRMeter = 0;
                _state.ALCMeter = 0;
                _state.CompressionMeter = 0;

                // Slowly varying environmentals so those gauges aren't frozen.
                _state.Temperature = 40 + (int)(10 * Math.Sin(phase / 4));
                _state.VDDMeter = 130;
                _state.IDDMeter = 0;

                try { await Task.Delay(500, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
