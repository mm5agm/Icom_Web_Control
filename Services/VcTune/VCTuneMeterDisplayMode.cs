namespace Yaesu_Web_Control.Services;

/// <summary>
/// Controls how the P5 preselector coupling indicator is formatted in the UI.
/// Stored in <see cref="VCTuneUserPreferences.MeterDisplayMode"/>.
/// </summary>
public enum VCTuneMeterDisplayMode
{
    /// <summary>
    /// Display the raw P5 integer value 0–255 as reported by the radio.
    /// Useful for calibration and diagnostics.
    /// </summary>
    Raw,

    /// <summary>
    /// Display P5 scaled to a 0–100 % range (P5 × 100 / 255).
    /// The default display mode.
    /// </summary>
    Percentage,

    /// <summary>
    /// Display P5 as a horizontal bar only, with no numeric label.
    /// Compact view for minimal-UI layouts.
    /// </summary>
    BarOnly,

    /// <summary>
    /// Hide the P5 meter entirely.
    /// Intended for operators who rely only on signal-level audio rather than
    /// the preselector coupling indicator.
    /// </summary>
    Hidden,
}
