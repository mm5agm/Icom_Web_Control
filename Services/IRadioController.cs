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

        // -- RF output power set (CI-V 14 0A) -----------------------------------
        // Power is expressed to the app as a 0–100 % level (the radio's native
        // form for CI-V 14 0A). The 100 W IC-7300 family maps watts 1:1 to
        // percent; a future higher-power radio would scale in its own controller.

        /// <summary>Read RF output power as a 0–100 % level (CI-V 14 0A read). -1 on a miss.</summary>
        Task<int> GetRfPowerPercentAsync(CancellationToken cancellationToken = default);

        /// <summary>Set RF output power as a 0–100 % level (CI-V 14 0A set, scaled to 0–255).</summary>
        Task SetRfPowerPercentAsync(int percent, CancellationToken cancellationToken = default);

        // -- AF (volume) level (CI-V 14 01) -------------------------------------
        // Receiver-wide 0–255 audio level (the IC-7300 has one receiver, so no
        // per-VFO addressing). Same 14-family form as RF power.

        /// <summary>Read AF (volume) level as a raw 0–255 value (CI-V 14 01 read). -1 on a miss.</summary>
        Task<int> GetAfGainAsync(CancellationToken cancellationToken = default);

        /// <summary>Set AF (volume) level as a raw 0–255 value (CI-V 14 01 set).</summary>
        Task SetAfGainAsync(int value, CancellationToken cancellationToken = default);

        // -- RX controls (receiver-wide on the IC-7300) -------------------------
        // The single-receiver IC-7300 has one set of these, so — like AF gain
        // and Twin PBT — they take no VFO. Levels are raw 0–255; the multi-state
        // functions use the radio's own small integer codes. All read methods
        // return -1 on a miss.

        // 14-family levels (0–255).
        Task<int> GetRfGainAsync(CancellationToken cancellationToken = default);
        Task SetRfGainAsync(int value, CancellationToken cancellationToken = default);
        Task<int> GetSquelchAsync(CancellationToken cancellationToken = default);
        Task SetSquelchAsync(int value, CancellationToken cancellationToken = default);
        Task<int> GetNrLevelAsync(CancellationToken cancellationToken = default);
        Task SetNrLevelAsync(int value, CancellationToken cancellationToken = default);
        Task<int> GetNbLevelAsync(CancellationToken cancellationToken = default);
        Task SetNbLevelAsync(int value, CancellationToken cancellationToken = default);
        /// <summary>Manual-notch position, 0–255 with 128 = centre of the passband (CI-V 14 0D).</summary>
        Task<int> GetNotchPositionAsync(CancellationToken cancellationToken = default);
        Task SetNotchPositionAsync(int value, CancellationToken cancellationToken = default);

        /// <summary>Preamp: 0=OFF, 1=P.AMP1, 2=P.AMP2 (CI-V 16 02).</summary>
        Task<int> GetPreampAsync(CancellationToken cancellationToken = default);
        Task SetPreampAsync(int value, CancellationToken cancellationToken = default);
        /// <summary>AGC time constant: 1=FAST, 2=MID, 3=SLOW (CI-V 16 12).</summary>
        Task<int> GetAgcAsync(CancellationToken cancellationToken = default);
        Task SetAgcAsync(int value, CancellationToken cancellationToken = default);
        /// <summary>Noise blanker on/off (CI-V 16 22).</summary>
        Task<bool> GetNoiseBlankerAsync(CancellationToken cancellationToken = default);
        Task SetNoiseBlankerAsync(bool on, CancellationToken cancellationToken = default);
        /// <summary>Noise reduction on/off (CI-V 16 40).</summary>
        Task<bool> GetNoiseReductionAsync(CancellationToken cancellationToken = default);
        Task SetNoiseReductionAsync(bool on, CancellationToken cancellationToken = default);
        /// <summary>Auto-notch filter on/off (CI-V 16 41).</summary>
        Task<bool> GetAutoNotchAsync(CancellationToken cancellationToken = default);
        Task SetAutoNotchAsync(bool on, CancellationToken cancellationToken = default);
        /// <summary>Manual-notch filter on/off (CI-V 16 48).</summary>
        Task<bool> GetManualNotchAsync(CancellationToken cancellationToken = default);
        Task SetManualNotchAsync(bool on, CancellationToken cancellationToken = default);
        /// <summary>Manual-notch filter width: 0=WIDE, 1=MID, 2=NAR (CI-V 16 57).</summary>
        Task<int> GetManualNotchWidthAsync(CancellationToken cancellationToken = default);
        Task SetManualNotchWidthAsync(int value, CancellationToken cancellationToken = default);
        /// <summary>IF DSP filter shape: 0=SHARP, 1=SOFT (CI-V 16 56).</summary>
        Task<int> GetIfFilterShapeAsync(CancellationToken cancellationToken = default);
        Task SetIfFilterShapeAsync(int value, CancellationToken cancellationToken = default);

        /// <summary>Attenuator: the IC-7300's single 20 dB pad, on/off (CI-V 11).</summary>
        Task<bool> GetAttenuatorAsync(CancellationToken cancellationToken = default);
        Task SetAttenuatorAsync(bool on, CancellationToken cancellationToken = default);

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

        // -- Twin PBT (Digital Passband Tuning, CI-V 14 07 / 14 08) -------------
        // A single receiver-wide passband adjustment (the IC-7300 has one
        // receiver, so no per-VFO addressing). Each edge is a 0–255 shift with
        // 128 as centre (no shift): the inner shift (PBT1) and the outer shift
        // (PBT2). Narrowing both toward opposite ends tightens the passband,
        // giving independent control of the low and high edges — the IC-7300's
        // equivalent of the Yaesu LCUT/HCUT audio filter.

        /// <summary>Read the PBT inner (PBT1) shift as a 0–255 value, 128=centre. -1 on a miss.</summary>
        Task<int> GetPbtInnerAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the PBT inner (PBT1) shift (0–255, 128=centre).</summary>
        Task SetPbtInnerAsync(int value, CancellationToken cancellationToken = default);

        /// <summary>Read the PBT outer (PBT2) shift as a 0–255 value, 128=centre. -1 on a miss.</summary>
        Task<int> GetPbtOuterAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the PBT outer (PBT2) shift (0–255, 128=centre).</summary>
        Task SetPbtOuterAsync(int value, CancellationToken cancellationToken = default);

        // -- Spectrum scope span (Phase 3 block 6, CI-V 27 15) -----------------

        /// <summary>
        /// Set the scope span (Center mode). <paramref name="spanHz"/> is the radio
        /// SPAN ± half-width in Hz: 2500, 5000, 10000, 25000, 50000, 100000,
        /// 250000 or 500000. The displayed full width is twice this.
        /// </summary>
        Task SetScopeSpanAsync(int spanHz, CancellationToken cancellationToken = default);

        // -- Power on/off (Phase 3 block 7, CI-V command 18) -------------------

        /// <summary>
        /// Turn the transceiver on or off (CI-V 18 01 / 18 00). Power-ON prepends
        /// the baud-dependent 0xFE wake-up preamble the asleep CI-V circuit needs.
        /// Caveat: over the IC-7300's USB CI-V the serial port is powered by the
        /// radio, so a full power-OFF drops the port entirely and remote power-ON
        /// only works over a separately-powered CI-V remote-jack link.
        /// </summary>
        Task SetPowerAsync(bool on, CancellationToken cancellationToken = default);
    }
}
