using System.Text.Json;
using Yaesu_Web_Control.Models;

namespace Yaesu_Web_Control.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;
        private readonly ILogger<SettingsService> _logger;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private ApplicationSettings? _cachedSettings;

        public SettingsService(IWebHostEnvironment environment, ILogger<SettingsService> logger)
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "Yaesu Web Control");
            MigrateAppDataIfNeeded(appData);
            Directory.CreateDirectory(appData);
            _settingsFilePath = Path.Combine(appData, "appsettings.user.json");
            _logger = logger;
            _logger.LogInformation("SettingsService initialized. File path: {Path}", _settingsFilePath);
        }

        public async Task<ApplicationSettings> GetSettingsAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                _logger.LogInformation("GetSettingsAsync called. File exists: {Exists}", File.Exists(_settingsFilePath));

                if (File.Exists(_settingsFilePath))
                {
                    var json = await File.ReadAllTextAsync(_settingsFilePath);
                    _logger.LogInformation("Raw JSON read: {Json}", json);

                    _cachedSettings = JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();

                    _logger.LogInformation("Settings deserialized: SerialPort={SerialPort}, BaudRate={BaudRate}, WebAddress={WebAddress}, HttpPort={HttpPort}",
                        _cachedSettings.SerialPort, _cachedSettings.BaudRate, _cachedSettings.WebAddress, _cachedSettings.HttpPort);

                    AutoQuoteCommandLinePaths(_cachedSettings);
                }
                else
                {
                    _cachedSettings = new ApplicationSettings();
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
        }

        private static string AutoQuote(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return trimmed;
            if (trimmed.StartsWith('"')) return trimmed;
            if (!trimmed.Contains(' ')) return trimmed;
            return System.IO.File.Exists(trimmed) ? $"\"{trimmed}\"" : trimmed;
        }

        private static void MigrateAppDataIfNeeded(string newFolder)
        {
            if (Directory.Exists(newFolder)) return;
            var oldFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MM5AGM", "FTdx101 WebApp");
            if (!Directory.Exists(oldFolder)) return;
            Directory.CreateDirectory(newFolder);
            foreach (var file in Directory.GetFiles(oldFolder))
                File.Copy(file, Path.Combine(newFolder, Path.GetFileName(file)), overwrite: false);
        }
    }
}