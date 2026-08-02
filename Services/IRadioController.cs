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
    /// One of the radio's own internal memory channels (1–99), in protocol-free
    /// terms. Frequencies are Hz; <see cref="Mode"/> is the app's display string.
    /// <see cref="IsEmpty"/> flags a blank/unprogrammed channel — its other fields
    /// are meaningless. The TX fields carry the split transmit frequency/mode when
    /// <see cref="SplitOn"/>; controllers that don't split mirror the RX values.
    /// </summary>
    public sealed record RadioMemoryChannel
    {
        public int Channel { get; init; }              // 1..99
        public bool IsEmpty { get; init; }
        public long FrequencyHz { get; init; }
        public string Mode { get; init; } = "USB";
        public int Filter { get; init; } = 1;          // 1=FIL1, 2=FIL2, 3=FIL3
        public bool SplitOn { get; init; }
        public long TxFrequencyHz { get; init; }
        public string TxMode { get; init; } = "USB";
        public string Name { get; init; } = "";        // up to 16 chars
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
        /// Re-broadcast the current scope status ("SdrStatus") on the next sweep
        /// instead of waiting for the periodic re-announce. Called when a browser
        /// connects: the spectrum panel starts hidden and is only revealed by an
        /// SdrStatus, so without this a late-joining client watches an empty gap
        /// while frames are already arriving. Default no-op — a controller with
        /// no scope has nothing to announce.
        /// </summary>
        void RequestScopeStatusAnnounce() { }

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

        // -- Antenna tuner (CI-V 1C 01) -----------------------------------------
        // The IC-7300's internal ATU. Unlike the Yaesu AC command, CI-V reports a
        // tuning cycle in progress, so state is a small integer, not a bool:
        //   0 = OFF (through), 1 = ON (in line), 2 = tuning cycle in progress.

        /// <summary>Read the antenna-tuner state: 0=OFF, 1=ON, 2=TUNING (CI-V 1C 01 read). -1 on a miss.</summary>
        Task<int> GetTunerAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Set the antenna tuner: 0=OFF, 1=ON, 2=start a tuning cycle
        /// (CI-V 1C 01 set). Values outside 0–2 are ignored.
        /// </summary>
        Task SetTunerAsync(int state, CancellationToken cancellationToken = default);

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

        // -- IF passband filter width + FIL slot (CI-V 1A 03 / 26) --------------
        // The width of the currently-selected FIL slot for the VFO's current
        // mode, in Hz. Valid Hz values depend on the mode group (SSB/CW, RTTY,
        // AM); FM has no adjustable width. The controller snaps any requested Hz
        // to the nearest value the radio supports. FIL1/2/3 is the preset slot
        // the width applies to, carried in the command 26 mode frame.

        /// <summary>Read the IF passband width in Hz for the current mode (CI-V 1A 03). -1 on a miss or when the mode has no width (FM).</summary>
        Task<int> GetIfFilterWidthHzAsync(RadioVfo vfo, CancellationToken cancellationToken = default);

        /// <summary>Set the IF passband width, snapping <paramref name="hz"/> to the nearest value the current mode supports (CI-V 1A 03).</summary>
        Task SetIfFilterWidthHzAsync(RadioVfo vfo, int hz, CancellationToken cancellationToken = default);

        /// <summary>Read the selected filter slot: 1=FIL1, 2=FIL2, 3=FIL3 (from the CI-V 26 mode reply). -1 on a miss.</summary>
        Task<int> GetSelectedFilterAsync(RadioVfo vfo, CancellationToken cancellationToken = default);

        /// <summary>Select filter slot 1/2/3 for the VFO, preserving its mode (CI-V 26).</summary>
        Task SetSelectedFilterAsync(RadioVfo vfo, int fil, CancellationToken cancellationToken = default);

        // -- RX Tone Control: HPF/LPF audio filter (CI-V 1A 05) ----------------
        // Per-mode receive audio high-pass (low-cut) and low-pass (high-cut)
        // edges — the IC-7300's equivalent of the Yaesu Audio Filter. Both edges
        // are reported/set in Hz with 0 = Through (no filtering). The controller
        // picks the menu item from the VFO's current mode; SSB-DATA and modes
        // without Tone Control report (-1, -1) / ignore the set.

        /// <summary>Read the RX HPF (low-cut) and LPF (high-cut) edges in Hz for the current mode; 0 = Through. (-1, -1) when unavailable (CI-V 1A 05).</summary>
        Task<(int hpfHz, int lpfHz)> GetRxFilterAsync(RadioVfo vfo, CancellationToken cancellationToken = default);

        /// <summary>Set the RX HPF (low-cut) and LPF (high-cut) edges in Hz for the current mode; 0 = Through. Snaps to the radio's 100 Hz steps (CI-V 1A 05).</summary>
        Task SetRxFilterAsync(RadioVfo vfo, int hpfHz, int lpfHz, CancellationToken cancellationToken = default);

        /// <summary>Attenuator: the IC-7300's single 20 dB pad, on/off (CI-V 11).</summary>
        Task<bool> GetAttenuatorAsync(CancellationToken cancellationToken = default);
        Task SetAttenuatorAsync(bool on, CancellationToken cancellationToken = default);

        // -- CW keyer (CI-V 17 / 14 0C / 14 09 / 14 0F / 16 47) ----------------
        // Memory-keyer send plus the keyer settings, in the operator's natural
        // units (WPM / Hz / dots / 0–1–2 break-in mode). The controller converts
        // to/from the radio's 0–255 code. Note break-in delay is in DOTS on the
        // IC-7300, not milliseconds.

        /// <summary>Key a memory message as Morse (CI-V 17). Non-sendable chars are dropped and the text capped at 30 chars; returns the cleaned text actually sent.</summary>
        Task<string> SendCwMessageAsync(string message, CancellationToken cancellationToken = default);

        /// <summary>Abort a CW message currently being keyed (CI-V 17 FF).</summary>
        Task StopCwAsync(CancellationToken cancellationToken = default);

        /// <summary>Read the keyer speed in WPM (6–48). -1 on a miss (CI-V 14 0C).</summary>
        Task<int> GetCwSpeedWpmAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the keyer speed in WPM (clamped 6–48; CI-V 14 0C).</summary>
        Task SetCwSpeedWpmAsync(int wpm, CancellationToken cancellationToken = default);

        /// <summary>Read the CW pitch/sidetone in Hz (300–900). -1 on a miss (CI-V 14 09).</summary>
        Task<int> GetCwPitchHzAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the CW pitch/sidetone in Hz (clamped 300–900; CI-V 14 09).</summary>
        Task SetCwPitchHzAsync(int hz, CancellationToken cancellationToken = default);

        /// <summary>Read the break-in delay in dots (2.0–13.0). -1 on a miss (CI-V 14 0F).</summary>
        Task<double> GetCwBreakInDelayDotsAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the break-in delay in dots (clamped 2.0–13.0; CI-V 14 0F).</summary>
        Task SetCwBreakInDelayDotsAsync(double dots, CancellationToken cancellationToken = default);

        /// <summary>Read the break-in mode: 0=OFF, 1=SEMI, 2=FULL. -1 on a miss (CI-V 16 47).</summary>
        Task<int> GetCwBreakInAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the break-in mode (0=OFF, 1=SEMI, 2=FULL; CI-V 16 47).</summary>
        Task SetCwBreakInAsync(int mode, CancellationToken cancellationToken = default);

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

        /// <summary>
        /// Set the scope mode (CI-V 27 14): Center or Fixed. Center keeps the
        /// sweep centred on the operating frequency — the assumption the web
        /// SpectrumPanel's axis is built on — so <paramref name="center"/> = true
        /// is the aligned mode; false selects Fixed. Retried until acknowledged.
        /// </summary>
        Task SetScopeModeAsync(bool center, CancellationToken cancellationToken = default);

        /// <summary>
        /// Turn the spectrum scope and its waveform-to-controller output on or off
        /// (CI-V 27 10 / 27 11). Off stops the radio streaming 27 00 frames, so the
        /// web spectrum goes quiet. A manual off is remembered so a reconnect
        /// doesn't switch the scope back on. Also the operator's control for
        /// testing whether the scope stream is the source of receiver noise.
        /// </summary>
        Task SetScopeEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

        /// <summary>
        /// Set the pseudo-dual watch panel's crop half-width in Hz (Phase 5,
        /// "ZoomIn" span mode). This is a display-only crop of the single physical
        /// sweep around the watch VFO — it emits no CI-V and never affects the
        /// primary panel or the real scope. <paramref name="halfHz"/> uses the same
        /// ± half-width values as <see cref="SetScopeSpanAsync"/>; 0 (or a value
        /// wider than the available sweep) means "auto" — the widest crop that fits.
        /// </summary>
        Task SetWatchCropSpanAsync(int halfHz, CancellationToken cancellationToken = default);

        // -- Power on/off (Phase 3 block 7, CI-V command 18) -------------------

        /// <summary>
        /// Turn the transceiver on or off (CI-V 18 01 / 18 00). Power-ON prepends
        /// the baud-dependent 0xFE wake-up preamble the asleep CI-V circuit needs.
        /// Caveat: over the IC-7300's USB CI-V the serial port is powered by the
        /// radio, so a full power-OFF drops the port entirely and remote power-ON
        /// only works over a separately-powered CI-V remote-jack link.
        /// </summary>
        Task SetPowerAsync(bool on, CancellationToken cancellationToken = default);

        // -- Radio memory channels (CI-V 1A 00) --------------------------------
        // The transceiver's own 99 internal memory channels, read/written as
        // whole-channel content so the operating VFO is never disturbed. These
        // back the Memories page's "Import from Radio" / "Export to Radio"
        // buttons; the app's own memory list is a separate store. Memory
        // read/write does not transmit.

        /// <summary>Read the full content of memory channel <paramref name="channel"/> (1–99) via CI-V 1A 00. Returns a channel flagged <see cref="RadioMemoryChannel.IsEmpty"/> when blank, or null on a transaction miss.</summary>
        Task<RadioMemoryChannel?> ReadMemoryChannelAsync(int channel, CancellationToken cancellationToken = default);

        /// <summary>Write the full content of a memory channel via CI-V 1A 00. Returns false if the write was not acknowledged.</summary>
        Task<bool> WriteMemoryChannelAsync(RadioMemoryChannel memory, CancellationToken cancellationToken = default);

        /// <summary>Clear (blank) memory channel <paramref name="channel"/> (1–99) via CI-V 1A 00 … FF. Returns false if not acknowledged.</summary>
        Task<bool> ClearMemoryChannelAsync(int channel, CancellationToken cancellationToken = default);
    }
}
