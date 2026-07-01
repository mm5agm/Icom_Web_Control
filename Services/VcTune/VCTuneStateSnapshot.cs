namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable point-in-time snapshot of one VC Tune receiver's state,
/// produced and stored by <see cref="IVCTuneStateMachine"/>.
/// Every instance is guaranteed to contain validated, in-range values.
/// </summary>
/// <param name="Band">
/// The receiver this snapshot describes (MAIN or SUB).
/// </param>
/// <param name="State">
/// The current operational state of the preselector.
/// </param>
/// <param name="Meter">
/// The P5 preselector coupling indicator at the time of this snapshot (0–255).
/// <c>-1</c> indicates the value is not yet known (initial placeholder before
/// the first READ response is received).
/// </param>
/// <param name="Availability">
/// The raw P6 availability byte: 0 = not installed, 1 = available,
/// 2 = fitted but frequency out of range.
/// Use <see cref="AvailabilityState"/> for the typed <see cref="VcTuneAvailability"/> value.
/// </param>
/// <param name="Timestamp">
/// UTC instant at which this snapshot was written. Use <see cref="AgeSinceUtc"/>
/// to compute staleness without repeated calls to <see cref="DateTime.UtcNow"/>.
/// </param>
public sealed record VCTuneStateSnapshot(
    VCTuneBand Band,
    VCTuneState State,
    int Meter,
    int Availability,
    DateTime Timestamp)
{
    /// <summary>Returns <see cref="Availability"/> as the typed <see cref="VcTuneAvailability"/> enum.</summary>
    public VcTuneAvailability AvailabilityState =>
        Availability is >= 0 and <= 2
            ? (VcTuneAvailability)Availability
            : VcTuneAvailability.NotInstalled;

    /// <summary>True when the preselector is actively in the signal path.</summary>
    public bool IsActive => State is VCTuneState.On or VCTuneState.Stepping or VCTuneState.Centering;

    /// <summary>True when the preselector hardware is present and in-band.</summary>
    public bool IsHardwareReady => Availability == 1;

    /// <summary>
    /// Returns the elapsed time since this snapshot was taken (using UTC now).
    /// </summary>
    public TimeSpan AgeSinceUtc => DateTime.UtcNow - Timestamp;
}
