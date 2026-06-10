// Yaesu Web Control — SdrManager
//
// Replaces SdrBackgroundService.
//
// Why this exists: the SDRplay API v3 service enforces one Selected device per
// host process. We can't open two RSPs from the main YWC process. So YWC main
// spawns a Yaesu_Sdr_Worker.exe per configured SDR; each worker holds exactly
// one device and streams FFT frames back to YWC over a localhost TCP
// connection. SdrManager supervises the workers and forwards their frames to
// SignalR using a per-VFO sdrId-tagged envelope so the frontend can route
// frames to the correct SpectrumPanel.
//
// See docs/decisions/0001-dual-sdr-architecture.md for the full reasoning.
//
// Envelope shape (v2.3.0+):
//   SpectrumUpdate value = { sdrId: "A"|"B", bins, centreHz, spanHz }
//   SdrStatus     value = { sdrId: "A"|"B", status: "..." }
//   SdrError      value = { sdrId: "A"|"B", error:  "..." }

using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services.Sdr;

public sealed class SdrManager : BackgroundService
{
    private readonly ISettingsService             _settings;
    private readonly IHubContext<RadioHub>        _hub;
    private readonly ILogger<SdrManager>          _logger;

    private const int RetryDelayMs           = 5_000;
    private const int UnconfiguredPollMs     = 10_000;
    private const int StatusHeartbeatFrames  = 30;     // ~3 s at 10 fps
    private const int WorkerConnectTimeoutMs = 10_000;

    private CancellationTokenSource _restartCts = new();

    // Device keys currently held by spawned workers, keyed by VFO id ("A"/"B").
    // Used by SdrController.GetDevices so the Settings page Scan can include
    // devices that workers have Selected (and which therefore disappear from
    // direct sdrplay_api_GetDevices enumeration). Updated under a lock so the
    // controller's read is consistent with concurrent session lifecycle.
    private readonly Dictionary<string, string> _activeDeviceKeys = new();
    private readonly object _activeLock = new();

