namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable parsed representation of a VT CAT response from the FTdx101.
/// Produced by <see cref="IVCTuneResponseParser.ParseResponse"/>.
/// All fields have been validated at parse time; every instance of this
/// record is guaranteed to contain coherent, in-range values.
/// </summary>
/// <param name="Band">
/// P1: which receiver this response describes.
/// </param>
/// <param name="CommandType">
/// P2: the current on/off/default state of the VC Tune preselector.
/// <see cref="VCTuneCommandType.On"/> = engaged,
/// <see cref="VCTuneCommandType.Off"/> = disengaged,
/// <see cref="VCTuneCommandType.Default"/> = auto-tune routine was the last command sent.
/// Null only when P2 contains an unexpected value not covered by the enum.
/// </param>
/// <param name="LastDirection">
/// P3: direction of the most recent step command reported by the radio.
/// '+' = forward, '-' = backward. Always present in the response even when
/// no step has yet been issued (radio defaults to '+').
/// </param>
/// <param name="LastStepAmount">
/// P4: amount of the most recent step (0–9) as reported by the radio.
/// </param>
/// <param name="Meter">
/// P5: preselector coupling indicator, 0–255. Higher values indicate better
/// capacitor alignment at the current frequency. This is <em>not</em> a
/// signal-strength meter; it is a measure of RF coupling through the
/// preselector at its current position.
/// </param>
/// <param name="Availability">
/// P6: hardware availability as a raw integer.
/// 0 = option board not fitted, 1 = available, 2 = fitted but out of range.
/// Use <see cref="AvailabilityState"/> for the typed <see cref="VcTuneAvailability"/> value.
/// </param>
/// <param name="RawResponse">
/// The complete, unmodified CAT response string exactly as received from the
/// radio (including trailing semicolon when present). Preserved for diagnostics
/// and developer log entries.
/// </param>
public sealed record VCTuneResponse(
    VCTuneBand Band,
    VCTuneCommandType? CommandType,
    char LastDirection,
    int LastStepAmount,
    int Meter,
    int Availability,
    string RawResponse)
{
    /// <summary>
    /// Returns <see cref="Availability"/> as the typed <see cref="VcTuneAvailability"/> enum.
    /// </summary>
    public VcTuneAvailability AvailabilityState =>
        Availability is >= 0 and <= 2
            ? (VcTuneAvailability)Availability
            : VcTuneAvailability.NotInstalled;

    /// <summary>True when P2 = 1 (VC Tune preselector is active).</summary>
    public bool IsOn => CommandType == VCTuneCommandType.On;

    /// <summary>True when P2 = 0 (VC Tune preselector is disengaged).</summary>
    public bool IsOff => CommandType == VCTuneCommandType.Off;

    /// <summary>
    /// True when P2 = 2 (the last command sent was DEFAULT / auto-tune;
    /// not yet resolved to On or Off by a confirming read).
    /// </summary>
    public bool IsDefault => CommandType == VCTuneCommandType.Default;

    /// <summary>True when P6 = 1 (hardware present and current frequency in range).</summary>
    public bool IsAvailable => Availability == 1;

    /// <summary>True when P6 = 0 (option board not fitted on this receiver).</summary>
    public bool IsNotInstalled => Availability == 0;

    /// <summary>True when P6 = 2 (board fitted but frequency out of range).</summary>
    public bool IsUnavailableAtFrequency => Availability == 2;
}
