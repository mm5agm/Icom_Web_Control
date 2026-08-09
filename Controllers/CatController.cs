using Microsoft.AspNetCore.Mvc;
using Icom_Web_Control.Services;
using Icom_Web_Control.Models;
using System.Text.Json;
using System.Threading;

namespace Icom_Web_Control.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CatController : ControllerBase
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger<CatController> _logger;
        private readonly RadioStateService _radioStateService;
        private readonly RadioStatePersistenceService _statePersistence;
        // The semantic seam. Every command in this controller goes through it;
        // nothing here knows a CI-V byte from a Yaesu one.
        private readonly IRadioController _radio;
        private static readonly SemaphoreSlim _requestSemaphore = new(1, 1);

        /// <summary>
        /// Returns true if the per-VFO state write should target *B (vs *A)
        /// for a user-clicked receiver. Delegates to
        /// <see cref="RadioCapabilities.VfoIsB"/>: the IC-7300 has one
        /// receiver, so the change lands on whichever VFO is active and the
        /// clicked panel is a hint, not an addressable target.
        /// </summary>
        private bool VfoIsB(string receiver) =>
            RadioCapabilities.VfoIsB(_radioStateService.IsSingleReceiver, _radioStateService.ActiveVfo, receiver);

        // AF (volume) level is receiver-wide on the IC-7300 (CI-V 14 01), so
        // both the A and B sliders drive the one level via the _radio seam.
        // Both state fields are mirrored so either panel reflects the change.
        [HttpPost("afgain/a")]
        public Task<IActionResult> SetAfGainA([FromBody] int value) => SetAfGainCore(value, "A");

        [HttpPost("afgain/b")]
        public Task<IActionResult> SetAfGainB([FromBody] int value) => SetAfGainCore(value, "B");

        private async Task<IActionResult> SetAfGainCore(int value, string receiver)
        {
            if (value < 0 || value > 255)
                return BadRequest(new { error = "AF Gain value out of range (0-255)" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetAfGainAsync(value, CancellationToken.None);
            _radioStateService.AfGainA = value;
            _radioStateService.AfGainB = value;
            return Ok(new { message = $"AF Gain {value} set for Receiver {receiver}" });
        }

        // The spectrum scope is receiver-wide on the IC-7300 (one receiver), so
        // either panel's span buttons drive the single 27 15 span. The body is
        // the SPAN ± half-width in Hz; the displayed full width is twice it.
        private static readonly int[] AllowedScopeSpansHz =
            { 2500, 5000, 10000, 25000, 50000, 100000, 250000, 500000 };

        [HttpPost("scopespan/{receiver}")]
        public async Task<IActionResult> SetScopeSpan(string receiver, [FromBody] int hz)
        {
            if (Array.IndexOf(AllowedScopeSpansHz, hz) < 0)
                return BadRequest(new { error = $"Scope span {hz} Hz not one of ±2.5k…±500k" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetScopeSpanAsync(hz, CancellationToken.None);
            return Ok(new { message = $"Scope span ±{hz} Hz set (Receiver {receiver})" });
        }

        // Set the physical scope mode (CI-V 27 14) from the click-the-badge
        // control on the spectrum panel. {mode} = "center" or "fixed". The
        // IC-7300 has one Main scope, so the receiver isn't relevant — both
        // panels reflect the same physical mode.
        [HttpPost("scopemode/{mode}")]
        public async Task<IActionResult> SetScopeMode(string mode)
        {
            bool center = string.Equals(mode, "center", StringComparison.OrdinalIgnoreCase);
            bool fixedMode = string.Equals(mode, "fixed", StringComparison.OrdinalIgnoreCase);
            if (!center && !fixedMode)
                return BadRequest(new { error = $"Scope mode '{mode}' must be 'center' or 'fixed'" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetScopeModeAsync(center, CancellationToken.None);
            return Ok(new { message = $"Scope mode {(center ? "Center" : "Fixed")} set" });
        }

        // Turn the spectrum scope on or off (CI-V 27 10 / 27 11). {state} = "on"
        // or "off". Off stops the radio streaming 27 00 frames — the web spectrum
        // goes quiet — and doubles as a way to test whether the scope stream is
        // the source of receiver noise.
        [HttpPost("scope/{state}")]
        public async Task<IActionResult> SetScopeEnabled(string state)
        {
            bool on = string.Equals(state, "on", StringComparison.OrdinalIgnoreCase);
            bool off = string.Equals(state, "off", StringComparison.OrdinalIgnoreCase);
            if (!on && !off)
                return BadRequest(new { error = $"Scope state '{state}' must be 'on' or 'off'" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetScopeEnabledAsync(on, CancellationToken.None);
            return Ok(new { message = $"Scope {(on ? "on" : "off")}" });
        }

        // Pseudo-dual "ZoomIn" span mode: narrow the watch panel's crop of the
        // single sweep around the watch VFO. Display-only — no CI-V, no effect on
        // the primary panel or the physical scope; the controller just crops fewer
        // bins on the next sweep. 0 = auto (widest crop that fits).
        [HttpPost("watchspan/{receiver}")]
        public async Task<IActionResult> SetWatchSpan(string receiver, [FromBody] int hz)
        {
            if (hz != 0 && Array.IndexOf(AllowedScopeSpansHz, hz) < 0)
                return BadRequest(new { error = $"Watch span {hz} Hz not one of ±2.5k…±500k (or 0 for auto)" });

            await _radio.SetWatchCropSpanAsync(hz, CancellationToken.None);
            return Ok(new { message = $"Watch crop ±{hz} Hz set (Receiver {receiver})" });
        }

        // Persist the four per-panel spectrum display sliders (Low/High dB range,
        // waterfall Speed, waterfall Brightness) so they survive across sessions
        // AND browsers/devices. Display-only — no CI-V. Read-modify-write into
        // appsettings.user.json via ISettingsService. Any field may be omitted;
        // only the supplied fields are updated. receiver = "A" or "B".
        [HttpPost("spectrumdisplay/{receiver}")]
        public async Task<IActionResult> SetSpectrumDisplay(string receiver, [FromBody] SpectrumDisplayRequest req)
        {
            bool isB = string.Equals(receiver, "B", StringComparison.OrdinalIgnoreCase);
            var s = await _settingsService.GetSettingsAsync();

            if (req.Low is float low)
            {
                low = Math.Clamp(low, -160f, -20f);
                if (isB) s.SdrSpectrumLowDbB = low; else s.SdrSpectrumLowDbA = low;
            }
            if (req.High is float high)
            {
                high = Math.Clamp(high, -100f, 20f);
                if (isB) s.SdrSpectrumHighDbB = high; else s.SdrSpectrumHighDbA = high;
            }
            if (req.Speed is int speed)
            {
                speed = Math.Clamp(speed, 1, 128);
                if (isB) s.SdrWaterfallSpeedB = speed; else s.SdrWaterfallSpeedA = speed;
            }
            if (req.Bright is int bright)
            {
                bright = Math.Clamp(bright, 0, 60);
                if (isB) s.SdrWaterfallBrightDbB = bright; else s.SdrWaterfallBrightDbA = bright;
            }

            await _settingsService.SaveSettingsAsync(s);
            return Ok(new { message = $"Spectrum display persisted (Receiver {receiver})" });
        }

        [HttpPost("micgain")]
        public async Task<IActionResult> SetMicGain([FromBody] MicGainRequest request)
        {
            if (request.Value < 0 || request.Value > 100)
                return BadRequest(new { error = "MIC Gain value out of range (0-100)" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetMicGainPercentAsync(request.Value, CancellationToken.None);
            _radioStateService.MicGain = request.Value;
            return Ok(new { message = $"MIC Gain set to {request.Value}" });
        }

        // "PROC" is the app's inherited name for the speech compressor (CI-V
        // 16 44). The IC-7300 has no separate parametric-mic-EQ switch sharing
        // the command, so unlike the Yaesu PR command there is no sub-selector
        // to get wrong here.
        [HttpPost("proc")]
        public async Task<IActionResult> SetProc([FromBody] ProcRequest request)
        {
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetSpeechCompressorAsync(request.Enabled, CancellationToken.None);
            _radioStateService.ProcEnabled = request.Enabled;
            return Ok(new { message = $"PROC {(request.Enabled ? "ON" : "OFF")}" });
        }

        // The slider is 0-100 %. On the radio's own screen the compressor level
        // reads 0-10, so 50 % here shows as 5 there — the wire range (0000-0255)
        // is what both are scaled from.
        [HttpPost("proclevel")]
        public async Task<IActionResult> SetProcLevel([FromBody] ProcLevelRequest request)
        {
            if (request.Value < 0 || request.Value > 100)
                return BadRequest(new { error = "PROC level out of range (0-100)" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetCompressorLevelPercentAsync(request.Value, CancellationToken.None);
            _radioStateService.ProcLevel = request.Value;
            return Ok(new { message = $"PROC level set to {request.Value}", actual = request.Value });
        }

        [HttpPost("radiopower")]
        public async Task<IActionResult> SetRadioPower([FromBody] RadioPowerRequest request)
        {
            _logger.LogInformation("[API] SetRadioPower called: powerOn={PowerOn}", request.PowerOn);
            try
            {
                // Power via the CI-V seam (command 18). Power-OFF needs a live
                // link (the radio must be listening to receive 18 00); power-ON
                // wakes the bus with the FE preamble and, over USB, only succeeds
                // if the port is still powered. After power-ON the CI-V poll loop
                // reconnects and re-identifies on its own — no explicit re-init.
                if (!request.PowerOn && !_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                await _radio.SetPowerAsync(request.PowerOn, CancellationToken.None);
                _radioStateService.RadioPowerOn = request.PowerOn;
                if (!request.PowerOn)
                    AppStatus.InitializationStatus = "radio_off";

                _logger.LogInformation("[API] SetRadioPower completed: powerOn={PowerOn}", request.PowerOn);
                return Ok(new
                {
                    message = request.PowerOn ? "Radio powered ON" : "Radio powered OFF",
                    powerOn = request.PowerOn
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting radio power");
                return StatusCode(500, new { error = "Failed to set radio power" });
            }
        }

        [HttpGet("radiopower")]
        public IActionResult GetRadioPowerStatus()
        {
            return Ok(new { powerOn = _radioStateService.RadioPowerOn });
        }

        [HttpPost("tx")]
        public async Task<IActionResult> ToggleTransmit([FromBody] TxRequest request)
        {
            _logger.LogInformation("[API] ToggleTransmit called: transmit={Transmit}", request.Transmit);
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Software PTT via the CI-V seam (command 1C 00). SetTransmitAsync
                // commits _radioStateService.IsTransmitting only on the radio's
                // ACK, so we don't set it optimistically here.
                await _radio.SetTransmitAsync(request.Transmit, CancellationToken.None);
                _logger.LogInformation("[API] ToggleTransmit completed: transmitting={Transmit}", request.Transmit);
                return Ok(new
                {
                    message = request.Transmit ? "TX ON" : "TX OFF",
                    transmitting = _radioStateService.IsTransmitting
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling TX");
                return StatusCode(500, new { error = "Failed to toggle TX" });
            }
        }

        [HttpGet("tx")]
        public IActionResult GetTxStatus()
        {
            return Ok(new { 
                transmitting = _radioStateService.IsTransmitting,
                txVfo = _radioStateService.TxVfo
            });
        }

        // Static band frequency mapping (apply this at the top of your class)
        private static readonly Dictionary<string, long> BandFreqs = new(StringComparer.OrdinalIgnoreCase)
        {
            { "160m", 1840000 }, { "80m", 3700000 }, { "60m", 5357000 },
            { "40m", 7100000 }, { "30m", 10136000 }, { "20m", 14074000 },
            { "17m", 18110000 }, { "15m", 21074000 }, { "12m", 24915000 },
            { "10m", 28074000 }, { "6m", 50313000 }, { "4m", 70100000 }
        };

        private static readonly Dictionary<string, string> CatCodeToMode = new()
        {
            { "1", "LSB" },
            { "2", "USB" },
            { "3", "CW-U" },
            { "4", "FM" },
            { "5", "AM" },
            { "6", "RTTY-L" },
            { "7", "CW-L" },
            { "8", "DATA-L" },
            { "9", "RTTY-U" },
            { "A", "DATA-FM" },
            { "B", "FM-N" },
            { "C", "DATA-U" },
            { "D", "AM-N" },
            { "E", "PSK" },
            { "F", "DATA-FM-N" }
        };

        public CatController(
            ISettingsService settingsService,
            ILogger<CatController> logger,
            RadioStateService radioStateService,
            RadioStatePersistenceService statePersistence,
            IRadioController radio)
        {
            _settingsService = settingsService;
            _logger = logger;
            _radioStateService = radioStateService;
            _statePersistence = statePersistence;
            _radio = radio;
        }

        // No EnsureConnectedAsync here. The serial port belongs to
        // CivBusService and the connect/reconnect loop to CivRadioController,
        // both hosted services — a request handler poking the port would be
        // racing them for the one 19200-baud bus. If the radio is down,
        // IsConnected says so and the endpoints return 503.

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            // Log what we're returning for debugging
            _logger.LogInformation("[API] GetStatus called");
            _logger.LogInformation("[API Status] Returning: FreqA={FreqA}, BandA={BandA}, FreqB={FreqB}, BandB={BandB}",
                _radioStateService.FrequencyA, _radioStateService.BandA,
                _radioStateService.FrequencyB, _radioStateService.BandB);

            var settings = await _settingsService.GetSettingsAsync();
            return Ok(new
            {
                isConnected = _radioStateService.IsConnected,
                radioModel = settings.RadioModel,
                vfoA = new
                {
                    frequency = _radioStateService.FrequencyA,
                    band = _radioStateService.BandA,
                    sMeter = _radioStateService.SMeterA ?? 0,
                    power = _radioStateService.PowerMeter ?? 0,
                    mode = _radioStateService.ModeA ?? "",
                    antenna = _radioStateService.AntennaA ?? "",
                    afGain = _radioStateService.AfGainA,
                    ifWidth = _radioStateService.IfWidthA ?? ""
                },
                vfoB = new
                {
                    frequency = _radioStateService.FrequencyB,
                    band = _radioStateService.BandB,
                    sMeter = _radioStateService.SMeterB ?? 0,
                    mode = _radioStateService.ModeB ?? "",
                    antenna = _radioStateService.AntennaB ?? "",
                    afGain = _radioStateService.AfGainB,
                    ifWidth = _radioStateService.IfWidthB ?? ""
                },
                micGain = _radioStateService.MicGain,
                powerMeter = _radioStateService.PowerMeter ?? 0,
                compressionMeter = _radioStateService.CompressionMeter ?? 0,
                swrMeter = _radioStateService.SWRMeter ?? 0,
                alcMeter = _radioStateService.ALCMeter ?? 0,
                iddMeter = _radioStateService.IDDMeter ?? 0,
                vddMeter = _radioStateService.VDDMeter ?? 0,
                temperature = _radioStateService.Temperature ?? 0
            });
        }

        [HttpGet("status/init")]
        public IActionResult GetInitStatus()
        {
            return Ok(new { status = AppStatus.InitializationStatus });
        }

        [HttpPost("frequency/a")]
        public async Task<IActionResult> SetFrequencyA([FromBody] FrequencyRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetFrequencyA called: freq={Freq}", request.FrequencyHz);
            try
            {
                var freq = request.FrequencyHz;
                if (freq < 30000 || freq > 75000000)
                    return BadRequest(new { error = "Frequency out of range" });

                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Phase 3: set frequency via the CI-V seam (command 05). The
                // seam updates RadioStateService on the radio's ACK; the ~3 Hz
                // poll then confirms the value the radio actually landed on.
                await _radio.SetFrequencyHzAsync(RadioVfo.A, freq, CancellationToken.None);

                _logger.LogInformation("Set Receiver A frequency to {Freq}", freq);
                _logger.LogInformation("[API] SetFrequencyA completed: freq={Freq}", freq);
                return Ok(new { message = $"Frequency {freq} Hz set for Receiver A" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver A frequency");
                return StatusCode(500, new { error = "Failed to set frequency" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("frequency/b")]
        public async Task<IActionResult> SetFrequencyB([FromBody] FrequencyRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetFrequencyB called: freq={Freq}", request.FrequencyHz);
            try
            {
                var freq = request.FrequencyHz;
                if (freq < 30000 || freq > 75000000)
                    return BadRequest(new { error = "Frequency out of range" });

                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Phase 3: set frequency via the CI-V seam (command 05).
                await _radio.SetFrequencyHzAsync(RadioVfo.B, freq, CancellationToken.None);

                _logger.LogInformation("Set Receiver B frequency to {Freq}", freq);
                _logger.LogInformation("[API] SetFrequencyB completed: freq={Freq}", freq);
                return Ok(new { message = $"Frequency {freq} Hz set for Receiver B" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver B frequency");
                return StatusCode(500, new { error = "Failed to set frequency" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("band/a")]
        public Task<IActionResult> SetBandA([FromBody] BandRequest request)
            => SetBandAsync(RadioVfo.A, request.Band);

        [HttpPost("band/b")]
        public Task<IActionResult> SetBandB([FromBody] BandRequest request)
            => SetBandAsync(RadioVfo.B, request.Band);

        // Band select via the CI-V seam, with a per-band "stacking register":
        // leaving a band remembers where we were on it (freq + mode); returning
        // to a band restores that spot. First-ever visit uses the band default
        // and leaves the current mode untouched. The inherited Yaesu IF-width /
        // IF-shift / antenna restore is dropped — none of it exists on the
        // direct-sampling, single-antenna IC-7300.
        private async Task<IActionResult> SetBandAsync(RadioVfo vfo, string band)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            var recv = vfo == RadioVfo.B ? "B" : "A";
            _logger.LogInformation("[API] SetBand{Recv} called: band={Band}", recv, band);
            try
            {
                if (string.IsNullOrWhiteSpace(band) || !BandFreqs.TryGetValue(band, out var defaultFreq))
                    return BadRequest(new { error = "Invalid band" });

                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                var settings = await _settingsService.GetSettingsAsync();
                var profiles = vfo == RadioVfo.B ? settings.BandProfilesB : settings.BandProfilesA;

                // Stack the band we're leaving (only if we know where we were).
                // "Unknown" is a real answer now that band names are resolved
                // against the operator's own region — a UK operator sitting on
                // 3.9 MHz is outside the Region 1 allocation — and it is not a
                // band we should be stacking a profile against.
                var oldBand = vfo == RadioVfo.B ? _radioStateService.BandB : _radioStateService.BandA;
                var curFreq = vfo == RadioVfo.B ? _radioStateService.FrequencyB : _radioStateService.FrequencyA;
                var curMode = vfo == RadioVfo.B ? _radioStateService.ModeB : _radioStateService.ModeA;
                if (!string.IsNullOrEmpty(oldBand) && oldBand != BandPlanService.UnknownBand && curFreq > 0)
                {
                    profiles[oldBand] = new BandProfile
                    {
                        FrequencyHz = curFreq,
                        Mode        = curMode ?? ""
                    };
                }

                // Where to land: the remembered spot for this band, else the default.
                long targetFreq = defaultFreq;
                string? targetMode = null;
                if (profiles.TryGetValue(band, out var profile) && profile.FrequencyHz > 0)
                {
                    targetFreq = profile.FrequencyHz;
                    targetMode = string.IsNullOrEmpty(profile.Mode) ? null : profile.Mode;
                }

                await _settingsService.SaveSettingsAsync(settings);

                await _radio.SetFrequencyHzAsync(vfo, targetFreq, CancellationToken.None);
                if (targetMode != null)
                    await _radio.SetModeAsync(vfo, targetMode, CancellationToken.None);

                _radioStateService.SetBand(recv, band);
                if (vfo == RadioVfo.B) _radioStateService.FrequencyB = targetFreq;
                else                   _radioStateService.FrequencyA = targetFreq;
                if (targetMode != null)
                {
                    if (vfo == RadioVfo.B) _radioStateService.ModeB = targetMode;
                    else                   _radioStateService.ModeA = targetMode;
                }

                _logger.LogInformation("[API] SetBand{Recv} completed: band={Band}, freq={Freq}, mode={Mode}",
                    recv, band, targetFreq, targetMode ?? "(unchanged)");
                return Ok(new { message = $"Band {band} selected", frequency = targetFreq });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver {Recv} band", recv);
                return StatusCode(500, new { error = "Failed to set band" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("mode/{receiver}")]
        public async Task<IActionResult> SetMode(string receiver, [FromBody] ModeRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                var recv = receiver.ToUpperInvariant();
                if (recv != "A" && recv != "B")
                    return BadRequest(new { error = "Invalid receiver specified" });

                // The web dropdown still posts the legacy CAT mode code (1..F);
                // map it to the display string the seam speaks (e.g. "2" → "USB").
                string displayMode = CatCodeToMode.TryGetValue(request.Mode, out var modeName) ? modeName : request.Mode;

                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Set mode via the CI-V seam (command 06). The seam updates
                // RadioStateService on the radio's ACK; the poll loop's
                // command-04 read then confirms it. Nothing is re-applied after
                // a mode change — the IC-7300 keeps its filter and APF settings
                // per mode itself, so re-sending them would only fight the radio.
                await _radio.SetModeAsync(VfoIsB(recv) ? RadioVfo.B : RadioVfo.A, displayMode, CancellationToken.None);

                _logger.LogInformation("Set Receiver {Receiver} mode to {Mode}", recv, displayMode);
                return Ok(new { message = $"Mode {displayMode} selected for Receiver {receiver}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver {Receiver} mode", receiver);
                return StatusCode(500, new { error = "Failed to set mode" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("power/{receiver}")]
        public async Task<IActionResult> SetPower(string receiver, [FromBody] PowerRequest request)
        {
            _logger.LogInformation("[Slider][CAT] SetPower endpoint called: receiver={Receiver}, watts={Watts}", receiver, request.Watts);
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                // IC-7300 family is a 100 W radio; the slider's watts map 1:1 to
                // the radio's 0–100 % RF-power level (CI-V 14 0A).
                const int maxPower = 100;

                if (request.Watts < 5 || request.Watts > maxPower)
                    return BadRequest(new { error = $"Power out of range (5-{maxPower}W)" });

                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Set RF power via the CI-V seam (command 14 0A),
                // then read it back so the UI shows the level the radio landed on.
                int percent = request.Watts;
                await _radio.SetRfPowerPercentAsync(percent, CancellationToken.None);
                int readback = await _radio.GetRfPowerPercentAsync(CancellationToken.None);
                int actualPower = readback >= 0 ? readback : request.Watts;
                _radioStateService.Power = actualPower;

                _logger.LogInformation("[Slider][CAT] RF power set via CI-V: requested={Watts}W, readback={Actual}W", request.Watts, actualPower);
                return Ok(new { message = $"Power set to {actualPower}W", maxPower = maxPower });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting power");
                return StatusCode(500, new { error = "Failed to set power" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        public class BandRequest { public string Band { get; set; } = string.Empty; }
        public class ModeRequest { public string Mode { get; set; } = string.Empty; }
        public class FrequencyRequest { public long FrequencyHz { get; set; } }
        public class PowerRequest
        {
            public int Watts { get; set; }
        }

        public class MicGainRequest
        {
            public int Value { get; set; }
        }

        // Body for POST spectrumdisplay/{receiver}. All nullable so the client
        // can PATCH just the slider that moved; omitted fields are left as-is.
        public class SpectrumDisplayRequest
        {
            public float? Low { get; set; }
            public float? High { get; set; }
            public int? Speed { get; set; }
            public int? Bright { get; set; }
        }

        public class ProcRequest
        {
            public bool Enabled { get; set; }
        }

        public class ProcLevelRequest
        {
            public int Value { get; set; }
        }

        public class AfGainRequest
        {
            public string Band { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        public class RadioPowerRequest
        {
            public bool PowerOn { get; set; }
        }

        public class TxRequest
        {
            public bool Transmit { get; set; }
        }

        public class AgcRequest        { public string Code { get; set; } = string.Empty; }
        public class IpoRequest         { public string Code { get; set; } = string.Empty; }
        public class AutoNotchRequest   { public string Code { get; set; } = string.Empty; }
        public class NrRequest          { public string Code { get; set; } = string.Empty; }
        public class AttenuatorRequest  { public string Code { get; set; } = string.Empty; }
        public class ManualNotchRequest    { public string Enabled { get; set; } = "0"; }
        public class ManualNotchWidthRequest { public string Code { get; set; } = "1"; }
        public class IfShapeRequest        { public string Code { get; set; } = "0"; }
        public class NoiseBlankerRequest       { public string Enabled { get; set; } = "0"; }
        public class ManualNotchFreqRequest    { public int FrequencyHz { get; set; } = 1000; }

        [HttpPost("agc/{receiver}")]
        public async Task<IActionResult> SetAgc(string receiver, [FromBody] AgcRequest request)
        {
            // IC-7300 AGC time constant (CI-V 16 12): 1=FAST, 2=MID, 3=SLOW.
            var validCodes = new[] { "1", "2", "3" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid AGC code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetAgcAsync(int.Parse(request.Code), CancellationToken.None);
                _radioStateService.AgcA = request.Code;
                _radioStateService.AgcB = request.Code;
                return Ok(new { message = $"AGC set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting AGC");
                return StatusCode(500, new { error = "Failed to set AGC" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        // "ipo" is an inherited name, like sdrId on the spectrum hub — the route,
        // the DTO and the IpoA/IpoB state fields all predate the carve. What it
        // drives is the IC-7300's preamp, and the three positions line up with
        // what the Yaesu control offered, so only the label is wrong. Renaming it
        // would touch this endpoint, site.js, IntentDispatcher, RadioState and
        // the persisted radio_state.json for no behavioural gain.
        [HttpPost("ipo/{receiver}")]
        public async Task<IActionResult> SetIpo(string receiver, [FromBody] IpoRequest request)
        {
            // IC-7300 preamp (CI-V 16 02): 0=OFF, 1=P.AMP1, 2=P.AMP2.
            var validCodes = new[] { "0", "1", "2" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid preamp code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetPreampAsync(int.Parse(request.Code), CancellationToken.None);
                _radioStateService.IpoA = request.Code;
                _radioStateService.IpoB = request.Code;
                return Ok(new { message = $"Preamp set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting preamp");
                return StatusCode(500, new { error = "Failed to set preamp" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("autonotch/{receiver}")]
        public async Task<IActionResult> SetAutoNotch(string receiver, [FromBody] AutoNotchRequest request)
        {
            var validCodes = new[] { "0", "1" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid Auto Notch code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetAutoNotchAsync(request.Code == "1", CancellationToken.None);
                _radioStateService.AutoNotchA = request.Code;
                _radioStateService.AutoNotchB = request.Code;
                return Ok(new { message = $"Auto Notch set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Auto Notch");
                return StatusCode(500, new { error = "Failed to set Auto Notch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("nr/{receiver}")]
        public async Task<IActionResult> SetNr(string receiver, [FromBody] NrRequest request)
        {
            // IC-7300 noise reduction is a simple on/off (CI-V 16 40); depth is
            // the separate NR level (14 06).
            var validCodes = new[] { "0", "1" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid NR code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetNoiseReductionAsync(request.Code == "1", CancellationToken.None);
                _radioStateService.NrA = request.Code;
                _radioStateService.NrB = request.Code;
                return Ok(new { message = $"NR set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Noise Reduction");
                return StatusCode(500, new { error = "Failed to set Noise Reduction" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("attenuator/{receiver}")]
        public async Task<IActionResult> SetAttenuator(string receiver, [FromBody] AttenuatorRequest request)
        {
            // IC-7300 has a single 20 dB attenuator (CI-V 11): OFF ("00") or 20 dB ("20").
            var validCodes = new[] { "00", "20" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid attenuator code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetAttenuatorAsync(request.Code == "20", CancellationToken.None);
                _radioStateService.AttA = request.Code;
                _radioStateService.AttB = request.Code;
                return Ok(new { message = $"Attenuator set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Attenuator");
                return StatusCode(500, new { error = "Failed to set Attenuator" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("manualnotch/{receiver}")]
        public async Task<IActionResult> SetManualNotch(string receiver, [FromBody] ManualNotchRequest request)
        {
            var validValues = new[] { "0", "1" };
            if (!validValues.Contains(request.Enabled))
                return BadRequest(new { error = $"Invalid Manual Notch value: {request.Enabled}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetManualNotchAsync(request.Enabled == "1", CancellationToken.None);
                _radioStateService.ManualNotchA = request.Enabled;
                _radioStateService.ManualNotchB = request.Enabled;
                return Ok(new { message = $"Manual Notch set to {request.Enabled}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Manual Notch");
                return StatusCode(500, new { error = "Failed to set Manual Notch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("manualnotchfreq/{receiver}")]
        public async Task<IActionResult> SetManualNotchFreq(string receiver, [FromBody] ManualNotchFreqRequest request)
        {
            // IC-7300 manual-notch position (CI-V 14 0D) is a 0–255 value with
            // 128 = centre of the passband — not a Hz frequency. The request
            // field name is legacy; it now carries the 0–255 position.
            if (request.FrequencyHz < 0 || request.FrequencyHz > 255)
                return BadRequest(new { error = "Notch position must be 0–255" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetNotchPositionAsync(request.FrequencyHz, CancellationToken.None);
                _radioStateService.ManualNotchFreqA = request.FrequencyHz;
                _radioStateService.ManualNotchFreqB = request.FrequencyHz;
                return Ok(new { message = $"Manual Notch position set to {request.FrequencyHz}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Manual Notch position");
                return StatusCode(500, new { error = "Failed to set Manual Notch position" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("manualnotchwidth/{receiver}")]
        public async Task<IActionResult> SetManualNotchWidth(string receiver, [FromBody] ManualNotchWidthRequest request)
        {
            // IC-7300 manual-notch filter width (CI-V 16 57): 0=WIDE, 1=MID, 2=NAR.
            var validCodes = new[] { "0", "1", "2" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid Manual Notch width: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetManualNotchWidthAsync(int.Parse(request.Code), CancellationToken.None);
                _radioStateService.ManualNotchWidthA = request.Code;
                _radioStateService.ManualNotchWidthB = request.Code;
                return Ok(new { message = $"Manual Notch width set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Manual Notch width");
                return StatusCode(500, new { error = "Failed to set Manual Notch width" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("ifshape/{receiver}")]
        public async Task<IActionResult> SetIfShape(string receiver, [FromBody] IfShapeRequest request)
        {
            // IC-7300 IF DSP filter shape (CI-V 16 56): 0=SHARP, 1=SOFT.
            var validCodes = new[] { "0", "1" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid IF shape: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetIfFilterShapeAsync(int.Parse(request.Code), CancellationToken.None);
                _radioStateService.IfShapeA = request.Code;
                _radioStateService.IfShapeB = request.Code;
                return Ok(new { message = $"IF shape set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IF shape");
                return StatusCode(500, new { error = "Failed to set IF shape" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("noiseblanker/{receiver}")]
        public async Task<IActionResult> SetNoiseBlanker(string receiver, [FromBody] NoiseBlankerRequest request)
        {
            if (request.Enabled != "0" && request.Enabled != "1")
                return BadRequest(new { error = $"Invalid Noise Blanker value: {request.Enabled}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetNoiseBlankerAsync(request.Enabled == "1", CancellationToken.None);
                _radioStateService.NbA = request.Enabled;
                _radioStateService.NbB = request.Enabled;
                return Ok(new { message = $"Noise Blanker set to {request.Enabled}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Noise Blanker");
                return StatusCode(500, new { error = "Failed to set Noise Blanker" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // IF Width and IF Shift are not here either.
        //
        // IF Width lives at POST /api/radio/ifwidth/{a|b} (RadioController),
        // which speaks the IC-7300's filter-select command through the seam;
        // ic7300-if-width.js points window.setIfWidth at it, so this Yaesu SH
        // pair had no callers left.
        //
        // IF Shift has no IC-7300 counterpart at all — the radio splits the
        // passband edges into Twin PBT instead, at POST /api/cat/pbt.

        public class ApfRequest
        {
            /// <summary>0=OFF, 1=WIDE, 2=MID, 3=NAR — the four positions of CI-V 16 32.</summary>
            public int Width { get; set; }
        }

        public class ClarifierRequest
        {
            public string Vfo { get; set; } = "A";
            public bool RxOn { get; set; }
            public bool TxOn { get; set; }
            public int OffsetHz { get; set; }
        }

        public class ClarifierNudgeRequest
        {
            public string Vfo { get; set; } = "A";
            public int DeltaHz { get; set; }
        }

        // There is no contour endpoint. Contour is a Yaesu control — a movable
        // notch/peak in the audio passband — and the IC-7300 has nothing that
        // maps onto it. The nearest equivalents it does have (Twin PBT, the
        // manual notch) already have their own endpoints.

        /// <summary>
        /// APF (Audio Peak Filter), CI-V 16 32. CW only.
        /// </summary>
        /// <remarks>
        /// The radio has no APF frequency shift: the filter always sits on the
        /// CW pitch and the only choice is its width, with OFF as one of the
        /// four positions. So this takes a width, not an on/off plus an offset.
        /// The receiver segment is accepted and mirrored into both VFOs' state
        /// because the IC-7300 has one receiver — the setting is not per-VFO.
        /// </remarks>
        [HttpPost("apf/{receiver}")]
        public async Task<IActionResult> SetApf(string receiver, [FromBody] ApfRequest request)
        {
            if (request.Width is < 0 or > 3)
                return BadRequest(new { error = "APF width must be 0 (off), 1 (wide), 2 (mid) or 3 (narrow)" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetApfAsync(request.Width, CancellationToken.None);

            _radioStateService.ApfWidthA = request.Width;
            _radioStateService.ApfWidthB = request.Width;
            _radioStateService.ApfOnA = request.Width != 0;
            _radioStateService.ApfOnB = request.Width != 0;

            return Ok(new { message = $"APF set to {ApfWidthName(request.Width)}" });
        }

        private static string ApfWidthName(int width) => width switch
        {
            1 => "wide",
            2 => "mid",
            3 => "narrow",
            _ => "off"
        };

        // The "clarifier" controls drive the IC-7300's RIT and ΔTX (CI-V 21).
        // The UI wording stays because that is what the operator's radio
        // background calls it, and the voice grammar already uses it.
        //
        // One offset serves both: the radio has a single RIT frequency, applied
        // to RX when RIT is on and to TX when ΔTX is on, so the per-VFO request
        // field only decides which state slot the UI reads back. The offset is
        // stored in 10 Hz steps on the radio; SetRitOffsetHzAsync rounds.
        [HttpPost("clarifier")]
        public async Task<IActionResult> SetClarifier([FromBody] ClarifierRequest request)
        {
            if (request.OffsetHz < -9990 || request.OffsetHz > 9990)
                return BadRequest(new { error = "Clarifier offset must be -9990 to +9990 Hz" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            await _radio.SetRitOffsetHzAsync(request.OffsetHz, CancellationToken.None);
            await _radio.SetRitEnabledAsync(request.RxOn, CancellationToken.None);
            await _radio.SetDeltaTxEnabledAsync(request.TxOn, CancellationToken.None);

            // Mirror the rounding the seam applies rather than reading it back —
            // it is deterministic, and the scope shares this 19200 bus.
            int stored = (int)Math.Round(request.OffsetHz / 10.0) * 10;

            _radioStateService.ClarifierOffsetA = stored;
            _radioStateService.ClarifierOffsetB = stored;
            _radioStateService.RxClarOn = request.RxOn;
            _radioStateService.TxClarOn = request.TxOn;
            return Ok(new { message = "Clarifier updated", offsetHz = stored });
        }

        [HttpPost("clarifier/nudge")]
        public async Task<IActionResult> NudgeClarifier([FromBody] ClarifierNudgeRequest request)
        {
            int absHz = Math.Abs(request.DeltaHz);
            if (absHz == 0 || absHz > 9990)
                return BadRequest(new { error = "DeltaHz must be 1–9990 Hz" });
            if (!_radio.IsConnected)
                return StatusCode(503, new { error = "Radio not connected" });

            // CI-V 21 00 is absolute, so the nudge is computed here. The base
            // comes from the radio, not from cached state, because the operator
            // may have turned the RIT knob since the last write. A read miss
            // reads as 0, which is also the offset's rest position.
            int current = await _radio.GetRitOffsetHzAsync(CancellationToken.None);
            int newOffset = Math.Clamp(current + request.DeltaHz, -9990, 9990);
            await _radio.SetRitOffsetHzAsync(newOffset, CancellationToken.None);

            _radioStateService.ClarifierOffsetA = newOffset;
            _radioStateService.ClarifierOffsetB = newOffset;
            return Ok(new { offsetHz = newOffset });
        }

        [HttpPost("clarifier/reset")]
        public async Task<IActionResult> ResetClarifier([FromBody] ClarifierRequest request)
        {
            request.RxOn = true;
            request.TxOn = false;
            request.OffsetHz = 0;
            return await SetClarifier(request);
        }

        [HttpPost("split/{mode}")]
        public async Task<IActionResult> SetSplit(int mode)
        {
            if (mode < 0 || mode > 2)
                return BadRequest(new { error = "Split mode must be 0 (off), 1 (on), or 2 (quick split +5 kHz)" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Phase 3 block 5: split via the CI-V seam (command 0F). Quick
                // split (mode 2) is composed above the seam — set VFO B to VFO A
                // + 5 kHz, then turn split on — so it works whether or not split
                // was already active.
                if (mode == 2)
                {
                    long freqA = _radioStateService.FrequencyA;
                    if (freqA > 0)
                    {
                        long freqB = Math.Min(freqA + 5000, 75_000_000);
                        await _radio.SetFrequencyHzAsync(RadioVfo.B, freqB, CancellationToken.None);
                    }
                    await _radio.SetSplitAsync(true, CancellationToken.None);
                    _radioStateService.SplitMode = 2; // seam sets 1; promote to quick-split
                    _logger.LogInformation("Quick Split: VFO B = VFO A + 5 kHz, split ON");
                    return Ok(new { splitMode = 2 });
                }

                bool on = mode == 1;
                await _radio.SetSplitAsync(on, CancellationToken.None);
                _radioStateService.SplitMode = on ? 1 : 0;
                _logger.LogInformation("Split mode set to {Mode}", mode);
                return Ok(new { splitMode = mode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting split mode");
                return StatusCode(500, new { error = "Failed to set split mode" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Select the operating (active) VFO. On the IC-7300 this is CI-V command
        // 07 00/01 — it also switches the radio out of memory into VFO mode. The
        // seam mirrors the choice into RadioStateService.ActiveVfo, which is the
        // source of truth for how the per-VFO 25/26 reads are addressed.
        [HttpPost("active-vfo/{vfo}")]
        public async Task<IActionResult> SetActiveVfo(string vfo)
        {
            var v = vfo.ToUpperInvariant();
            if (v != "A" && v != "B")
                return BadRequest(new { error = "VFO must be A or B" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                await _radio.SelectVfoAsync(v == "B" ? RadioVfo.B : RadioVfo.A, CancellationToken.None);
                _logger.LogInformation("Active VFO set to {Vfo} (CI-V 07)", v);
                return Ok(new { activeVfo = v == "B" ? 1 : 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting active VFO");
                return StatusCode(500, new { error = "Failed to set active VFO" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("swap-vfo")]
        public async Task<IActionResult> SwapVfo()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                // Phase 3 block 5: exchange VFO A ↔ B via the CI-V seam (07 B0).
                // The seam swaps the cached freq/mode immediately to avoid UI
                // flicker; the poll confirms both within a couple of loops.
                await _radio.ExchangeVfosAsync(CancellationToken.None);

                _logger.LogInformation("VFO A and VFO B swapped");
                return Ok(new { message = "VFO swapped" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error swapping VFO");
                return StatusCode(500, new { error = "Failed to swap VFO" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // POST /api/cat/copy-vfo/{direction}
        //   direction = "ba" → copy VFO B to VFO A (source B unchanged)
        //   direction = "ab" → copy VFO A to VFO B (source A unchanged)
        //
        // Differs from swap in that it does NOT exchange — the source VFO keeps
        // its value. The IC-7300 has no directional-copy CI-V command (only
        // equalize 07 A0, which always copies selected→unselected), so this is
        // composed above the seam: write the cached source freq+mode into the
        // destination VFO via the per-VFO 25/26 path.
        [HttpPost("copy-vfo/{direction}")]
        public async Task<IActionResult> CopyVfo(string direction)
        {
            var dir = (direction ?? "").ToLowerInvariant();
            if (dir != "ba" && dir != "ab")
                return BadRequest(new { error = "direction must be 'ba' or 'ab'" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                if (dir == "ba")
                {
                    // Copy B → A.
                    await _radio.SetFrequencyHzAsync(RadioVfo.A, _radioStateService.FrequencyB, CancellationToken.None);
                    if (!string.IsNullOrEmpty(_radioStateService.ModeB))
                        await _radio.SetModeAsync(RadioVfo.A, _radioStateService.ModeB, CancellationToken.None);
                }
                else
                {
                    // Copy A → B.
                    await _radio.SetFrequencyHzAsync(RadioVfo.B, _radioStateService.FrequencyA, CancellationToken.None);
                    if (!string.IsNullOrEmpty(_radioStateService.ModeA))
                        await _radio.SetModeAsync(RadioVfo.B, _radioStateService.ModeA, CancellationToken.None);
                }

                _logger.LogInformation("VFO copy {Dir} completed", dir.ToUpperInvariant());
                return Ok(new { message = $"VFO {dir.ToUpperInvariant()} copy completed" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error copying VFO ({Dir})", dir);
                return StatusCode(500, new { error = "Failed to copy VFO" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("reinitialize")]
        public async Task<IActionResult> Reinitialize()
        {
            // "Test Connection" (Settings page). IWC drives the IC-7300 through
            // the CI-V seam (IRadioController / CivRadioController), which owns
            // the serial link and auto-reconnects on its own loop. So this
            // button just reports the seam's live connection state, and asks it
            // to (re)connect if it isn't currently up — e.g. the user changed
            // the COM port in Settings, or plugged the radio in after launch.
            //
            // The old Yaesu path (RadioInitializationService full read-burst +
            // an ID; CAT probe) was deleted in the carve; it also used to crash
            // IWC when clicked mid-session by racing the meter poller. The seam
            // serialises CI-V traffic, so a connect here coexists with polling.
            try
            {
                _logger.LogInformation("Test Connection requested from Settings page (IsConnected={IsConnected})", _radio.IsConnected);

                if (!_radio.IsConnected)
                {
                    _logger.LogInformation("Test Connection: not currently connected — asking the CI-V seam to connect");
                    await _radio.ConnectAsync(CancellationToken.None);
                }

                if (!_radio.IsConnected)
                {
                    _logger.LogWarning("Test Connection: CI-V seam did not come up (radio off / wrong COM port / CI-V not enabled).");
                    return Ok(new
                    {
                        success = false,
                        message = "Radio did not respond. Check the radio is powered on, " +
                                  "CI-V is enabled in the radio's menu, and the correct USB / COM " +
                                  "port is selected in Settings.",
                    });
                }

                var idCode = string.IsNullOrEmpty(_radio.ModelId) ? "IC-7300" : _radio.ModelId;
                _logger.LogInformation("Test Connection: CI-V seam connected — model {Model}", idCode);
                return Ok(new { success = true, message = $"Connection succeeded — {idCode}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test Connection failed");
                return Ok(new { success = false, message = ex.Message });
            }
        }

        // --- ATU ---
        public class AtuRequest { public bool Enabled { get; set; } }

        [HttpPost("atu")]
        public async Task<IActionResult> SetAtu([FromBody] AtuRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                // CI-V 1C 01: 01=tuner ON (in line), 00=tuner OFF (through).
                await _radio.SetTunerAsync(request.Enabled ? 1 : 0, CancellationToken.None);
                _radioStateService.AtuEnabled = request.Enabled;
                return Ok(new { atuEnabled = request.Enabled });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting ATU");
                return StatusCode(500, new { error = "Failed to set ATU" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Auto-tune trigger. The UI binds this to a long-press of the ATU
        // button; it toggles the tuning cycle — start if idle, stop if a
        // cycle is already running.
        //
        // Unlike the Yaesu AC command, CI-V 1C 01 *does* report a tuning
        // cycle in progress (status 02), so the frontend "Tuning…" state is
        // driven by real radio status broadcast from the poll loop rather
        // than a client-side timer. Sending 1C 01 02 starts a cycle; sending
        // 1C 01 01 (ON) mid-cycle stops it and leaves the tuner in line.
        [HttpPost("atu/tune")]
        public async Task<IActionResult> StartAtuTune()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                // Toggle: if a cycle is already running, stop it (→ ON); else start one.
                bool tuning = _radioStateService.AtuTuning;
                await _radio.SetTunerAsync(tuning ? 1 : 2, CancellationToken.None);
                _logger.LogInformation("ATU auto-tune {Action} (CI-V 1C 01 {Sub:X2})",
                    tuning ? "stopped" : "started", tuning ? 1 : 2);
                return Ok(new { message = "ATU tune toggled" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling ATU auto-tune");
                return StatusCode(500, new { error = "Failed to toggle ATU tune" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Refresh the ATU state by reading CI-V 1C 01 from the radio. Called
        // by the frontend after a tune cycle to capture the settled on/off.
        //   status: 0=OFF, 1=ON, 2=TUNING.
        [HttpGet("atu")]
        public async Task<IActionResult> RefreshAtuState()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                int status = await _radio.GetTunerAsync(CancellationToken.None);
                if (status >= 0)
                {
                    _radioStateService.AtuEnabled = status != 0;
                    _radioStateService.AtuTuning = status == 2;
                    _logger.LogDebug("ATU refresh: status={Status} → enabled={Enabled} tuning={Tuning}",
                        status, _radioStateService.AtuEnabled, _radioStateService.AtuTuning);
                }
                else
                {
                    _logger.LogWarning("ATU refresh: 1C 01 read missed (no status)");
                }
                return Ok(new { atuEnabled = _radioStateService.AtuEnabled });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing ATU state");
                return StatusCode(500, new { error = "Failed to refresh ATU state" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- NB LEVEL ---
        public class NbLevelRequest { public int Level { get; set; } = 10; }

        [HttpPost("nblevel/{receiver}")]
        public async Task<IActionResult> SetNbLevel(string receiver, [FromBody] NbLevelRequest request)
        {
            // IC-7300 NB level (CI-V 14 12) is 0–255 (0–100 %).
            if (request.Level < 0 || request.Level > 255)
                return BadRequest(new { error = "NB level must be 0–255" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetNbLevelAsync(request.Level, CancellationToken.None);
                _radioStateService.NbLevelA = request.Level;
                _radioStateService.NbLevelB = request.Level;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting NB level");
                return StatusCode(500, new { error = "Failed to set NB level" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- NR LEVEL (the IC-7300's digital noise reduction depth) ---
        public class NrLevelRequest { public int Level { get; set; } = 1; }

        [HttpPost("nrlevel/{receiver}")]
        public async Task<IActionResult> SetNrLevel(string receiver, [FromBody] NrLevelRequest request)
        {
            // IC-7300 NR level (CI-V 14 06) is 0–255 (0–100 %).
            if (request.Level < 0 || request.Level > 255)
                return BadRequest(new { error = "NR level must be 0–255" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetNrLevelAsync(request.Level, CancellationToken.None);
                _radioStateService.NrLevelA = request.Level;
                _radioStateService.NrLevelB = request.Level;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting NR level");
                return StatusCode(500, new { error = "Failed to set NR level" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW PITCH ---
        // IC-7300 CW pitch is 300–900 Hz (CI-V 14 09), set directly in Hz.
        public class CwPitchRequest { public int Hz { get; set; } = 600; }

        [HttpPost("cwpitch")]
        public async Task<IActionResult> SetCwPitch([FromBody] CwPitchRequest request)
        {
            if (request.Hz < 300 || request.Hz > 900)
                return BadRequest(new { error = "CW pitch must be 300–900 Hz" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetCwPitchHzAsync(request.Hz, CancellationToken.None);
                _radioStateService.CwPitch = request.Hz;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting CW pitch");
                return StatusCode(500, new { error = "Failed to set CW pitch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- RF GAIN ---
        public class RfGainRequest { public int Value { get; set; } = 255; }

        [HttpPost("rfgain/{receiver}")]
        public async Task<IActionResult> SetRfGain(string receiver, [FromBody] RfGainRequest request)
        {
            if (request.Value < 0 || request.Value > 255)
                return BadRequest(new { error = "RF Gain must be 0–255" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetRfGainAsync(request.Value, CancellationToken.None);
                _radioStateService.RfGainA = request.Value;
                _radioStateService.RfGainB = request.Value;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting RF gain");
                return StatusCode(500, new { error = "Failed to set RF gain" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- SQUELCH ---
        public class SquelchRequest { public int Value { get; set; } = 0; }

        [HttpPost("squelch/{receiver}")]
        public async Task<IActionResult> SetSquelch(string receiver, [FromBody] SquelchRequest request)
        {
            if (request.Value < 0 || request.Value > 255)
                return BadRequest(new { error = "Squelch must be 0–255" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetSquelchAsync(request.Value, CancellationToken.None);
                _radioStateService.SquelchA = request.Value;
                _radioStateService.SquelchB = request.Value;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting squelch");
                return StatusCode(500, new { error = "Failed to set squelch" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- MONITOR ON/OFF ---
        public class MonitorOnRequest { public bool On { get; set; } }

        [HttpPost("monitoron")]
        public async Task<IActionResult> SetMonitorOn([FromBody] MonitorOnRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetMonitorAsync(request.On, CancellationToken.None);
                _radioStateService.MonitorOn = request.On;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting monitor on/off");
                return StatusCode(500, new { error = "Failed to set monitor" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- MONITOR LEVEL ---
        // One receiver, one monitor level: the receiver segment is accepted for
        // URL compatibility but both state slots get the same value.
        public class MonitorLevelRequest { public int Level { get; set; } = 0; }

        [HttpPost("monitorlevel/{receiver}")]
        public async Task<IActionResult> SetMonitorLevel(string receiver, [FromBody] MonitorLevelRequest request)
        {
            if (request.Level < 0 || request.Level > 100)
                return BadRequest(new { error = "Monitor level must be 0–100" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetMonitorLevelPercentAsync(request.Level, CancellationToken.None);
                _radioStateService.MonitorLevelA = request.Level;
                _radioStateService.MonitorLevelB = request.Level;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting monitor level");
                return StatusCode(500, new { error = "Failed to set monitor level" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // --- CONNECT / DISCONNECT ---
        [HttpPost("connect")]
        public async Task<IActionResult> Connect()
        {
            try
            {
                await _radio.ConnectAsync(CancellationToken.None);
                AppStatus.InitializationStatus = _radio.IsConnected ? "complete" : "error";
                return Ok(new { success = _radio.IsConnected });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual connect failed");
                AppStatus.InitializationStatus = "error";
                return Ok(new { success = false, message = ex.Message });
            }
        }

        [HttpPost("disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            try
            {
                await _radio.DisconnectAsync();
                AppStatus.InitializationStatus = "disconnected";
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual disconnect failed");
                return Ok(new { success = false, message = ex.Message });
            }
        }

        // --- VOX ---
        public class VoxRequest
        {
            public bool On { get; set; }
            public int Gain { get; set; } = 50;
            public int Delay { get; set; } = 50;
            public int AntiVoxGain { get; set; } = 50;
        }

        [HttpPost("vox")]
        public async Task<IActionResult> SetVox([FromBody] VoxRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetVoxAsync(request.On, CancellationToken.None);
                _radioStateService.VoxOn = request.On;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        public class VoxGainRequest { public int Gain { get; set; } = 50; }
        public class VoxDelayRequest { public int Delay { get; set; } = 50; }
        public class AntiVoxGainRequest { public int Gain { get; set; } = 50; }

        [HttpPost("vox/gain")]
        public async Task<IActionResult> SetVoxGain([FromBody] VoxGainRequest request)
        {
            if (request.Gain < 0 || request.Gain > 100)
                return BadRequest(new { error = "Gain 0–100" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetVoxGainPercentAsync(request.Gain, CancellationToken.None);
                _radioStateService.VoxGain = request.Gain;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX gain"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("vox/delay")]
        public async Task<IActionResult> SetVoxDelay([FromBody] VoxDelayRequest request)
        {
            // The radio's VOX DELAY tops out at 2.0 s in 0.1 s steps; the seam
            // rounds to the step, so the state echoes the rounded value.
            if (request.Delay < 0 || request.Delay > 2000)
                return BadRequest(new { error = "Delay 0–2000 ms" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetVoxDelayMsAsync(request.Delay, CancellationToken.None);
                _radioStateService.VoxDelay = (int)Math.Round(request.Delay / 100.0) * 100;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX delay"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("vox/antivox")]
        public async Task<IActionResult> SetAntiVoxGain([FromBody] AntiVoxGainRequest request)
        {
            if (request.Gain < 0 || request.Gain > 100)
                return BadRequest(new { error = "Anti-VOX gain 0–100" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                // Anti-VOX really does reach the radio now — it is CI-V 14 17,
                // not a menu-only setting as it was on the Yaesu path.
                await _radio.SetAntiVoxPercentAsync(request.Gain, CancellationToken.None);
                _radioStateService.AntiVoxGain = request.Gain;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting anti-VOX gain"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- FM REPEATER ---
        //
        // The IC-7300 has no DUP+/DUP− command: repeater operation is split with
        // a signed offset, and the offset itself is a SET-menu item (one value
        // for HF, another for 50 MHz — the seam picks by the operating
        // frequency). So "shift" here means: off = split off; up/down = write
        // the signed offset, put VFO B on VFO A ± offset, and turn split on.
        //
        // CTCSS follows the radio's own two switches rather than the Yaesu
        // four-way: tone (16 42) transmits the tone, TSQL (16 43) transmits it
        // and gates the receiver on it. Both frequencies are set together so a
        // later switch between them does not need the dialog re-opened.
        public class FmRepeaterRequest
        {
            /// <summary>"0" = simplex, "1" = shift up, "2" = shift down.</summary>
            public string ShiftDir { get; set; } = "0";

            /// <summary>Repeater offset MAGNITUDE in Hz; the sign comes from <see cref="ShiftDir"/>.</summary>
            public int OffsetHz { get; set; } = 600000;

            /// <summary>"00" = off, "01" = tone (encode), "02" = tone squelch.</summary>
            public string CtcssMode { get; set; } = "00";

            /// <summary>CTCSS frequency in TENTHS of a Hz — 885 is 88.5 Hz.</summary>
            public int CtcssToneTenths { get; set; } = 885;
        }

        [HttpPost("fmrepeater")]
        public async Task<IActionResult> SetFmRepeater([FromBody] FmRepeaterRequest request)
        {
            if (!new[] { "0", "1", "2" }.Contains(request.ShiftDir))
                return BadRequest(new { error = "Shift must be 0 (simplex), 1 (up) or 2 (down)" });
            if (!new[] { "00", "01", "02" }.Contains(request.CtcssMode))
                return BadRequest(new { error = "CTCSS mode must be 00 (off), 01 (tone) or 02 (tone squelch)" });
            if (request.CtcssToneTenths < 670 || request.CtcssToneTenths > 2541)
                return BadRequest(new { error = "CTCSS tone must be 67.0–254.1 Hz (670–2541 tenths)" });
            if (request.OffsetHz < 0 || request.OffsetHz > 9_999_000)
                return BadRequest(new { error = "Offset must be 0–9999000 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });

                var ct = CancellationToken.None;

                // Tone first: on a repeater the access tone matters before the
                // transmit frequency moves.
                await _radio.SetRepeaterToneTenthsHzAsync(request.CtcssToneTenths, ct);
                await _radio.SetToneSquelchTenthsHzAsync(request.CtcssToneTenths, ct);
                await _radio.SetRepeaterToneAsync(request.CtcssMode != "00", ct);
                await _radio.SetToneSquelchAsync(request.CtcssMode == "02", ct);

                int signedOffset = request.ShiftDir == "2" ? -request.OffsetHz : request.OffsetHz;
                await _radio.SetFmSplitOffsetHzAsync(signedOffset, ct);

                if (request.ShiftDir == "0")
                {
                    await _radio.SetSplitAsync(false, ct);
                    _radioStateService.SplitMode = 0;
                }
                else
                {
                    long freqA = _radioStateService.FrequencyA;
                    if (freqA > 0)
                    {
                        long txHz = Math.Clamp(freqA + signedOffset, 30_000, 74_800_000);
                        await _radio.SetFrequencyHzAsync(RadioVfo.B, txHz, ct);
                    }
                    await _radio.SetSplitAsync(true, ct);
                    _radioStateService.SplitMode = 1;
                }

                _radioStateService.FmShiftDir = request.ShiftDir;
                _radioStateService.FmOffsetHz = request.OffsetHz;
                _radioStateService.CtcssMode = request.CtcssMode;
                _radioStateService.CtcssTone = request.CtcssToneTenths.ToString();
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting FM repeater"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW KEYER ---
        public class CwSpeedRequest { public int Speed { get; set; } = 20; }
        public class CwBreakInRequest { public string Mode { get; set; } = "0"; }
        public class CwBreakInDelayRequest { public double Dots { get; set; } = 3.0; }

        [HttpPost("cw/speed")]
        public async Task<IActionResult> SetCwSpeed([FromBody] CwSpeedRequest request)
        {
            // IC-7300 keyer range is 6–48 WPM (CI-V 14 0C).
            if (request.Speed < 6 || request.Speed > 48)
                return BadRequest(new { error = "CW speed 6–48 WPM" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetCwSpeedWpmAsync(request.Speed, CancellationToken.None);
                _radioStateService.CwSpeed = request.Speed;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW speed"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW KEYER STATE (read current settings from the radio) ---
        // Lets the CW popup show the radio's live speed/pitch/delay/break-in
        // when it opens, rather than stale server-rendered defaults.
        public class CwStateResponse
        {
            public int SpeedWpm { get; set; }
            public int PitchHz { get; set; }
            public double DelayDots { get; set; }
            public int BreakIn { get; set; }
        }

        [HttpGet("cw/state")]
        public async Task<IActionResult> GetCwState()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                var speed  = await _radio.GetCwSpeedWpmAsync(CancellationToken.None);
                var pitch  = await _radio.GetCwPitchHzAsync(CancellationToken.None);
                var delay  = await _radio.GetCwBreakInDelayDotsAsync(CancellationToken.None);
                var breakIn = await _radio.GetCwBreakInAsync(CancellationToken.None);
                return Ok(new CwStateResponse
                {
                    SpeedWpm  = speed  < 0 ? _radioStateService.CwSpeed : speed,
                    PitchHz   = pitch  < 0 ? 600 : pitch,
                    DelayDots = delay  < 0 ? 3.0 : delay,
                    BreakIn   = breakIn < 0 ? 0 : breakIn
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "Error reading CW state"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // Abort a CW memory message that is currently keying (CI-V 17 FF).
        [HttpPost("cw/stop")]
        public async Task<IActionResult> StopCw()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.StopCwAsync(CancellationToken.None);
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error stopping CW"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("cw/breakin")]
        public async Task<IActionResult> SetCwBreakIn([FromBody] CwBreakInRequest request)
        {
            if (!new[] { "0", "1", "2" }.Contains(request.Mode))
                return BadRequest(new { error = "Break-in mode 0/1/2" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetCwBreakInAsync(int.Parse(request.Mode), CancellationToken.None);
                _radioStateService.CwBreakIn = request.Mode;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW break-in"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("cw/breakindelay")]
        public async Task<IActionResult> SetCwBreakInDelay([FromBody] CwBreakInDelayRequest request)
        {
            // IC-7300 break-in delay is in DOTS (2.0–13.0), not milliseconds.
            if (request.Dots < 2.0 || request.Dots > 13.0)
                return BadRequest(new { error = "Delay 2.0–13.0 dots" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetCwBreakInDelayDotsAsync(request.Dots, CancellationToken.None);
                _radioStateService.CwBreakInDelay = (int)Math.Round(request.Dots * 10);
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW break-in delay"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        public class CwMessageRequest { public string Message { get; set; } = ""; }

        [HttpPost("cw/send")]
        public async Task<IActionResult> SendCwMessage([FromBody] CwMessageRequest request)
        {
            if (string.IsNullOrEmpty(request.Message))
                return BadRequest(new { error = "Empty message" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                // The controller filters to the radio's sendable character set
                // and caps at the 30-char limit, returning what was actually keyed.
                var sent = await _radio.SendCwMessageAsync(request.Message, CancellationToken.None);
                if (string.IsNullOrEmpty(sent))
                    return BadRequest(new { error = "No valid CW characters" });
                return Ok(new { sent });
            }
            catch (Exception ex) { _logger.LogError(ex, "Error sending CW message"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW MESSAGES ---
        [HttpGet("cw/messages")]
        public async Task<IActionResult> GetCwMessages()
        {
            var settings = await _settingsService.GetSettingsAsync();
            return Ok(settings.CwMessages);
        }

        [HttpPost("cw/messages")]
        public async Task<IActionResult> SaveCwMessages([FromBody] List<string> messages)
        {
            if (messages == null || messages.Count != 5)
                return BadRequest(new { error = "Exactly 5 messages required" });
            var settings = await _settingsService.GetSettingsAsync();
            settings.CwMessages = messages.Select(m => m ?? "").Take(5).ToList();
            await _settingsService.SaveSettingsAsync(settings);
            return Ok(new { saved = true });
        }

        // -- TWIN PBT (Digital Passband Tuning, CI-V 14 07 / 14 08) ----------
        //
        // The IC-7300's equivalent of an audio bandpass filter. Two 0–255 shift
        // values (128 = centre / no shift): the inner (PBT1) and outer (PBT2)
        // edges. Single receiver-wide on the IC-7300, so there is no per-VFO
        // addressing — this replaces the Yaesu LCUT/HCUT audio-filter path,
        // which drove the (now inert) EX-menu multiplexer.

        public class PbtReadResponse
        {
            public int Inner  { get; set; } = 128;
            public int Outer  { get; set; } = 128;
            public int Centre { get; set; } = 128;
        }

        public class PbtSetRequest
        {
            public int Value { get; set; }
        }

        [HttpGet("pbt")]
        public async Task<IActionResult> ReadPbt()
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                var inner = await _radio.GetPbtInnerAsync(CancellationToken.None);
                var outer = await _radio.GetPbtOuterAsync(CancellationToken.None);
                return Ok(new PbtReadResponse
                {
                    Inner = inner < 0 ? 128 : inner,
                    Outer = outer < 0 ? 128 : outer
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading Twin PBT");
                return StatusCode(500, new { error = "Failed to read Twin PBT" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("pbt/{edge}")]
        public async Task<IActionResult> SetPbt(string edge, [FromBody] PbtSetRequest request)
        {
            var e = (edge ?? "").Trim().ToLowerInvariant();
            if (e != "inner" && e != "outer")
                return BadRequest(new { error = "Invalid edge (must be 'inner' or 'outer')" });
            if (request == null || request.Value < 0 || request.Value > 255)
                return BadRequest(new { error = "Value must be 0–255" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                if (e == "inner") await _radio.SetPbtInnerAsync(request.Value, CancellationToken.None);
                else              await _radio.SetPbtOuterAsync(request.Value, CancellationToken.None);
                return Ok(new { edge = e, value = request.Value });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Twin PBT {Edge}", e);
                return StatusCode(500, new { error = "Failed to set Twin PBT" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // -- RX TONE CONTROL: HPF/LPF audio filter (CI-V 1A 05) -------------
        //
        // Per-mode receive audio high-pass (low-cut) / low-pass (high-cut)
        // edges — the IC-7300's native equivalent of the Yaesu Audio Filter
        // the carve removed. Both edges are Hz with 0 = Through. The {vfo} segment
        // picks which VFO's *current mode* the menu item is read from; the
        // radio stores values per mode, not per VFO. Available == false when
        // the current mode has no Tone Control (SSB-DATA, etc.).

        public class RxFilterReadResponse
        {
            public bool Available { get; set; }
            public int HpfHz { get; set; }
            public int LpfHz { get; set; }
        }

        public class RxFilterSetRequest
        {
            public int HpfHz { get; set; }
            public int LpfHz { get; set; }
        }

        [HttpGet("rxfilter/{vfo}")]
        public async Task<IActionResult> ReadRxFilter(string vfo)
        {
            var v = (vfo ?? "").Trim().ToLowerInvariant();
            if (v != "a" && v != "b")
                return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                var (hpf, lpf) = await _radio.GetRxFilterAsync(
                    v == "b" ? RadioVfo.B : RadioVfo.A, CancellationToken.None);
                if (hpf < 0)
                    return Ok(new RxFilterReadResponse { Available = false });
                return Ok(new RxFilterReadResponse { Available = true, HpfHz = hpf, LpfHz = lpf });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading RX filter");
                return StatusCode(500, new { error = "Failed to read RX filter" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("rxfilter/{vfo}")]
        public async Task<IActionResult> SetRxFilter(string vfo, [FromBody] RxFilterSetRequest request)
        {
            var v = (vfo ?? "").Trim().ToLowerInvariant();
            if (v != "a" && v != "b")
                return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });
            if (request == null)
                return BadRequest(new { error = "Missing body" });
            // 0 = Through; otherwise HPF 100–2000, LPF 500–2400 (the controller
            // snaps to 100 Hz steps, but reject wildly out-of-range values).
            if (request.HpfHz < 0 || request.HpfHz > 2000 || request.LpfHz < 0 || request.LpfHz > 2400)
                return BadRequest(new { error = "HPF 0–2000 Hz, LPF 0–2400 Hz (0 = Through)" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetRxFilterAsync(
                    v == "b" ? RadioVfo.B : RadioVfo.A, request.HpfHz, request.LpfHz, CancellationToken.None);
                return Ok(new { vfo = v, hpfHz = request.HpfHz, lpfHz = request.LpfHz });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting RX filter");
                return StatusCode(500, new { error = "Failed to set RX filter" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // -- RX TONE CONTROL: Bass/Treble (CI-V 1A 05) ----------------------
        //
        // The shelf half of the same menu group as rxfilter above, kept on its
        // own endpoint because its availability differs: the radio has Bass and
        // Treble for SSB, AM and FM only, so CW and RTTY report Available ==
        // false here while still returning edges from rxfilter. Levels are
        // −5…+5 with 0 flat, exactly as the radio's own menu shows them.

        public class RxToneReadResponse
        {
            public bool Available { get; set; }
            public int Bass { get; set; }
            public int Treble { get; set; }
        }

        public class RxToneSetRequest
        {
            public int Bass { get; set; }
            public int Treble { get; set; }
        }

        [HttpGet("rxtone/{vfo}")]
        public async Task<IActionResult> ReadRxTone(string vfo)
        {
            var v = (vfo ?? "").Trim().ToLowerInvariant();
            if (v != "a" && v != "b")
                return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                var (available, bass, treble) = await _radio.GetRxToneAsync(
                    v == "b" ? RadioVfo.B : RadioVfo.A, CancellationToken.None);
                if (!available)
                    return Ok(new RxToneReadResponse { Available = false });
                return Ok(new RxToneReadResponse { Available = true, Bass = bass, Treble = treble });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading RX tone");
                return StatusCode(500, new { error = "Failed to read RX tone" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("rxtone/{vfo}")]
        public async Task<IActionResult> SetRxTone(string vfo, [FromBody] RxToneSetRequest request)
        {
            var v = (vfo ?? "").Trim().ToLowerInvariant();
            if (v != "a" && v != "b")
                return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });
            if (request == null)
                return BadRequest(new { error = "Missing body" });
            if (request.Bass < -5 || request.Bass > 5 || request.Treble < -5 || request.Treble > 5)
                return BadRequest(new { error = "Bass and Treble are −5 to +5" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                if (!_radio.IsConnected)
                    return StatusCode(503, new { error = "Radio not connected" });
                await _radio.SetRxToneAsync(
                    v == "b" ? RadioVfo.B : RadioVfo.A, request.Bass, request.Treble, CancellationToken.None);
                return Ok(new { vfo = v, bass = request.Bass, treble = request.Treble });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting RX tone");
                return StatusCode(500, new { error = "Failed to set RX tone" });
            }
            finally { _requestSemaphore.Release(); }
        }

    }
}
