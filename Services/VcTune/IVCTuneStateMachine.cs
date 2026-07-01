namespace Yaesu_Web_Control.Services;

/// <summary>
/// Contract for the VC Tune preselector state machine.
/// Maintains and transitions the operational state of MAIN and SUB receivers
/// based on parsed VT CAT responses and SET-command notifications.
/// <para>
/// Implementations are thread-safe and may be registered as singletons.
/// No CAT commands are issued and no responses are parsed by this interface;
/// it is a pure state-management contract.
/// </para>
/// </summary>
public interface IVCTuneStateMachine
{
    /// <summary>
    /// Updates the state of the appropriate receiver based on a parsed VT response.
    /// <para>
    /// The receiver is determined by <see cref="VCTuneResponse.Band"/>.
    /// P6 availability takes priority over <see cref="VCTuneResponse.CommandType"/>:
    /// if P6 = 0 the state becomes <see cref="VCTuneState.NotInstalled"/>;
    /// if P6 = 2 the state becomes <see cref="VCTuneState.Unavailable"/>;
    /// only when P6 = 1 does <see cref="VCTuneResponse.CommandType"/> drive the transition.
    /// </para>
    /// <para>
    /// This method also accepts synthesised <see cref="VCTuneResponse"/> objects, for
    /// example those constructed by <see cref="VcTuneService"/> after sending a Step or
    /// Center command, so that optimistic state transitions can be applied before
    /// the confirming READ response arrives.
    /// </para>
    /// </summary>
    /// <param name="response">The parsed (or synthesised) VT CAT response.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="response"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="VCTuneResponse.Availability"/> is outside 0–2.
    /// </exception>
    void UpdateState(VCTuneResponse response);

    /// <summary>
    /// Applies an optimistic state transition when a SET command has just been sent
    /// and the confirming READ response has not yet arrived.
    /// <para>
    /// The current <see cref="VCTuneStateSnapshot.Meter"/> and
    /// <see cref="VCTuneStateSnapshot.Availability"/> values are preserved;
    /// only <see cref="VCTuneStateSnapshot.State"/> and
    /// <see cref="VCTuneStateSnapshot.Timestamp"/> are updated.
    /// </para>
    /// <para>
    /// If the receiver is currently <see cref="VCTuneState.NotInstalled"/> or
    /// <see cref="VCTuneState.Unavailable"/>, the call is rejected with an exception.
    /// </para>
    /// </summary>
    /// <param name="band">The target receiver.</param>
    /// <param name="commandType">
    /// The type of SET command that was just sent.
    /// Read and null values are accepted but produce no state change.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="band"/> is not a defined <see cref="VCTuneBand"/> value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the current state is <see cref="VCTuneState.NotInstalled"/> or
    /// <see cref="VCTuneState.Unavailable"/> and <paramref name="commandType"/> would
    /// attempt to engage the preselector.
    /// </exception>
    void NotifyCommand(VCTuneBand band, VCTuneCommandType commandType);

    /// <summary>
    /// Returns the most recent <see cref="VCTuneState"/> for the given receiver.
    /// </summary>
    /// <param name="band">The receiver to query.</param>
    /// <returns>The current state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="band"/> is not a defined <see cref="VCTuneBand"/> value.
    /// </exception>
    VCTuneState GetState(VCTuneBand band);

    /// <summary>
    /// Returns the most recent <see cref="VCTuneStateSnapshot"/> for the given receiver.
    /// The returned snapshot is immutable; callers may retain it safely across threads.
    /// </summary>
    /// <param name="band">The receiver to query.</param>
    /// <returns>The latest snapshot (never null).</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="band"/> is not a defined <see cref="VCTuneBand"/> value.
    /// </exception>
    VCTuneStateSnapshot GetLastSnapshot(VCTuneBand band);
}
