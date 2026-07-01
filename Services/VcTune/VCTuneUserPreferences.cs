namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable record of user-facing VC Tune preferences that persist across
/// application sessions. Stored via <see cref="IVCTuneConfigurationStore"/> in
/// <c>vcTune_config.json</c> in the per-user application-data folder.
/// <para>
/// All properties have safe defaults so the record is usable immediately even
/// if the file has never been written — <see cref="Default"/> returns a value
/// equivalent to calling <c>new VCTuneUserPreferences()</c>.
/// </para>
/// </summary>
public sealed record VCTuneUserPreferences
{
    /// <summary>
    /// The receiver selected by default when a voice command or UI button does
    /// not explicitly name a band. Defaults to <see cref="VCTuneBand.Main"/>.
    /// </summary>
    public VCTuneBand PreferredBand { get; init; } = VCTuneBand.Main;

    /// <summary>
    /// The step amount (0–9) pre-filled in the step control.
    /// 0 = one click of motor movement; 9 = nine clicks.
    /// Default is 3.
    /// </summary>
    public int PreferredStepAmount { get; init; } = 3;

    /// <summary>
    /// When <see langword="true"/>, the step direction defaults to '+' (forward / capacitance
    /// increase). When <see langword="false"/>, defaults to '−' (backward / capacitance
    /// decrease). The user can always override per-step via the UI.
    /// </summary>
    public bool PreferredDirectionIsPlus { get; init; } = true;

    /// <summary>
    /// Whether the MAIN VC Tune panel is expanded (true) or collapsed (false)
    /// in the UI. Persisted so the layout is remembered across sessions.
    /// </summary>
    public bool MainPanelExpanded { get; init; } = true;

    /// <summary>
    /// Whether the SUB VC Tune panel is expanded (true) or collapsed (false).
    /// Persisted independently of the MAIN panel state.
    /// </summary>
    public bool SubPanelExpanded { get; init; } = false;

    /// <summary>
    /// When <see langword="true"/>, the UI shows a confirmation prompt before
    /// sending a VC Tune OFF command. Useful for operators who routinely leave
    /// the preselector engaged and want a guard against accidental deactivation.
    /// </summary>
    public bool ConfirmOffCommand { get; init; } = false;

    /// <summary>
    /// When <see langword="true"/>, the UI shows a confirmation prompt before
    /// sending a VC Tune DEFAULT (auto-tune) command, because the motor sweep
    /// takes several seconds and temporarily peaks the received signal.
    /// </summary>
    public bool ConfirmDefaultCommand { get; init; } = false;

    /// <summary>
    /// How the P5 preselector coupling indicator is formatted in the UI.
    /// Defaults to <see cref="VCTuneMeterDisplayMode.Percentage"/>.
    /// </summary>
    public VCTuneMeterDisplayMode MeterDisplayMode { get; init; } = VCTuneMeterDisplayMode.Percentage;

    /// <summary>
    /// Returns an instance with all properties at their factory defaults.
    /// Equivalent to <c>new VCTuneUserPreferences()</c>.
    /// </summary>
    public static VCTuneUserPreferences Default => new();

    /// <summary>
    /// Returns a copy of this instance with <see cref="PreferredStepAmount"/>
    /// clamped to the valid range 0–9.
    /// </summary>
    public VCTuneUserPreferences WithValidatedStep() =>
        PreferredStepAmount is >= 0 and <= 9
            ? this
            : this with { PreferredStepAmount = Math.Clamp(PreferredStepAmount, 0, 9) };
}
