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

        // -- RF output power set (CI-V 14 0A, canned) --------------------------

        private int _rfPowerPercent = 100; // stub starts at full power

        public Task<int> GetRfPowerPercentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_rfPowerPercent);

        public Task SetRfPowerPercentAsync(int percent, CancellationToken cancellationToken = default)
        {
            _rfPowerPercent = Math.Clamp(percent, 0, 100);
            _state.Power = _rfPowerPercent;
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

        // -- AF (volume) level (CI-V 14 01, canned) ----------------------------

        private int _afGain = 128; // mid volume

        public Task<int> GetAfGainAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_afGain);

        public Task SetAfGainAsync(int value, CancellationToken cancellationToken = default)
        {
            _afGain = Math.Clamp(value, 0, 255);
            _state.AfGainA = _afGain;
            _state.AfGainB = _afGain;
            return Task.CompletedTask;
        }

        // -- RX controls (receiver-wide, canned) -------------------------------

        private int _rfGain = 255, _squelch = 0, _nrLevel = 128, _nbLevel = 128, _notchPos = 128;
        private int _preamp = 0, _agc = 2, _manualNotchWidth = 1, _ifShape = 0; // 2 = MID; width 1 = MID; shape 0 = SHARP
        private bool _nb, _nr, _autoNotch, _manualNotch, _att;

        public Task<int> GetRfGainAsync(CancellationToken ct = default) => Task.FromResult(_rfGain);
        public Task SetRfGainAsync(int value, CancellationToken ct = default) { _rfGain = Math.Clamp(value, 0, 255); _state.RfGainA = _rfGain; _state.RfGainB = _rfGain; return Task.CompletedTask; }
        public Task<int> GetSquelchAsync(CancellationToken ct = default) => Task.FromResult(_squelch);
        public Task SetSquelchAsync(int value, CancellationToken ct = default) { _squelch = Math.Clamp(value, 0, 255); _state.SquelchA = _squelch; _state.SquelchB = _squelch; return Task.CompletedTask; }
        public Task<int> GetNrLevelAsync(CancellationToken ct = default) => Task.FromResult(_nrLevel);
        public Task SetNrLevelAsync(int value, CancellationToken ct = default) { _nrLevel = Math.Clamp(value, 0, 255); _state.NrLevelA = _nrLevel; _state.NrLevelB = _nrLevel; return Task.CompletedTask; }
        public Task<int> GetNbLevelAsync(CancellationToken ct = default) => Task.FromResult(_nbLevel);
        public Task SetNbLevelAsync(int value, CancellationToken ct = default) { _nbLevel = Math.Clamp(value, 0, 255); _state.NbLevelA = _nbLevel; _state.NbLevelB = _nbLevel; return Task.CompletedTask; }
        public Task<int> GetNotchPositionAsync(CancellationToken ct = default) => Task.FromResult(_notchPos);
        public Task SetNotchPositionAsync(int value, CancellationToken ct = default) { _notchPos = Math.Clamp(value, 0, 255); _state.ManualNotchFreqA = _notchPos; _state.ManualNotchFreqB = _notchPos; return Task.CompletedTask; }

        public Task<int> GetPreampAsync(CancellationToken ct = default) => Task.FromResult(_preamp);
        public Task SetPreampAsync(int value, CancellationToken ct = default) { _preamp = Math.Clamp(value, 0, 2); _state.IpoA = _preamp.ToString(); _state.IpoB = _state.IpoA; return Task.CompletedTask; }
        public Task<int> GetAgcAsync(CancellationToken ct = default) => Task.FromResult(_agc);
        public Task SetAgcAsync(int value, CancellationToken ct = default) { _agc = Math.Clamp(value, 1, 3); _state.AgcA = _agc.ToString(); _state.AgcB = _state.AgcA; return Task.CompletedTask; }

        public Task<bool> GetNoiseBlankerAsync(CancellationToken ct = default) => Task.FromResult(_nb);
        public Task SetNoiseBlankerAsync(bool on, CancellationToken ct = default) { _nb = on; _state.NbA = on ? "1" : "0"; _state.NbB = _state.NbA; return Task.CompletedTask; }
        public Task<bool> GetNoiseReductionAsync(CancellationToken ct = default) => Task.FromResult(_nr);
        public Task SetNoiseReductionAsync(bool on, CancellationToken ct = default) { _nr = on; _state.NrA = on ? "1" : "0"; _state.NrB = _state.NrA; return Task.CompletedTask; }
        public Task<bool> GetAutoNotchAsync(CancellationToken ct = default) => Task.FromResult(_autoNotch);
        public Task SetAutoNotchAsync(bool on, CancellationToken ct = default) { _autoNotch = on; _state.AutoNotchA = on ? "1" : "0"; _state.AutoNotchB = _state.AutoNotchA; return Task.CompletedTask; }
        public Task<bool> GetManualNotchAsync(CancellationToken ct = default) => Task.FromResult(_manualNotch);
        public Task SetManualNotchAsync(bool on, CancellationToken ct = default) { _manualNotch = on; _state.ManualNotchA = on ? "1" : "0"; _state.ManualNotchB = _state.ManualNotchA; return Task.CompletedTask; }
        public Task<int> GetManualNotchWidthAsync(CancellationToken ct = default) => Task.FromResult(_manualNotchWidth);
        public Task SetManualNotchWidthAsync(int value, CancellationToken ct = default) { _manualNotchWidth = Math.Clamp(value, 0, 2); _state.ManualNotchWidthA = _manualNotchWidth.ToString(); _state.ManualNotchWidthB = _state.ManualNotchWidthA; return Task.CompletedTask; }
        public Task<int> GetIfFilterShapeAsync(CancellationToken ct = default) => Task.FromResult(_ifShape);
        public Task SetIfFilterShapeAsync(int value, CancellationToken ct = default) { _ifShape = Math.Clamp(value, 0, 1); _state.IfShapeA = _ifShape.ToString(); _state.IfShapeB = _state.IfShapeA; return Task.CompletedTask; }

        public Task<bool> GetAttenuatorAsync(CancellationToken ct = default) => Task.FromResult(_att);
        public Task SetAttenuatorAsync(bool on, CancellationToken ct = default) { _att = on; _state.AttA = on ? "20" : "00"; _state.AttB = _state.AttA; return Task.CompletedTask; }

        // -- Twin PBT (CI-V 14 07 / 14 08, canned) -----------------------------

        private int _pbtInner = 128; // 128 = passband centre / no shift
        private int _pbtOuter = 128;

        public Task<int> GetPbtInnerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_pbtInner);

        public Task SetPbtInnerAsync(int value, CancellationToken cancellationToken = default)
        {
            _pbtInner = Math.Clamp(value, 0, 255);
            return Task.CompletedTask;
        }

        public Task<int> GetPbtOuterAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_pbtOuter);

        public Task SetPbtOuterAsync(int value, CancellationToken cancellationToken = default)
        {
            _pbtOuter = Math.Clamp(value, 0, 255);
            return Task.CompletedTask;
        }

        // -- Spectrum scope span (CI-V 27 15, canned) --------------------------

        private int _scopeSpanHz = 25000;

        public Task SetScopeSpanAsync(int spanHz, CancellationToken cancellationToken = default)
        {
            _scopeSpanHz = spanHz;
            return Task.CompletedTask;
        }

        // -- Power on/off (Phase 3 block 7, canned) ----------------------------

        public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default)
        {
            _state.RadioPowerOn = on;
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

                // IC-7300 has no temperature meter over CI-V; the PA rail idles
                // near 13.8 V (raw ≈ 157 on the Vd scale: raw 13=10 V, 241=16 V).
                _state.VDDMeter = 157;
                _state.IDDMeter = 0;

                try { await Task.Delay(500, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
