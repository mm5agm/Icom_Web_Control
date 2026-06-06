// Yaesu Web Control – SDR Background Service
// Manages the SDR lifecycle: open device → configure → stream IQ → FFT → broadcast.
// No UI, no DOM. Publishes spectrum data via SignalR using the existing RadioHub
// and the same { property, value } message envelope that WsUpdatePipeline handles.
//
// Device selection is driven by ApplicationSettings.SdrDeviceKey.
// Keys prefixed with "sdrplay:" are handled by SdrplayDevice (sdrplay_api.dll).

using Yaesu_Web_Control.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Yaesu_Web_Control.Services.Sdr
{
    public sealed class SdrBackgroundService : BackgroundService
    {
        private readonly ISettingsService              _settings;
        private readonly IHubContext<RadioHub>          _hub;
        private readonly ILogger<SdrBackgroundService>  _logger;

        // Target broadcast rate.
        private const int FrameIntervalMs = 100;   // 10 fps

        // Delay before retrying after a device error.
        private const int RetryDelayMs = 5_000;

        // How long to wait between re-checking settings when no device is configured.
        private const int UnconfiguredPollMs = 10_000;

        // Re-broadcast "streaming" every N frames so clients that connect after startup
        // receive the current status without waiting for a device change event.
        private const int StatusHeartbeatFrames = 30;   // ~3 s at 10 fps

        private CancellationTokenSource _restartCts = new();

        public SdrBackgroundService(
            ISettingsService              settings,
            IHubContext<RadioHub>          hub,
            ILogger<SdrBackgroundService>  logger)
        {
            _settings = settings;
            _hub      = hub;
            _logger   = logger;
        }

        /// <summary>
        /// Cancels the current streaming session so it restarts immediately with fresh settings.
        /// Safe to call from any thread.
        /// </summary>
        public void RequestRestart()
        {
            var old = Interlocked.Exchange(ref _restartCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var config       = await _settings.GetSettingsAsync().ConfigureAwait(false);
                var restartToken = _restartCts.Token;
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, restartToken);

                if (string.IsNullOrWhiteSpace(config.SdrDeviceKey))
                {
                    await BroadcastStatus("unconfigured", stoppingToken).ConfigureAwait(false);
                    try { await Task.Delay(UnconfiguredPollMs, sessionCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    continue;
                }

                await RunStreamingSession(config, sessionCts.Token).ConfigureAwait(false);

                // Skip the retry delay when a span change triggered the restart.
                if (!stoppingToken.IsCancellationRequested && !restartToken.IsCancellationRequested)
                    await Task.Delay(RetryDelayMs, stoppingToken).ConfigureAwait(false);
            }
        }

        // ── Streaming session ────────────────────────────────────────────────────

        private async Task RunStreamingSession(
            Models.ApplicationSettings config,
            CancellationToken stoppingToken)
        {
            ISdrDevice? device = null;
            try
            {
                _logger.LogInformation("SDR: Opening '{Key}'", config.SdrDeviceKey);
                await BroadcastStatus("connecting", stoppingToken).ConfigureAwait(false);

                device = CreateDevice(config.SdrDeviceKey ?? "");
                device.Configure(config.SdrIfFrequencyHz, config.SdrSampleRateHz, config.SdrFftSize);
                device.StartStreaming();

                int     fftSize  = config.SdrFftSize;
                float[] iqBuffer = new float[fftSize * 2];

                _logger.LogInformation(
                    "SDR: Streaming '{Label}' — IF {IfHz} Hz, SR {Sr} Hz, FFT {Fft} pts",
                    device.Label, config.SdrIfFrequencyHz, config.SdrSampleRateHz, fftSize);

                await BroadcastStatus("streaming", stoppingToken).ConfigureAwait(false);

                int heartbeatCounter = 0;

                while (!stoppingToken.IsCancellationRequested)
                {
                    bool got = await device
                        .TryReadIqFrameAsync(iqBuffer, FrameIntervalMs * 2, stoppingToken)
                        .ConfigureAwait(false);

                    if (!got) continue;   // timeout — keep waiting

                    float[] bins = FftProcessor.ComputeSpectrum(iqBuffer, fftSize);

                    // Round to 1 decimal place before serialisation to keep JSON compact.
                    for (int i = 0; i < bins.Length; i++)
                        bins[i] = MathF.Round(bins[i], 1);

                    await _hub.Clients.All.SendAsync(
                        "RadioStateUpdate",
                        new
                        {
                            property = "SpectrumUpdate",
                            value = new
                            {
                                bins,
                                centreHz = config.SdrIfFrequencyHz,
                                spanHz   = config.SdrSampleRateHz
                            }
                        },
                        stoppingToken).ConfigureAwait(false);

                    // Periodic heartbeat so clients that connect after the initial
                    // "streaming" broadcast still learn the current status promptly.
                    if (++heartbeatCounter >= StatusHeartbeatFrames)
                    {
                        heartbeatCounter = 0;
                        await BroadcastStatus("streaming", stoppingToken).ConfigureAwait(false);
                    }

                    await Task.Yield();
                }
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogWarning("SDR: Required DLL not found — {Message}", ex.Message);
                await BroadcastStatus("nodll", stoppingToken).ConfigureAwait(false);

                // Long sleep — no point retrying if the DLL is missing.
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SDR: Streaming error — {Message}", ex.Message);
                await BroadcastStatus("disconnected", stoppingToken).ConfigureAwait(false);
                await BroadcastDetail(ex.Message, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                device?.Stop();
                device?.Dispose();
            }
        }

        // ── Device factory ────────────────────────────────────────────────────────

        private static ISdrDevice CreateDevice(string key)
        {
            if (key.StartsWith(SdrplayDevice.KeyPrefix, StringComparison.OrdinalIgnoreCase))
                return new SdrplayDevice(key);

            // Treat everything else as a SoapySDR kwargs string
            // (e.g. "driver=rtlsdr,label=...", "driver=airspy,serial=...")
            return new SoapySdrDevice(key);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private async Task BroadcastStatus(string status, CancellationToken ct)
        {
            try
            {
                await _hub.Clients.All.SendAsync(
                    "RadioStateUpdate",
                    new { property = "SdrStatus", value = status },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SDR: Failed to broadcast status '{Status}'", status);
            }
        }

        private async Task BroadcastDetail(string detail, CancellationToken ct)
        {
            try
            {
                await _hub.Clients.All.SendAsync(
                    "RadioStateUpdate",
                    new { property = "SdrError", value = detail },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { }
        }
    }
}