    /// <summary>
    /// Snapshot of which device keys are currently being held by SDR workers.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetActiveDeviceKeys()
    {
        lock (_activeLock)
            return _activeDeviceKeys.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public SdrManager(
        ISettingsService             settings,
        IHubContext<RadioHub>        hub,
        ILogger<SdrManager>          logger)
    {
        _settings = settings;
        _hub      = hub;
        _logger   = logger;
    }

    /// <summary>
    /// Cancels the current sessions so they restart with fresh settings.
    /// Triggered when the user saves SDR-related settings.
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

            bool aConfigured = !string.IsNullOrWhiteSpace(config.SdrDeviceKeyA);
            bool bConfigured = !string.IsNullOrWhiteSpace(config.SdrDeviceKeyB);

            if (!aConfigured && !bConfigured)
            {
                await BroadcastStatus("A", "unconfigured", stoppingToken).ConfigureAwait(false);
                await BroadcastStatus("B", "unconfigured", stoppingToken).ConfigureAwait(false);
                try { await Task.Delay(UnconfiguredPollMs, sessionCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                continue;
            }

            // Tell the frontend explicitly when one side is unconfigured so the
            // corresponding panel can clear itself rather than show a stale state.
            if (!aConfigured) await BroadcastStatus("A", "unconfigured", stoppingToken).ConfigureAwait(false);
            if (!bConfigured) await BroadcastStatus("B", "unconfigured", stoppingToken).ConfigureAwait(false);

            // Run any configured sessions concurrently.
            var tasks = new List<Task>(2);
            if (aConfigured)
                tasks.Add(RunSessionAsync("A", config.SdrDeviceKeyA!, config, sessionCts.Token));
            if (bConfigured)
                tasks.Add(RunSessionAsync("B", config.SdrDeviceKeyB!, config, sessionCts.Token));

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Skip retry delay if a restart was requested (e.g. settings changed).
            if (!stoppingToken.IsCancellationRequested && !restartToken.IsCancellationRequested)
                await Task.Delay(RetryDelayMs, stoppingToken).ConfigureAwait(false);
        }
    }

    // ── One worker session, tagged with VFO label ────────────────────────────

    private async Task RunSessionAsync(
        string              vfo,
        string              deviceKey,
        ApplicationSettings config,
        CancellationToken   stoppingToken)
    {
        WorkerProcess? worker = null;
        TcpClient?     client = null;

        try
        {
            await BroadcastStatus(vfo, "connecting", stoppingToken).ConfigureAwait(false);

            worker = WorkerProcess.Start(
                _logger,
                vfo:           vfo,
                deviceKey:     deviceKey,
                ifFrequencyHz: config.SdrIfFrequencyHz,
                sampleRateHz:  config.SdrSampleRateHz,
                fftSize:       config.SdrFftSize);

            lock (_activeLock) _activeDeviceKeys[vfo] = deviceKey;

            client = await ConnectToWorkerAsync(worker.Port, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("SDR {Vfo}: connected to worker on localhost:{Port}", vfo, worker.Port);

            using var stream = client.GetStream();
            var reader = new FrameReader(stream);

            int heartbeatCounter = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                WorkerMessage? msg;
                try
                {
                    msg = await reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    _logger.LogInformation("SDR {Vfo}: worker disconnected — will restart", vfo);
                    break;
                }

                if (msg == null)
                {
                    _logger.LogInformation("SDR {Vfo}: worker EOF — will restart", vfo);
                    break;
                }

                switch (msg)
                {
                    case SpectrumFrameMsg sf:
                        await _hub.Clients.All.SendAsync(
                            "RadioStateUpdate",
                            new
                            {
                                property = "SpectrumUpdate",
                                value    = new { sdrId = vfo, bins = sf.Bins, centreHz = sf.CentreHz, spanHz = sf.SpanHz },
                            },
                            stoppingToken).ConfigureAwait(false);

                        if (++heartbeatCounter >= StatusHeartbeatFrames)
                        {
                            heartbeatCounter = 0;
                            await BroadcastStatus(vfo, "streaming", stoppingToken).ConfigureAwait(false);
                        }
                        break;

                    case StatusUpdateMsg s:
                        _logger.LogInformation("SDR {Vfo}: worker status — {Status}", vfo, s.Status);
                        await BroadcastStatus(vfo, s.Status, stoppingToken).ConfigureAwait(false);
                        break;

                    case ErrorReportMsg er:
                        _logger.LogWarning("SDR {Vfo}: worker error — {Error}", vfo, er.Error);
                        await BroadcastDetail(vfo, er.Error, stoppingToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown / restart request.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDR {Vfo}: session error — {Message}", vfo, ex.Message);
            await BroadcastStatus(vfo, "disconnected", stoppingToken).ConfigureAwait(false);
            await BroadcastDetail(vfo, ex.Message, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeLock) _activeDeviceKeys.Remove(vfo);
            try { client?.Close(); } catch { }
            if (worker != null)
            {
                await worker.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                worker.Dispose();
            }
        }
    }

    private static async Task<TcpClient> ConnectToWorkerAsync(int port, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(WorkerConnectTimeoutMs);
        Exception? lastEx = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var c = new TcpClient();
                await c.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
                return c;
            }
            catch (SocketException ex) { lastEx = ex; }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Could not connect to SDR worker on localhost:{port} within {WorkerConnectTimeoutMs}ms",
            lastEx);
    }

    // ── SignalR helpers — sdrId-tagged for per-VFO routing on the frontend ────

    private async Task BroadcastStatus(string vfo, string status, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "RadioStateUpdate",
                new { property = "SdrStatus", value = new { sdrId = vfo, status } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SDR {Vfo}: failed to broadcast status '{Status}'", vfo, status);
        }
    }

    private async Task BroadcastDetail(string vfo, string error, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.All.SendAsync(
                "RadioStateUpdate",
                new { property = "SdrError", value = new { sdrId = vfo, error } },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
    }
}
