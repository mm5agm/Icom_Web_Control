using Microsoft.AspNetCore.Mvc;
using Yaesu_Web_Control.Services;
using System.Text.Json;
using System.Threading;

namespace Yaesu_Web_Control.Controllers
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
        private readonly RadioInitializationService _radioInitService;
        private static readonly SemaphoreSlim _requestSemaphore = new(1, 1);

        [HttpPost("afgain/a")]
        public async Task<IActionResult> SetAfGainA([FromBody] int value)
        {
            if (value < 0 || value > 255)
                return BadRequest(new { error = "AF Gain value out of range (0-255)" });
            await EnsureConnectedAsync();
            await _catClient.SendCommandAsync($"AG0{value:D3};", "WebUI", CancellationToken.None);
            _radioStateService.AfGainA = value;
            return Ok(new { message = $"AF Gain {value} set for Receiver A" });
        }

        [HttpPost("afgain/b")]
        public async Task<IActionResult> SetAfGainB([FromBody] int value)
        {
            if (value < 0 || value > 255)
                return BadRequest(new { error = "AF Gain value out of range (0-255)" });
            await EnsureConnectedAsync();
            await _catClient.SendCommandAsync($"AG1{value:D3};", "WebUI", CancellationToken.None);
            _radioStateService.AfGainB = value;
            return Ok(new { message = $"AF Gain {value} set for Receiver B" });
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
                string command = request.Enabled ? "PR1;" : "PR0;";
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
                _radioStateService.ProcLevel = request.Value;
                return Ok(new { message = $"PROC level set to {request.Value}" });
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
                if (request.PowerOn)
                {
                    _logger.LogInformation("Turning radio ON...");
                    await _catClient.SendCommandAsync("PS1;", "WebUI", CancellationToken.None);
                    await Task.Delay(1500);
                    await _catClient.SendCommandAsync("PS1;", "WebUI", CancellationToken.None);
                    _radioStateService.RadioPowerOn = true;
                    _logger.LogInformation("Radio power ON command sent");
                    await Task.Delay(3000);
                    _logger.LogInformation("Re-initializing radio after power on...");
                    await _radioInitService.InitializeRadioAsync();
                    _logger.LogInformation("[API] SetRadioPower completed: powerOn=true");
                    return Ok(new { message = "Radio powered ON and initialized", powerOn = true });
                }
                else
                {
                    _logger.LogInformation("Turning radio OFF...");
                    await _catClient.SendCommandAsync("PS0;", "WebUI", CancellationToken.None);
                    _radioStateService.RadioPowerOn = false;
                    AppStatus.InitializationStatus = "radio_off";
                    _logger.LogInformation("Radio power OFF command sent");
                    _logger.LogInformation("[API] SetRadioPower completed: powerOn=false");
                    return Ok(new { message = "Radio powered OFF", powerOn = false });
                }
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
                if (request.Transmit)
                {
                    _logger.LogInformation("Turning TX ON...");
                    await _catClient.SendCommandAsync("TX1;", "WebUI", CancellationToken.None);
                    _radioStateService.IsTransmitting = true;
                    _logger.LogInformation("[API] ToggleTransmit completed: transmitting=true");
                    return Ok(new { message = "TX ON", transmitting = true });
                }
                else
                {
                    _logger.LogInformation("Turning TX OFF...");
                    await _catClient.SendCommandAsync("TX0;", "WebUI", CancellationToken.None);
                    _radioStateService.IsTransmitting = false;
                    _logger.LogInformation("[API] ToggleTransmit completed: transmitting=false");
                    return Ok(new { message = "TX OFF", transmitting = false });
                }
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
            RadioInitializationService radioInitService)
        {
            _catClient = catClient;
            _settingsService = settingsService;
            _logger = logger;
            _radioStateService = radioStateService;
            _statePersistence = statePersistence;
            _radioInitService = radioInitService;
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
                    roofingFilter = _radioStateService.RoofingFilterA ?? ""
                },
                vfoB = new
                {
                    frequency = _radioStateService.FrequencyB,
                    band = _radioStateService.BandB,
                    sMeter = _radioStateService.SMeterB ?? 0,
                    mode = _radioStateService.ModeB ?? "",
                    antenna = _radioStateService.AntennaB ?? "",
                    afGain = _radioStateService.AfGainB,
                    roofingFilter = _radioStateService.RoofingFilterB ?? ""
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
                await EnsureConnectedAsync();
                var freq = request.FrequencyHz;
                if (freq < 30000 || freq > 75000000)
                    return BadRequest(new { error = "Frequency out of range" });

                var command = $"FA{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.FrequencyA = freq;

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
                await EnsureConnectedAsync();
                var freq = request.FrequencyHz;
                if (freq < 30000 || freq > 75000000)
                    return BadRequest(new { error = "Frequency out of range" });

                var command = $"FB{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                _radioStateService.FrequencyB = freq;

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
        public async Task<IActionResult> SetBandA([FromBody] BandRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetBandA called: band={Band}", request.Band);
            try
            {
                await EnsureConnectedAsync();

                if (!BandFreqs.TryGetValue(request.Band, out var freq))
                    return BadRequest(new { error = "Invalid band" });

                var command = $"FA{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                var actualFreq = await _catClient.QueryFrequencyAAsync("WebUI", CancellationToken.None);

                _radioStateService.SetBand("A", request.Band);
                _radioStateService.FrequencyA = actualFreq;

                _logger.LogInformation("Set Receiver A band to {Band} (freq {Freq})", request.Band, actualFreq);
                _logger.LogInformation("[API] SetBandA completed: band={Band}, freq={Freq}", request.Band, actualFreq);
                return Ok(new { message = $"Band {request.Band} selected", frequency = actualFreq });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver A band");
                return StatusCode(500, new { error = "Failed to set band" });
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        [HttpPost("band/b")]
        public async Task<IActionResult> SetBandB([FromBody] BandRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            _logger.LogInformation("[API] SetBandB called: band={Band}", request.Band);
            try
            {
                await EnsureConnectedAsync();

                if (!BandFreqs.TryGetValue(request.Band, out var freq))
                    return BadRequest(new { error = "Invalid band" });

                var command = $"FB{freq:D9};";
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);

                var actualFreq = await _catClient.QueryFrequencyBAsync("WebUI", CancellationToken.None);

                _radioStateService.SetBand("B", request.Band);
                _radioStateService.FrequencyB = actualFreq;

                _logger.LogInformation("Set Receiver B band to {Band} (freq {Freq})", request.Band, actualFreq);
                _logger.LogInformation("[API] SetBandB completed: band={Band}, freq={Freq}", request.Band, actualFreq);
                return Ok(new { message = $"Band {request.Band} selected", frequency = actualFreq });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Receiver B band");
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

        // FTdx10 roofing filter display names (RU command code -> display name)
        private static readonly Dictionary<string, string> FtdxTenRoofingFilterNames = new()
        {
            { "1", "15 kHz" },
            { "2", "6 kHz" },
            { "3", "3 kHz" }
        };

        // FTDX3000 roofing filter display names (RF0x code -> display name)
        private static readonly Dictionary<string, string> Ftdx3000RoofingFilterNames = new()
        {
            { "0", "Auto" }, { "1", "15 kHz" }, { "2", "6 kHz" },
            { "3", "3 kHz" }, { "4", "600 Hz" }, { "5", "300 Hz" }
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

                if (isFtdx10 || isFt710)
                    return Ok(new { message = "Roofing filter is selected automatically by the radio" });

                if (isFtdx3000)
                {
                    // FTDX3000: P1 is always 0 (single receiver); code is the filter number directly
                    await _catClient.SendCommandAsync($"RF0{request.Filter};", "WebUI", CancellationToken.None);
                    await Task.Delay(100);
                    var readback = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);
                    var actualCode = readback?.Length >= 4 ? readback[3].ToString() : request.Filter;
                    var displayName = Ftdx3000RoofingFilterNames.GetValueOrDefault(actualCode, actualCode);
                    _radioStateService.RoofingFilterA = actualCode;
                    _logger.LogInformation("Set Main roofing filter (FTDX3000) to {Filter}", displayName);
                    return Ok(new { message = $"Roofing filter set to {displayName}", filter = actualCode, filterName = displayName });
                }

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

                if (isFtdx10 || isFt710)
                    return Ok(new { message = "Roofing filter is selected automatically by the radio" });

                if (isFtdx3000)
                {
                    // FTDX3000 has a single receiver — P1 is always 0; VFO B shares the same filter
                    await _catClient.SendCommandAsync($"RF0{request.Filter};", "WebUI", CancellationToken.None);
                    await Task.Delay(100);
                    var readback = await _catClient.SendCommandAsync("RF0;", "WebUI", CancellationToken.None);
                    var actualCode = readback?.Length >= 4 ? readback[3].ToString() : request.Filter;
                    var displayName = Ftdx3000RoofingFilterNames.GetValueOrDefault(actualCode, actualCode);
                    _radioStateService.RoofingFilterB = actualCode;
                    _logger.LogInformation("Set Sub roofing filter (FTDX3000) to {Filter}", displayName);
                    return Ok(new { message = $"Roofing filter set to {displayName}", filter = actualCode, filterName = displayName });
                }

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

        [HttpPost("mode/{receiver}")]
        public async Task<IActionResult> SetMode(string receiver, [FromBody] ModeRequest request)
        {
            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                string displayMode = CatCodeToMode.TryGetValue(request.Mode, out var modeName) ? modeName : request.Mode;

                bool vfoIsA = receiver.ToUpper() == "A";
                if (vfoIsA)
                {
                    await _catClient.SendCommandAsync($"MD0{request.Mode};", "User");
                    _radioStateService.ModeA = displayMode;
                }
                else if (receiver.ToUpper() == "B")
                {
                    await _catClient.SendCommandAsync($"MD1{request.Mode};", "User");
                    _radioStateService.ModeB = displayMode;
                }
                else
                {
                    return BadRequest(new { error = "Invalid receiver specified" });
                }

                // Re-apply Contour and APF state — mode changes on the FTdx101 cause the
                // radio to restore its per-mode Contour/APF settings, overriding what we have set.
                var modeSettings = await _settingsService.GetSettingsAsync();
                bool isFtdx3000 = modeSettings.RadioModel == "FTDX3000";
                string p1 = (!isFtdx3000 && !vfoIsA) ? "1" : "0";

                if (isFtdx3000)
                {
                    bool cOn = _radioStateService.ContourOnA;
                    bool aOn = _radioStateService.ApfOnA;
                    if (cOn)
                    {
                        await _catClient.SendCommandAsync("CO0001;", "User");
                        int vv = Math.Max(1, Math.Min(40, _radioStateService.ContourFreqA / 100));
                        await _catClient.SendCommandAsync($"CO01{vv:D2};", "User");
                    }
                    else if (aOn)
                    {
                        await _catClient.SendCommandAsync("CO0002;", "User");
                        int vv = Math.Max(0, Math.Min(20, (_radioStateService.ApfFreqA / 25) + 10));
                        await _catClient.SendCommandAsync($"CO02{vv:D2};", "User");
                    }
                    else
                    {
                        await _catClient.SendCommandAsync("CO0000;", "User");
                    }
                }
                else
                {
                    bool contourOn  = vfoIsA ? _radioStateService.ContourOnA  : _radioStateService.ContourOnB;
                    int  contourHz  = vfoIsA ? _radioStateService.ContourFreqA : _radioStateService.ContourFreqB;
                    bool apfOn      = vfoIsA ? _radioStateService.ApfOnA       : _radioStateService.ApfOnB;
                    int  apfHz      = vfoIsA ? _radioStateService.ApfFreqA     : _radioStateService.ApfFreqB;

                    int  cFreq = Math.Max(100, Math.Min(3200, contourHz));
                    await _catClient.SendCommandAsync($"CO{p1}0000{(contourOn ? 1 : 0)};", "User");
                    await _catClient.SendCommandAsync($"CO{p1}1{cFreq:D4};", "User");

                    int  aVvvv = Math.Max(0, Math.Min(50, (apfHz / 10) + 25));
                    await _catClient.SendCommandAsync($"CO{p1}2000{(apfOn ? 1 : 0)};", "User");
                    await _catClient.SendCommandAsync($"CO{p1}3{aVvvv:D4};", "User");
                }

                _logger.LogInformation("Sending CAT command: MD{Vfo}{Mode}; for Receiver {Receiver}", vfoIsA ? "0" : "1", request.Mode, receiver);
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
                await EnsureConnectedAsync();

                var settings = await _settingsService.GetSettingsAsync();
                int maxPower = settings.RadioModel == "FTdx101MP" ? 200 : 100;

                _logger.LogInformation("[API] Received SetPower request: receiver={Receiver}, Watts={Watts}, Model={Model}", receiver, request.Watts, settings.RadioModel);
                _logger.LogInformation("[API] DEBUG: Received slider value = {Watts}", request.Watts);

                if (request.Watts < 5 || request.Watts > maxPower)
                    return BadRequest(new { error = $"Power out of range (5-{maxPower}W for {settings.RadioModel})" });

                var command = $"PC{request.Watts:D3};";
                _logger.LogInformation("[API] Sending CAT command: {Command}", command);
                await _catClient.SendCommandAsync(command, "WebUI", CancellationToken.None);
                // Immediately send PC; to read back power
                var readResponse = await _catClient.SendCommandAsync("PC;", "WebUI", CancellationToken.None);
                int actualPower = ParsePower(readResponse ?? "");
                _logger.LogInformation("[Slider][CAT] Sent PC command: watts={Watts}, readback={Readback}, actualPower={ActualPower}", request.Watts, readResponse, actualPower);

                _logger.LogInformation("[API] Setting Power to {ActualPower}", actualPower);
                _radioStateService.Power = actualPower;

                _logger.LogInformation("[API] Power set to {Power}W on {RadioModel}", actualPower, settings.RadioModel);
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

        // Add this helper method (put it near other helper methods like ParseSMeter)
        private int ParsePower(string response)
        {
            // Response format: PC123; (3 digits for watts)
            if (response.Length >= 5 && response.StartsWith("PC"))
            {
                if (int.TryParse(response.Substring(2, 3), out int watts))
                {
                    return watts;
                }
            }
            return 100; // Default to 100W if can't parse
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
        public class NoiseBlankerRequest       { public string Enabled { get; set; } = "0"; }
        public class ManualNotchFreqRequest    { public int FrequencyHz { get; set; } = 1000; }
        public class IfWidthRequest            { public string Code { get; set; } = "8"; }
        public class IfShiftRequest            { public int ShiftHz { get; set; } = 0; }

        [HttpPost("agc/{receiver}")]
        public async Task<IActionResult> SetAgc(string receiver, [FromBody] AgcRequest request)
        {
            var validCodes = new[] { "0", "1", "2", "3", "4" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid AGC code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                await _catClient.SendCommandAsync($"GT{vfo}{request.Code};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.AgcA = request.Code;
                else            _radioStateService.AgcB = request.Code;

                return Ok(new { message = $"AGC {receiver} set to {request.Code}" });
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
            var validCodes = new[] { "0", "1", "2" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid IPO/AMP code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                await _catClient.SendCommandAsync($"PA{vfo}{request.Code};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.IpoA = request.Code;
                else            _radioStateService.IpoB = request.Code;

                return Ok(new { message = $"IPO/AMP {receiver} set to {request.Code}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting IPO/AMP");
                return StatusCode(500, new { error = "Failed to set IPO/AMP" });
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
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                await _catClient.SendCommandAsync($"BC{vfo}{request.Code};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.AutoNotchA = request.Code;
                else            _radioStateService.AutoNotchB = request.Code;

                return Ok(new { message = $"Auto Notch {receiver} set to {request.Code}" });
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
            var validCodes = new[] { "0", "1", "2" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid NR code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                await _catClient.SendCommandAsync($"NR{vfo}{request.Code};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.NrA = request.Code;
                else            _radioStateService.NrB = request.Code;

                return Ok(new { message = $"NR {receiver} set to {request.Code}" });
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
            var validCodes = new[] { "00", "06", "12", "18" };
            if (!validCodes.Contains(request.Code))
                return BadRequest(new { error = $"Invalid attenuator code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                var catCode = request.Code switch { "00" => "0", "06" => "1", "12" => "2", "18" => "3", _ => "0" };
                await _catClient.SendCommandAsync($"RA{vfo}{catCode};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.AttA = request.Code;
                else            _radioStateService.AttB = request.Code;

                return Ok(new { message = $"Attenuator {receiver} set to {request.Code}" });
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
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                var val = request.Enabled == "1" ? "001" : "000";
                await _catClient.SendCommandAsync($"BP{vfo}0{val};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.ManualNotchA = request.Enabled;
                else            _radioStateService.ManualNotchB = request.Enabled;

                return Ok(new { message = $"Manual Notch {receiver} set to {request.Enabled}" });
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
            if (request.FrequencyHz < 10 || request.FrequencyHz > 3200)
                return BadRequest(new { error = $"Notch frequency must be 10–3200 Hz" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });

            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                var catValue = request.FrequencyHz / 10;
                await _catClient.SendCommandAsync($"BP{vfo}1{catValue:D3};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.ManualNotchFreqA = request.FrequencyHz;
                else            _radioStateService.ManualNotchFreqB = request.FrequencyHz;

                return Ok(new { message = $"Manual Notch freq {receiver} set to {request.FrequencyHz} Hz" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Manual Notch frequency");
                return StatusCode(500, new { error = "Failed to set Manual Notch frequency" });
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
                await EnsureConnectedAsync();
                var vfo = receiver.Equals("A", StringComparison.OrdinalIgnoreCase) ? "0" : "1";
                await _catClient.SendCommandAsync($"NB{vfo}{request.Enabled};", "WebUI", CancellationToken.None);

                if (vfo == "0") _radioStateService.NbA = request.Enabled;
                else            _radioStateService.NbB = request.Enabled;

                return Ok(new { message = $"Noise Blanker {receiver} set to {request.Enabled}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting Noise Blanker");
                return StatusCode(500, new { error = "Failed to set Noise Blanker" });
            }
            finally { _requestSemaphore.Release(); }
        }

        [HttpPost("ifwidth/{receiver}")]
        public async Task<IActionResult> SetIfWidth(string receiver, [FromBody] IfWidthRequest request)
        {
            // Codes 0-25 cover all supported models (FTdx101=0-8, FTdx10=0-15, FT-710=0-22, FTDX3000=1-25)
            if (!int.TryParse(request.Code, out int codeNum) || codeNum < 0 || codeNum > 25)
                return BadRequest(new { error = $"Invalid IF Width code: {request.Code}" });

            if (!await _requestSemaphore.WaitAsync(2000))
                return StatusCode(503, new { error = "Radio busy" });
            try
            {
                await EnsureConnectedAsync();
                var vfo = receiver.ToUpper() == "A" ? "0" : "1";
                await _catClient.SendCommandAsync($"SH{vfo}0{int.Parse(request.Code):D2};", "WebUI", CancellationToken.None);
                if (vfo == "0") _radioStateService.IfWidthA = request.Code;
                else            _radioStateService.IfWidthB = request.Code;
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
                var vfo = receiver.ToUpper() == "A" ? "0" : "1";
                var sign = request.ShiftHz >= 0 ? '+' : '-';
                var abs = Math.Abs(request.ShiftHz);
                await _catClient.SendCommandAsync($"IS{vfo}0{sign}{abs:D4};", "WebUI", CancellationToken.None);
                if (vfo == "0") _radioStateService.IfShiftA = request.ShiftHz;
                else            _radioStateService.IfShiftB = request.ShiftHz;
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
                string p1 = (!isFtdx3000 && receiver.ToUpper() == "B") ? "1" : "0";

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

                if (receiver.ToUpper() == "B") { _radioStateService.ContourOnB = request.On; _radioStateService.ContourFreqB = request.FreqHz; }
                else                           { _radioStateService.ContourOnA = request.On; _radioStateService.ContourFreqA = request.FreqHz; }

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
                string p1 = (!isFtdx3000 && receiver.ToUpper() == "B") ? "1" : "0";

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

                if (receiver.ToUpper() == "B") { _radioStateService.ApfOnB = request.On; _radioStateService.ApfFreqB = request.FreqHz; }
                else                           { _radioStateService.ApfOnA = request.On; _radioStateService.ApfFreqA = request.FreqHz; }

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
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync($"ST{mode};", "WebUI", CancellationToken.None);

                // Read back actual state
                var stResponse = await _catClient.SendCommandAsync("ST;", "WebUI", CancellationToken.None);
                int actualMode = mode;
                if (!string.IsNullOrWhiteSpace(stResponse) && stResponse.StartsWith("ST"))
                    int.TryParse(stResponse.Substring(2, 1), out actualMode);
                _radioStateService.SplitMode = actualMode;

                // After Quick Split (ST2), read back VFO B so UI reflects the new +5 kHz frequency immediately
                if (mode == 2)
                {
                    var fbResponse = await _catClient.SendCommandAsync("FB;", "WebUI", CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(fbResponse) && fbResponse.StartsWith("FB") &&
                        long.TryParse(fbResponse.Substring(2).TrimEnd(';'), out long freqB))
                    {
                        _radioStateService.FrequencyB = freqB;
                    }
                }

                _logger.LogInformation("Split mode set to {Mode}", actualMode);
                return Ok(new { splitMode = actualMode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting split mode");
                return StatusCode(500, new { error = "Failed to set split mode" });
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
                await EnsureConnectedAsync();
                await _catClient.SendCommandAsync("SV;", "WebUI", CancellationToken.None);

                // Read back both frequencies immediately — auto-info will also arrive but this avoids UI flicker
                var faResponse = await _catClient.SendCommandAsync("FA;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(faResponse) && faResponse.StartsWith("FA") &&
                    long.TryParse(faResponse.Substring(2).TrimEnd(';'), out long freqA))
                {
                    _radioStateService.FrequencyA = freqA;
                }

                var fbResponse = await _catClient.SendCommandAsync("FB;", "WebUI", CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(fbResponse) && fbResponse.StartsWith("FB") &&
                    long.TryParse(fbResponse.Substring(2).TrimEnd(';'), out long freqB))
                {
                    _radioStateService.FrequencyB = freqB;
                }

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

        [HttpPost("reinitialize")]
        public async Task<IActionResult> Reinitialize()
        {
            try
            {
                _logger.LogInformation("Manual re-initialization requested from Settings page");
                await _radioInitService.InitializeRadioAsync();

                // Update status to complete so Index page polling sees it
                AppStatus.InitializationStatus = "complete";

                return Ok(new { success = true, message = "Radio connected successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual re-initialization failed");
                AppStatus.InitializationStatus = "error";
                return Ok(new { success = false, message = ex.Message });
            }
        }
    }
}
