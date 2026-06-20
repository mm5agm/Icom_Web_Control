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
        private readonly CatMessageDispatcher _dispatcher;
        private readonly ILogger<MeterPollingService> _logger;
        private readonly ISettingsService _settingsService;

        // TX debounce: require 2 consecutive TX=false readings before declaring not-transmitting.
        // A single TX=false poll (e.g. from a stale CAT response mid-burst) would otherwise
        // clear the SWR rolling-average history and cause a momentary spike on the next reading.
        private int _txFalseCount = 0;
        private bool _stableIsTransmitting = false;

        // Antenna sync: the radio doesn't broadcast AN auto-information messages when the
        // operator changes the antenna on the front panel, so we have to poll. Polling on
        // every meter-cycle (2 Hz) would be wasteful for a near-static value, so we poll
        // every Nth cycle (~2 seconds at the current loop cadence). Keeps the YWC antenna
        // selector in sync with the radio without saturating the serial bus.
        private int _antennaPollCounter = 0;
        private const int AntennaPollEvery = 4;

        // Connection health: track consecutive null poll responses to detect radio power-off.
        // The serial port stays "open" on Windows when the radio powers off; null responses (timeouts)
        // are the only signal. After 5 consecutive nulls (~2.5 s) we broadcast disconnected.
        private int _consecutiveNullCount = 0;
        private const int DisconnectThreshold = 5;

        public MeterPollingService(
            CatMultiplexerService multiplexer,
            RadioStateService stateService,
            CatMessageDispatcher dispatcher,
            ILogger<MeterPollingService> logger,
            ISettingsService settingsService)
        {
            _multiplexer = multiplexer;
            _stateService = stateService;
            _dispatcher = dispatcher;
            _logger = logger;
            _settingsService = settingsService;
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
                    // Don't count null responses toward disconnect while transmitting — the radio may
                    // be too busy handling TX to answer CAT queries (hardware PTT in particular).
                    if (txResponse == null)
                    {
                        if (_stateService.IsConnected && !_stableIsTransmitting)
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
                    else if (txResponse != null)
                    {
                        // Only count an explicit TX0 response toward the debounce — a null response
                        // (radio busy / no reply) should not be treated as "not transmitting".
                        _txFalseCount++;
                        if (_txFalseCount >= 5)
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

                    // SMeterB is no longer displayed -- v2.3.9 moved the S-meter
                    // (and its history strip) into the top meter row as a
                    // single shared gauge. Yaesu radios only have one calibrated
                    // S-meter: on dual-receiver FTdx101MP/D it's tied to MAIN,
                    // on single-receiver radios there's only one receiver. So
                    // we skip the SM1; query entirely (it returns junk on
                    // FTdx101 and nothing on single-receiver radios anyway).
                    // SMeterB stays at its default for any leftover consumers
                    // (rigctld doesn't read it); the front-end ignores it.

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

                    // SWR and Compression meter polling differs by radio model.
                    //
                    //  - FTdx101MP / FTdx101D: RM6 (the documented SWR read) returns
                    //    stale/wrong values, so we have to set both meter slots via
                    //    MS13 (compression-left, SWR-right) and then read both at once
                    //    with RM0. Pre-existing workaround, do not regress.
                    //
                    //  - FTdx10 / FT-710 / FTDX3000: MS13+RM0 doesn't read SWR
                    //    correctly (reported by OE5HMR on FTdx10, 2026-06-01).
                    //    Reverting to the documented direct reads — RM3 for
                    //    compression, RM6 for SWR — which is what Yaesu's CAT
                    //    manual specifies for these radios.
                    var settings = await _settingsService.GetSettingsAsync();
                    bool useRm0Pair = settings.RadioModel is "FTdx101MP" or "FTdx101D";
                    int compression;
                    int swr;
                    if (useRm0Pair)
                    {
                        _logger.LogInformation("[MeterPolling][DEBUG] Polling Compression+SWR (MS13+RM0)... TX={IsTransmitting}", isTransmitting);
                        await _multiplexer.SendCommandAsync(CatCommands.SetMetersCompAndSWR + ";", "MeterPoll", stoppingToken);
                        var compSwrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterBoth + ";", "MeterPoll", stoppingToken);
                        _logger.LogInformation("[MeterPolling][DEBUG] MS13+RM0 response: '{Raw}'", compSwrResponse);
                        compression = CatCommands.ParseRm0LeftMeter(compSwrResponse ?? "");
                        swr         = CatCommands.ParseRm0RightMeter(compSwrResponse ?? "");
                    }
                    else
                    {
                        _logger.LogInformation("[MeterPolling][DEBUG] Polling Compression (RM3) and SWR (RM6) directly... TX={IsTransmitting}", isTransmitting);
                        var compResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterComp + ";", "MeterPoll", stoppingToken);
                        _logger.LogInformation("[MeterPolling][DEBUG] RM3 response: '{Raw}'", compResponse);
                        compression = CatCommands.ParseMeterReading(compResponse ?? "");
                        var swrResponse = await _multiplexer.SendCommandAsync(CatCommands.MeterSWR + ";", "MeterPoll", stoppingToken);
                        _logger.LogInformation("[MeterPolling][DEBUG] RM6 response: '{Raw}'", swrResponse);
                        swr = CatCommands.ParseMeterReading(swrResponse ?? "");
                    }
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

                    // Antenna poll (low cadence — see field comment above).
                    if (++_antennaPollCounter >= AntennaPollEvery)
                    {
                        _antennaPollCounter = 0;
                        var an0 = await _multiplexer.SendCommandAsync("AN0;", "MeterPoll", stoppingToken);
                        if (!string.IsNullOrEmpty(an0)) _dispatcher.DispatchMessage(an0);
                        var an1 = await _multiplexer.SendCommandAsync("AN1;", "MeterPoll", stoppingToken);
                        if (!string.IsNullOrEmpty(an1)) _dispatcher.DispatchMessage(an1);
                    }

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

        // FTdx101 power-meter restore on shutdown (discussion #6, F1ubw) lives
        // in RadioInitializationService.StopAsync, not here — the hosted-service
        // reverse-shutdown order means MeterPollingService stops AFTER
        // RadioInitializationService has already disconnected the multiplexer,
        // so sending MS01 from this StopAsync would talk to a closed port (at
        // best a no-op, at worst a hang that stalls host shutdown).
    }
}
