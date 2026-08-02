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

        // -- Antenna tuner (CI-V 1C 01, canned) --------------------------------

        private int _tuner; // 0=OFF, 1=ON, 2=TUNING

        public Task<int> GetTunerAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_tuner);

        public Task SetTunerAsync(int state, CancellationToken cancellationToken = default)
        {
            if (state < 0 || state > 2) return Task.CompletedTask;
            _tuner = state;
            _state.AtuEnabled = state != 0;
            _state.AtuTuning = state == 2;
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

        private int _ifWidthHz = 2400, _selectedFilter = 1;   // canned SSB-ish defaults
        public Task<int> GetIfFilterWidthHzAsync(RadioVfo vfo, CancellationToken ct = default) => Task.FromResult(_ifWidthHz);
        public Task SetIfFilterWidthHzAsync(RadioVfo vfo, int hz, CancellationToken ct = default)
        {
            _ifWidthHz = Math.Clamp(hz, 50, 10000);
            if (vfo == RadioVfo.A) _state.IfWidthA = _ifWidthHz.ToString(); else _state.IfWidthB = _ifWidthHz.ToString();
            return Task.CompletedTask;
        }
        public Task<int> GetSelectedFilterAsync(RadioVfo vfo, CancellationToken ct = default) => Task.FromResult(_selectedFilter);
        public Task SetSelectedFilterAsync(RadioVfo vfo, int fil, CancellationToken ct = default)
        {
            _selectedFilter = Math.Clamp(fil, 1, 3);
            if (vfo == RadioVfo.A) _state.SelectedFilterA = _selectedFilter.ToString(); else _state.SelectedFilterB = _selectedFilter.ToString();
            return Task.CompletedTask;
        }

        public Task<bool> GetAttenuatorAsync(CancellationToken ct = default) => Task.FromResult(_att);
        public Task SetAttenuatorAsync(bool on, CancellationToken ct = default) { _att = on; _state.AttA = on ? "20" : "00"; _state.AttB = _state.AttA; return Task.CompletedTask; }

        // -- RX Tone Control HPF/LPF (CI-V 1A 05, canned) ----------------------

        private int _rxHpfHz = 100;   // 0 = Through
        private int _rxLpfHz = 2400;  // 0 = Through
        public Task<(int hpfHz, int lpfHz)> GetRxFilterAsync(RadioVfo vfo, CancellationToken ct = default)
            => Task.FromResult((_rxHpfHz, _rxLpfHz));
        public Task SetRxFilterAsync(RadioVfo vfo, int hpfHz, int lpfHz, CancellationToken ct = default)
        {
            _rxHpfHz = hpfHz <= 0 ? 0 : Math.Clamp(hpfHz, 100, 2000);
            _rxLpfHz = lpfHz <= 0 ? 0 : Math.Clamp(lpfHz, 500, 2400);
            return Task.CompletedTask;
        }

        // -- CW keyer (CI-V 17 / 14 0C / 14 09 / 14 0F / 16 47, canned) --------

        private int _cwSpeedWpm = 20;
        private int _cwPitchHz = 600;
        private double _cwDelayDots = 3.0;
        private int _cwBreakIn = 0; // 0=OFF, 1=SEMI, 2=FULL
        public Task<string> SendCwMessageAsync(string message, CancellationToken ct = default)
            => Task.FromResult(message ?? "");
        public Task StopCwAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetCwSpeedWpmAsync(CancellationToken ct = default) => Task.FromResult(_cwSpeedWpm);
        public Task SetCwSpeedWpmAsync(int wpm, CancellationToken ct = default) { _cwSpeedWpm = Math.Clamp(wpm, 6, 48); _state.CwSpeed = _cwSpeedWpm; return Task.CompletedTask; }
        public Task<int> GetCwPitchHzAsync(CancellationToken ct = default) => Task.FromResult(_cwPitchHz);
        public Task SetCwPitchHzAsync(int hz, CancellationToken ct = default) { _cwPitchHz = Math.Clamp(hz, 300, 900); return Task.CompletedTask; }
        public Task<double> GetCwBreakInDelayDotsAsync(CancellationToken ct = default) => Task.FromResult(_cwDelayDots);
        public Task SetCwBreakInDelayDotsAsync(double dots, CancellationToken ct = default) { _cwDelayDots = Math.Clamp(dots, 2.0, 13.0); return Task.CompletedTask; }
        public Task<int> GetCwBreakInAsync(CancellationToken ct = default) => Task.FromResult(_cwBreakIn);
        public Task SetCwBreakInAsync(int mode, CancellationToken ct = default) { _cwBreakIn = Math.Clamp(mode, 0, 2); _state.CwBreakIn = _cwBreakIn.ToString(); return Task.CompletedTask; }

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

        // Scope mode (CI-V 27 14, canned) — the fake sweep is always "Center",
        // so just remember the request; the emitted frames stay centred.
        private bool _scopeCenter = true;
        private volatile bool _scopeEnabled = true;

        public Task SetScopeModeAsync(bool center, CancellationToken cancellationToken = default)
        {
            _scopeCenter = center;
            return Task.CompletedTask;
        }

        public Task SetScopeEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            _scopeEnabled = enabled;
            return Task.CompletedTask;
        }

        // Watch-panel crop span (Phase 5 "ZoomIn" mode) — display-only; the stub
        // has no real crop path, so just remember the value.
        private int _watchCropHalfHz;

        public Task SetWatchCropSpanAsync(int halfHz, CancellationToken cancellationToken = default)
        {
            _watchCropHalfHz = System.Math.Max(0, halfHz);
            return Task.CompletedTask;
        }

        // -- Power on/off (Phase 3 block 7, canned) ----------------------------

        public Task SetPowerAsync(bool on, CancellationToken cancellationToken = default)
        {
            _state.RadioPowerOn = on;
            return Task.CompletedTask;
        }

        // -- Radio memory channels (canned; no hardware) -----------------------
        // A tiny fake bank so the Memories import/export flow can be exercised
        // without a radio: channels 1–3 are programmed, the rest report empty.

        public Task<RadioMemoryChannel?> ReadMemoryChannelAsync(int channel, CancellationToken cancellationToken = default)
        {
            RadioMemoryChannel ch = channel switch
            {
                1 => new() { Channel = 1, FrequencyHz = 14_074_000, Mode = "USB", Name = "20m FT8" },
                2 => new() { Channel = 2, FrequencyHz =  7_074_000, Mode = "USB", Name = "40m FT8" },
                3 => new() { Channel = 3, FrequencyHz =  3_573_000, Mode = "USB", Name = "80m FT8" },
                _ => new() { Channel = channel, IsEmpty = true },
            };
            return Task.FromResult<RadioMemoryChannel?>(ch);
        }

        public Task<bool> WriteMemoryChannelAsync(RadioMemoryChannel memory, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> ClearMemoryChannelAsync(int channel, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        // -- Canned spectrum (Phase 5 dual-panel demo) -------------------------
        //
        // The real CivRadioController reassembles CI-V 27 00 scope frames and
        // broadcasts SpectrumUpdate {sdrId, bins, centreHz, spanHz}. Here we
        // fake the same envelope for BOTH VFOs so the pseudo-dual two-panel UI
        // can be developed/demoed without a radio. bins are dBFS values matching
        // CivScopeAssembler's contract (~-120..0). The frontend gates panel B on
        // the pseudo-dual setting, so emitting B here is harmless when it's off.
        private const int ScopeBinCount = 475;
        private readonly Random _rng = new();
        private readonly float[] _binsA = new float[ScopeBinCount];
        private readonly float[] _binsB = new float[ScopeBinCount];
        private int _scopeFrame;

        // See CivRadioController: a browser that connects mid-stream needs an
        // SdrStatus before its spectrum panel is revealed, and the frame counter
        // runs from app start rather than from connect.
        private int _announceScopeStatus;

        /// <inheritdoc />
        public void RequestScopeStatusAnnounce() => Interlocked.Exchange(ref _announceScopeStatus, 1);

        /// <summary>
        /// Fill <paramref name="bins"/> with a canned noise floor plus a couple of
        /// slowly-drifting Gaussian "signal" humps, so the trace and waterfall
        /// actually move. <paramref name="phase"/> drives the drift.
        /// </summary>
        private void FillCannedSpectrum(float[] bins, double phase)
        {
            const float floorDb = -102f;
            // Two drifting signals at different rates so the two panels look alive
            // and distinct. Centres wander across the middle of the span.
            double c1 = ScopeBinCount * (0.50 + 0.18 * Math.Sin(phase * 0.7));
            double c2 = ScopeBinCount * (0.35 + 0.10 * Math.Sin(phase * 1.3 + 1.0));
            for (int i = 0; i < bins.Length; i++)
            {
                float noise = floorDb + (float)(_rng.NextDouble() * 4.0);
                float s1 = 55f * (float)Math.Exp(-Math.Pow(i - c1, 2) / (2 * 6.0 * 6.0));
                float s2 = 38f * (float)Math.Exp(-Math.Pow(i - c2, 2) / (2 * 4.0 * 4.0));
                bins[i] = Math.Min(0f, noise + s1 + s2);
            }
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

                // Canned dual-panel spectrum (Phase 5). Feed A always and B too
                // (frontend hides B unless pseudo-dual is on). Re-assert streaming
                // status periodically so late-connecting clients pick it up.
                if (_scopeEnabled)
                {
                    FillCannedSpectrum(_binsA, phase);
                    FillCannedSpectrum(_binsB, phase * 1.1 + 0.5);
                    string mode = _scopeCenter ? "CENT" : "FIX";
                    try
                    {
                        await _hubContext.Clients.All.SendAsync("SpectrumUpdate",
                            new { sdrId = "A", bins = _binsA, centreHz = _freqA, spanHz = _scopeSpanHz * 2, mode }, stoppingToken);
                        await _hubContext.Clients.All.SendAsync("SpectrumUpdate",
                            new { sdrId = "B", bins = _binsB, centreHz = _freqB, spanHz = _scopeSpanHz * 2, mode }, stoppingToken);
                        bool requested = Interlocked.Exchange(ref _announceScopeStatus, 0) == 1;
                        if (_scopeFrame++ % 6 == 0 || requested)
                        {
                            await _hubContext.Clients.All.SendAsync("SdrStatus", new { sdrId = "A", status = "streaming" }, stoppingToken);
                            await _hubContext.Clients.All.SendAsync("SdrStatus", new { sdrId = "B", status = "streaming" }, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* no clients / hub busy — ignore in the stub */ }
                }

                try { await Task.Delay(500, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
