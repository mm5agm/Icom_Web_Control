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

        // All currently open SignalR connections
        private static readonly ConcurrentDictionary<string, byte> _connections = new();

        // Connections that have sent at least one heartbeat (i.e. the main page tab)
        private static readonly ConcurrentDictionary<string, DateTime> _heartbeats = new();

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
            bool wasHeartbeating = _heartbeats.TryRemove(Context.ConnectionId, out _);

            await base.OnDisconnectedAsync(exception);

            // Only trigger shutdown countdown when a heartbeating client (main page tab)
            // disconnects and no other heartbeating clients remain — and only
            // when AutoShutdownWhenNoBrowsers is on, which it is by default.
            if (wasHeartbeating && _heartbeats.IsEmpty)
            {
                var settings = await _settings.GetSettingsAsync();
                if (!settings.AutoShutdownWhenNoBrowsers)
                {
                    _logger.LogInformation(
                        "All browser tabs closed — auto-shutdown disabled; host keeps running.");
                    return;
                }

                _logger.LogInformation("All browser tabs closed. Shutting down in {s}s if none reconnect.",
                    ShutdownGrace.TotalSeconds);
                ScheduleShutdown();
            }
        }

        // Called by the main page every 5 seconds
        public Task Heartbeat()
        {
            _heartbeats[Context.ConnectionId] = DateTime.UtcNow;
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
                    if (!t.IsCanceled && _heartbeats.IsEmpty)
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
