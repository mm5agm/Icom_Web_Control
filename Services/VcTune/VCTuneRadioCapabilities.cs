namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable record of VC Tune hardware capabilities for a specific radio model,
/// derived from a combination of static model knowledge and runtime P6 VT responses.
/// <para>
/// Persisted to disk per radio-model key so that a reconnect to the same radio can
/// restore <see cref="SubInstallationConfirmed"/> without waiting for a fresh probe.
/// Values are always re-validated against the first successful VT READ after connect
/// — if a stored value conflicts with a live P6 response, the P6 reading wins.
/// </para>
/// <para>
/// <b>Safety invariants</b>
/// <list type="bullet">
///   <item>Records for one radio model are never used for a different model.</item>
///   <item><see cref="SupportsVCTuneSub"/> is only <see langword="true"/> when
///     both <see cref="SubInstallationConfirmed"/> is <see langword="true"/> AND the
///     static <see cref="RadioCapabilities.SupportsVCTuneSubStatic"/> check passes.</item>
///   <item>P6 availability (out-of-range status) is NOT stored here — it is
///     session-only state held in <see cref="VCTuneSessionState"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed record VCTuneRadioCapabilities
{
    /// <summary>
    /// The radio model string this record applies to (e.g. "FTdx101MP").
    /// Must match <see cref="ApplicationSettings.RadioModel"/> exactly.
    /// </summary>
    public string RadioModel { get; init; } = string.Empty;

    /// <summary>
    /// Whether this radio model supports MAIN VC Tune via the VT CAT command.
    /// Set once from <see cref="RadioCapabilities.SupportsVCTuneMain"/> at load
    /// time; always overwritten by the live static check to prevent stale values.
    /// </summary>
    public bool SupportsVCTuneMain { get; init; } = false;

    /// <summary>
    /// Whether a P6 = 1 (option board installed and available) response has been
    /// observed for the SUB receiver of this radio during at least one successful
    /// VT READ since installation. This flag is only set once confirmed by hardware
    /// — it is never set by static capability alone.
    /// </summary>
    public bool SubInstallationConfirmed { get; init; } = false;

    /// <summary>
    /// Whether SUB VC Tune commands should be allowed.
    /// This is <see langword="true"/> only when <see cref="SubInstallationConfirmed"/>
    /// is <see langword="true"/> AND the radio model statically supports the SUB board.
    /// A value of <see langword="true"/> persisted here does not guarantee that SUB VC
    /// Tune is available <em>right now</em> — P6 may be 2 (out of frequency range).
    /// </summary>
    public bool SupportsVCTuneSub { get; init; } = false;

    /// <summary>
    /// UTC instant of the most recent VT READ response that confirmed these
    /// capability values. <see cref="DateTime.MinValue"/> when no READ has been
    /// performed since installation or after a settings reset.
    /// </summary>
    public DateTime LastCapabilityReadUtc { get; init; } = DateTime.MinValue;

    /// <summary>
    /// Returns a conservative starting record for the given radio model using
    /// only static capability information. <see cref="SubInstallationConfirmed"/>
    /// and <see cref="SupportsVCTuneSub"/> are always <see langword="false"/> —
    /// they require a live P6 = 1 response before being set.
    /// </summary>
    public static VCTuneRadioCapabilities ForModel(string radioModel) => new()
    {
        RadioModel = radioModel,
        SupportsVCTuneMain = RadioCapabilities.SupportsVCTuneMain(radioModel),
        SubInstallationConfirmed = false,
        SupportsVCTuneSub = false,
        LastCapabilityReadUtc = DateTime.MinValue,
    };

    /// <summary>
    /// Returns a copy of this record with the SUB installation flag updated from a
    /// new P6 reading for the given receiver. The returned record should be saved
    /// immediately via <see cref="IVCTuneConfigurationStore.SaveCapabilitiesAsync"/>.
    /// </summary>
    /// <param name="p6">The raw P6 value (0 = not installed, 1 = available, 2 = out of range).</param>
    public VCTuneRadioCapabilities WithSubP6Update(int p6)
    {
        bool nowConfirmed = p6 >= 1; // P6 = 1 or 2 both mean the board is physically present
        bool nowAvailable = p6 == 1;
        bool confirmedAfterUpdate = SubInstallationConfirmed || nowConfirmed;

        return this with
        {
            SubInstallationConfirmed = confirmedAfterUpdate,
            SupportsVCTuneSub = confirmedAfterUpdate
                && RadioCapabilities.SupportsVCTuneSubStatic(RadioModel),
            LastCapabilityReadUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Returns a copy of this record with <see cref="SupportsVCTuneMain"/>
    /// overridden by the current static capability check result. Called at load
    /// time to prevent stale persisted values from diverging from the static truth.
    /// </summary>
    public VCTuneRadioCapabilities WithRefreshedStaticCapabilities() =>
        this with { SupportsVCTuneMain = RadioCapabilities.SupportsVCTuneMain(RadioModel) };
}
