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
