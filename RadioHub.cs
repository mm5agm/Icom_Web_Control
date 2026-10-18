using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Icom_Web_Control.Services;

namespace Icom_Web_Control.Hubs
{
    public class RadioHub : Hub
    {
        private readonly ILogger<RadioHub> _logger;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly RadioStateService _radioState;
        private readonly IRadioController _radio;
        private readonly ISettingsService _settings;

        // Every currently open SignalR connection, and the whole of what the
        // host means by "a browser is watching". Presence used to mean a
        // connection that had called Heartbeat(), which only site.js and the
        // Classic pop-outs do. The Radio panel (/radio) and its pop-outs set
        // Layout = null and never heartbeated, so with /radio open and one
        // Classic pop-out, closing the pop-out shut the host down 30s later.
        // Counting the connection itself asks nothing of a new page beyond
        // connecting to this hub. Same rule as YWC's RadioHub.
        private static readonly ConcurrentDictionary<string, byte> _connections = new();

        // Which spectrum panels each main-page tab currently has on screen, as
        // reported by SpectrumPanels(). The cross-band peek borrows the receiver
        // (and dips the audio) to fill the watch panel, so it needs to know when
        // nobody can see that panel — "VFO A only", scope collapsed, no browser.
        private static readonly ConcurrentDictionary<string, (bool A, bool B)> _spectrumPanels = new();

        // Grace-period shutdown: starts when all heartbeating clients disconnect,
        // cancelled if any client reconnects within the window.
        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(30);
        private static CancellationTokenSource? _shutdownCts;
        private static readonly object _shutdownLock = new();

        public RadioHub(ILogger<RadioHub> logger, IHostApplicationLifetime lifetime,
                        RadioStateService radioState, IRadioController radio,
                        ISettingsService settings)
        {
            _logger   = logger;
            _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            _radioState = radioState;
            _radio = radio;
            _settings = settings;
        }

        public override async Task OnConnectedAsync()
        {
            _connections.TryAdd(Context.ConnectionId, 0);
            CancelShutdown();

            // Replay the full state snapshot to this client only. Regular
            // broadcasts fire on change, so without this a browser that
            // connects after startup (second tab, another computer) keeps the
            // frontend JS defaults for everything not server-rendered in the
            // Razor page — most visibly ActiveVfo/TxVfo/SplitMode, which made
            // VFO A always appear active on late-joining clients.
            foreach (var (property, value) in _radioState.GetClientStateSnapshot())
            {
                await Clients.Caller.SendAsync("RadioStateUpdate", new { property, value });
            }

            // The snapshot above covers RadioStateUpdate properties, but the
            // spectrum panel is revealed by SdrStatus, which is broadcast on a
            // sweep counter running from app start — not from connect. Ask for an
            // immediate re-announce so this client's panel appears on the next
            // sweep rather than up to 29 sweeps later, with frames arriving into a
            // still-hidden card the whole time.
            _radio.RequestScopeStatusAnnounce();

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _connections.TryRemove(Context.ConnectionId, out _);
            _spectrumPanels.TryRemove(Context.ConnectionId, out _);

            // A navigation or a closed tab arrives with no exception. A
            // connection the server gave up on -- one that sent nothing within
            // ClientTimeoutInterval -- arrives with one. Logged so that a host
            // that exits under an idle page says which end let go first.
            if (exception is not null)
            {
                _logger.LogWarning(exception,
                    "Browser connection {ConnectionId} ended with an error rather than a clean close.",
                    Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);

            // Only trigger the shutdown countdown when the last connection of
            // any kind has gone — and only when AutoShutdownWhenNoBrowsers is
            // on, which it is by default.
            if (_connections.IsEmpty)
            {
                var settings = await _settings.GetSettingsAsync();
                if (!settings.AutoShutdownWhenNoBrowsers)
                {
                    _logger.LogInformation(
                        "All browser tabs closed — auto-shutdown disabled; host keeps running.");
                    return;
                }

                // This fires on every page change too: the page being left
                // drops its connection a moment before the new page opens its
                // own, and the cancel in OnConnectedAsync follows at once.
                _logger.LogInformation(
                    "Last live browser connection dropped (page change or tab closed). Shutting down in {s}s unless one reconnects.",
                    ShutdownGrace.TotalSeconds);
                ScheduleShutdown();
            }
        }

        // Called every 5 seconds by site.js and by the Classic pop-outs.
        // Presence is the connection itself now, so this no longer decides
        // whether the host lives -- but it still cancels a pending shutdown,
        // because a page that connects before the previous tab's
        // OnDisconnectedAsync runs would otherwise miss the cancel in
        // OnConnectedAsync and let that disconnect arm the timer.
        public Task Heartbeat()
        {
            CancelShutdown();
            return Task.CompletedTask;
        }

        // Called by the main page whenever its spectrum layout changes, and
        // again every few seconds so a reconnected connection (new id) is
        // re-registered without the page having to notice the reconnect.
        public Task SpectrumPanels(bool a, bool b)
        {
            var now = (a, b);
            if (!_spectrumPanels.TryGetValue(Context.ConnectionId, out var was) || was != now)
            {
                _spectrumPanels[Context.ConnectionId] = now;
                // Logged on change only. The id is the SignalR connection, so a
                // second tab or another machine shows up as a second line — the
                // peek runs if ANY of them wants the watch panel.
                _logger.LogInformation("[RadioHub] Spectrum panels wanted by {Id}: A={A} B={B} ({N} browser(s) reporting)",
                    Context.ConnectionId[..8], a, b, _spectrumPanels.Count);
            }
            return Task.CompletedTask;
        }

        /// <summary>True if any connected browser is showing the spectrum panel for <paramref name="sdrId"/> ("A" or "B").</summary>
        public static bool AnyClientShowsSpectrumPanel(string sdrId)
        {
            bool wantB = sdrId == "B";
            foreach (var (_, p) in _spectrumPanels)
                if (wantB ? p.B : p.A) return true;
            return false;
        }

        // ── Shutdown helpers ──────────────────────────────────────────────────

        private void ScheduleShutdown()
        {
            lock (_shutdownLock)
            {
                _shutdownCts?.Cancel();
                _shutdownCts?.Dispose();
                _shutdownCts = new CancellationTokenSource();
                var token = _shutdownCts.Token;

                Task.Delay(ShutdownGrace, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && _connections.IsEmpty)
                    {
                        _logger.LogInformation("No clients reconnected — stopping application.");
                        _lifetime.StopApplication();
                    }
                }, TaskScheduler.Default);
            }
        }

        private static void CancelShutdown()
        {
            lock (_shutdownLock)
            {
                if (_shutdownCts is not null)
                {
                    _shutdownCts.Cancel();
                    _shutdownCts.Dispose();
                    _shutdownCts = null;
                }
            }
        }

        public async Task SendInitializationStatus(string status)
        {
            await Clients.All.SendAsync("InitializationStatus", status);
        }
    }
}
