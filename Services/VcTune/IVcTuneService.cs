namespace Yaesu_Web_Control.Services;

/// <summary>
/// Contract for the VC Tune preselector service.
/// Provides SET and READ operations over the FTdx101 VT CAT command.
/// All operations are async and safe to call concurrently; internal
/// serialisation is handled via <see cref="CatRequestSemaphore"/>.
/// </summary>
public interface IVcTuneService
{
    /// <summary>
    /// Probes MAIN and (on FTdx101MP) SUB VC Tune after radio initialisation
    /// to establish baseline on/off state, meter value, and P6 availability.
    /// Called by RadioInitializationService after DT0 is confirmed.
    /// Does not acquire <see cref="CatRequestSemaphore"/>; runs during the
    /// exclusive init phase.
    /// </summary>
    Task ProbeCapabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>Enables VC Tune on the specified receiver.</summary>
    /// <param name="receiver">MAIN or SUB.</param>
    Task<VcTuneSetResult> SetVCTuneOnAsync(VcTuneReceiver receiver, CancellationToken cancellationToken = default);

    /// <summary>Disables VC Tune on the specified receiver.</summary>
    Task<VcTuneSetResult> SetVCTuneOffAsync(VcTuneReceiver receiver, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the automatic default-tune routine on the specified receiver.
    /// The radio sweeps the capacitor to find the position giving the strongest
    /// received signal, then reports On (P2=1) or Off (P2=0) via the confirmation READ.
    /// </summary>
    Task<VcTuneSetResult> SetVCTuneDefaultAsync(VcTuneReceiver receiver, CancellationToken cancellationToken = default);

    /// <summary>
    /// Steps the VC Tune capacitor in the given direction by the given amount.
    /// VC Tune must be On before stepping; the guard is enforced here.
    /// </summary>
    /// <param name="receiver">MAIN or SUB.</param>
    /// <param name="direction">'+' (forward) or '-' (backward).</param>
    /// <param name="amount">Step amount 0–9.</param>
    Task<VcTuneSetResult> SetVCTuneStepAsync(VcTuneReceiver receiver, char direction, int amount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Centers the VC Tune capacitor on the current frequency by sending VT{P1}+0.
    /// VC Tune must be On before centering; the guard is enforced here.
    /// Motor lockout of 3 s is enforced after each center command.
    /// </summary>
    Task<VcTuneSetResult> SetVCTuneCenterAsync(VcTuneReceiver receiver, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads current VC Tune status from the radio for the specified receiver.
    /// Acquires <see cref="CatRequestSemaphore"/>; returns cached state if a
    /// READ occurred within the last second.
    /// </summary>
    Task<VcTuneReadResult> ReadVCTuneStatusAsync(VcTuneReceiver receiver, CancellationToken cancellationToken = default);
}
