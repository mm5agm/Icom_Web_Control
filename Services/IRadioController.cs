using System.Threading;
using System.Threading.Tasks;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Which receiver / VFO a semantic operation targets. Protocol-free — the
    /// concrete controller maps this onto whatever the radio's wire format needs
    /// (Yaesu P1 digit, Icom CI-V VFO select, etc.).
    /// </summary>
    public enum RadioVfo
    {
        A,
        B
    }

    /// <summary>
    /// The semantic seam IWC introduces (the thing YWC lacked — see
    /// docs/design/iwc-clone-split-plan.md). Everything above this line —
    /// touch UI (CatController), voice (IntentDispatcher), Hamlib (RigctldServer),
    /// meter polling — speaks in radio concepts (frequency in Hz, mode name,
    /// S-meter units) and knows nothing about the wire protocol.
    ///
    /// Exactly one class below this line emits bytes on the wire:
    ///   - <see cref="StubRadioController"/> today (canned values, no hardware)
    ///   - CivRadioController from Phase 2 (the only class that emits CI-V)
    ///
    /// Frequencies are always in Hz. Modes are the app's display strings
    /// (e.g. "USB", "LSB", "CW-U"), matching what RadioStateService stores.
    /// </summary>
    public interface IRadioController
    {
        /// <summary>Open the link to the radio. Returns false on failure (no throw for the common "radio absent" case).</summary>
        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>Close the link to the radio.</summary>
        Task DisconnectAsync();

        /// <summary>True once the link is up.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// The radio's self-reported model identifier, read from the radio at
        /// connect (never hard-coded). Null until known. For CI-V this is the
        /// value returned by the transceiver-ID query.
        /// </summary>
        string? ModelId { get; }

        Task<long> GetFrequencyHzAsync(RadioVfo vfo, CancellationToken cancellationToken = default);
        Task SetFrequencyHzAsync(RadioVfo vfo, long frequencyHz, CancellationToken cancellationToken = default);

        Task<string> GetModeAsync(RadioVfo vfo, CancellationToken cancellationToken = default);
        Task SetModeAsync(RadioVfo vfo, string mode, CancellationToken cancellationToken = default);

        /// <summary>Signal strength as a raw 0–255 meter reading (same scale RadioStateService/calibration expect).</summary>
        Task<int> ReadSMeterAsync(RadioVfo vfo, CancellationToken cancellationToken = default);

        Task<bool> GetTransmitAsync(CancellationToken cancellationToken = default);
        Task SetTransmitAsync(bool transmit, CancellationToken cancellationToken = default);

        // -- VFO / split (Phase 3 block 5) --------------------------------------
        // GetFrequencyHzAsync / SetFrequencyHzAsync / GetModeAsync / SetModeAsync
        // above already take a RadioVfo and, from block 5, address each VFO
        // independently (CI-V 25/26). The members below add the operations that
        // have no per-VFO frequency/mode equivalent.

        /// <summary>Make <paramref name="vfo"/> the operating (selected) VFO (CI-V 07 00/01).</summary>
        Task SelectVfoAsync(RadioVfo vfo, CancellationToken cancellationToken = default);

        /// <summary>Exchange the contents of VFO A and VFO B; the selected VFO stays selected (CI-V 07 B0).</summary>
        Task ExchangeVfosAsync(CancellationToken cancellationToken = default);

        /// <summary>Make both VFOs equal — Icom "equalize" (CI-V 07 A0).</summary>
        Task EqualizeVfosAsync(CancellationToken cancellationToken = default);

        /// <summary>Read split state — true when transmitting on the other VFO (CI-V 0F).</summary>
        Task<bool> GetSplitAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn split on or off (CI-V 0F 01/00).</summary>
        Task SetSplitAsync(bool on, CancellationToken cancellationToken = default);
    }
}
