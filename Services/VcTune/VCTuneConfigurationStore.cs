using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services;

/// <summary>
/// Singleton implementation of <see cref="IVCTuneConfigurationStore"/>.
/// Owns the <c>vcTune_config.json</c> file in
/// <c>%APPDATA%\MM5AGM\Yaesu Web Control\</c> alongside the other YWC
/// per-user data files.
/// <para>
/// Thread safety:
/// <list type="bullet">
///   <item>File reads and writes are serialised through a <see cref="SemaphoreSlim"/>(1,1).</item>
///   <item>In-memory session-state mutations use <c>volatile</c> reference replacement
///     (records are immutable, so assignment is atomic).</item>
///   <item>Capability-cache reads and writes use a lock to prevent torn reads when
///     multiple callers update different models concurrently.</item>
/// </list>
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;IVCTuneConfigurationStore, VCTuneConfigurationStore&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneConfigurationStore : IVCTuneConfigurationStore
{
    private readonly string _filePath;
    private readonly ILogger<VCTuneConfigurationStore> _logger;

    // Async file-I/O serialiser — same pattern as SettingsService.
    private readonly SemaphoreSlim _fileSemaphore = new(1, 1);

    // In-memory cache — volatile reference replacement; records are immutable.
    private volatile VCTuneUserPreferences _preferences = VCTuneUserPreferences.Default;
    private volatile VCTuneSessionState _sessionState = VCTuneSessionState.Empty;

    // Capability cache keyed by radio model. Dictionary is replaced atomically;
    // reads hold _capabilitiesLock for safe snapshot copy.
    private readonly object _capabilitiesLock = new();
    private Dictionary<string, VCTuneRadioCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Initialises the store, resolving the file path to the standard per-user
    /// data directory used by all YWC data files.
    /// </summary>
    public VCTuneConfigurationStore(
        IWebHostEnvironment env,
        ILogger<VCTuneConfigurationStore> logger)
    {
        _ = env; // reserved for future content-root lookups
        _logger = logger;

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MM5AGM", "Yaesu Web Control");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "vcTune_config.json");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Initialisation
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _fileSemaphore.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation(
                    "[VCTuneConfigurationStore] Config file not found at {Path}; using defaults.",
                    _filePath);
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath, ct);
            var doc = JsonSerializer.Deserialize<PersistedConfig>(json, _jsonOptions);
            if (doc is null)
            {
                _logger.LogWarning(
                    "[VCTuneConfigurationStore] Failed to deserialise {Path}; using defaults.",
                    _filePath);
                return;
            }

            // Apply loaded preferences, clamping any out-of-range values.
            if (doc.UserPreferences is not null)
                _preferences = doc.UserPreferences.WithValidatedStep();

            // Restore capability records, refreshing static fields so stale persisted
            // values cannot override the current build's RadioCapabilities answers.
            if (doc.RadioCapabilities is { Count: > 0 })
            {
                lock (_capabilitiesLock)
                {
                    _capabilities = new Dictionary<string, VCTuneRadioCapabilities>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var (model, cap) in doc.RadioCapabilities)
                    {
                        _capabilities[model] = cap.WithRefreshedStaticCapabilities();
                    }
                }
            }

            _logger.LogInformation(
                "[VCTuneConfigurationStore] Loaded; preferences and {Count} capability record(s).",
                _capabilities.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[VCTuneConfigurationStore] Error loading config from {Path}; using defaults.",
                _filePath);
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // User preferences
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public VCTuneUserPreferences GetPreferences() => _preferences;

    /// <inheritdoc/>
    public async Task SavePreferencesAsync(
        VCTuneUserPreferences preferences, CancellationToken ct = default)
    {
        var validated = preferences.WithValidatedStep();
        _preferences = validated;
        await PersistToDiskAsync(ct);
        _logger.LogDebug("[VCTuneConfigurationStore] User preferences saved.");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Radio capabilities
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public VCTuneRadioCapabilities GetCapabilities(string radioModel)
    {
        lock (_capabilitiesLock)
        {
            if (_capabilities.TryGetValue(radioModel, out var stored))
                return stored.WithRefreshedStaticCapabilities();
        }
        return VCTuneRadioCapabilities.ForModel(radioModel);
    }

    /// <inheritdoc/>
    public async Task SaveCapabilitiesAsync(
        VCTuneRadioCapabilities capabilities, CancellationToken ct = default)
    {
        lock (_capabilitiesLock)
        {
            _capabilities[capabilities.RadioModel] = capabilities;
        }
        await PersistToDiskAsync(ct);
        _logger.LogDebug(
            "[VCTuneConfigurationStore] Capabilities saved for model {Model}.",
            capabilities.RadioModel);
    }

    /// <inheritdoc/>
    public async Task<VCTuneRadioCapabilities> RefineSubCapabilityFromP6Async(
        string radioModel, int p6, CancellationToken ct = default)
    {
        var current = GetCapabilities(radioModel);
        var refined = current.WithSubP6Update(p6);

        // Only write to disk if the record actually changed. Record equality
        // on all init properties avoids unnecessary file writes.
        if (refined != current)
        {
            await SaveCapabilitiesAsync(refined, ct);
            _logger.LogDebug(
                "[VCTuneConfigurationStore] SUB capability refined from P6={P6} for {Model}: " +
                "SubInstallationConfirmed={Confirmed}, SupportsVCTuneSub={Supported}.",
                p6, radioModel, refined.SubInstallationConfirmed, refined.SupportsVCTuneSub);
        }

        return refined;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Session state
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public VCTuneSessionState GetSessionState() => _sessionState;

    /// <inheritdoc/>
    public void RecordReadResult(VcTuneReceiver receiver, VcTuneReadResult readResult)
    {
        if (!readResult.IsValid)
            return;

        // Replace the session-state reference atomically. No lock needed because
        // VCTuneSessionState is an immutable record — the WithReadResult call
        // produces a fresh instance; the volatile write is atomic on .NET.
        _sessionState = _sessionState.WithReadResult(receiver, readResult);
    }

    /// <inheritdoc/>
    public void RecordCommand(VCTuneBand band, VCTuneCommandType commandType)
    {
        _sessionState = _sessionState.WithCommand(band, commandType);
    }

    /// <inheritdoc/>
    public void ResetSessionState()
    {
        _sessionState = VCTuneSessionState.Empty;
        _logger.LogDebug("[VCTuneConfigurationStore] Session state reset.");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Disk I/O
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serialises the current in-memory preferences and capability cache to
    /// <c>vcTune_config.json</c> using a write-to-temp-then-rename pattern to
    /// prevent a partial write from corrupting the file.
    /// </summary>
    private async Task PersistToDiskAsync(CancellationToken ct)
    {
        await _fileSemaphore.WaitAsync(ct);
        try
        {
            Dictionary<string, VCTuneRadioCapabilities> capSnapshot;
            lock (_capabilitiesLock)
                capSnapshot = new Dictionary<string, VCTuneRadioCapabilities>(
                    _capabilities, StringComparer.OrdinalIgnoreCase);

            var doc = new PersistedConfig
            {
                UserPreferences = _preferences,
                RadioCapabilities = capSnapshot,
            };

            var json = JsonSerializer.Serialize(doc, _jsonOptions);

            // Atomic write: write to a temp file then replace.
            var tmpPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json, ct);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[VCTuneConfigurationStore] Failed to persist config to {Path}.", _filePath);
        }
        finally
        {
            _fileSemaphore.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Serialisation POCO
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Root JSON structure for <c>vcTune_config.json</c>.
    /// Mutable properties are required by System.Text.Json for deserialization.
    /// Session state is intentionally absent — it is never persisted.
    /// </summary>
    private sealed class PersistedConfig
    {
        /// <summary>User preferences. Null in very old or hand-crafted files; safe default applied on load.</summary>
        public VCTuneUserPreferences? UserPreferences { get; set; }

        /// <summary>Per-model capability records, keyed by radio model string.</summary>
        public Dictionary<string, VCTuneRadioCapabilities> RadioCapabilities { get; set; } = new();
    }
}
