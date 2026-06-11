// Wire protocol between YWC main and an SDR worker.
//
// All multi-byte integers are big-endian. Floats are IEEE 754 single-precision
// in network byte order.
//
//   Framing
//   ───────
//   Every message starts with:
//     [4 bytes]  payloadLength (uint32, BE) — number of bytes in the payload
//                                              that follows, NOT including
//                                              the type byte
//     [1 byte]   messageType  (see MessageType enum)
//     [payloadLength bytes]  payload (varies by type)
//
//   Total bytes on wire = 4 + 1 + payloadLength.
//
//   The 4-byte length lets the reader pre-allocate, and lets us add new
//   message types without breaking the framing.
//
//   Messages: worker → main
//   ───────────────────────
//   SpectrumFrame (type 0x01)
//     [8 bytes]   sequence    (uint64) — frame counter, monotonically increasing
//     [8 bytes]   centreHz    (int64)  — centre frequency in Hz
//     [8 bytes]   spanHz      (int64)  — full visible span in Hz (= sample rate)
//     [4 bytes]   binCount    (int32)  — number of float bins that follow
//     [binCount × 4 bytes]  bins (float32 each, BE) — dBFS values
//
//   StatusUpdate (type 0x02)
//     [UTF-8 string]  status text  — e.g. "connecting", "streaming", "nodll"
//
//   ErrorReport (type 0x03)
//     [UTF-8 string]  error message — human-readable diagnostic
//
//   Messages: main → worker (none currently)
//   ────────────────────────────────────────
//   Configuration changes are handled by killing and respawning the worker,
//   so no main→worker control messages are needed in v1. The protocol leaves
//   room for them to be added later without breaking compatibility.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Yaesu_Web_Control.Workers.Sdr;

public enum MessageType : byte
{
    SpectrumFrame = 0x01,
    StatusUpdate  = 0x02,
    ErrorReport   = 0x03,
}

/// <summary>
/// Frame writer for the worker side. Encapsulates the length-prefix framing
/// and big-endian field layout so worker code can just call high-level
/// methods like <see cref="WriteSpectrumAsync"/>.
/// </summary>
public sealed class FrameWriter
{
    private readonly NetworkStream _stream;
    // Reusable buffer for the frame header + small payloads. Spectrum frames
    // allocate a one-shot buffer per call (bin count varies).
    private readonly byte[] _headerBuf = new byte[5];   // 4-byte length + 1-byte type

    public FrameWriter(NetworkStream stream) => _stream = stream;

    public async Task WriteSpectrumAsync(
        ulong sequence, long centreHz, long spanHz, float[] bins, CancellationToken ct)
    {
        // Payload layout: 8 + 8 + 8 + 4 + binCount*4
        int payloadLen = 8 + 8 + 8 + 4 + bins.Length * 4;
        var buf = new byte[5 + payloadLen];   // length + type + payload
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)payloadLen);
        buf[4] = (byte)MessageType.SpectrumFrame;
        int o = 5;
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(o, 8), sequence); o += 8;
        BinaryPrimitives.WriteInt64BigEndian (buf.AsSpan(o, 8), centreHz); o += 8;
        BinaryPrimitives.WriteInt64BigEndian (buf.AsSpan(o, 8), spanHz);   o += 8;
        BinaryPrimitives.WriteInt32BigEndian (buf.AsSpan(o, 4), bins.Length); o += 4;
        for (int i = 0; i < bins.Length; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(buf.AsSpan(o, 4), bins[i]);
            o += 4;
        }
        await _stream.WriteAsync(buf.AsMemory(), ct).ConfigureAwait(false);
    }

    public Task WriteStatusAsync(string status, CancellationToken ct) =>
        WriteStringMessageAsync(MessageType.StatusUpdate, status, ct);

    public Task WriteErrorAsync(string error, CancellationToken ct) =>
        WriteStringMessageAsync(MessageType.ErrorReport, error, ct);

    private async Task WriteStringMessageAsync(MessageType type, string text, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        BinaryPrimitives.WriteUInt32BigEndian(_headerBuf.AsSpan(0, 4), (uint)payload.Length);
        _headerBuf[4] = (byte)type;
        await _stream.WriteAsync(_headerBuf.AsMemory(0, 5), ct).ConfigureAwait(false);
        await _stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
    }
}
