using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using FTdx101_WebApp.Hubs;
using System.Diagnostics; // Place at the top of the file if not already present

namespace FTdx101_WebApp.Services
{
    public class RadioInitializationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly BrowserLauncher _browserLauncher;
        private readonly CatMultiplexerService _multiplexer;

        public RadioInitializationService(
            IServiceProvider serviceProvider,
            IHubContext<RadioHub> hubContext,
            BrowserLauncher browserLauncher,
            CatMultiplexerService multiplexer)
        {
            _serviceProvider = serviceProvider;
            _hubContext = hubContext;
            _browserLauncher = browserLauncher;
            _multiplexer = multiplexer;
        }

        public async Task InitializeRadioAsync()
        {
            await ExecuteInitializationAsync(CancellationToken.None);
        }

        private async Task ExecuteInitializationAsync(CancellationToken stoppingToken)
        {
            ILogger<RadioInitializationService>? logger = null; // Make logger nullable
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var multiplexer = _multiplexer;
                var radioStateService = scope.ServiceProvider.GetRequiredService<RadioStateService>();
                var statePersistence = scope.ServiceProvider.GetRequiredService<RadioStatePersistenceService>();
                logger = scope.ServiceProvider.GetRequiredService<ILogger<RadioInitializationService>>();

                var settings = await settingsService.GetSettingsAsync();

                // Check if COM port is configured - if not, redirect to Settings
                if (string.IsNullOrWhiteSpace(settings.SerialPort) || settings.SerialPort == "Not Set")
                {
                    logger.LogWarning("[RadioInitializationService] No COM port configured - redirecting to Settings");
                    AppStatus.InitializationStatus = "error";
                    await _hubContext.Clients.All.SendAsync("ShowSettingsPage");
                    if (!Debugger.IsAttached &&
                        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                    {
                        _browserLauncher.OpenOnce("http://localhost:8080/Settings");
                    }
                    return;
                }

                logger.LogInformation("Attempting to connect to radio on port {SerialPort} at baud {BaudRate}", settings.SerialPort, settings.BaudRate);

                try
                {
                    await multiplexer.ConnectAsync(settings.SerialPort, settings.BaudRate);
                }
                catch (Exception connEx)
                {
                    // COM port error (wrong port, port in use, etc.) - go to Settings
                    logger.LogError(connEx, "[RadioInitializationService] Failed to open COM port {SerialPort} - redirecting to Settings", settings.SerialPort);
                    AppStatus.InitializationStatus = "error";
                    await _hubContext.Clients.All.SendAsync("ShowSettingsPage");
                    if (!Debugger.IsAttached &&
                        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                    {
                        _browserLauncher.OpenOnce("http://localhost:8080/Settings");
                    }
                    return;
                }

                logger.LogInformation("Disabling auto information...");
                await multiplexer.DisableAutoInformationAsync();

                logger.LogInformation("Sending FA; command...");
                var faResponse = await multiplexer.SendCommandAsync("FA;", "Initialization", stoppingToken);
                if (string.IsNullOrWhiteSpace(faResponse) || !faResponse.StartsWith("FA"))
                {
                    // Radio not responding - likely OFF. Attempt to power on.
                    logger.LogWarning("[RadioInitializationService] Radio not responding to FA;. Attempting to power on...");
                    await multiplexer.SendCommandAsync("PS1;", "Initialization", stoppingToken); // Power ON
                    await Task.Delay(1200, stoppingToken); // Wait for radio to power up (empirical: 1.2s)
                    logger.LogInformation("Retrying FA; after power on attempt...");
                    faResponse = await multiplexer.SendCommandAsync("FA;", "Initialization", stoppingToken);

                    if (string.IsNullOrWhiteSpace(faResponse) || !faResponse.StartsWith("FA"))
                    {
                        // Still no response, treat as OFF
                        logger.LogWarning("[RadioInitializationService] Radio still not responding after power on attempt. User can power on via UI.");
                        radioStateService.RadioPowerOn = false;
                        AppStatus.InitializationStatus = "radio_off";
                        await _hubContext.Clients.All.SendAsync("InitializationStatus", "radio_off");

                        // Open Settings page if radio is off
                        if (!Debugger.IsAttached && 
                            string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                        {
                            _browserLauncher.OpenOnce("http://localhost:8080/Settings");
                        }
                        return;
                    }
                    else
                    {
                        logger.LogInformation("[RadioInitializationService] Radio responded to FA; after power on attempt: {Response}", faResponse);
                    }
                }

                // Radio responded - it's ON
                radioStateService.RadioPowerOn = true;
                logger.LogInformation("[RadioInitializationService] Radio responded to FA;: {Response}", faResponse);

                // Send initialization commands and wait for DT0 response (with timeout)
                logger.LogInformation("[RadioInitializationService] Sending full initialization sequence and waiting for DT0 (timeout 5s)...");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);

                try
                {
                    await multiplexer.InitializeRadioAsync();
                    logger.LogInformation("[RadioInitializationService] ✓ DT0 received, initialization sequence complete.");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    logger.LogWarning("[RadioInitializationService] ⚠ Timeout waiting for DT0 response - continuing anyway");
                }

                // 2. Load persisted state from .json
                var persistedState = statePersistence.Load();
                logger.LogInformation("[RadioInitializationService] Persisted values before initialization: " +
                    "ModeA={ModeA}, ModeB={ModeB}, Power={Power}, AntennaA={AntennaA}, AntennaB={AntennaB}, MicGain={MicGain}",
                    persistedState.ModeA, persistedState.ModeB, persistedState.Power, persistedState.AntennaA, persistedState.AntennaB, persistedState.MicGain);

                // 3. Send only non-empty/non-zero values to the radio (parallelized)
                var stateTasks = new List<Task>();
                if (!string.IsNullOrEmpty(persistedState.ModeA))
                {
                    logger.LogInformation("About to send ModeA={ModeA} to radio", persistedState.ModeA);
                    stateTasks.Add(multiplexer.SendCommandAsync(CatCommands.FormatMode(persistedState.ModeA, false), "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.ModeA = persistedState.ModeA; }));
                }
                if (!string.IsNullOrEmpty(persistedState.ModeB))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync(CatCommands.FormatMode(persistedState.ModeB, true), "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.ModeB = persistedState.ModeB; }));
                }
                if (persistedState.Power > 0)
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"PC{persistedState.Power};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.Power = persistedState.Power; }));
                }
                if (!string.IsNullOrEmpty(persistedState.AntennaA))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"AN0{persistedState.AntennaA};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AntennaA = persistedState.AntennaA; }));
                }
                if (!string.IsNullOrEmpty(persistedState.AntennaB))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"AN1{persistedState.AntennaB};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AntennaB = persistedState.AntennaB; }));
                }
                // Restore AF Gain
                if (persistedState.AfGainA >= 0 && persistedState.AfGainA <= 255)
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"AG0{persistedState.AfGainA:D3};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AfGainA = persistedState.AfGainA; }));
                }
                if (persistedState.AfGainB >= 0 && persistedState.AfGainB <= 255)
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"AG1{persistedState.AfGainB:D3};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.AfGainB = persistedState.AfGainB; }));
                }
                // Restore MIC Gain
                if (persistedState.MicGain >= 0 && persistedState.MicGain <= 100)
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"MG{persistedState.MicGain:D3};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.MicGain = persistedState.MicGain; }));
                }
                // Restore IF Width
                if (!string.IsNullOrEmpty(persistedState.IfWidthA))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"SH00{int.Parse(persistedState.IfWidthA):D2};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.IfWidthA = persistedState.IfWidthA; }));
                }
                if (!string.IsNullOrEmpty(persistedState.IfWidthB))
                {
                    stateTasks.Add(multiplexer.SendCommandAsync($"SH10{int.Parse(persistedState.IfWidthB):D2};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.IfWidthB = persistedState.IfWidthB; }));
                }
                // Restore IF Shift
                {
                    var signA = persistedState.IfShiftA >= 0 ? '+' : '-';
                    var absA = Math.Abs(persistedState.IfShiftA);
                    stateTasks.Add(multiplexer.SendCommandAsync($"IS00{signA}{absA:D4};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.IfShiftA = persistedState.IfShiftA; }));
                }
                {
                    var signB = persistedState.IfShiftB >= 0 ? '+' : '-';
                    var absB = Math.Abs(persistedState.IfShiftB);
                    stateTasks.Add(multiplexer.SendCommandAsync($"IS10{signB}{absB:D4};", "Initialization", stoppingToken)
                        .ContinueWith(t => { if (!t.IsFaulted) radioStateService.IfShiftB = persistedState.IfShiftB; }));
                }
                await Task.WhenAll(stateTasks);

                // 4. Read actual radio state (frequencies, band, etc.) before marking initialized
                logger.LogInformation("[RadioInitializationService] Reading actual radio state...");

                // Query VFO A frequency
                var faFreqResponse = await multiplexer.SendCommandAsync("FA;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(faFreqResponse) && faFreqResponse.StartsWith("FA"))
                {
                    var freqStr = faFreqResponse.Substring(2).TrimEnd(';');
                    if (int.TryParse(freqStr, out int freqHz))
                    {
                        radioStateService.FrequencyA = freqHz;
                        logger.LogInformation("[RadioInitializationService] VFO A frequency: {FreqHz} Hz", freqHz);
                    }
                }

                // Query VFO B frequency  
                var fbFreqResponse = await multiplexer.SendCommandAsync("FB;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(fbFreqResponse) && fbFreqResponse.StartsWith("FB"))
                {
                    var freqStr = fbFreqResponse.Substring(2).TrimEnd(';');
                    if (int.TryParse(freqStr, out int freqHz))
                    {
                        radioStateService.FrequencyB = freqHz;
                        logger.LogInformation("[RadioInitializationService] VFO B frequency: {FreqHz} Hz", freqHz);
                    }
                }

                // Query TX VFO (FT0 = VFO A is TX, FT1 = VFO B is TX)
                var ftResponse = await multiplexer.SendCommandAsync("FT;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(ftResponse) && ftResponse.StartsWith("FT"))
                {
                    var txVfoStr = ftResponse.Substring(2).TrimEnd(';');
                    if (int.TryParse(txVfoStr, out int txVfo))
                    {
                        radioStateService.TxVfo = txVfo;
                        logger.LogInformation("[RadioInitializationService] TX VFO: {TxVfo} ({VfoName})", txVfo, txVfo == 0 ? "VFO A" : "VFO B");
                    }
                }

                // Query Split mode (ST0=off, ST1=on, ST2=on+5kHz)
                var stResponse = await multiplexer.SendCommandAsync("ST;", "Initialization", stoppingToken);
                if (!string.IsNullOrWhiteSpace(stResponse) && stResponse.StartsWith("ST"))
                {
                    if (int.TryParse(stResponse.Substring(2, 1), out int splitMode))
                    {
                        radioStateService.SplitMode = splitMode;
                        logger.LogInformation("[RadioInitializationService] Split mode: {SplitMode}", splitMode);
                    }
                }

                // 5. Set IsInitialized = true FIRST to allow property changes to be persisted and broadcast
                radioStateService.IsInitialized = true;

                // Derive bands from the actual frequencies (must be AFTER IsInitialized = true)
                var bandA = radioStateService.GetBandFromFrequency(radioStateService.FrequencyA);
                var bandB = radioStateService.GetBandFromFrequency(radioStateService.FrequencyB);
                logger.LogInformation("[RadioInitializationService] Calculated bands from frequencies: A={BandA} ({FreqA} Hz), B={BandB} ({FreqB} Hz)",
                    bandA, radioStateService.FrequencyA, bandB, radioStateService.FrequencyB);

                radioStateService.SetBand("A", bandA);
                radioStateService.SetBand("B", bandB);
                logger.LogInformation("[RadioInitializationService] Bands set: A={BandA}, B={BandB}", bandA, bandB);

                // 6. Enable auto information
                await multiplexer.EnableAutoInformationAsync();

                logger.LogInformation("[RadioInitializationService] ✓ Radio connected, initialized, and Auto Information streaming enabled");
                AppStatus.InitializationStatus = "complete";
                logger.LogInformation("[RadioInitializationService] InitializationStatus set to complete");
                await _hubContext.Clients.All.SendAsync("InitializationStatus", "complete");

                // On success, open main page only in Production and not under debugger
                if (!Debugger.IsAttached && 
                    string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                {
                    _browserLauncher.OpenOnce("http://localhost:8080");
                }
            }
            catch (Exception ex)
            {
                AppStatus.InitializationStatus = "error";
                logger?.LogError(ex, "[RadioInitializationService] Radio initialization failed");
                await _hubContext.Clients.All.SendAsync("InitializationStatus", "error");
                await _hubContext.Clients.All.SendAsync("ShowSettingsPage");

                // On failure, open settings page only in Production and not under debugger
                if (!Debugger.IsAttached &&
                    string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase))
                {
                    _browserLauncher.OpenOnce("http://localhost:8080/Settings");
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await ExecuteInitializationAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Prevent app crash - log the error and set status
                Console.WriteLine($"[RadioInitializationService] Fatal error: {ex.Message}");
                AppStatus.InitializationStatus = "error";

                // Try to open Settings page even if initialization completely failed
                try
                {
                    _browserLauncher.OpenOnce("http://localhost:8080/Settings");
                }
                catch { /* Ignore browser launch errors */ }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await _multiplexer.DisconnectAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}