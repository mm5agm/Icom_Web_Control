namespace Yaesu_Web_Control.Services;

/// <summary>
/// Thread-safe implementation of <see cref="IVCTuneStateMachine"/>.
/// Maintains one <see cref="VCTuneStateSnapshot"/> per receiver (MAIN index 0, SUB index 1).
/// <para>
/// The initial state for both receivers is <see cref="VCTuneState.NotInstalled"/>
/// with Meter = -1, reflecting that nothing is known until the first VT READ response
/// is received. The first <see cref="UpdateState"/> call for each receiver will
/// replace this placeholder with real data.
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;IVCTuneStateMachine, VCTuneStateMachine&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneStateMachine : IVCTuneStateMachine
{
    // Index 0 = VCTuneBand.Main, Index 1 = VCTuneBand.Sub.
    // Both match the integer values of the VCTuneBand enum.
    private readonly VCTuneStateSnapshot[] _snapshots = new VCTuneStateSnapshot[2];
    private readonly object _lock = new();

    /// <summary>Initialises both receivers to <see cref="VCTuneState.Off"/> so commands are not blocked before the first probe.</summary>
    public VCTuneStateMachine()
    {
        var now = DateTime.UtcNow;
        _snapshots[(int)VCTuneBand.Main] =
            new VCTuneStateSnapshot(VCTuneBand.Main, VCTuneState.Off, Meter: -1, Availability: 1, now);
        _snapshots[(int)VCTuneBand.Sub] =
            new VCTuneStateSnapshot(VCTuneBand.Sub, VCTuneState.Off, Meter: -1, Availability: 1, now);
    }

    // ══════════════════════════════════════════════════════════════════════
    // IVCTuneStateMachine — mutators
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void UpdateState(VCTuneResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Availability is < 0 or > 2)
            throw new ArgumentOutOfRangeException(
                nameof(response),
                $"VCTuneResponse.Availability = {response.Availability} is outside the valid range 0–2.");

        int idx = BandIndex(response.Band);
        VCTuneState next = ResolveState(response.Availability, response.CommandType, previous: default);

        lock (_lock)
        {
            VCTuneState previous = _snapshots[idx].State;

            // Re-resolve now that we hold the lock and have the real previous state.
            next = ResolveState(response.Availability, response.CommandType, previous);

            _snapshots[idx] = new VCTuneStateSnapshot(
                response.Band,
                next,
                response.Meter,
                response.Availability,
                DateTime.UtcNow);
        }
    }

    /// <inheritdoc/>
    public void NotifyCommand(VCTuneBand band, VCTuneCommandType commandType)
    {
        GuardBand(band);

        // Read commands and null-equivalent values produce no state change.
        if (commandType == VCTuneCommandType.Read)
            return;

        int idx = BandIndex(band);

        lock (_lock)
        {
            VCTuneStateSnapshot current = _snapshots[idx];

            VCTuneState nextState = CommandTypeToOptimisticState(commandType, current.State);

            _snapshots[idx] = current with
            {
                State = nextState,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // IVCTuneStateMachine — queries
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public VCTuneState GetState(VCTuneBand band)
    {
        GuardBand(band);
        lock (_lock)
            return _snapshots[BandIndex(band)].State;
    }

    /// <inheritdoc/>
    public VCTuneStateSnapshot GetLastSnapshot(VCTuneBand band)
    {
        GuardBand(band);
        lock (_lock)
            return _snapshots[BandIndex(band)];
    }

    // ══════════════════════════════════════════════════════════════════════
    // Transition logic
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the next <see cref="VCTuneState"/> from P6 availability and the
    /// command type carried in a VT response.
    /// P6 always takes priority; the command type is only consulted when P6 = 1.
    /// </summary>
    private static VCTuneState ResolveState(
        int availability,
        VCTuneCommandType? commandType,
        VCTuneState previous)
    {
        // P6 = 0: option board not fitted — hard override regardless of P2.
        if (availability == 0) return VCTuneState.NotInstalled;

        // P6 = 1 or 2: board is fitted. P6 = 2 is a transient condition
        // (preselector off, settling, or RF-path transition) — not a band limit.
        // P2 always drives the state regardless of which P6 value is present.
        return commandType switch
        {
            VCTuneCommandType.On      => VCTuneState.On,
            VCTuneCommandType.Off     => VCTuneState.Off,
            VCTuneCommandType.Default => VCTuneState.Default,
            VCTuneCommandType.Step    => VCTuneState.Stepping,
            VCTuneCommandType.Center  => VCTuneState.Centering,

            // Read-only or absent command type: preserve the previous state.
            // This covers pure VT{P1}; READ responses where the radio echos its
            // current P2, which is already reflected in the previous snapshot.
            _ => previous
        };
    }

    /// <summary>
    /// Maps a SET command type to the optimistic state to apply immediately
    /// before the confirming READ response arrives.
    /// </summary>
    private static VCTuneState CommandTypeToOptimisticState(
        VCTuneCommandType commandType,
        VCTuneState previous)
        => commandType switch
        {
            VCTuneCommandType.On      => VCTuneState.On,
            VCTuneCommandType.Off     => VCTuneState.Off,
            VCTuneCommandType.Default => VCTuneState.Default,
            VCTuneCommandType.Step    => VCTuneState.Stepping,
            VCTuneCommandType.Center  => VCTuneState.Centering,
            _                         => previous
        };

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Maps a <see cref="VCTuneBand"/> to the internal array index.</summary>
    private static int BandIndex(VCTuneBand band) => (int)band; // Main=0, Sub=1

    /// <summary>Guards that <paramref name="band"/> is a defined enum member.</summary>
    private static void GuardBand(VCTuneBand band)
    {
        if (!Enum.IsDefined(band))
            throw new ArgumentOutOfRangeException(
                nameof(band), band,
                $"'{band}' is not a valid VCTuneBand value.");
    }
}
