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
        private readonly ICatClient _catClient;
        private readonly ISettingsService _settingsService;
        private readonly ILogger<CatController> _logger;
        private readonly RadioStateService _radioStateService;
        private readonly RadioStatePersistenceService _statePersistence;
        private readonly AudioFilterMapService _audioFilterMap;
        // Phase 3: the semantic seam. Frequency set is repointed here (real CI-V
        // via CivRadioController) while the rest of this controller still speaks
        // the inert Yaesu _catClient until each command is migrated in turn.
        private readonly IRadioController _radio;
        private static readonly SemaphoreSlim _requestSemaphore = new(1, 1);

        // -- P1=0-Fixed outgoing-command helpers -------------------------------
        //
        // On single-receiver radios (FTdx10 / FT-710 / FTDX3000 / FT-991A)
        // every P1=0-Fixed receive-control CAT command (GT, PA, RA, NR, NB,
        // NL, BC, BP, CO, SH, IS, SL, RL, AG, RG, SQ) must use P1=0 -- the
        // radio's firmware hard-codes that position to 0, and silently
        // rejects commands sent with P1=1 (which is what IWC was doing when
        // the user clicked a control on panel B). On dual-receiver (FTdx101)
        // P1 genuinely addresses MAIN vs SUB.
        //
        // SP3L Jacek #34 pre7: this is the outbound match for the inbound
        // dispatcher fix we did in pre5/pre6 (SetPerVfo). Without it, Jacek
        // saw "VFO-B active, Contour switching does not work, IF width does
        // not work" -- because CO1... and SH1... were being sent and the
        // FTdx10 was ignoring them.

        /// <summary>
        /// Returns the P1 character for a per-VFO CAT command, given the
        /// user's clicked receiver ("A" or "B"). Delegates to
        /// <see cref="RadioCapabilities.VfoP1"/> -- see that method for the
        /// single- vs dual-receiver rule.
        /// </summary>
        private string VfoP1Outgoing(string receiver) =>
            RadioCapabilities.VfoP1(_radioStateService.IsSingleReceiver, receiver);

        /// <summary>
        /// Returns true if the per-VFO state write should target *B (vs *A)
        /// for a user-clicked receiver. Delegates to
        /// <see cref="RadioCapabilities.VfoIsB"/>.
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
            _logger.LogInformation("[API] SetMicGain called: value={Value}", request.Value);

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                if (request.Value < 0 || request.Value > 100)
                    return BadRequest(new { error = "MIC Gain value out of range (0-100)" });

                string command = $"MG{request.Value:D3};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                // Persist MIC Gain value
                _logger.LogWarning("[MicGain API] Setting _radioStateService.MicGain to {Value}", request.Value);
                _radioStateService.MicGain = request.Value;

                _logger.LogInformation("Set MIC Gain to {Value}", request.Value);
                return Ok(new { message = $"MIC Gain set to {request.Value}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting MIC Gain");
                return StatusCode(500, new { error = "Failed to set MIC Gain" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("proc")]
        public async Task<IActionResult> SetProc([FromBody] ProcRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                // PR set format is "PR P1 P2 ;" where P1=0 selects Speech
                // Processor (P1=1 is Parametric Mic EQ, not what we want), and
                // P2=0=OFF / P2=1=ON. The CAT manual lists P2 as 1=OFF/2=ON
                // but bench testing on the FTdx101MP (2026-06-25) showed the
                // manual is wrong: 0=OFF and 1=ON are the values the radio
                // actually accepts. Sending "PR0;"/"PR1;" (without P2) is a
                // read command, which is why the button used to be a no-op.
                string command = request.Enabled ? "PR01;" : "PR00;";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);
                _radioStateService.ProcEnabled = request.Enabled;
                return Ok(new { message = $"PROC {(request.Enabled ? "ON" : "OFF")}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting PROC");
                return StatusCode(500, new { error = "Failed to set PROC" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("proclevel")]
        public async Task<IActionResult> SetProcLevel([FromBody] ProcLevelRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                if (request.Value < 0 || request.Value > 100)
                    return BadRequest(new { error = "PROC level out of range (0-100)" });
                await _catClient.SendCommandAsync($"PL{request.Value:D3};", "WebUI", CancellationToken.None);

                // Read back to confirm what the radio actually stored.
                // Response format: "PLnnn;" (nnn = 000-100).
                var response = await _catClient.SendCommandAsync("PL;", "WebUI", CancellationToken.None);
                int actualValue = request.Value;
                if (!string.IsNullOrEmpty(response) && response.Length >= 5)
                {
                    var valueStr = response.Substring(2, 3);
                    if (int.TryParse(valueStr, out int parsed) && parsed >= 0 && parsed <= 100)
                        actualValue = parsed;
                }
                _radioStateService.ProcLevel = actualValue;
                if (actualValue != request.Value)
                    _logger.LogWarning("PROC level mismatch: requested {Requested}, radio returned {Actual}", request.Value, actualValue);
                return Ok(new { message = $"PROC level set to {actualValue}", actual = actualValue });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting PROC level");
                return StatusCode(500, new { error = "Failed to set PROC level" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
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
            ICatClient catClient,
            ISettingsService settingsService,
            ILogger<CatController> logger,
            RadioStateService radioStateService,
            RadioStatePersistenceService statePersistence,
            AudioFilterMapService audioFilterMap,
            IRadioController radio)
        {
            _catClient = catClient;
            _settingsService = settingsService;
            _logger = logger;
            _radioStateService = radioStateService;
            _statePersistence = statePersistence;
            _audioFilterMap = audioFilterMap;
            _radio = radio;
        }

        private async Task EnsureConnectedAsync()
        {
            // RadioInitializationService handles connection and state restoration on startup.
            // This method only needs to verify the connection is still active.
            if (!_catClient.IsConnected)
            {
                var settings = await _settingsService.GetSettingsAsync();
                await _catClient.ConnectAsync(settings.SerialPort, settings.BaudRate);
            }
            // No redundant restoration needed - RadioInitializationService already did it
        }

        private async Task<string> GetMainVfoAsync()
        {
            var response = await _catClient.SendCommandAsync("IF;", "WebUI", CancellationToken.None);
            if (!string.IsNullOrEmpty(response) && response.Length > 5)
                return response[5] == '1' ? "B" : "A";
            return "A";
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            if (_radioStateService.FrequencyA < 100 || _radioStateService.FrequencyB < 100)
            {
                await EnsureConnectedAsync();
            }

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
                    roofingFilter = _radioStateService.RoofingFilterA ?? "",
                    ifWidth = _radioStateService.IfWidthA ?? "",
                    ifShift = _radioStateService.IfShiftA
                },
                vfoB = new
                {
                    frequency = _radioStateService.FrequencyB,
                    band = _radioStateService.BandB,
                    sMeter = _radioStateService.SMeterB ?? 0,
                    mode = _radioStateService.ModeB ?? "",
                    antenna = _radioStateService.AntennaB ?? "",
                    afGain = _radioStateService.AfGainB,
                    roofingFilter = _radioStateService.RoofingFilterB ?? "",
                    ifWidth = _radioStateService.IfWidthB ?? "",
                    ifShift = _radioStateService.IfShiftB
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
                var oldBand = vfo == RadioVfo.B ? _radioStateService.BandB : _radioStateService.BandA;
                var curFreq = vfo == RadioVfo.B ? _radioStateService.FrequencyB : _radioStateService.FrequencyA;
                var curMode = vfo == RadioVfo.B ? _radioStateService.ModeB : _radioStateService.ModeA;
                if (!string.IsNullOrEmpty(oldBand) && curFreq > 0)
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

        [HttpPost("antenna/a")]
        public async Task<IActionResult> SetAntennaA([FromBody] AntennaRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
            {
                return StatusCode(503, new { error = "Radio busy" });
            }

            try
            {
                await EnsureConnectedAsync();
                var command = $"AN0{request.Antenna};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.AntennaA = request.Antenna;

                // Persist immediately into the current band's profile.
                // Without this, the antenna selection only lands in
                // settings.BandProfilesA when the user switches AWAY from
                // the band — so a shutdown mid-band would lose the choice.
                var bandA = _radioStateService.BandA;
                if (!string.IsNullOrEmpty(bandA))
                {
                    var settings = await _settingsService.GetSettingsAsync();
                    if (!settings.BandProfilesA.TryGetValue(bandA, out var prof))
                        prof = new BandProfile();
                    prof.Antenna = request.Antenna;
                    settings.BandProfilesA[bandA] = prof;
                    await _settingsService.SaveSettingsAsync(settings);
                }

                _logger.LogInformation("Set Main antenna to {Antenna}", request.Antenna);
                return Ok(new { message = $"Antenna {request.Antenna} selected" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Main antenna");
                return StatusCode(500, new { error = "Failed to set antenna" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("antenna/b")]
        public async Task<IActionResult> SetAntennaB([FromBody] AntennaRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
            {
                return StatusCode(503, new { error = "Radio busy" });
            }

            try
            {
                await EnsureConnectedAsync();
                var command = $"AN1{request.Antenna};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.AntennaB = request.Antenna;

                // Persist immediately into the current band's profile.
                // See SetAntennaA for the rationale.
                var bandB = _radioStateService.BandB;
                if (!string.IsNullOrEmpty(bandB))
                {
                    var settings = await _settingsService.GetSettingsAsync();
                    if (!settings.BandProfilesB.TryGetValue(bandB, out var prof))
                        prof = new BandProfile();
                    prof.Antenna = request.Antenna;
                    settings.BandProfilesB[bandB] = prof;
                    await _settingsService.SaveSettingsAsync(settings);
                }

                _logger.LogInformation("Set Sub antenna to {Antenna}", request.Antenna);
                return Ok(new { message = $"Antenna {request.Antenna} selected" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Sub antenna");
                return StatusCode(500, new { error = "Failed to set antenna" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        // FTdx101MP/D roofing filter display names (response code -> display name)
        private static readonly Dictionary<string, string> RoofingFilterNames = new()
        {
            { "6", "12 kHz" },
            { "7", "3 kHz" },
            { "8", "1.2 kHz" },
            { "9", "600 Hz" },
            { "A", "300 Hz" }
        };

        // FTdx101MP/D roofing filter set codes (response code -> set code used in RF command)
        private static readonly Dictionary<string, string> RoofingFilterSetCodes = new()
        {
            { "6", "1" },  // 12 kHz
            { "7", "2" },  // 3 kHz
            { "8", "3" },  // 1.2 kHz (option)
            { "9", "4" },  // 600 Hz
            { "A", "5" }   // 300 Hz (option)
        };

        // FTdx10 roofing filter display names (RF read code P3 -> display name)
        private static readonly Dictionary<string, string> FtdxTenRoofingFilterNames = new()
        {
            { "6", "12 kHz" },
            { "7", "3 kHz" },
            { "9", "500 Hz" },
            { "A", "300 Hz" }
        };

        // FTdx10 roofing filter set codes (read code P3 -> set code P2 used in RF command)
        private static readonly Dictionary<string, string> FtdxTenRoofingFilterSetCodes = new()
        {
            { "6", "1" },  // 12 kHz
            { "7", "2" },  // 3 kHz
            { "9", "4" },  // 500 Hz
            { "A", "5" }   // 300 Hz (optional)
        };

        [HttpPost("roofingfilter/a")]
        public async Task<IActionResult> SetRoofingFilterA([FromBody] RoofingFilterRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();

                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx10  = settings.RadioModel == "FTdx10";
                bool isFt710   = settings.RadioModel == "FT-710";
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";

                if (isFt710)
                    return Ok(new { message = "Roofing filter is selected automatically by the radio" });

                if (isFtdx10)
                    return await SetFtdx10RoofingFilterAsync(request);

                if (isFtdx3000)
                    return await SetFtdx3000RoofingFilterAsync(request);

                // FTdx101MP/D: RF command with set code conversion
                if (!RoofingFilterSetCodes.TryGetValue(request.Filter, out var setCode))
                    return BadRequest(new { error = $"Invalid filter code: {request.Filter}" });

                var rfCommand = $"RF0{setCode};";
                _logger.LogInformation("Sending roofing filter command: {Command}", rfCommand);
                await _catClient.SendCommandAsync(rfCommand, "WebUI", CancellationToken.None);

                await Task.Delay(100);
                var rfReadResponse = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);
                _logger.LogInformation("Read back roofing filter response: {Response}", rfReadResponse);

                if (!string.IsNullOrEmpty(rfReadResponse) && rfReadResponse.Length >= 4)
                {
                    var actualFilter = rfReadResponse[3].ToString();
                    _radioStateService.RoofingFilterA = actualFilter;

                    if (actualFilter != request.Filter)
                    {
                        var requestedName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                        var actualName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                        _logger.LogWarning("Roofing filter {Requested} not available, radio returned {Actual}", requestedName, actualName);
                        return Ok(new { message = $"Filter {requestedName} not installed. Using {actualName}.", warning = true, filter = actualFilter, filterName = actualName });
                    }

                    var filterName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                    _logger.LogInformation("Set Main roofing filter to {Filter}", filterName);
                    return Ok(new { message = $"Roofing filter {filterName} selected", filter = actualFilter, filterName });
                }

                _radioStateService.RoofingFilterA = request.Filter;
                var fallbackName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                return Ok(new { message = $"Roofing filter {fallbackName} selected", filter = request.Filter, filterName = fallbackName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Main roofing filter");
                return StatusCode(500, new { error = "Failed to set roofing filter" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("roofingfilter/b")]
        public async Task<IActionResult> SetRoofingFilterB([FromBody] RoofingFilterRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();

                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx10  = settings.RadioModel == "FTdx10";
                bool isFt710   = settings.RadioModel == "FT-710";
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";

                if (isFt710)
                    return Ok(new { message = "Roofing filter is selected automatically by the radio" });

                if (isFtdx10)
                    return await SetFtdx10RoofingFilterAsync(request);

                if (isFtdx3000)
                    return await SetFtdx3000RoofingFilterAsync(request);

                // FTdx101MP/D: RF command with set code conversion
                if (!RoofingFilterSetCodes.TryGetValue(request.Filter, out var setCode))
                    return BadRequest(new { error = $"Invalid filter code: {request.Filter}" });

                var rfCommand = $"RF1{setCode};";
                _logger.LogInformation("Sending roofing filter command: {Command}", rfCommand);
                await _catClient.SendCommandAsync(rfCommand, "WebUI", CancellationToken.None);

                await Task.Delay(100);
                var rfReadResponse = await _catClient.SendCommandAsync("RF1;", "WebUI", CancellationToken.None);
                _logger.LogInformation("Read back roofing filter response: {Response}", rfReadResponse);

                if (!string.IsNullOrEmpty(rfReadResponse) && rfReadResponse.Length >= 4)
                {
                    var actualFilter = rfReadResponse[3].ToString();
                    _radioStateService.RoofingFilterB = actualFilter;

                    if (actualFilter != request.Filter)
                    {
                        var requestedName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                        var actualName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                        _logger.LogWarning("Roofing filter {Requested} not available, radio returned {Actual}", requestedName, actualName);
                        return Ok(new { message = $"Filter {requestedName} not installed. Using {actualName}.", warning = true, filter = actualFilter, filterName = actualName });
                    }

                    var filterName = RoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                    _logger.LogInformation("Set Sub roofing filter to {Filter}", filterName);
                    return Ok(new { message = $"Roofing filter {filterName} selected", filter = actualFilter, filterName });
                }

                _radioStateService.RoofingFilterB = request.Filter;
                var fallbackName = RoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                return Ok(new { message = $"Roofing filter {fallbackName} selected", filter = request.Filter, filterName = fallbackName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Sub roofing filter");
                return StatusCode(500, new { error = "Failed to set roofing filter" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        /// <summary>
        /// FTdx10 single-receiver roofing filter: RF0 P2 set / RF0 P3 read.
        /// Per-VFO state is tracked in the active VFO slot (inactive panel is
        /// not editable on single-receiver radios).
        /// </summary>
        private async Task<IActionResult> SetFtdx10RoofingFilterAsync(RoofingFilterRequest request)
        {
            if (!FtdxTenRoofingFilterSetCodes.TryGetValue(request.Filter, out var setCode))
                return BadRequest(new { error = $"Invalid filter code: {request.Filter}" });

            var rfCommand = $"RF0{setCode};";
            _logger.LogInformation("Sending roofing filter command (FTdx10): {Command}", rfCommand);
            await _catClient.SendCommandAsync(rfCommand, "WebUI", CancellationToken.None);

            await Task.Delay(100);
            var rfReadResponse = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);
            _logger.LogInformation("Read back roofing filter response (FTdx10): {Response}", rfReadResponse);

            if (!string.IsNullOrEmpty(rfReadResponse) && rfReadResponse.Length >= 4)
            {
                var actualFilter = rfReadResponse[3].ToString();
                if (_radioStateService.ActiveVfo == 1) _radioStateService.RoofingFilterB = actualFilter;
                else                                   _radioStateService.RoofingFilterA = actualFilter;

                if (actualFilter != request.Filter)
                {
                    var requestedName = FtdxTenRoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
                    var actualName = FtdxTenRoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                    _logger.LogWarning("Roofing filter {Requested} not available, radio returned {Actual}", requestedName, actualName);
                    return Ok(new { message = $"Filter {requestedName} not installed. Using {actualName}.", warning = true, filter = actualFilter, filterName = actualName });
                }

                var filterName = FtdxTenRoofingFilterNames.GetValueOrDefault(actualFilter, actualFilter);
                _logger.LogInformation("Set roofing filter (FTdx10) to {Filter}", filterName);
                return Ok(new { message = $"Roofing filter {filterName} selected", filter = actualFilter, filterName });
            }

            if (_radioStateService.ActiveVfo == 1) _radioStateService.RoofingFilterB = request.Filter;
            else                                   _radioStateService.RoofingFilterA = request.Filter;
            var fallbackName = FtdxTenRoofingFilterNames.GetValueOrDefault(request.Filter, request.Filter);
            return Ok(new { message = $"Roofing filter {fallbackName} selected", filter = request.Filter, filterName = fallbackName });
        }

        /// <summary>
        /// FTDX3000 single-receiver roofing filter: RF0 P2 set / RF0 P3 read.
        /// The read-back code (P3) uses a different value space than the set code
        /// (P2) — 600 Hz reads back as 7, 300 Hz as 8, and AUTO reports the
        /// filter in circuit (4/5/6/9/A) — so the read code is normalised back
        /// to the dropdown's set-code space. Per-VFO state is tracked in the
        /// active VFO slot. See <see cref="Ftdx3000Roofing"/>.
        /// </summary>
        private async Task<IActionResult> SetFtdx3000RoofingFilterAsync(RoofingFilterRequest request)
        {
            // P1 is always 0 (single receiver); the set code is the filter number directly.
            await _catClient.SendCommandAsync($"RF0{request.Filter};", "WebUI", CancellationToken.None);
            await Task.Delay(100);
            var readback = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);

            var readCode = readback?.Length >= 4 ? readback[3].ToString() : request.Filter;
            var stateCode = Ftdx3000Roofing.NormalizeReadCode(readCode);
            var displayName = Ftdx3000Roofing.ReadCodeNames.GetValueOrDefault(readCode,
                              Ftdx3000Roofing.SetCodeNames.GetValueOrDefault(stateCode, stateCode));

            if (_radioStateService.ActiveVfo == 1) _radioStateService.RoofingFilterB = stateCode;
            else                                   _radioStateService.RoofingFilterA = stateCode;
            _logger.LogInformation("Set roofing filter (FTDX3000) to {Filter} (read code {ReadCode})", displayName, readCode);
            return Ok(new { message = $"Roofing filter set to {displayName}", filter = stateCode, filterName = displayName });
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

                // Phase 3 block 2: set mode via the CI-V seam (command 06). The
                // seam updates RadioStateService on the radio's ACK; the poll
                // loop's command-04 read then confirms it. No Yaesu Contour/APF
                // re-apply — that was FTdx101-specific and does not apply here.
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

        [HttpPost("afgain")]
        public async Task<IActionResult> SetAfGain([FromBody] AfGainRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                if (request == null || (request.Band != "0" && request.Band != "1"))
                    return BadRequest(new { error = "Invalid band (must be '0' or '1')" });
                if (!int.TryParse(request.Value, out int val) || val < 0 || val > 255)
                    return BadRequest(new { error = "AF Gain value out of range (0-255)" });

                string command = $"AG{request.Band}{val:D3};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                // Read back the actual AF Gain value from the radio
                string readCmd = request.Band == "0" ? "AG0;" : "AG1;";
                var response = await _catClient.SendCommandAsync(readCmd, "WebUI", CancellationToken.None);
                int actualValue = val;
                if (!string.IsNullOrEmpty(response) && response.Length >= 6)
                {
                    // Response format: AG0nnn; or AG1nnn;
                    var valueStr = response.Substring(3, 3);
                    if (int.TryParse(valueStr, out int parsed))
                        actualValue = parsed;
                }

                // Persist the actual value
                if (request.Band == "0")
                    _radioStateService.AfGainA = actualValue;
                else if (request.Band == "1")
                    _radioStateService.AfGainB = actualValue;
                _logger.LogInformation("Set AF Gain band {Band} to {Requested} (actual: {Actual})", request.Band, val, actualValue);
                if (actualValue != val)
                    _logger.LogWarning("AF Gain mismatch: requested {Requested}, radio returned {Actual}", val, actualValue);
                return Ok(new { message = $"AF Gain set to {actualValue} for band {request.Band}", actual = actualValue });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting AF Gain");
                return StatusCode(500, new { error = "Failed to set AF Gain" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        public class BandRequest { public string Band { get; set; } = string.Empty; }
        public class AntennaRequest { public string Antenna { get; set; } = string.Empty; }
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

        public class RoofingFilterRequest
        {
            public string Filter { get; set; } = string.Empty;
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
        public class IfWidthRequest            { public string Code { get; set; } = "8"; }
        public class IfShiftRequest            { public int ShiftHz { get; set; } = 0; }

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

        // GET — query the radio for its current IF Width code and refresh
        // RadioStateService. Used for live calibration discovery: the user
        // changes WIDTH on the radio's front panel, then hits this URL to
        // see what SH code came back. Returns 99 max to allow probing codes
        // beyond the official documented range (post-firmware extensions).
        [HttpGet("ifwidth/{receiver}")]
        public async Task<IActionResult> QueryIfWidth(string receiver)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var p1 = VfoP1Outgoing(receiver);
                var response = await _catClient.SendCommandAsync($"SH{p1};", "WebUI", CancellationToken.None);
                // The dispatcher will have updated RadioStateService.IfWidthA/B by now.
                var current = VfoIsB(receiver) ? _radioStateService.IfWidthB : _radioStateService.IfWidthA;
                return Ok(new { vfo = receiver.ToUpper(), code = current, rawResponse = response });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying IF Width");
                return StatusCode(500, new { error = "Failed to query IF Width" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("ifwidth/{receiver}")]
        public async Task<IActionResult> SetIfWidth(string receiver, [FromBody] IfWidthRequest request)
        {
            // 0-99 allows probing post-firmware codes beyond the official 0-25 range.
            if (!int.TryParse(request.Code, out int codeNum) || codeNum < 0 || codeNum > 99)
                return BadRequest(new { error = $"Invalid IF Width code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"SH{VfoP1Outgoing(receiver)}0{int.Parse(request.Code):D2};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.IfWidthB = request.Code;
                else                  _radioStateService.IfWidthA = request.Code;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IF Width");
                return StatusCode(500, new { error = "Failed to set IF Width" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("ifshift/{receiver}")]
        public async Task<IActionResult> SetIfShift(string receiver, [FromBody] IfShiftRequest request)
        {
            if (request.ShiftHz < -1000 || request.ShiftHz > 1000)
                return BadRequest(new { error = "IF Shift must be -1000 to +1000 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var sign = request.ShiftHz >= 0 ? '+' : '-';
                var abs = Math.Abs(request.ShiftHz);
                await _catClient.SendCommandAsync($"IS{VfoP1Outgoing(receiver)}0{sign}{abs:D4};", "WebUI", CancellationToken.None);
                if (VfoIsB(receiver)) _radioStateService.IfShiftB = request.ShiftHz;
                else                  _radioStateService.IfShiftA = request.ShiftHz;
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IF Shift");
                return StatusCode(500, new { error = "Failed to set IF Shift" });
            }
            finally { _requestSemaphore.Release(); }
        }

        public class ContourRequest
        {
            public bool On { get; set; }
            public int FreqHz { get; set; } = 800;
        }

        public class ApfRequest
        {
            public bool On { get; set; }
            public int FreqHz { get; set; } = 0;
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

        [HttpPost("contour/{receiver}")]
        public async Task<IActionResult> SetContour(string receiver, [FromBody] ContourRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";
                // VfoP1Outgoing forces "0" on every single-receiver model
                // (including FTDX3000 which is also single-receiver per
                // RadioCapabilities), so the FTDX3000 special-case can use
                // it too -- the original code's special-case existed because
                // the CO command itself has a different shape on FTDX3000,
                // not because of P1 routing differences.
                string p1 = isFtdx3000 ? "0" : VfoP1Outgoing(receiver);

                if (isFtdx3000)
                {
                    // Mode: 00=off, 01=contour on, 02=APF on
                    string mode = request.On ? "01" : "00";
                    await _catClient.SendCommandAsync($"CO00{mode};", "WebUI", CancellationToken.None);
                    int vv = Math.Max(1, Math.Min(40, request.FreqHz / 100));
                    await _catClient.SendCommandAsync($"CO01{vv:D2};", "WebUI", CancellationToken.None);
                }
                else
                {
                    int freq = Math.Max(100, Math.Min(3200, request.FreqHz));
                    await _catClient.SendCommandAsync($"CO{p1}0000{(request.On ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync($"CO{p1}1{freq:D4};", "WebUI", CancellationToken.None);
                }

                if (VfoIsB(receiver)) { _radioStateService.ContourOnB = request.On; _radioStateService.ContourFreqB = request.FreqHz; }
                else                  { _radioStateService.ContourOnA = request.On; _radioStateService.ContourFreqA = request.FreqHz; }

                if (isFtdx3000 && request.On)
                {
                    _radioStateService.ApfOnA = false;
                    _radioStateService.ApfOnB = false;
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Contour");
                return StatusCode(500, new { error = "Failed to set Contour" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("apf/{receiver}")]
        public async Task<IActionResult> SetApf(string receiver, [FromBody] ApfRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool isFtdx3000 = settings.RadioModel == "FTDX3000";
                string p1 = isFtdx3000 ? "0" : VfoP1Outgoing(receiver);

                if (isFtdx3000)
                {
                    string mode = request.On ? "02" : "00";
                    await _catClient.SendCommandAsync($"CO00{mode};", "WebUI", CancellationToken.None);
                    int vv = Math.Max(0, Math.Min(20, (request.FreqHz / 25) + 10));
                    await _catClient.SendCommandAsync($"CO02{vv:D2};", "WebUI", CancellationToken.None);
                }
                else
                {
                    int vvvv = Math.Max(0, Math.Min(50, (request.FreqHz / 10) + 25));
                    await _catClient.SendCommandAsync($"CO{p1}2000{(request.On ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync($"CO{p1}3{vvvv:D4};", "WebUI", CancellationToken.None);
                }

                if (VfoIsB(receiver)) { _radioStateService.ApfOnB = request.On; _radioStateService.ApfFreqB = request.FreqHz; }
                else                  { _radioStateService.ApfOnA = request.On; _radioStateService.ApfFreqA = request.FreqHz; }

                if (isFtdx3000 && request.On)
                {
                    _radioStateService.ContourOnA = false;
                    _radioStateService.ContourOnB = false;
                }

                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting APF");
                return StatusCode(500, new { error = "Failed to set APF" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("clarifier")]
        public async Task<IActionResult> SetClarifier([FromBody] ClarifierRequest request)
        {
            if (request.OffsetHz < -9990 || request.OffsetHz > 9990)
                return BadRequest(new { error = "Clarifier offset must be -9990 to +9990 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool useCf = settings.RadioModel is "FTdx10" or "FT-710";
                string p1 = request.Vfo == "B" ? "1" : "0";

                if (useCf)
                {
                    int rxBit = request.RxOn ? 1 : 0;
                    int txBit = request.TxOn ? 1 : 0;
                    await _catClient.SendCommandAsync($"CF{p1}00{rxBit}{txBit}000;", "WebUI", CancellationToken.None);
                    string sign = request.OffsetHz >= 0 ? "+" : "-";
                    await _catClient.SendCommandAsync($"CF{p1}01{sign}{Math.Abs(request.OffsetHz):D4};", "WebUI", CancellationToken.None);
                }
                else
                {
                    await _catClient.SendCommandAsync($"RT{(request.RxOn ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync($"XT{(request.TxOn ? 1 : 0)};", "WebUI", CancellationToken.None);
                    await _catClient.SendCommandAsync("RC;", "WebUI", CancellationToken.None);
                    if (request.OffsetHz > 0)
                        await _catClient.SendCommandAsync($"RU{request.OffsetHz:D4};", "WebUI", CancellationToken.None);
                    else if (request.OffsetHz < 0)
                        await _catClient.SendCommandAsync($"RD{Math.Abs(request.OffsetHz):D4};", "WebUI", CancellationToken.None);
                }

                if (request.Vfo == "B") _radioStateService.ClarifierOffsetB = request.OffsetHz;
                else                     _radioStateService.ClarifierOffsetA = request.OffsetHz;
                _radioStateService.RxClarOn = request.RxOn;
                _radioStateService.TxClarOn = request.TxOn;
                return Ok(new { message = "Clarifier updated" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting clarifier");
                return StatusCode(500, new { error = "Failed to set clarifier" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("clarifier/nudge")]
        public async Task<IActionResult> NudgeClarifier([FromBody] ClarifierNudgeRequest request)
        {
            int absHz = Math.Abs(request.DeltaHz);
            if (absHz == 0 || absHz > 9990)
                return BadRequest(new { error = "DeltaHz must be 1–9990 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                bool useCf = settings.RadioModel is "FTdx10" or "FT-710";
                string p1 = request.Vfo == "B" ? "1" : "0";

                int currentOffset = request.Vfo == "B" ? _radioStateService.ClarifierOffsetB : _radioStateService.ClarifierOffsetA;
                int newOffset = Math.Max(-9990, Math.Min(9990, currentOffset + request.DeltaHz));

                if (useCf)
                {
                    string sign = newOffset >= 0 ? "+" : "-";
                    await _catClient.SendCommandAsync($"CF{p1}01{sign}{Math.Abs(newOffset):D4};", "WebUI", CancellationToken.None);
                }
                else
                {
                    // RU/RD are incremental — send only the delta, no RC clear
                    if (request.DeltaHz > 0)
                        await _catClient.SendCommandAsync($"RU{absHz:D4};", "WebUI", CancellationToken.None);
                    else
                        await _catClient.SendCommandAsync($"RD{absHz:D4};", "WebUI", CancellationToken.None);
                }

                if (request.Vfo == "B") _radioStateService.ClarifierOffsetB = newOffset;
                else                     _radioStateService.ClarifierOffsetA = newOffset;
                return Ok(new { offsetHz = newOffset });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error nudging clarifier");
                return StatusCode(500, new { error = "Failed to nudge clarifier" });
            }
            finally { _requestSemaphore.Release(); }
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

        // --- NR LEVEL (DNR algorithm on FTdx10) ---
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
        public class CwPitchRequest { public int Code { get; set; } = 30; }

        [HttpPost("cwpitch")]
        public async Task<IActionResult> SetCwPitch([FromBody] CwPitchRequest request)
        {
            if (request.Code < 0 || request.Code > 75)
                return BadRequest(new { error = "CW pitch code must be 0–75 (300–1050 Hz)" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"KP{request.Code:D2};", "WebUI", CancellationToken.None);
                _radioStateService.CwPitch = request.Code;
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
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync(request.On ? "ML0001;" : "ML0000;", "WebUI", CancellationToken.None);
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
                await EnsureConnectedAsync();
                var vfo = receiver.ToUpper() == "A" ? "0" : "1";
                await _catClient.SendCommandAsync($"ML{vfo}{request.Level:D3};", "WebUI", CancellationToken.None);
                if (vfo == "0") _radioStateService.MonitorLevelA = request.Level;
                else            _radioStateService.MonitorLevelB = request.Level;
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
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"VX{(request.On ? 1 : 0)};", "WebUI", CancellationToken.None);
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
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"VG{request.Gain:D3};", "WebUI", CancellationToken.None);
                _radioStateService.VoxGain = request.Gain;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting VOX gain"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("vox/delay")]
        public async Task<IActionResult> SetVoxDelay([FromBody] VoxDelayRequest request)
        {
            if (request.Delay < 0 || request.Delay > 2500)
                return BadRequest(new { error = "Delay 0–2500 ms" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"VD{request.Delay:D4};", "WebUI", CancellationToken.None);
                _radioStateService.VoxDelay = request.Delay;
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
                await EnsureConnectedAsync();
                // Anti-VOX is typically stored in menu — store locally only
                _radioStateService.AntiVoxGain = request.Gain;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting anti-VOX gain"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- FM REPEATER ---
        public class FmRepeaterRequest
        {
            public string ShiftDir { get; set; } = "0";
            public int OffsetHz { get; set; } = 600000;
            public string CtcssMode { get; set; } = "00";
            public string CtcssTone { get; set; } = "01";
        }

        [HttpPost("fmrepeater")]
        public async Task<IActionResult> SetFmRepeater([FromBody] FmRepeaterRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                if (new[] { "0", "1", "2", "3" }.Contains(request.ShiftDir))
                {
                    await _catClient.SendCommandAsync($"RS{request.ShiftDir};", "WebUI", CancellationToken.None);
                    _radioStateService.FmShiftDir = request.ShiftDir;
                }
                int offsetClamp = Math.Max(0, Math.Min(999999, request.OffsetHz));
                await _catClient.SendCommandAsync($"RO{offsetClamp:D6};", "WebUI", CancellationToken.None);
                _radioStateService.FmOffsetHz = offsetClamp;
                if (new[] { "00", "01", "02", "03" }.Contains(request.CtcssMode))
                {
                    await _catClient.SendCommandAsync($"CT{request.CtcssMode};", "WebUI", CancellationToken.None);
                    _radioStateService.CtcssMode = request.CtcssMode;
                }
                await _catClient.SendCommandAsync($"CN{request.CtcssTone};", "WebUI", CancellationToken.None);
                _radioStateService.CtcssTone = request.CtcssTone;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting FM repeater"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // --- CW KEYER ---
        public class CwSpeedRequest { public int Speed { get; set; } = 20; }
        public class CwBreakInRequest { public string Mode { get; set; } = "0"; }
        public class CwBreakInDelayRequest { public int DelayMs { get; set; } = 200; }

        [HttpPost("cw/speed")]
        public async Task<IActionResult> SetCwSpeed([FromBody] CwSpeedRequest request)
        {
            if (request.Speed < 4 || request.Speed > 60)
                return BadRequest(new { error = "CW speed 4–60 WPM" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"KS{request.Speed:D3};", "WebUI", CancellationToken.None);
                _radioStateService.CwSpeed = request.Speed;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW speed"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        // CW Auto Zero In — fire-and-forget trigger. Radio nudges the VFO so
        // the received CW signal sits exactly at the operator's preferred CW
        // pitch (the value set via KP). Requested by IK2XRW Alessandro (#55).
        //
        // Yaesu format: ZI{P1};
        //   P1 = 0  MAIN band (= VFO A on dual-receiver, the only receiver
        //           on single-receiver radios)
        //   P1 = 1  SUB band (FTdx101 only; rejected on single-receiver)
        //
        // {vfo} URL segment selects which VFO:
        //   "a"      → P1=0 explicitly                    (VFO A button)
        //   "b"      → P1=1 on dual-receiver, P1=0 forced on single-receiver
        //              (which silently rejects P1=1 on P1=0-Fixed commands)
        //   "active" → follow VS / single-receiver fall-back. Used by the
        //              CW Keyer popup button so one click does the right
        //              thing without needing to know which side is in focus.
        [HttpPost("cw/zin/{vfo}")]
        public async Task<IActionResult> CwZeroIn(string vfo)
        {
            string v = (vfo ?? "").Trim().ToLowerInvariant();
            if (v != "a" && v != "b" && v != "active")
                return BadRequest(new { error = "VFO must be 'a', 'b', or 'active'" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                string p1;
                if (v == "active")
                {
                    p1 = _radioStateService.IsSingleReceiver
                        ? "0"
                        : (_radioStateService.ActiveVfo == 1 ? "1" : "0");
                }
                else
                {
                    // Explicit per-VFO targeting. On single-receiver radios
                    // P1=1 is silently ignored by the radio firmware, so the
                    // VFO B button is functionally a no-op there — that's
                    // accurate to how the hardware behaves.
                    p1 = _radioStateService.IsSingleReceiver
                        ? "0"
                        : (v == "b" ? "1" : "0");
                }
                await _catClient.SendCommandAsync($"ZI{p1};", "WebUI", CancellationToken.None);
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error sending CW Zero In"); return StatusCode(500, new { error = "Failed" }); }
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
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"BI{request.Mode};", "WebUI", CancellationToken.None);
                _radioStateService.CwBreakIn = request.Mode;
                return Ok();
            }
            catch (Exception ex) { _logger.LogError(ex, "Error setting CW break-in"); return StatusCode(500, new { error = "Failed" }); }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("cw/breakindelay")]
        public async Task<IActionResult> SetCwBreakInDelay([FromBody] CwBreakInDelayRequest request)
        {
            if (request.DelayMs < 0 || request.DelayMs > 2500)
                return BadRequest(new { error = "Delay 0–2500 ms" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"SD{request.DelayMs:D4};", "WebUI", CancellationToken.None);
                _radioStateService.CwBreakInDelay = request.DelayMs;
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
            var clean = new string(request.Message.ToUpper().Where(c =>
                (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                c == ' ' || c == '?' || c == '/' || c == '.' || c == ','
            ).Take(24).ToArray());
            if (string.IsNullOrEmpty(clean))
                return BadRequest(new { error = "No valid CW characters" });
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"KY {clean};", "WebUI", CancellationToken.None);
                return Ok(new { sent = clean });
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

        // -- AUDIO FILTER (LCUT/HCUT FREQ + SLOPE per mode class) ------------
        //
        // Yaesu stores LCUT FREQ, LCUT SLOPE, HCUT FREQ, HCUT SLOPE as
        // per-mode-class menu values (one set per SSB/AM/FM/DATA/RTTY/CW),
        // accessed via the EX command. The address differs per radio model
        // and is looked up from AudioFilterMapService (which reads the
        // wwwroot/data/audio-filter-ex-map.json table sourced from each
        // radio's CAT manual).
        //
        // The {vfo} URL segment ("a" or "b") tells the controller which VFO's
        // *current mode* to look up. Since the radio stores values per
        // mode class (not per VFO), the actual EX address depends only on
        // the mode, not the VFO. When both VFOs share a mode they share the
        // values — the response includes vfoBMode/vfoAMode so the UI can
        // surface that to the user.

        public class AudioFilterValueResult
        {
            public string? Code { get; set; }     // raw P4 code from the radio, e.g. "05" or "0"
            public int?    Hz { get; set; }       // freq in Hz, or null for slopes / OFF / unsupported
            public string? Label { get; set; }    // human label (slope: "6 dB/oct"; freq: "300 Hz" or "OFF")
            public bool    Supported { get; set; }
        }

        public class AudioFilterReadResponse
        {
            public string  RadioModel       { get; set; } = "";
            public string  Vfo              { get; set; } = "";
            public string? VfoMode          { get; set; }   // friendly mode of the requested VFO
            public string? OtherVfoMode     { get; set; }   // friendly mode of the *other* VFO
            public string? ModeClass        { get; set; }   // SSB / AM / FM / DATA / RTTY / CW
            public string? OtherModeClass   { get; set; }   // mode class of the *other* VFO
            public bool    OtherVfoShares   { get; set; }   // true if other VFO is in same mode class
            public AudioFilterValueResult LcutFreq  { get; set; } = new();
            public AudioFilterValueResult LcutSlope { get; set; } = new();
            public AudioFilterValueResult HcutFreq  { get; set; } = new();
            public AudioFilterValueResult HcutSlope { get; set; } = new();
        }

        public class AudioFilterSetRequest
        {
            public string Code { get; set; } = "";   // raw P4 code, formatted to the right digit count
        }

        [HttpGet("audiofilter/{vfo}")]
        public async Task<IActionResult> ReadAudioFilter(string vfo)
        {
            var v = (vfo ?? "").Trim().ToUpperInvariant();
            if (v != "A" && v != "B") return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                var radioModel = settings.RadioModel ?? "";

                var resp = new AudioFilterReadResponse { RadioModel = radioModel, Vfo = v };

                if (!_audioFilterMap.IsRadioSupported(radioModel))
                {
                    // Not in the map — return a response with everything unsupported
                    // so the UI can grey out cleanly.
                    return Ok(resp);
                }

                resp.VfoMode        = v == "B" ? _radioStateService.ModeB : _radioStateService.ModeA;
                resp.OtherVfoMode   = v == "B" ? _radioStateService.ModeA : _radioStateService.ModeB;
                resp.ModeClass      = AudioFilterMapService.ModeClassFor(resp.VfoMode);
                resp.OtherModeClass = AudioFilterMapService.ModeClassFor(resp.OtherVfoMode);
                resp.OtherVfoShares = resp.ModeClass != null && resp.ModeClass == resp.OtherModeClass;

                if (resp.ModeClass == null)
                {
                    // Mode not yet known (radio still initialising) — return supported=false everywhere.
                    return Ok(resp);
                }

                resp.LcutFreq  = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "lcutFreq");
                resp.LcutSlope = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "lcutSlope");
                resp.HcutFreq  = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "hcutFreq");
                resp.HcutSlope = await ReadOneAudioFilterValue(radioModel, resp.ModeClass, "hcutSlope");

                return Ok(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading audio filter for VFO {Vfo}", v);
                return StatusCode(500, new { error = "Failed to read audio filter values" });
            }
            finally { _requestSemaphore.Release(); }
        }

        private async Task<AudioFilterValueResult> ReadOneAudioFilterValue(
            string radioModel, string modeClass, string setting)
        {
            var result = new AudioFilterValueResult { Supported = false };

            var addr = _audioFilterMap.GetAddress(radioModel, modeClass, setting);
            if (addr == null) return result;

            var readCmd = _audioFilterMap.BuildReadCommand(radioModel, addr);
            var response = await _catClient.SendCommandAsync(readCmd, "WebUI", CancellationToken.None);
            var code = _audioFilterMap.ParseAnswerValueCode(radioModel, addr, response ?? "");
            if (code == null) return result;

            result.Supported = true;
            result.Code      = code;
            DecorateValueResult(result, setting, code);
            return result;
        }

        // Adds Hz / Label fields based on the raw code and which setting it is.
        private void DecorateValueResult(AudioFilterValueResult result, string setting, string code)
        {
            var ranges = _audioFilterMap.ValueRanges;
            if (setting == "lcutFreq" || setting == "hcutFreq")
            {
                var r = setting == "lcutFreq" ? ranges.LcutFreq : ranges.HcutFreq;
                if (code == r.Off)
                {
                    result.Label = "OFF";
                    result.Hz = null;
                }
                else if (int.TryParse(code, out int n))
                {
                    var hz = r.Min.Hz + (n - int.Parse(r.Min.Code)) * r.StepHz;
                    result.Hz = hz;
                    result.Label = $"{hz} Hz";
                }
            }
            else if (setting == "lcutSlope" || setting == "hcutSlope")
            {
                var opt = ranges.Slope.Options.FirstOrDefault(o => o.Code == code);
                result.Label = opt?.Label ?? code;
            }
        }

        [HttpPost("audiofilter/{vfo}/{setting}")]
        public async Task<IActionResult> WriteAudioFilter(
            string vfo, string setting, [FromBody] AudioFilterSetRequest request)
        {
            var v = (vfo ?? "").Trim().ToUpperInvariant();
            if (v != "A" && v != "B") return BadRequest(new { error = "Invalid VFO (must be 'a' or 'b')" });

            var allowedSettings = new[] { "lcutFreq", "lcutSlope", "hcutFreq", "hcutSlope" };
            if (!allowedSettings.Contains(setting))
                return BadRequest(new { error = $"Invalid setting (must be one of: {string.Join(", ", allowedSettings)})" });

            if (request == null || string.IsNullOrWhiteSpace(request.Code))
                return BadRequest(new { error = "Missing value code" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var settings = await _settingsService.GetSettingsAsync();
                var radioModel = settings.RadioModel ?? "";

                if (!_audioFilterMap.IsRadioSupported(radioModel))
                    return BadRequest(new { error = $"Audio filter not supported on radio model '{radioModel}'" });

                var mode = v == "B" ? _radioStateService.ModeB : _radioStateService.ModeA;
                var modeClass = AudioFilterMapService.ModeClassFor(mode);
                if (modeClass == null)
                    return StatusCode(503, new { error = "Radio mode not yet known; try again shortly" });

                var addr = _audioFilterMap.GetAddress(radioModel, modeClass, setting);
                if (addr == null)
                    return BadRequest(new { error = $"{setting} is not exposed for {modeClass} mode on {radioModel}" });

                if (!ValidateValueCode(setting, request.Code))
                    return BadRequest(new { error = $"Invalid value code '{request.Code}' for {setting}" });

                var cmd = _audioFilterMap.BuildSetCommand(radioModel, addr, request.Code);
                await _catClient.SendCommandAsync(cmd, "WebUI", CancellationToken.None);

                // Re-read to confirm the radio accepted the write — EX writes
                // are documented as brittle, so we surface what the radio
                // actually stored rather than just trusting our request.
                var readCmd  = _audioFilterMap.BuildReadCommand(radioModel, addr);
                var response = await _catClient.SendCommandAsync(readCmd, "WebUI", CancellationToken.None);
                var actual   = _audioFilterMap.ParseAnswerValueCode(radioModel, addr, response ?? "");

                var result = new AudioFilterValueResult { Supported = true };
                if (actual != null)
                {
                    result.Code = actual;
                    DecorateValueResult(result, setting, actual);
                }
                else
                {
                    result.Code = request.Code;
                    DecorateValueResult(result, setting, request.Code);
                }

                return Ok(new { setting, modeClass, result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing audio filter {Setting} for VFO {Vfo}", setting, v);
                return StatusCode(500, new { error = "Failed to write audio filter value" });
            }
            finally { _requestSemaphore.Release(); }
        }

        // Sanity-check the value code against the relevant value range.
        private bool ValidateValueCode(string setting, string code)
        {
            var ranges = _audioFilterMap.ValueRanges;
            if (setting == "lcutFreq" || setting == "hcutFreq")
            {
                var r = setting == "lcutFreq" ? ranges.LcutFreq : ranges.HcutFreq;
                if (code == r.Off) return true;
                if (!int.TryParse(code, out int n)) return false;
                if (!int.TryParse(r.Min.Code, out int min)) return false;
                if (!int.TryParse(r.Max.Code, out int max)) return false;
                return n >= min && n <= max && code.Length == r.Digits;
            }
            if (setting == "lcutSlope" || setting == "hcutSlope")
            {
                return ranges.Slope.Options.Any(o => o.Code == code);
            }
            return false;
        }
    }
}
