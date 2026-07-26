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
