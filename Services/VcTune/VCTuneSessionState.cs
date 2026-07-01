namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable snapshot of in-session VC Tune state held in memory by
/// <see cref="IVCTuneConfigurationStore"/>.
/// <para>
/// <b>Session state is never persisted to disk.</b> It is populated after the
/// first successful VT READ following a radio connection, and is reset to
/// <see cref="Empty"/> whenever the radio disconnects. Meter (P5) and
/// availability (P6) values are carried here because they are meaningful only
/// for the current connection and must not survive a reconnect.
/// </para>
/// </summary>
public sealed record VCTuneSessionState
{
    /// <summary>Current operational state of the MAIN VC Tune preselector.</summary>
    public VCTuneState MainState { get; init; } = VCTuneState.NotInstalled;

    /// <summary>Current operational state of the SUB VC Tune preselector.</summary>
    public VCTuneState SubState { get; init; } = VCTuneState.NotInstalled;

    /// <summary>
    /// Most recent P5 coupling indicator for the MAIN receiver (0–255).
    /// <c>−1</c> when no READ has been performed this session.
    /// <b>Never persisted to disk.</b>
    /// </summary>
    public int MainMeter { get; init; } = -1;

    /// <summary>
    /// Most recent P5 coupling indicator for the SUB receiver (0–255).
    /// <c>−1</c> when no READ has been performed or the SUB board is absent.
    /// <b>Never persisted to disk.</b>
    /// </summary>
    public int SubMeter { get; init; } = -1;

    /// <summary>
    /// Most recent raw P6 availability byte for the MAIN receiver.
    /// 0 = not installed, 1 = available, 2 = out of frequency range.
    /// <b>Never persisted to disk.</b>
    /// </summary>
    public int MainAvailability { get; init; } = 0;

    /// <summary>
    /// Most recent raw P6 availability byte for the SUB receiver.
    /// <b>Never persisted to disk.</b>
    /// </summary>
    public int SubAvailability { get; init; } = 0;

    /// <summary>
    /// The type of the last VC Tune command sent this session, or
    /// <see langword="null"/> if no command has been sent yet.
    /// </summary>
    public VCTuneCommandType? LastCommand { get; init; }

    /// <summary>
    /// The receiver targeted by the last VC Tune command, or
    /// <see langword="null"/> if no command has been sent yet.
    /// </summary>
    public VCTuneBand? LastCommandBand { get; init; }

    /// <summary>
    /// UTC instant of the most recent successful VT READ for the MAIN receiver,
    /// or <see langword="null"/> if none has occurred this session.
    /// </summary>
    public DateTime? LastMainReadUtc { get; init; }

    /// <summary>
    /// UTC instant of the most recent successful VT READ for the SUB receiver,
    /// or <see langword="null"/> if none has occurred this session.
    /// </summary>
    public DateTime? LastSubReadUtc { get; init; }

    /// <summary>
    /// Returns a session state with all fields at their initial values.
    /// Equivalent to <c>new VCTuneSessionState()</c>.
    /// </summary>
    public static VCTuneSessionState Empty => new();

    /// <summary>
    /// Returns a copy of this record updated with data from a successful
    /// MAIN or SUB VT READ response.
    /// </summary>
    public VCTuneSessionState WithReadResult(VcTuneReceiver receiver, VcTuneReadResult readResult)
    {
        if (receiver == VcTuneReceiver.Main)
        {
            return this with
            {
                MainState = readResult.IsValid
                    ? MapReadState(readResult)
                    : MainState,
                MainMeter = readResult.IsValid ? readResult.Meter : MainMeter,
                MainAvailability = readResult.IsValid ? (int)readResult.Availability : MainAvailability,
                LastMainReadUtc = readResult.IsValid ? DateTime.UtcNow : LastMainReadUtc,
            };
        }
        else
        {
            return this with
            {
                SubState = readResult.IsValid
                    ? MapReadState(readResult)
                    : SubState,
                SubMeter = readResult.IsValid ? readResult.Meter : SubMeter,
                SubAvailability = readResult.IsValid ? (int)readResult.Availability : SubAvailability,
                LastSubReadUtc = readResult.IsValid ? DateTime.UtcNow : LastSubReadUtc,
            };
        }
    }

    /// <summary>
    /// Returns a copy of this record updated to reflect that a SET command was
    /// just sent to the given receiver, recording it as the last command.
    /// </summary>
    public VCTuneSessionState WithCommand(VCTuneBand band, VCTuneCommandType commandType) =>
        this with
        {
            LastCommand = commandType,
            LastCommandBand = band,
        };

    // Maps a valid VcTuneReadResult to the corresponding VCTuneState.
    private static VCTuneState MapReadState(VcTuneReadResult r)
    {
        if (r.Availability == VcTuneAvailability.NotInstalled) return VCTuneState.NotInstalled;
        if (r.Availability == VcTuneAvailability.UnavailableFrequency) return VCTuneState.Unavailable;
        if (r.IsDefault) return VCTuneState.Default;
        return r.IsOn ? VCTuneState.On : VCTuneState.Off;
    }
}
