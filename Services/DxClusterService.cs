using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Yaesu_Web_Control.Hubs;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Maintains a TCP connection to a DX cluster server, parses incoming spot
    /// lines into <see cref="DxSpot"/> records, keeps an in-memory ring buffer,
    /// and broadcasts new spots over SignalR.
    ///
    /// Disabled (silently does nothing) when the user has not configured a
    /// cluster host in Settings or has unticked the enable flag. Reconnects
    /// on its own if the connection drops.
    /// </summary>
    public class DxClusterService : BackgroundService
    {
        private const int RingBufferSize = 500;
        private const int ReconnectDelaySeconds = 15;

        private readonly ISettingsService _settingsService;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly ILogger<DxClusterService> _logger;

        private readonly LinkedList<DxSpot> _spots = new();
        private readonly object _spotsLock = new();

        // Cluster lines tend to look like one of:
        //   "DX de F5OYE-#:   14074.0  W2AAA       FT8 RTTY                 1234Z"
        //   "DX de SP9XYZ:    7050.5   DL1ABC      CW EU                    0815Z"
        // Field widths vary by cluster software (AR-Cluster vs CC-Cluster vs DXSpider).
        // This regex is permissive on whitespace and tolerant of the spotter
        // suffix (-#, -@ etc.). It captures: 1=spotter, 2=freq-kHz, 3=callsign,
        // 4=comment-and-time.
        private static readonly Regex SpotRegex = new(
            @"^DX\s+de\s+([A-Z0-9/\-#@]+)\s*:\s*([\d.]+)\s+([A-Z0-9/]+)\s+(.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public DxClusterService(
            ISettingsService settingsService,
            IHubContext<RadioHub> hubContext,
            ILogger<DxClusterService> logger)
        {
            _settingsService = settingsService;
            _hubContext = hubContext;
            _logger = logger;
        }

        /// <summary>Returns a snapshot of all current (non-aged-off) spots, newest first.</summary>
        public List<DxSpot> GetAllSpots()
        {
            lock (_spotsLock)
            {
                return _spots.OrderByDescending(s => s.ReceivedUtc).ToList();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[DxCluster] Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                ApplicationSettings settings;
                try { settings = await _settingsService.GetSettingsAsync(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[DxCluster] Failed to read settings — retrying in {Sec}s", ReconnectDelaySeconds);
                    await SafeDelay(ReconnectDelaySeconds, stoppingToken);
                    continue;
                }

                // Disabled or no host configured — idle until the user enables
                // it. Re-check every 15 s so a Settings save takes effect
                // without restarting the app.
                if (!settings.DxClusterEnabled
                    || string.IsNullOrWhiteSpace(settings.DxClusterHost)
                    || string.IsNullOrWhiteSpace(settings.DxClusterLoginCallsign))
                {
                    await SafeDelay(ReconnectDelaySeconds, stoppingToken);
                    continue;
                }

                AgeOffOldSpots(settings.DxSpotAgeMinutes);

                try
                {
                    await RunSession(settings, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DxCluster] Session ended unexpectedly — reconnecting in {Sec}s",
                        ReconnectDelaySeconds);
                }

                await SafeDelay(ReconnectDelaySeconds, stoppingToken);
            }

            _logger.LogInformation("[DxCluster] Service stopped");
        }

        private async Task RunSession(ApplicationSettings settings, CancellationToken stoppingToken)
        {
            _logger.LogInformation("[DxCluster] Connecting to {Host}:{Port} as {Callsign}",
                settings.DxClusterHost, settings.DxClusterPort, settings.DxClusterLoginCallsign);

            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(settings.DxClusterHost, settings.DxClusterPort, connectCts.Token);

            await BroadcastConnectionStatus("connected");

            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };

            bool loggedIn = false;
            long ageOffCounter = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Read with a soft timeout so we can periodically age off old
                // spots even when the cluster is quiet.
                using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lineCts.CancelAfter(TimeSpan.FromSeconds(30));

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(lineCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Read timeout — refresh age-off and continue.
                    if ((++ageOffCounter % 2) == 0)
                        AgeOffOldSpots(settings.DxSpotAgeMinutes);
                    continue;
                }

                if (line == null) break; // remote closed
                if (line.Length == 0) continue;

                // Login: most clusters print a prompt containing "call:" or
                // "callsign" or "login". Respond with the configured callsign.
                if (!loggedIn && (line.Contains("call:", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("login", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("Please enter", StringComparison.OrdinalIgnoreCase)))
                {
                    await writer.WriteLineAsync(settings.DxClusterLoginCallsign);
                    loggedIn = true;
                    _logger.LogInformation("[DxCluster] Sent login callsign");
                    continue;
                }

                if (TryParseSpot(line, out var spot))
                {
                    AddSpot(spot);
                    await BroadcastSpot(spot);
                }
            }

            await BroadcastConnectionStatus("disconnected");
        }

        /// <summary>
        /// Parses a single line from the cluster into a <see cref="DxSpot"/>.
        /// Returns false for any line that isn't a spot (login prompts,
        /// announcements, etc.). Public for unit testing.
        /// </summary>
        public static bool TryParseSpot(string line, out DxSpot spot)
        {
            spot = new DxSpot();
            var m = SpotRegex.Match(line.Trim());
            if (!m.Success) return false;

            if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kHz))
                return false;

            spot.Spotter     = m.Groups[1].Value.TrimEnd('-', '#', '@');
            spot.FrequencyHz = (long)Math.Round(kHz * 1000.0);
            spot.Callsign    = m.Groups[3].Value.ToUpperInvariant();
            spot.Comment     = m.Groups[4].Value.Trim();
            spot.ReceivedUtc = DateTime.UtcNow;
            return true;
        }

        private void AddSpot(DxSpot spot)
        {
            lock (_spotsLock)
            {
                _spots.AddFirst(spot);
                while (_spots.Count > RingBufferSize)
                    _spots.RemoveLast();
            }
        }

        private void AgeOffOldSpots(int ageMinutes)
        {
            if (ageMinutes <= 0) return;
            var cutoff = DateTime.UtcNow.AddMinutes(-ageMinutes);
            lock (_spotsLock)
            {
                var node = _spots.Last;
                while (node != null && node.Value.ReceivedUtc < cutoff)
                {
                    var prev = node.Previous;
                    _spots.Remove(node);
                    node = prev;
                }
            }
        }

        private async Task BroadcastSpot(DxSpot spot)
        {
            await _hubContext.Clients.All.SendAsync("RadioStateUpdate",
                new { property = "DxSpot", value = spot });
        }

        private async Task BroadcastConnectionStatus(string status)
        {
            await _hubContext.Clients.All.SendAsync("RadioStateUpdate",
                new { property = "DxClusterStatus", value = status });
        }

        private static async Task SafeDelay(int seconds, CancellationToken token)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token); }
            catch (OperationCanceledException) { }
        }
    }
}
