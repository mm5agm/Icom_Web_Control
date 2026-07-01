namespace Yaesu_Web_Control.Services;

/// <summary>
/// Contract for the VC Tune configuration-storage subsystem.
/// Manages three distinct data scopes:
/// <list type="bullet">
///   <item><term>User preferences</term>
///     <description>Persisted to disk immediately when changed; survive application restarts.</description></item>
///   <item><term>Radio capabilities</term>
///     <description>
///       Persisted to disk per radio-model key after a successful VT READ confirms them.
///       Static capability (model supports MAIN VC Tune) is re-applied from
///       <see cref="RadioCapabilities"/> on every load to prevent stale file values.
///     </description></item>
///   <item><term>Session state</term>
///     <description>
///       Held in memory only. Never written to disk.
///       Reset automatically when the radio disconnects.
///     </description></item>
/// </list>
/// Implementations must be registered as singletons and are thread-safe.
/// </summary>
public interface IVCTuneConfigurationStore
{
    // ══════════════════════════════════════════════════════════════════════
    // Initialisation
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads user preferences and all persisted radio-capability records from disk.
    /// Called once at application startup before any other store method is used.
    /// If the file does not exist, safe defaults are used without error.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default);

    // ══════════════════════════════════════════════════════════════════════
    // User preferences
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the current user preferences. Returns <see cref="VCTuneUserPreferences.Default"/>
    /// until <see cref="LoadAsync"/> has been called.
    /// </summary>
    VCTuneUserPreferences GetPreferences();

    /// <summary>
    /// Persists <paramref name="preferences"/> to disk immediately and updates the
    /// in-memory cache. The write is atomic — a failed write leaves the previous
    /// file unchanged.
    /// </summary>
    Task SavePreferencesAsync(VCTuneUserPreferences preferences, CancellationToken ct = default);

    // ══════════════════════════════════════════════════════════════════════
    // Radio capabilities
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the persisted radio capabilities for <paramref name="radioModel"/>.
    /// If no record exists for this model, returns a conservative default from
    /// <see cref="VCTuneRadioCapabilities.ForModel"/>.
    /// <para>
    /// The <see cref="VCTuneRadioCapabilities.SupportsVCTuneMain"/> field is always
    /// refreshed from the static <see cref="RadioCapabilities"/> check before being
    /// returned, so stale persisted values cannot override the static truth.
    /// </para>
    /// </summary>
    VCTuneRadioCapabilities GetCapabilities(string radioModel);

    /// <summary>
    /// Persists <paramref name="capabilities"/> to disk under the key
    /// <see cref="VCTuneRadioCapabilities.RadioModel"/> and updates the in-memory cache.
    /// Call this only after a successful VT READ has confirmed the values being stored.
    /// </summary>
    Task SaveCapabilitiesAsync(VCTuneRadioCapabilities capabilities, CancellationToken ct = default);

    /// <summary>
    /// Updates the SUB-capability record for <paramref name="radioModel"/> based on a
    /// freshly read P6 value and, if the record changed, saves it to disk.
    /// Implements the safety rule: if a stored SUB capability conflicts with P6,
    /// P6 wins.
    /// </summary>
    /// <param name="radioModel">The current radio model string.</param>
    /// <param name="p6">The raw P6 byte from the most recent VT READ for the SUB receiver.</param>
    /// <returns>The updated (or unchanged) capability record.</returns>
    Task<VCTuneRadioCapabilities> RefineSubCapabilityFromP6Async(
        string radioModel, int p6, CancellationToken ct = default);

    // ══════════════════════════════════════════════════════════════════════
    // Session state (in-memory only — never persisted)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the current in-memory session state.
    /// Returns <see cref="VCTuneSessionState.Empty"/> until a VT READ has been processed.
    /// </summary>
    VCTuneSessionState GetSessionState();

    /// <summary>
    /// Updates the in-memory session state with data from a completed VT READ.
    /// P5 meter and P6 availability values are stored here and never persisted to disk.
    /// </summary>
    void RecordReadResult(VcTuneReceiver receiver, VcTuneReadResult readResult);

    /// <summary>
    /// Records that a SET or CENTER command was sent, updating the last-command
    /// tracking fields in the session state.
    /// </summary>
    void RecordCommand(VCTuneBand band, VCTuneCommandType commandType);

    /// <summary>
    /// Resets all session state to <see cref="VCTuneSessionState.Empty"/>.
    /// Must be called whenever the radio connection is lost.
    /// </summary>
    void ResetSessionState();
}
