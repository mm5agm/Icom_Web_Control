using System;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// CI-V wire-protocol constants and the two pure conversions Phase 2 needs
    /// (frame framing + little-endian BCD frequency). This is the Icom analogue
    /// of the Yaesu ASCII CAT format — binary, addressed, BCD, framed by
    /// <c>FE FE … FD</c>. No I/O here; see <see cref="CivBusService"/> for the
    /// serial transport and <see cref="CivRadioController"/> for the semantics.
    ///
    /// Verified against the IC-7300 MkII on 2026-07-25 (COM8, 19200 8N1,
    /// radio B6 / controller E0): read frequency = command 03,
    /// send <c>FE FE B6 E0 03 FD</c>, reply
    /// <c>FE FE E0 B6 03 &lt;5 BCD bytes little-endian&gt; FD</c>.
    /// </summary>
    public static class CivProtocol
    {
        public const byte Preamble = 0xFE; // sent twice: FE FE
        public const byte End = 0xFD;

        /// <summary>Our (the PC's) CI-V address.</summary>
        public const byte ControllerAddress = 0xE0;

        /// <summary>
        /// CI-V broadcast ("to all") address. A frame addressed here is answered
        /// by every transceiver on the bus, which is how we discover the radio's
        /// real address at connect time without knowing the model in advance —
        /// the reply's From byte carries the true address. Used only for the
        /// first read-ID (19 00); all subsequent frames go to the learned address.
        /// </summary>
        public const byte BroadcastAddress = 0x00;

        /// <summary>
        /// IC-7300 MkII factory default CI-V address. Used for the first frame
        /// only; the real address is then learned from the reply's From byte
        /// (and confirmed by the 19 00 transceiver-ID read) rather than trusted
        /// blindly — see the design doc's "read the ID at connect, don't
        /// hard-code" rule.
        /// </summary>
        public const byte DefaultRadioAddress = 0xB6;

        // Commands used in Phase 2.
        public const byte CmdReadFrequency = 0x03;
        public const byte CmdSetFrequency = 0x05;
        public const byte CmdReadId = 0x19;        // sub-command 0x00 = read transceiver ID
        public const byte SubReadId = 0x00;

        // Phase 3 block 2 — operating mode.
        //   Read : send 04            → reply 04 <mode> <filter>
        //   Set  : send 06 <mode>     → reply FB / FA
        // <mode> is a single byte (LSB=00, USB=01, AM=02, CW=03, RTTY=04,
        // FM=05, CW-R=07, RTTY-R=08); <filter> (FIL1/2/3) is reported on read
        // and left untouched on set (mode byte only).
        public const byte CmdReadMode = 0x04;
        public const byte CmdSetMode = 0x06;

        // Phase 3 block 2 — DATA mode + filter width (command 1A, sub 06). On
        // the IC-7300 a "data" mode (USB-D / LSB-D / FM-D, used for FT8 etc.)
        // is the base mode plus this flag, not a distinct mode byte.
        //   Read : send 1A 06                     → reply 1A 06 <dataOn> <filter>
        //   Set  : send 1A 06 <dataOn> <filter>   → reply FB / FA
        // dataOn: 00=OFF, 01=ON (SSB/AM/FM only; fixed OFF for CW/RTTY).
        // filter: 00 when data OFF, else 01=FIL1 / 02=FIL2 / 03=FIL3.
        // Manual constraint: when dataOn=00, filter must be 00.
        public const byte CmdMenu = 0x1A;
        public const byte SubDataMode = 0x06;

        // Memory-channel content (command 1A 00) — the radio's own 99 internal
        // memory channels, read/written whole so the operating VFO is untouched.
        //   Read : send 1A 00 <chHi> <chLo>              → reply 1A 00 <chHi> <chLo> <content…> (or no content when the channel is blank)
        //   Write: send 1A 00 <chHi> <chLo> <content…>   → reply FB / FA
        //   Clear: send 1A 00 <chHi> <chLo> FF           → reply FB / FA
        // Channel number is 2 BCD bytes (00 01–00 99). See MemoryChannelCodec in
        // CivRadioController for the 47-byte content layout.
        public const byte SubMemoryChannel = 0x00;

        // IF passband filter width (command 1A 03) — the width of the currently
        // selected FIL slot for the current mode. One BCD data byte whose code
        // range and Hz meaning depend on the mode group (SSB/CW, RTTY, AM); FM
        // has no adjustable width. See FilterWidthCodec in CivRadioController.
        //   Read : send 1A 03            → reply 1A 03 <code(BCD)>
        //   Set  : send 1A 03 <code(BCD)> → reply FB / FA
        public const byte SubIfWidth = 0x03;

        // RX Tone Control — HPF/LPF audio filter (command 1A 05, menu family).
        // The RX high-pass (low-cut) and low-pass (high-cut) audio edges live in
        // the menu tree, reached over CI-V as 1A 05 with a TWO-byte item selector
        // (high byte always 00). Each mode owns its own item: SSB=01, AM=04,
        // FM=07, CW=10, RTTY=11 (the low byte is the literal BCD menu number).
        //   Read : send 1A 05 00 <item>            → reply 1A 05 00 <item> HH LL
        //   Set  : send 1A 05 00 <item> HH LL      → reply FB / FA
        // Data is two BCD bytes HH (HPF code) LL (LPF code):
        //   HPF: 00 = Through, 01–20 = 100–2000 Hz (×100, low-cut)
        //   LPF: 05–24 = 500–2400 Hz (×100, high-cut), 25 = Through
        // The IC-7300's RX Tone Control is unavailable in SSB-DATA mode.
        public const byte SubRxTone = 0x05;

        /// <summary>
        /// The same byte as <see cref="SubRxTone"/>, under the name the CI-V
        /// manual uses. 1A 05 is the whole SET menu, not just Tone Control —
        /// every item is 1A 05 &lt;hi&gt; &lt;lo&gt;, and the RX HPF/LPF items just
        /// happen to be the first ones IWC needed. Use this name for anything
        /// that is not Tone Control (VOX delay, APF type, …).
        /// </summary>
        public const byte SubSetMenu = 0x05;

        // Phase 3 block 3 — meters (command 15 family). Sub 02 = S-meter.
        //   Read : send 15 02   → reply 15 02 <d1> <d2>
        // The level is a 0–255 value as two big-endian BCD bytes (unlike the
        // little-endian frequency): 00 00=S0, 01 20=S9, 02 41=S9+60 dB, 02 55=255.
        // Decode with value = <d1 as BCD>*100 + <d2 as BCD>; see BcdByte.
        public const byte CmdReadMeter = 0x15;
        public const byte SubSMeter = 0x02;

        // Phase 3 block 4 — PTT / TX status and the two TX meters. Command 1C 00
        // both reads and sets the transmit state (data byte present = set); the
        // Po and SWR meters share the 15-family big-endian BCD format of the
        // S-meter (0–255).
        //   TX status : send 1C 00        → reply 1C 00 <00=RX | 01=TX>
        //   Set PTT   : send 1C 00 <v>    → reply FB / FA   (v: 00=RX, 01=TX)
        //   Po meter  : send 15 11        → reply 15 11 <d1> <d2>  (00 00=0%, 01 43=50%, 02 13=100%)
        //   SWR meter : send 15 12        → reply 15 12 <d1> <d2>  (00 00=SWR1.0 … 02 55=SWR∞)
        public const byte CmdTransmit = 0x1C;
        public const byte SubTxStatus = 0x00;
        public const byte SubPoMeter = 0x11;
        public const byte SubSwrMeter = 0x12;

        // Antenna tuner (command 1C 01). Unlike the Yaesu AC command — which
        // cannot report a tuning cycle in progress — the IC-7300 reports and
        // sets all three states here:
        //   Read : send 1C 01        → reply 1C 01 <00=OFF | 01=ON | 02=TUNING>
        //   Set  : send 1C 01 <v>    → reply FB / FA
        //     v: 00 = tuner OFF (through), 01 = tuner ON (in line),
        //        02 = start a tuning cycle (radio ACKs, then reports 02 while
        //             tuning and drops back to 01 when done).
        // See the IC-7300 MkII CI-V manual p.13.
        public const byte SubTuner = 0x01;
        public const byte TunerOff  = 0x00;
        public const byte TunerOn   = 0x01;
        public const byte TunerTune = 0x02;

        // The remaining 15-family meters (same 0–255 big-endian BCD form as
        // above). ALC/COMP read 00 00 while receiving; Id is ~0 on RX and rises
        // on TX; Vd (PA supply) reads the idle rail on RX (~13.8 V) and sags
        // under load on TX. The IC-7300 has NO temperature meter over CI-V —
        // that inherited YWC gauge is dropped for this radio.
        //   ALC  : 15 13  (00 00=min … 01 20=max)
        //   COMP : 15 14  (00 00=0 dB … 02 10=30 dB)
        //   Vd   : 15 15  (00 00=0 V, 00 13=10 V, 02 41=16 V)
        //   Id   : 15 16  (00 00=0, 00 97=10 A, 01 46=15 A, 02 41=25 A)
        public const byte SubAlcMeter = 0x13;
        public const byte SubCompMeter = 0x14;
        public const byte SubVdMeter = 0x15;
        public const byte SubIdMeter = 0x16;

        // RF output power set (command 14, sub 0A). Unlike the
        // Yaesu "PCnnn;" watts command, the IC-7300 sets power as a 0000–0255
        // level in two big-endian BCD bytes (same packing as the meters)
        // mapping to 0–100 %: 00 00=0 %, 01 43=50 %, 02 55=100 %.
        //   Read : send 14 0A            → reply 14 0A <d1> <d2>
        //   Set  : send 14 0A <d1> <d2>  → FB / FA
        public const byte CmdSetLevel = 0x14;
        public const byte SubRfPower = 0x0A;

        // AF (volume) level — 14 01, same 14-family form: two big-endian BCD
        // bytes 00 00–02 55 (value 0–255). Receiver-wide on the IC-7300.
        public const byte SubAfGain = 0x01;

        // Twin PBT (Digital Passband Tuning) — same 14-family form as RF power,
        // two big-endian BCD bytes 00 00–02 55 where 01 28 (=128) is the centre
        // (no shift), 00 00 narrows the upper edge, 02 55 narrows the lower edge.
        //   14 07 = PBT1 (inner shift),  14 08 = PBT2 (outer shift)
        public const byte SubPbtInner = 0x07;
        public const byte SubPbtOuter = 0x08;
        public const int  PbtCentre   = 128;   // 01 28 = passband centre / no shift

        // Other 14-family RX levels — all the same 00 00–02 55 (0–255) form.
        //   14 02 = RF gain,  14 03 = squelch,  14 06 = noise-reduction level,
        //   14 12 = noise-blanker level,  14 0D = manual-notch position
        // Notch position is centred like PBT (01 28 = middle of the passband;
        // 00 00 moves higher, 02 55 moves lower).
        public const byte SubRfGain   = 0x02;
        public const byte SubSquelch  = 0x03;
        public const byte SubNrLevel  = 0x06;
        public const byte SubNotchPos = 0x0D;
        public const byte SubNbLevel  = 0x12;
        public const int  NotchCentre = 128;

        // 16-family on/off & multi-state RX functions. Read: send 16 <sub> →
        // reply 16 <sub> <val>; set: send 16 <sub> <val> → FB/FA.
        //   16 02 = preamp (00=OFF, 01=P.AMP1, 02=P.AMP2)
        //   16 12 = AGC time constant (01=FAST, 02=MID, 03=SLOW — no OFF/AUTO)
        //   16 22 = noise blanker (00/01)
        //   16 40 = noise reduction (00/01)
        //   16 41 = auto notch (00/01)
        //   16 48 = manual notch (00/01)
        //   16 56 = IF filter shape (00=SHARP, 01=SOFT)
        //   16 57 = manual-notch filter width (00=WIDE, 01=MID, 02=NAR)
        public const byte CmdSetFunc      = 0x16;
        public const byte SubPreamp       = 0x02;
        public const byte SubAgc          = 0x12;
        public const byte SubNoiseBlanker = 0x22;
        public const byte SubNoiseReduction = 0x40;
        public const byte SubAutoNotch    = 0x41;
        public const byte SubManualNotch  = 0x48;
        public const byte SubIfFilterShape    = 0x56;   // 00=SHARP, 01=SOFT
        public const byte SubManualNotchWidth = 0x57;   // 00=WIDE, 01=MID, 02=NAR

        // Attenuator (command 11) — single 20 dB pad on the IC-7300.
        //   Read: send 11 → reply 11 <val>;  set: send 11 <val> → FB/FA
        //   val: 00 = OFF, 20 (BCD, i.e. 0x20) = 20 dB ON
        public const byte CmdAttenuator = 0x11;
        public const byte AttOff        = 0x00;
        public const byte AttOn20dB     = 0x20;

        // CW keyer — memory keyer send (command 17) plus the keyer settings that
        // ride the 14 (level) and 16 (function) families.
        //   Send : 17 <ASCII bytes…>   → keys the message (≤30 chars). The byte
        //          for each character IS its ASCII code (0-9=30–39, A-Z=41–5A,
        //          space=20, / . , ? etc. = their ASCII value); 0x5E ('^') is a
        //          no-inter-character-space marker, not a keyed glyph.
        //   Stop : 17 FF               → abort a message in progress.
        // Settings:
        //   14 0C = keyer speed   (0–255 = 6–48 WPM)
        //   14 09 = CW pitch      (0–255 = 300–900 Hz)
        //   14 0F = break-in delay(0–255 = 2.0–13.0 dots)   ← dots, not ms
        //   16 47 = break-in mode (00=OFF, 01=SEMI, 02=FULL)
        public const byte CmdCwSend       = 0x17;
        public const byte CwStop          = 0xFF; // 17 FF — stop keying
        public const byte SubCwSpeed      = 0x0C; // 14 0C
        public const byte SubCwPitch      = 0x09; // 14 09
        public const byte SubCwBreakInDelay = 0x0F; // 14 0F
        public const byte SubCwBreakIn    = 0x47; // 16 47

        // -- TX-side audio chain: mic, speech compressor, monitor, VOX --------
        //
        // All of these follow the two families already established above, so
        // they need no new decoding: the levels are 14-family (two big-endian
        // BCD bytes, 0000–0255) and the on/off switches are 16-family (one
        // byte, 00/01). CI-V manual pp. 5–6.
        //   14 0B = microphone gain      (0–255)
        //   14 0E = speech-compressor level (0000–0255 maps to the panel's 0–10)
        //   14 15 = monitor level        (0–255 = 0–100 %)
        //   14 16 = VOX sensitivity/gain (0–255 = 0–100 %)
        //   14 17 = ANTI-VOX             (0–255; HIGHER is LESS sensitive)
        //   16 44 = speech compressor on/off
        //   16 45 = monitor on/off
        //   16 46 = VOX on/off
        public const byte SubMicGain      = 0x0B; // 14 0B
        public const byte SubCompLevel    = 0x0E; // 14 0E
        public const byte SubMonitorLevel = 0x15; // 14 15
        public const byte SubVoxGain      = 0x16; // 14 16
        public const byte SubAntiVox      = 0x17; // 14 17
        public const byte SubSpeechComp   = 0x44; // 16 44
        public const byte SubMonitor      = 0x45; // 16 45
        public const byte SubVox          = 0x46; // 16 46

        // APF (Audio Peak Filter) — 16 32, CW only. Unlike the other 16-family
        // switches this is not a simple on/off: the value selects the WIDTH,
        // and OFF is one of the four positions rather than a separate flag.
        //   00 = OFF, 01 = WIDE, 02 = MID, 03 = NAR
        // The widths above are the SOFT-type figures; with APF TYPE set to
        // SHARP the same three positions mean 320 / 160 / 80 Hz. Type itself is
        // a SET-menu item (1A 05 02 69), as is the APF AF level (1A 05 02 70).
        public const byte SubApf          = 0x32; // 16 32
        public const byte ApfOff          = 0x00;
        public const byte ApfWide         = 0x01;
        public const byte ApfMid          = 0x02;
        public const byte ApfNarrow       = 0x03;

        // VOX delay — a SET-menu item rather than a 14-family level, reached the
        // same way as the RX tone controls (SubRxTone is the 1A 05 SET-menu
        // subcommand) but with a two-byte item selector of 02 67:
        //   Read : send 1A 05 02 67          → reply 1A 05 02 67 <val>
        //   Set  : send 1A 05 02 67 <val>    → FB / FA
        // val is 00–20 BCD = 0.0–2.0 s in 0.1 s steps — NOT the 0–255 the
        // 14-family levels use. Note 1A 05 00 67 is a different item entirely
        // (the calibration marker), so the high selector byte matters.
        public const byte MenuHiVoxDelay   = 0x02; // 1A 05 [02] 67
        public const byte MenuLoVoxDelay   = 0x67;
        public const int  VoxDelayMaxTenths = 20;  // 20 = 2.0 s, the radio's ceiling

        // RIT / ΔTX (command 21) — the IC-7300's equivalent of the Yaesu
        // clarifier. Three subcommands:
        //   21 00 = RIT offset frequency (read/set)
        //   21 01 = RIT on/off      (00/01)
        //   21 02 = ΔTX on/off      (00/01)
        //
        // The offset is THREE data bytes, and only the first two are BCD digit
        // pairs — the third is a sign flag, not a digit:
        //   <d0> = 1 Hz + 10 Hz digits,  <d1> = 100 Hz + 1 kHz digits,
        //   <sign> = 00 for +, 01 for −
        // i.e. little-endian BCD like the frequency commands, then the sign.
        // Range is therefore ±9.99 kHz; the front panel clamps to ±9.999 kHz.
        public const byte CmdRit          = 0x21;
        public const byte SubRitFrequency = 0x00;
        public const byte SubRitOn        = 0x01;
        public const byte SubDeltaTxOn    = 0x02;
        public const int  RitMaxHz        = 9990;  // largest offset the 2-BCD-byte field can carry
        public const byte RitSignPlus     = 0x00;
        public const byte RitSignMinus    = 0x01;

        // -- FM repeater tone (16 42 / 16 43 / 1B 00 / 1B 01) ------------------
        // Sub-audible CTCSS for repeater access. The on/off switches are
        // ordinary 16-family functions; the frequencies get their own command.
        //   1B 00 = repeater (TX) tone frequency
        //   1B 01 = tone-squelch (TSQL) frequency
        // Three data bytes, big-endian BCD, in tenths of a Hz:
        //   <00> <100Hz|10Hz> <1Hz|0.1Hz>   e.g. 88.5 Hz → 00 08 85
        // The first byte's two digits are fixed 0 (there is no 10 kHz or 1 kHz
        // CTCSS tone), so the usable range is 0.0–999.9 Hz.
        public const byte SubRepeaterTone = 0x42; // 16 42 — tone on/off
        public const byte SubTsql         = 0x43; // 16 43 — tone squelch on/off
        public const byte CmdToneFreq     = 0x1B;
        public const byte SubToneFrequency = 0x00; // 1B 00
        public const byte SubTsqlFrequency = 0x01; // 1B 01

        // FM split (repeater) offset — a SET-menu item, with a SEPARATE item per
        // band group because the radio remembers a different shift for HF and
        // for 6 m:
        //   1A 05 00 34 = FM SPLIT Offset (HF)
        //   1A 05 00 35 = FM SPLIT Offset (50M)
        // Four data bytes: three little-endian BCD pairs carrying the magnitude
        // (1 kHz/100 Hz, 100 kHz/10 kHz, 10 MHz/1 MHz — the 100 Hz and 10 MHz
        // digits are fixed 0, so the offset is a whole number of kHz below
        // 10 MHz), then a direction byte, 00 = plus, 01 = minus.
        public const byte MenuHiFmSplitOffset    = 0x00;
        public const byte MenuLoFmSplitOffsetHf  = 0x34; // 1A 05 00 34
        public const byte MenuLoFmSplitOffset6m  = 0x35; // 1A 05 00 35
        /// <summary>Frequency at or above which the 50 MHz FM-split-offset menu item applies.</summary>
        public const long SixMetreBandStartHz    = 50_000_000;

        // Phase 3 block 5 — VFO select, split, and true per-VFO frequency/mode.
        //
        // VFO select (command 07) — set-only, no read of the active VFO exists:
        //   07 00 = switch to VFO mode, select VFO A
        //   07 01 = switch to VFO mode, select VFO B
        //   07 A0 = equalize (make VFO A and VFO B the same)
        //   07 B0 = exchange VFO A with VFO B
        public const byte CmdSelectVfo = 0x07;
        public const byte VfoSelectA = 0x00;
        public const byte VfoSelectB = 0x01;
        public const byte VfoEqualize = 0xA0;
        public const byte VfoExchange = 0xB0;

        // Split (command 0F):
        //   0F        (no data) → reply 0F <00=OFF | 01=ON>   (read)
        //   0F 00 / 0F 01                                     (set OFF / ON)
        public const byte CmdSplit = 0x0F;
        public const byte SplitOff = 0x00;
        public const byte SplitOn = 0x01;

        // Per-VFO frequency (25) and mode+DATA+filter (26). Both take a selector
        // byte that is NOT A/B but selected/unselected — the caller maps A/B onto
        // it via the tracked active VFO (there is no CI-V read for "which VFO is
        // active", so we track it from our own 07 sends):
        //   00 = selected VFO, 01 = unselected VFO.
        //   Freq read : send 25 <sel>                        → reply 25 <sel> <5 BCD LE>
        //   Freq set  : send 25 <sel> <5 BCD LE>             → FB / FA
        //   Mode read : send 26 <sel>                        → reply 26 <sel> <mode> <data> <filter>
        //   Mode set  : send 26 <sel> <mode> <data> <filter> → FB / FA
        // <mode> bytes are the same as command 04/06; <data> 00=OFF/01=ON;
        // <filter> 01=FIL1 / 02=FIL2 / 03=FIL3. On set, <data>/<filter> may be
        // omitted (radio then picks DATA OFF + the mode's default filter).
        public const byte CmdVfoFrequency = 0x25;
        public const byte CmdVfoMode = 0x26;
        public const byte VfoSelected = 0x00;
        public const byte VfoUnselected = 0x01;

        // Phase 3 block 6 — CI-V spectrum scope (command 27). Once BOTH the
        // scope (27 10) AND the waveform-output-to-controller function (27 11,
        // valid only through USB/LAN) are ON, the radio *streams* waveform
        // frames at us unsolicited:
        //   27 00 <VFO> <order> <div> [<mode> <info×10> <oor>] <bins…>  (radio → PC)
        // Over USB one sweep is split into 11 frames: order 01 is a header
        // (mode + centre/span + out-of-range, no bins); orders 02–11 carry the
        // 475 waveform bytes (0x00–0xA0, one per bin) that concatenate left
        // edge → right edge. See docs/manuals IC-7300MK2 CI-V p.15 / p.24–26,
        // and CivScopeAssembler for the reassembly.
        //   27 10 01/00 = spectrum scope ON/OFF
        //   27 11 01/00 = waveform output to controller ON/OFF (USB/LAN only)
        //   27 14 00    = Center scope mode — centres the sweep on the operating
        //                 frequency, which is what the web SpectrumPanel assumes.
        public const byte CmdScope         = 0x27;
        public const byte SubScopeWaveform = 0x00;  // 27 00 — waveform data (radio → PC)
        public const byte SubScopeOnOff    = 0x10;  // 27 10 — scope on/off
        public const byte SubScopeOutput   = 0x11;  // 27 11 — waveform output to controller
        public const byte SubScopeMode     = 0x14;  // 27 14 — center/fixed/scroll
        // 27 14 takes TWO data bytes: a leading scope-number selector then the
        // mode (CI-V manual p.25 shows the format as "00 XX"). The IC-7300 MkII
        // has a single (Main) scope, so the selector is always 00. Sending only
        // one byte (27 14 00) is a short frame the radio NG-rejects — that was
        // why "scope center mode was not acknowledged" appeared in the log.
        public const byte ScopeMain        = 0x00;  // 27 14 <this> XX — Main scope selector
        public const byte ScopeModeCenter  = 0x00;  // 27 14 00 00
        public const byte ScopeModeFixed   = 0x01;  // 27 14 00 01
        // 27 15 — span (Center / SCROLL-C mode), sent as a 5-byte BCD frequency.
        // The value is the ± half-width in Hz: 2500, 5000, 10000, 25000, 50000,
        // 100000, 250000 or 500000 (radio SPAN ±2.5k … ±500k). The reported
        // waveform-info span field reads back the same value.
        public const byte SubScopeSpan     = 0x15;  // 27 15

        // Phase 3 block 7 — transceiver power on/off (command 18).
        //   18 00 = power OFF, 18 01 = power ON. The radio ACKs FB.
        // Power OFF is a plain frame (the radio is awake and listening).
        // Power ON must wake the asleep CI-V circuit first with a burst of
        // 0xFE bytes sent immediately before the 18 01 frame — see
        // PowerOnWakePreambleCount and the IC-7300 MkII CI-V manual p.16.
        public const byte CmdPower = 0x18;
        public const byte PowerOff = 0x00;
        public const byte PowerOn  = 0x01;

        /// <summary>
        /// The number of leading 0xFE wake-up bytes to prepend to an 18 01
        /// power-ON frame. The asleep CI-V circuit needs a burst of FEs to wake
        /// before it will read the frame, and the required count scales with the
        /// baud rate — the IC-7300 MkII CI-V manual (p.16) lists 25 @ 19200,
        /// 13 @ 9600, 7 @ 4800. Generalised as ⌈baud/768⌉ (which reproduces all
        /// three listed values exactly) with a floor of 7.
        /// </summary>
        public static int PowerOnWakePreambleCount(int baudRate)
            => Math.Max(7, (int)Math.Ceiling(baudRate / 768.0));

        /// <summary>Decode one packed-BCD byte (two decimal digits) to 0–99.</summary>
        public static int BcdByte(byte b) => ((b >> 4) & 0x0F) * 10 + (b & 0x0F);

        // Radio's one-byte acknowledgements to a set/write command.
        public const byte AckOk = 0xFB; // completed
        public const byte AckNg = 0xFA; // not good (rejected / bad frame)

        /// <summary>
        /// Build a controller→radio frame: <c>FE FE &lt;radio&gt; &lt;controller&gt; &lt;body…&gt; FD</c>.
        /// <paramref name="body"/> is the command byte, any sub-command, then data.
        /// </summary>
        public static byte[] BuildFrame(byte radioAddress, byte controllerAddress, params byte[] body)
        {
            var frame = new byte[body.Length + 5];
            frame[0] = Preamble;
            frame[1] = Preamble;
            frame[2] = radioAddress;
            frame[3] = controllerAddress;
            Array.Copy(body, 0, frame, 4, body.Length);
            frame[^1] = End;
            return frame;
        }

        /// <summary>
        /// Decode Icom little-endian packed BCD (2 decimal digits per byte, low
        /// nibble = lower order, byte 0 = least significant) into Hz. For the
        /// 5-byte frequency payload this covers 1 Hz … 9,999,999,999 Hz.
        /// </summary>
        public static long DecodeBcd(ReadOnlySpan<byte> bcd)
        {
            long value = 0;
            long place = 1;
            foreach (var b in bcd)
            {
                int low = b & 0x0F;
                int high = (b >> 4) & 0x0F;
                value += low * place;
                place *= 10;
                value += high * place;
                place *= 10;
            }
            return value;
        }

        /// <summary>
        /// Encode a non-negative integer as <paramref name="byteCount"/> bytes of
        /// Icom little-endian packed BCD (inverse of <see cref="DecodeBcd"/>).
        /// Digits beyond the requested width are dropped.
        /// </summary>
        public static byte[] EncodeBcd(long value, int byteCount)
        {
            var bytes = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                int low = (int)(value % 10); value /= 10;
                int high = (int)(value % 10); value /= 10;
                bytes[i] = (byte)((high << 4) | low);
            }
            return bytes;
        }
    }

    /// <summary>
    /// A parsed CI-V frame. <see cref="Body"/> is everything between the address
    /// pair and the <c>FD</c> terminator — i.e. command byte, optional
    /// sub-command, then data. <see cref="Cmd"/>/<see cref="Data"/> are
    /// convenience views over it.
    /// </summary>
    public sealed class CivFrame
    {
        /// <summary>Destination address (byte 2). A reply to us has To == controller (E0).</summary>
        public byte To { get; init; }

        /// <summary>Source address (byte 3). A radio reply has From == the radio's address.</summary>
        public byte From { get; init; }

        public byte[] Body { get; init; } = Array.Empty<byte>();

        public byte Cmd => Body.Length > 0 ? Body[0] : (byte)0x00;

        /// <summary>Payload after the command byte (includes any sub-command).</summary>
        public byte[] Data => Body.Length > 1 ? Body[1..] : Array.Empty<byte>();

        public override string ToString()
            => $"CI-V[to={To:X2} from={From:X2} body={BitConverter.ToString(Body)}]";
    }
}
