using System.Text.Json;
using Icom_Web_Control.Models;

namespace Icom_Web_Control.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;
        private readonly ILogger<SettingsService> _logger;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private ApplicationSettings? _cachedSettings;

        public SettingsService(IWebHostEnvironment environment, ILogger<SettingsService> logger)
        {
            _logger = logger;
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "Icom Web Control");
            Directory.CreateDirectory(appData);
            _settingsFilePath = Path.Combine(appData, "appsettings.user.json");
            MigrateSettingsFromYwcIfNeeded();
            _logger.LogInformation("SettingsService initialized. File path: {Path}", _settingsFilePath);
        }

        public async Task<ApplicationSettings> GetSettingsAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                // These fire on every meter-poll cycle (GetSettingsAsync is
                // called ~2 Hz). At Information level — and especially dumping
                // the entire settings JSON — they were a major contributor to
                // the synchronous-logging flood that starved the thread pool
                // during startup (issue #73). Kept at Debug so they're still
                // available when explicitly troubleshooting settings.
                _logger.LogDebug("GetSettingsAsync called. File exists: {Exists}", File.Exists(_settingsFilePath));

                if (File.Exists(_settingsFilePath))
                {
                    var json = await File.ReadAllTextAsync(_settingsFilePath);
                    _logger.LogDebug("Raw JSON read: {Json}", json);

                    _cachedSettings = JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();

                    _logger.LogDebug("Settings deserialized: SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                        _cachedSettings.SerialPort, _cachedSettings.BaudRate, _cachedSettings.WebAddress, _cachedSettings.HttpPort);

                    MigrateSdrDeviceKey(_cachedSettings);
                    MigrateSdrSampleRate(_cachedSettings);
                    AutoQuoteCommandLinePaths(_cachedSettings);
                }
                else
                {
                    _cachedSettings = new ApplicationSettings();
                    MigrateSdrSampleRate(_cachedSettings);   // fills A/B from defaults when file is brand new
                    _logger.LogWarning("Settings file does not exist at {Path}. Using defaults: SerialPort={SerialPort}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                        _settingsFilePath, _cachedSettings.SerialPort, _cachedSettings.WebAddress, _cachedSettings.HttpPort);
                }

                return _cachedSettings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading settings from {Path}", _settingsFilePath);
                return new ApplicationSettings();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SaveSettingsAsync(ApplicationSettings settings)
        {
            await _semaphore.WaitAsync();
            try
            {
                _logger.LogInformation("SaveSettingsAsync called with: SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                    settings.SerialPort, settings.BaudRate, settings.WebAddress, settings.HttpPort);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                var json = JsonSerializer.Serialize(settings, options);
                _logger.LogInformation("Serialized to JSON: {Json}", json);

                await File.WriteAllTextAsync(_settingsFilePath, json);
                _cachedSettings = settings;

                _logger.LogInformation("Settings saved successfully to {Path}", _settingsFilePath);

                // Verify
                if (File.Exists(_settingsFilePath))
                {
                    var verify = await File.ReadAllTextAsync(_settingsFilePath);
                    _logger.LogInformation("Verification: File content after save: {Content}", verify);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving settings to {Path}", _settingsFilePath);
                throw;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public string GetSettingsFilePath() => _settingsFilePath;

        public void InvalidateCache()
        {
            _semaphore.Wait();
            try { _cachedSettings = null; }
            finally { _semaphore.Release(); }
        }

        // v2.2.x → v2.3.0 migration: the SDR settings split from a single
        // SdrDeviceKey into per-VFO SdrDeviceKeyA / SdrDeviceKeyB. On read,
        // if the legacy field has a value and SdrDeviceKeyA does not, promote
        // the legacy value into A. The legacy field is then cleared on the
        // next save so the file gradually converges on the new shape.
        // See docs/decisions/0001-dual-sdr-architecture.md.
        private static void MigrateSdrDeviceKey(ApplicationSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.SdrDeviceKey) &&
                string.IsNullOrWhiteSpace(s.SdrDeviceKeyA))
            {
                s.SdrDeviceKeyA = s.SdrDeviceKey;
                s.SdrDeviceKey  = string.Empty;
            }
        }

        // v2.3.0+ per-VFO sample rate. The model property defaults are 0
        // (sentinel for "field not in JSON"), so we can distinguish between
        // an absent field on disk vs an explicit 0 saved by the user.
        // Rules:
        //   - If legacy SdrSampleRateHz has a value and either A or B is 0,
        //     copy legacy → the missing slot(s). Clear legacy.
        //   - If A or B is still 0 after that, fall back to the v2.2.x
        //     default 2_048_000 so a brand-new settings file or one missing
        //     all three fields still gets sane defaults.
        private const double DefaultSampleRateHz = 2_048_000;
        private static void MigrateSdrSampleRate(ApplicationSettings s)
        {
            if (s.SdrSampleRateHz > 0)
            {
                if (s.SdrSampleRateHzA == 0) s.SdrSampleRateHzA = s.SdrSampleRateHz;
                if (s.SdrSampleRateHzB == 0) s.SdrSampleRateHzB = s.SdrSampleRateHz;
                s.SdrSampleRateHz = 0;
            }
            if (s.SdrSampleRateHzA == 0) s.SdrSampleRateHzA = DefaultSampleRateHz;
            if (s.SdrSampleRateHzB == 0) s.SdrSampleRateHzB = DefaultSampleRateHz;
        }

        // Backward-compat for users whose *CommandLine settings were saved before
        // the strict-quoting rule was introduced. Auto-quote any unquoted path
        // whose entire value is an existing file (no command-line arguments
        // included). Paths with arguments require the user to add quotes
        // themselves — there is no reliable heuristic that distinguishes
        // "path-with-spaces" from "path args-with-spaces".
        private static void AutoQuoteCommandLinePaths(ApplicationSettings s)
        {
            s.WsjtxCommandLine       = AutoQuote(s.WsjtxCommandLine);
            s.JtalertCommandLine     = AutoQuote(s.JtalertCommandLine);
            s.Log4omCommandLine      = AutoQuote(s.Log4omCommandLine);
            s.GridtrackerCommandLine = AutoQuote(s.GridtrackerCommandLine);
            s.FldigiCommandLine      = AutoQuote(s.FldigiCommandLine);
        }

        private static string AutoQuote(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return trimmed;
            if (trimmed.StartsWith('"')) return trimmed;
            if (!trimmed.Contains(' ')) return trimmed;
            return System.IO.File.Exists(trimmed) ? $"\"{trimmed}\"" : trimmed;
        }

        // One-time first-run migration of radio-AGNOSTIC user preferences from a
        // sibling Yaesu Web Control (or the older FTdx101 WebApp) install into
        // this Icom Web Control settings file. Runs only when IWC has no settings
        // file of its own yet, so it never clobbers an existing IWC config and
        // never runs twice.
        //
        // Radio-SPECIFIC fields are deliberately NOT carried across: IWC talks to
        // an IC-7300 (COM8, 19200, CI-V) not a Yaesu (COM4, 38400, Yaesu CAT), the
        // SDR wiring and meter calibration differ, and the Yaesu IF-width codes in
        // band profiles / last-radio-state don't map onto Icom. Those stay at
        // IWC's own defaults — see CopyRadioAgnosticSettings for the exact allowlist.
        //
        // Best-effort: any failure here must never stop the app from starting.
        // Other user-data files (memories.json, memory-banks.json, labels.json,
        // voice_phrases.json) are NOT touched — a Yaesu calibration.user.json /
        // radio_state.json would be wrong on Icom, and the rest are a separate
        // decision. This is settings-only by design.
        private void MigrateSettingsFromYwcIfNeeded()
        {
            try
            {
                if (File.Exists(_settingsFilePath)) return;   // IWC already has settings — nothing to do

                var appDataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MM5AGM");

                // Newest lineage first; both are Yaesu-flavoured YWC configs.
                string[] legacyFolders = { "Yaesu Web Control", "FTdx101 WebApp" };

                var sourceFile = legacyFolders
                    .Select(f => Path.Combine(appDataRoot, f, "appsettings.user.json"))
                    .FirstOrDefault(File.Exists);

                if (sourceFile is null) return;   // fresh install, no YWC to inherit from

                var json = File.ReadAllText(sourceFile);
                var old = JsonSerializer.Deserialize<ApplicationSettings>(json);
                if (old is null) return;

                // Start from IWC defaults (correct IC-7300 port/baud/model/SDR/etc.)
                // and overlay ONLY the radio-agnostic preferences.
                var merged = new ApplicationSettings();
                CopyRadioAgnosticSettings(from: old, to: merged);

                var outJson = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, outJson);

                _logger.LogInformation(
                    "First-run: migrated radio-agnostic settings from '{Source}' into '{Dest}' " +
                    "(radio/SDR/calibration fields left at IWC defaults).",
                    sourceFile, _settingsFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "First-run settings migration from a Yaesu Web Control install failed; continuing with IWC defaults.");
            }
        }

        // The allowlist of radio-AGNOSTIC fields carried across on first-run
        // migration (see MigrateSettingsFromYwcIfNeeded). Anything NOT copied here
        // — SerialPort, BaudRate, RadioModel, every Sdr* field, SdrplayInstallPath,
        // InstalledRoofingFilters, BandProfilesA/B, LastRadioState — is Yaesu-
        // specific and intentionally stays at IWC's IC-7300 defaults.
        private static void CopyRadioAgnosticSettings(ApplicationSettings from, ApplicationSettings to)
        {
            // Web server host settings
            to.WebAddress = from.WebAddress;
            to.HttpPort   = from.HttpPort;

            // External application launchers (installed-app paths are portable)
            to.WsjtxCommandLine       = from.WsjtxCommandLine;
            to.JtalertCommandLine     = from.JtalertCommandLine;
            to.Log4omCommandLine      = from.Log4omCommandLine;
            to.GridtrackerCommandLine = from.GridtrackerCommandLine;
            to.FldigiCommandLine      = from.FldigiCommandLine;
            to.App1Name = from.App1Name;
            to.App2Name = from.App2Name;
            to.App3Name = from.App3Name;
            to.App4Name = from.App4Name;
            to.App5Name = from.App5Name;
            to.ShowWsjtxButton       = from.ShowWsjtxButton;
            to.ShowJtalertButton     = from.ShowJtalertButton;
            to.ShowLog4omButton      = from.ShowLog4omButton;
            to.ShowGridtrackerButton = from.ShowGridtrackerButton;
            to.ShowFldigiButton      = from.ShowFldigiButton;

            // WSJT-X UDP link
            to.WsjtxUdpAddress = from.WsjtxUdpAddress;
            to.WsjtxUdpPort    = from.WsjtxUdpPort;

            // Band-plan region (regulatory IARU region, not a radio setting)
            to.BandPlan = from.BandPlan;

            // DX cluster
            to.DxClusterEnabled           = from.DxClusterEnabled;
            to.DxClusterHost              = from.DxClusterHost;
            to.DxClusterPort              = from.DxClusterPort;
            to.DxClusterLoginCallsign     = from.DxClusterLoginCallsign;
            to.DxSpotAgeMinutes           = from.DxSpotAgeMinutes;
            to.DxClusterPostLoginCommands = from.DxClusterPostLoginCommands;
            to.DxClusterWatchedCallsigns  = from.DxClusterWatchedCallsigns;

            // CW keyer message memories (user text macros; radio-agnostic)
            to.CwMessages = from.CwMessages;

            // Accessibility / input
            to.ShowFrequencyArrowButtons = from.ShowFrequencyArrowButtons;
            to.TxToggleKey               = from.TxToggleKey;

            // Voice control
            to.VoiceControlEnabled            = from.VoiceControlEnabled;
            to.VoiceSpokenConfirmationEnabled = from.VoiceSpokenConfirmationEnabled;
            to.VoiceNudgeStepHzA              = from.VoiceNudgeStepHzA;
            to.VoiceNudgeStepHzB              = from.VoiceNudgeStepHzB;
            to.VoiceAdvancedModeEnabled       = from.VoiceAdvancedModeEnabled;
            to.VoiceActiveLocale              = from.VoiceActiveLocale;
        }
    }
}