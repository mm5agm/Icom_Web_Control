using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Background service that polls S-meters, power, and SWR independently of AI mode
    /// </summary>
    public class MeterPollingService : BackgroundService
    {
        private readonly CatMultiplexerService _multiplexer;
        private readonly RadioStateService _stateService;
        private readonly ILogger<MeterPollingService> _logger;

        // TX debounce: require 2 consecutive TX=false readings before declaring not-transmitting.
        // A single TX=false poll (e.g. from a stale CAT response mid-burst) would otherwise
        // clear the SWR rolling-average history and cause a momentary spike on the next reading.
        private int _txFalseCount = 0;
        private bool _stableIsTransmitting = false;

        // Connection health: track consecutive null poll responses to detect radio power-off.
        // The serial port stays "open" on Windows when the radio powers off; null responses (timeouts)
        // are the only signal. After 5 consecutive nulls (~2.5 s) we broadcast disconnected.
        private int _consecutiveNullCount = 0;
        private const int DisconnectThreshold = 5;

        public MeterPollingService(
            CatMultiplexerService multiplexer,
            RadioStateService stateService,
            ILogger<MeterPollingService> logger)
        {
            _multiplexer = multiplexer;
            _stateService = stateService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Meter polling service started (S-Meter, Power, SWR)");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("[MeterPolling][DEBUG] Polling TX status...");
                    var txResponse = await _multiplexer.SendCommandAsync("TX;", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] TX response: {0}", txResponse);

                    // Connection health tracking: only applies once the radio has been successfully
                    // initialised (IsConnected = true). Null responses mean no reply from the radio.
                    if (txResponse == null)
                    {
                        if (_stateService.IsConnected)
                        {
                            _consecutiveNullCount++;
                            if (_consecutiveNullCount >= DisconnectThreshold)
                                _stateService.IsConnected = false;
                        }
                    }
                    else
                    {
                        _consecutiveNullCount = 0;
                        if (!_stateService.IsConnected)
                            _stateService.IsConnected = true;
                    }

                    bool rawIsTransmitting = !string.IsNullOrEmpty(txResponse) && txResponse.Contains("TX1");
                    if (rawIsTransmitting)
                    {
                        _txFalseCount = 0;
                        _stableIsTransmitting = true;
                    }
                    else
                    {
                        _txFalseCount++;
                        if (_txFalseCount >= 2)
                            _stableIsTransmitting = false;
                    }
                    bool isTransmitting = _stableIsTransmitting;
                    _stateService.IsTransmitting = isTransmitting;
                    _logger.LogInformation("[MeterPolling] TX poll: raw='{Raw}', rawTX={RawTX}, stableTX={StableTX}", txResponse, rawIsTransmitting, isTransmitting);

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling S-Meter A...");
                    var smAResponse = await _multiplexer.SendCommandAsync(CatCommands.SMeterMain + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] S-Meter A response: {0}", smAResponse);
                    int sMeterA = CatCommands.ParseSMeter(smAResponse ?? "");
                    _stateService.SMeterA = sMeterA;

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling S-Meter B...");
                    var smBResponse = await _multiplexer.SendCommandAsync(CatCommands.SMeterSub + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] S-Meter B response: {0}", smBResponse);
                    int sMeterB = CatCommands.ParseSMeter(smBResponse ?? "");
                    _stateService.SMeterB = sMeterB;

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling Power Meter...");
                    var powerResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterPower + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] Power Meter response: {0}", powerResponse);
                    int power = CatCommands.ParseMeterReading(powerResponse ?? "");
                    _logger.LogInformation("[MeterPolling][DEBUG] TX={IsTransmitting} Power raw='{Raw}', parsed={Value}", isTransmitting, powerResponse, power);
                    if (isTransmitting)
                    {
                        _stateService.PowerMeter = power;
                        _logger.LogInformation("[MeterPolling] Power meter (TX): raw='{Raw}', value={Value}", powerResponse, power);
                    }
                    else
                    {
                        // Always broadcast PowerMeter=0 when not transmitting
                        if (_stateService.PowerMeter != 0)
                        {
                            _stateService.PowerMeter = 0;
                            _logger.LogInformation("[MeterPolling] Power meter (not TX): value=0");
                        }
                    }

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling Compression+SWR (MS13+RM0)... TX={IsTransmitting}", isTransmitting);
                    await _multiplexer.SendCommandAsync(CatCommands.SetMetersCompAndSWR + ";", "MeterPoll", stoppingToken);
                    var compSwrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterBoth + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] MS13+RM0 response: '{Raw}'", compSwrResponse);
                    int compression = CatCommands.ParseRm0LeftMeter(compSwrResponse ?? "");
                    int swr = CatCommands.ParseRm0RightMeter(compSwrResponse ?? "");
                    _logger.LogInformation("[MeterPolling][DEBUG] TX={IsTransmitting} Compression={Comp} SWR={SWR}", isTransmitting, compression, swr);
                    _stateService.SWRMeter = isTransmitting ? swr : 0;
                    if (isTransmitting)
                    {
                        _stateService.CompressionMeter = compression;
                    }
                    else
                    {
                        if (_stateService.CompressionMeter != 0)
                            _stateService.CompressionMeter = 0;
                    }

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling IDD Meter...");
                    var iddResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterIDD + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] IDD Meter response: {0}", iddResponse);
                    int idd = CatCommands.ParseMeterReading(iddResponse ?? "");
                    _stateService.IDDMeter = idd;

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling ALC Meter...");
                    var alcResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterALC + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] ALC Meter response: {0}", alcResponse);
                    int alc = CatCommands.ParseMeterReading(alcResponse ?? "");
                    if (isTransmitting)
                    {
                        _stateService.ALCMeter = alc;
                    }
                    else
                    {
                        if (_stateService.ALCMeter != 0)
                            _stateService.ALCMeter = 0;
                    }

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling VDD Meter...");
                    var vddResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterVDD + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] VDD Meter response: {0}", vddResponse);
                    int vdd = CatCommands.ParseMeterReading(vddResponse ?? "");
                    _stateService.VDDMeter = vdd;

                    _logger.LogInformation("[MeterPolling][DEBUG] Polling Temperature...");
                    var tempResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterTemp + ";", "MeterPoll", stoppingToken);
                    _logger.LogInformation("[MeterPolling][DEBUG] Temperature response: {0}", tempResponse);
                    int temp = CatCommands.ParseMeterReading(tempResponse ?? "");
                    _stateService.Temperature = temp;

                    await Task.Delay(500, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MeterPolling][FATAL] Exception in polling loop: {Message}\nStackTrace: {StackTrace}", ex.Message, ex.StackTrace);
                    try { await Task.Delay(500, stoppingToken); } catch (OperationCanceledException) { break; }
                }
            }
        }
    }
}
