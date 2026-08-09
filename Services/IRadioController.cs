using System.Collections.Generic;
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
        /// Re-broadcast the current scope status ("SdrStatus") now, and again on
        /// the next sweep, instead of waiting for the periodic re-announce. Called
        /// when a browser connects: the spectrum panel's state comes from
        /// SdrStatus, so without this a late-joining client watches an empty gap
        /// while frames are already arriving — and if no frames are arriving it
        /// never hears why at all (GitHub #1). Default no-op — a controller with
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

        // -- RX Tone Control: Bass/Treble (CI-V 1A 05) -------------------------
        // The other half of the same SET > Tone Control > RX menu group: a bass
        // and a treble shelf, each −5…+5 with 0 flat. Narrower coverage than the
        // HPF/LPF pair above — the radio has these for SSB, AM and FM only, so
        // CW, RTTY and SSB-DATA report unavailable rather than a level.

        /// <summary>Read the RX Bass and Treble levels (−5…+5, 0 = flat) for the VFO's current mode (CI-V 1A 05). <c>available</c> is false in CW/RTTY/SSB-DATA or on a miss.</summary>
        Task<(bool available, int bass, int treble)> GetRxToneAsync(RadioVfo vfo, CancellationToken cancellationToken = default);

        /// <summary>Set the RX Bass and Treble levels (−5…+5, 0 = flat) for the VFO's current mode (CI-V 1A 05). Ignored when the mode has no Bass/Treble.</summary>
        Task SetRxToneAsync(RadioVfo vfo, int bass, int treble, CancellationToken cancellationToken = default);

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

        // -- TX audio chain: mic, speech compressor, monitor (CI-V 14 / 16) ----
        // Percentages, not raw 0–255: these are the units the sliders and the
        // radio's own menus use, and the controller does the 0–255 scaling. The
        // compressor level is the exception the IC-7300 forces on us — its
        // 0000–0255 wire range shows on the panel as 0–10, so the percentage
        // here is of full scale, not the panel number.

        /// <summary>Read microphone gain as 0–100 %. -1 on a miss (CI-V 14 0B).</summary>
        Task<int> GetMicGainPercentAsync(CancellationToken cancellationToken = default);

        /// <summary>Set microphone gain as 0–100 % (CI-V 14 0B, scaled to 0–255).</summary>
        Task SetMicGainPercentAsync(int percent, CancellationToken cancellationToken = default);

        /// <summary>Read the speech-compressor on/off state (CI-V 16 44).</summary>
        Task<bool> GetSpeechCompressorAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn the speech compressor on or off (CI-V 16 44).</summary>
        Task SetSpeechCompressorAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Read the speech-compressor level as 0–100 % of full scale. -1 on a miss (CI-V 14 0E).</summary>
        Task<int> GetCompressorLevelPercentAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the speech-compressor level as 0–100 % of full scale (CI-V 14 0E).</summary>
        Task SetCompressorLevelPercentAsync(int percent, CancellationToken cancellationToken = default);

        /// <summary>Read the TX monitor on/off state (CI-V 16 45).</summary>
        Task<bool> GetMonitorAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn the TX monitor on or off (CI-V 16 45).</summary>
        Task SetMonitorAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Read the monitor audio level as 0–100 %. -1 on a miss (CI-V 14 15).</summary>
        Task<int> GetMonitorLevelPercentAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the monitor audio level as 0–100 % (CI-V 14 15).</summary>
        Task SetMonitorLevelPercentAsync(int percent, CancellationToken cancellationToken = default);

        // -- VOX (CI-V 16 46 / 14 16 / 14 17 / 1A 05 02 67) --------------------
        // Sensitivity and ANTI-VOX are percentages; the delay is in
        // MILLISECONDS at the seam because that is what an operator sets, but
        // the radio stores 0.1 s steps up to 2.0 s, so the controller rounds and
        // clamps. Note ANTI-VOX runs the other way to sensitivity: a HIGHER
        // value makes VOX LESS likely to trip on receiver audio.

        /// <summary>Read the VOX on/off state (CI-V 16 46).</summary>
        Task<bool> GetVoxAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn VOX on or off (CI-V 16 46).</summary>
        Task SetVoxAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Read VOX sensitivity as 0–100 %. -1 on a miss (CI-V 14 16).</summary>
        Task<int> GetVoxGainPercentAsync(CancellationToken cancellationToken = default);

        /// <summary>Set VOX sensitivity as 0–100 % (CI-V 14 16).</summary>
        Task SetVoxGainPercentAsync(int percent, CancellationToken cancellationToken = default);

        /// <summary>Read ANTI-VOX as 0–100 %; higher = less sensitive. -1 on a miss (CI-V 14 17).</summary>
        Task<int> GetAntiVoxPercentAsync(CancellationToken cancellationToken = default);

        /// <summary>Set ANTI-VOX as 0–100 %; higher = less sensitive (CI-V 14 17).</summary>
        Task SetAntiVoxPercentAsync(int percent, CancellationToken cancellationToken = default);

        /// <summary>Read the VOX hang delay in ms (0–2000, 100 ms steps). -1 on a miss (CI-V 1A 05 02 67).</summary>
        Task<int> GetVoxDelayMsAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the VOX hang delay in ms; rounded to the radio's 100 ms steps and clamped to 0–2000 (CI-V 1A 05 02 67).</summary>
        Task SetVoxDelayMsAsync(int ms, CancellationToken cancellationToken = default);

        // -- APF: Audio Peak Filter (CI-V 16 32) -------------------------------
        // CW only, and not a simple on/off — OFF is one of four positions, the
        // other three being the filter width. The audible width of each position
        // depends on the APF TYPE menu setting (SHARP: 320/160/80 Hz, SOFT:
        // wider), which IWC does not change.

        /// <summary>Read the APF setting: 0=OFF, 1=WIDE, 2=MID, 3=NAR. -1 on a miss (CI-V 16 32).</summary>
        Task<int> GetApfAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the APF: 0=OFF, 1=WIDE, 2=MID, 3=NAR. Values outside 0–3 are ignored (CI-V 16 32).</summary>
        Task SetApfAsync(int setting, CancellationToken cancellationToken = default);

        // -- RIT / ΔTX (CI-V 21) -----------------------------------------------
        // The IC-7300's receive-incremental-tuning offset, which is what the
        // app's "clarifier" controls drive. One offset serves both RIT and ΔTX,
        // exactly as on the radio's own screen — turning ΔTX on makes the same
        // offset apply to transmit. Offset is in Hz, ±9990, in 10 Hz steps.

        /// <summary>Read the RIT offset in Hz (±9990). Returns 0 on a miss (CI-V 21 00).</summary>
        Task<int> GetRitOffsetHzAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the RIT offset in Hz, clamped to ±9990 and rounded to 10 Hz (CI-V 21 00).</summary>
        Task SetRitOffsetHzAsync(int hz, CancellationToken cancellationToken = default);

        /// <summary>Read whether RIT is on (CI-V 21 01).</summary>
        Task<bool> GetRitEnabledAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn RIT on or off (CI-V 21 01).</summary>
        Task SetRitEnabledAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Read whether ΔTX is on — the RIT offset also applied to transmit (CI-V 21 02).</summary>
        Task<bool> GetDeltaTxEnabledAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn ΔTX on or off (CI-V 21 02).</summary>
        Task SetDeltaTxEnabledAsync(bool on, CancellationToken cancellationToken = default);

        // -- FM repeater tone (CI-V 16 42 / 16 43 / 1B 00 / 1B 01) -------------
        // Sub-audible tone for FM repeater access. "Tone" is the transmitted
        // CTCSS tone; "tone squelch" (TSQL) additionally gates the receiver on
        // the same tone. Each has its own frequency, in tenths of a Hz at the
        // seam so 88.5 Hz is 885 — the standard CTCSS set is not integral.

        /// <summary>Read whether the repeater tone is on (CI-V 16 42).</summary>
        Task<bool> GetRepeaterToneAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn the repeater tone on or off (CI-V 16 42).</summary>
        Task SetRepeaterToneAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Read whether tone squelch (TSQL) is on (CI-V 16 43).</summary>
        Task<bool> GetToneSquelchAsync(CancellationToken cancellationToken = default);

        /// <summary>Turn tone squelch (TSQL) on or off (CI-V 16 43).</summary>
        Task SetToneSquelchAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Read the repeater tone frequency in TENTHS of a Hz (885 = 88.5 Hz). -1 on a miss (CI-V 1B 00).</summary>
        Task<int> GetRepeaterToneTenthsHzAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the repeater tone frequency in TENTHS of a Hz (885 = 88.5 Hz; CI-V 1B 00).</summary>
        Task SetRepeaterToneTenthsHzAsync(int tenthsHz, CancellationToken cancellationToken = default);

        /// <summary>Read the tone-squelch (TSQL) frequency in TENTHS of a Hz. -1 on a miss (CI-V 1B 01).</summary>
        Task<int> GetToneSquelchTenthsHzAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the tone-squelch (TSQL) frequency in TENTHS of a Hz (CI-V 1B 01).</summary>
        Task SetToneSquelchTenthsHzAsync(int tenthsHz, CancellationToken cancellationToken = default);

        /// <summary>Read the FM split (repeater) offset in Hz for the current band. -1 on a miss (CI-V 1A 05 00 34 / 00 35).</summary>
        Task<int> GetFmSplitOffsetHzAsync(CancellationToken cancellationToken = default);

        /// <summary>Set the FM split (repeater) offset in Hz for the current band; the controller picks the HF or 50 MHz menu item (CI-V 1A 05 00 34 / 00 35).</summary>
        Task SetFmSplitOffsetHzAsync(int hz, CancellationToken cancellationToken = default);

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

        // -- Raw command escape hatch ------------------------------------------
        // Every other member above is semantic by design. This one is not, and
        // it exists for exactly one caller: user-defined voice macros (Settings →
        // Voice Control → Custom Commands), which are a data-driven extension
        // point — the user names a command the app has no intent for and IWC
        // sends it. Giving that a home on the seam keeps the rule intact that
        // only the concrete controller builds frames and touches the port: the
        // caller supplies a command body, never a framed, addressed message.
        // Nothing else in the app should use this; add a semantic member instead.

        /// <summary>
        /// Send one raw command body — command byte, optional sub-command byte,
        /// then data — and report whether the radio acknowledged it. The
        /// controller adds its own framing and address. Returns false on a
        /// rejection, a timeout, or an empty body.
        /// </summary>
        Task<bool> SendRawCommandAsync(IReadOnlyList<byte> commandBody, CancellationToken cancellationToken = default);

        /// <summary>
        /// Band-scope health for the About page's diagnostics block. "No spectrum"
        /// bug reports are unanswerable without it — whether the scope is switched
        /// on, whether sweeps are arriving at all, and how many are being dropped
        /// separate three completely different faults. Default: a controller with
        /// no scope has nothing to report.
        /// </summary>
        ScopeDiagnostics GetScopeDiagnostics() => new(false, 0, 0, null);
    }

    /// <summary>
    /// Snapshot of the band scope's state, for diagnostics only.
    /// </summary>
    /// <param name="Enabled">False once the operator has switched the scope off.</param>
    /// <param name="SweepsCompleted">Fully-assembled sweeps since app start.</param>
    /// <param name="SweepsDiscarded">Sweeps dropped on a lost or out-of-order segment.</param>
    /// <param name="SecondsSinceLastSweep">Null when no sweep has ever arrived.</param>
    public record ScopeDiagnostics(
        bool Enabled,
        long SweepsCompleted,
        long SweepsDiscarded,
        double? SecondsSinceLastSweep);
}
