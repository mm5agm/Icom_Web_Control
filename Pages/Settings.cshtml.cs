using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using Icom_Web_Control.Hubs;
using Icom_Web_Control.Models;
using Icom_Web_Control.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Icom_Web_Control.Pages
{
    public class SettingsModel : PageModel
    {
        private readonly ISettingsService _settingsService;
        private readonly ILogger<SettingsModel> _logger;
        private readonly RadioInitializationService _radioInitializationService;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IHubContext<RadioHub> _hubContext;
        private readonly HttpPortInfo _portInfo;

        /// <summary>
        /// The port IWC is actually listening on right now. This is the port
        /// the displayed network URLs on the Settings page use — so the
        /// Local-Access / Network-Access boxes always show something that
        /// actually works, even when the configured Settings.HttpPort has been
        /// changed but the app hasn't yet been restarted.
        /// </summary>
        public int CurrentRunningPort => _portInfo.Port;

        [BindProperty]
        public ApplicationSettings Settings { get; set; } = new();

        /// <summary>
        /// Path the SdrplayDllResolver would currently auto-detect, or null
        /// if no SDRplay install was found in any of the standard locations.
        /// Shown on the Settings page next to the manual override input so
        /// users can see whether auto-detect is working and grab the path
        /// with one click if they want to fill the override.
        /// </summary>
        public string? DetectedSdrplayInstallPath { get; set; }

        [TempData]
        public string? StatusMessage { get; set; } = string.Empty;

        /// <summary>
        /// Set after a save where a restart-sensitive setting (most importantly
        /// RadioModel) was changed. Lifts the user past the bug reported as
        /// Issue #9 — the change has been written to disk and is picked up by
        /// backend services on their next read, but some frontend state and
        /// any open browser tabs from before the change can show the old radio.
        /// A full restart is the unambiguous way to reset everything.
        /// </summary>
        [TempData]
        public string? RestartRequiredReason { get; set; }

        public List<string> NetworkAddresses { get; set; } = new();

        public SettingsModel(
            ISettingsService settingsService,
            ILogger<SettingsModel> logger,
            RadioInitializationService radioInitializationService,
            IHostApplicationLifetime lifetime,
            IHubContext<RadioHub> hubContext,
            HttpPortInfo portInfo)
        {
            _settingsService = settingsService;
            _logger = logger;
            _radioInitializationService = radioInitializationService;
            _lifetime = lifetime;
            _hubContext = hubContext;
            _portInfo = portInfo;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            Settings = await _settingsService.GetSettingsAsync();
            Settings.BandPlan = Settings.BandPlan switch { "UK" => "Region1", "USA" => "Region2", var v => v };
            Settings.TxToggleKey = NormalizeTxToggleKey(Settings.TxToggleKey);
            NetworkAddresses = GetLocalIPAddresses();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // DX cluster host/callsign also allowed empty (empty = feature disabled).
            ModelState.Remove("Settings.DxClusterHost");
            ModelState.Remove("Settings.DxClusterLoginCallsign");
            ModelState.Remove("Settings.DxClusterPostLoginCommands");
            // Accessibility TX shortcut is optional — empty = disabled.
            ModelState.Remove("Settings.TxToggleKey");

            if (!ModelState.IsValid)
            {
                // Log every ModelState error so we can see exactly which
                // field failed validation. Without this the page silently
                // redisplays with no visible error indication.
                foreach (var (key, entry) in ModelState)
                {
                    foreach (var err in entry.Errors)
                    {
                        _logger.LogWarning("[Settings] Validation error on {Key}: {Msg}",
                            key, err.ErrorMessage);
                    }
                }
                StatusMessage = "❌ Settings not saved — see console log for validation errors.";
                NetworkAddresses = GetLocalIPAddresses();
                return Page();
            }

            // Validate Serial Port format
            if (!Settings.SerialPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                ModelState.AddModelError("Settings.SerialPort",
                    "Serial port must start with 'COM' (e.g., COM3, COM4).");
                NetworkAddresses = GetLocalIPAddresses();
                return Page();
            }

            try
            {
                // Read-modify-write: load current settings so fields not on this form
                // (external app paths, LastRadioState, etc.) are not overwritten with defaults.
                var current = await _settingsService.GetSettingsAsync();

                // Capture pre-change values for restart-sensitive fields BEFORE we
                // overwrite `current` — used below to decide whether the user
                // needs to restart for the change to fully take effect (Issue #9).
                var oldRadioModel = current.RadioModel;
                var oldWebAddress = current.WebAddress;
                var oldHttpPort   = current.HttpPort;

                // Capture pre-change CAT connection values so the radio reconnect
                // below only fires when something that actually affects the CAT
                // link changed — previously it ran unconditionally on every save,
                // including fields like Accessibility/Voice Control toggles that
                // have nothing to do with the radio connection.
                var oldSerialPort = current.SerialPort;
                var oldBaudRate   = current.BaudRate;

                current.RadioModel        = Settings.RadioModel;
                // HttpPort is bound from a number input; clamp to a sane range.
                // 1024+ avoids privileged-port territory; we'd reach the upper
                // limit through user typo long before normal use.
                current.HttpPort          = (Settings.HttpPort >= 1 && Settings.HttpPort <= 65535)
                    ? Settings.HttpPort
                    : 8080;
                current.SerialPort        = Settings.SerialPort;
                current.BaudRate          = Settings.BaudRate;
                current.WebAddress        = Settings.WebAddress;
                // External-SDR spectrum config was removed from the Settings page
                // (the IC-7300's scope comes over CI-V, not an IF-tapped SDR).
                // The Sdr* fields are left untouched here so their persisted
                // values survive the read-modify-write; the CI-V scope work
                // (Phase 3 block 6) will decide their ultimate fate.
                current.BandPlan          = Settings.BandPlan;
                // Icom IC-7300 / MkII is a direct-sampling SDR with no roofing
                // filters and no CAT filter-selection command, so there is nothing
                // to configure. Keep the list empty. (Field retained on the model
                // as a migration anchor from the inherited Yaesu code.)
                current.InstalledRoofingFilters = new List<string>();
                if (Settings.CwMessages != null && Settings.CwMessages.Count == 5)
                    current.CwMessages = Settings.CwMessages;

                // DX cluster settings — copy through. Normalise callsign to upper case.
                current.DxClusterEnabled         = Settings.DxClusterEnabled;
                current.DxClusterHost            = (Settings.DxClusterHost ?? "").Trim();
                current.DxClusterPort            = Settings.DxClusterPort > 0 ? Settings.DxClusterPort : 7300;
                current.DxClusterLoginCallsign   = (Settings.DxClusterLoginCallsign ?? "").Trim().ToUpperInvariant();
                current.DxSpotAgeMinutes         = Settings.DxSpotAgeMinutes > 0 ? Settings.DxSpotAgeMinutes : 15;
                current.DxClusterPostLoginCommands = Settings.DxClusterPostLoginCommands ?? "";

                // Accessibility
                current.ShowFrequencyArrowButtons = Settings.ShowFrequencyArrowButtons;
                // Space cannot round-trip as a lone " " through HTML form posts —
                // store the KeyboardEvent.code token "Space" (and migrate legacy " ").
                current.TxToggleKey = NormalizeTxToggleKey(Settings.TxToggleKey);

                // Voice Control (v1)
                current.VoiceControlEnabled = Settings.VoiceControlEnabled;
                current.VoiceSpokenConfirmationEnabled = Settings.VoiceSpokenConfirmationEnabled;

                // Pseudo-dual receiver (Phase 5)
                current.PseudoDualReceiverEnabled = Settings.PseudoDualReceiverEnabled;
                current.PseudoDualCrossBandEnabled = Settings.PseudoDualCrossBandEnabled;
                // Clamp peek interval to the 5–60 s the UI offers; fall back to
                // the 15 s default if an out-of-range value is posted.
                current.PseudoDualPeekIntervalSeconds =
                    (Settings.PseudoDualPeekIntervalSeconds >= 5 && Settings.PseudoDualPeekIntervalSeconds <= 60)
                        ? Settings.PseudoDualPeekIntervalSeconds
                        : 15;
                // Watch-panel (VFO B) span-button behaviour; fall back to the
                // ZoomIn default for any unrecognised value.
                current.PseudoDualWatchSpanMode =
                    Settings.PseudoDualWatchSpanMode is "ZoomIn" or "Shared" or "Hidden"
                        ? Settings.PseudoDualWatchSpanMode
                        : "ZoomIn";

                await _settingsService.SaveSettingsAsync(current);

                // Only reconnect the radio if something that actually affects the
                // CAT link changed — Radio Model (different init command set),
                // Serial Port, or Baud Rate. Previously this ran on every save
                // regardless of which field changed, so even an unrelated toggle
                // (e.g. Accessibility/Voice Control) would visibly reconnect the
                // radio.
                bool catConnectionChanged =
                       !string.Equals(oldRadioModel, current.RadioModel, StringComparison.Ordinal)
                    || !string.Equals(oldSerialPort, current.SerialPort, StringComparison.OrdinalIgnoreCase)
                    || oldBaudRate != current.BaudRate;

                if (catConnectionChanged)
                {
                    // Reset initialization status so app will try again
                    Icom_Web_Control.Services.AppStatus.InitializationStatus = "initializing";

                    // Automatic retry: trigger radio initialization in the background rather
                    // than awaiting it here. The full sequence (CAT burst, DT0 wait up to 5s,
                    // state restore, auto-info settle) can legitimately take several seconds,
                    // which was blocking this POST response and making Save appear to hang
                    // (wa6auf11, #73 follow-up). The existing initializing-overlay/polling on
                    // the main page already handles showing progress once redirected there.
                    // InitializeRadioAsync's own top-level catch means this never surfaces an
                    // unobserved exception.
                    _ = _radioInitializationService.InitializeRadioAsync();
                }

                StatusMessage = "✓ Settings saved successfully.";

                // Decide whether a restart is needed. Backend services re-read
                // settings from disk on each request, so most field changes
                // are picked up immediately — but a radio-model change can
                // leave stale state in any browser tab opened before the
                // change, and a WebAddress change requires Kestrel to rebind.
                // A full app restart resets everything unambiguously.
                var reasons = new List<string>();
                if (!string.Equals(oldRadioModel, current.RadioModel, StringComparison.Ordinal))
                    reasons.Add($"radio model ({oldRadioModel} → {current.RadioModel})");
                if (!string.Equals(oldWebAddress, current.WebAddress, StringComparison.Ordinal))
                    reasons.Add($"web server address ({oldWebAddress} → {current.WebAddress})");
                if (oldHttpPort != current.HttpPort)
                    reasons.Add($"HTTP port ({oldHttpPort} → {current.HttpPort})");
                if (reasons.Count > 0)
                {
                    RestartRequiredReason = string.Join(" and ", reasons);
                }

                _logger.LogInformation(
                    "Settings updated: RadioModel={RadioModel}, SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}",
                    Settings.RadioModel, Settings.SerialPort, Settings.BaudRate, Settings.WebAddress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings");
                StatusMessage = "❌ Error saving settings. Please try again.";
                ModelState.AddModelError(string.Empty, "An error occurred while saving settings.");
                NetworkAddresses = GetLocalIPAddresses();
                return Page();
            }

            return RedirectToPage();
        }

        /// <summary>
        /// Restart Now action — invoked from the restart-required banner on the
        /// Settings page. Spawns a small detached helper that waits two seconds
        /// then relaunches the IWC executable, broadcasts ServerShutdown so any
        /// open browser tab shows the existing "Icom Web Control has stopped"
        /// overlay, and stops the host. The user reloads the browser once the
        /// app is back up.
        ///
        /// Restart only works when running as the published `Icom_Web_Control.exe`
        /// — if IWC was launched via `dotnet run` the auto-relaunch path is
        /// skipped, but the host still stops cleanly. Dev users can rerun
        /// `dotnet run` manually.
        /// </summary>
        public async Task<IActionResult> OnPostRestartAsync()
        {
            // Tell open browser tabs the server is going down so they don't sit
            // on stale data. Best-effort — short timeout so we don't block the
            // restart if SignalR is slow.
            try
            {
                await _hubContext.Clients.All
                    .SendAsync("RadioStateUpdate", new { property = "ServerShutdown", value = true })
                    .WaitAsync(TimeSpan.FromMilliseconds(300));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast ServerShutdown before restart");
            }

            // Schedule a relaunch of the same exe. We use a detached cmd helper
            // that waits 2 s (long enough for the current host to fully stop and
            // release its HTTP port) and then `start`s the exe — `start` returns
            // immediately so cmd exits cleanly, leaving the new IWC running.
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) &&
                    exePath.EndsWith("Icom_Web_Control.exe", StringComparison.OrdinalIgnoreCase) &&
                    System.IO.File.Exists(exePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName  = "cmd.exe",
                        Arguments = $"/c timeout /t 2 /nobreak >nul && start \"\" \"{exePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                    });
                    _logger.LogInformation("Scheduled IWC relaunch via detached cmd helper: {Exe}", exePath);
                }
                else
                {
                    _logger.LogWarning("Skipping auto-relaunch — running under dotnet run or exe not found. User will need to relaunch manually.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to schedule IWC relaunch — user will need to relaunch manually");
            }

            // Stop the host. Hosted services (MeterPollingService, the tray icon,
            // etc.) get their StopAsync calls; the multiplexer's serial port is
            // closed cleanly via RadioInitializationService.StopAsync.
            _lifetime.StopApplication();
            return new EmptyResult();
        }

        private List<string> GetLocalIPAddresses()
        {
            var addresses = new List<string>();

            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var networkInterface in networkInterfaces)
                {
                    var ipProperties = networkInterface.GetIPProperties();
                    foreach (var ip in ipProperties.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork) // IPv4 only
                        {
                            addresses.Add(ip.Address.ToString());
                        }
                    }
                }

                _logger.LogDebug("Detected network addresses: {Addresses}", string.Join(", ", addresses));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to detect network addresses");
            }

            return addresses;
        }

        /// <summary>
        /// Space cannot round-trip through an HTML form as a lone " " (Tag Helpers
        /// and browsers treat whitespace-only input values as empty). Persist the
        /// KeyboardEvent.code token instead; migrate any legacy " " on save.
        /// </summary>
        private static string NormalizeTxToggleKey(string? key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (key == " " || key.Equals("Space", StringComparison.OrdinalIgnoreCase))
                return "Space";
            return key;
        }
    }
}