// Reads wire-protocol messages from an SDR worker process.
//
// Mirror of Workers/Yaesu_Sdr_Worker/WireProtocol.cs (FrameWriter side).
// All multi-byte integers are big-endian; floats are IEEE 754 single-precision
// in network byte order.

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Yaesu_Web_Control.Services.Sdr;

public enum WorkerMessageType : byte
{
    SpectrumFrame = 0x01,
    StatusUpdate  = 0x02,
    ErrorReport   = 0x03,
}

/// <summary>Tagged-union of all possible worker → main messages.</summary>
public abstract record WorkerMessage;
public sealed record SpectrumFrameMsg(ulong Sequence, long CentreHz, long SpanHz, float[] Bins) : WorkerMessage;
public sealed record StatusUpdateMsg(string Status) : WorkerMessage;
public sealed record ErrorReportMsg(string Error) : WorkerMessage;

public sealed class FrameReader
{
    private readonly NetworkStream _stream;
    // Header is 4-byte length + 1-byte type.
    private readonly byte[] _header = new byte[5];

    public FrameReader(NetworkStream stream) => _stream = stream;

    /// <summary>
    /// Read the next message. Returns null on clean EOF (worker exited).
    /// Throws if framing is corrupted or cancellation fires.
    /// </summary>
    public async Task<WorkerMessage?> ReadAsync(CancellationToken ct)
    {
        // Header: length (4) + type (1)
        if (!await ReadExactlyAsync(_header, 5, ct).ConfigureAwait(false))
            return null;

        uint payloadLen = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(0, 4));
        var  type       = (WorkerMessageType)_header[4];

        if (payloadLen > 16 * 1024 * 1024)
            throw new InvalidDataException(
                $"Frame payload claims {payloadLen} bytes — likely corrupted wire protocol");

        var payload = new byte[payloadLen];
        if (!await ReadExactlyAsync(payload, (int)payloadLen, ct).ConfigureAwait(false))
            throw new InvalidDataException("Truncated payload — connection lost mid-frame");

        return type switch
        {
            WorkerMessageType.SpectrumFrame => ParseSpectrumFrame(payload),
            WorkerMessageType.StatusUpdate  => new StatusUpdateMsg(Encoding.UTF8.GetString(payload)),
            WorkerMessageType.ErrorReport   => new ErrorReportMsg (Encoding.UTF8.GetString(payload)),
            _ => throw new InvalidDataException($"Unknown worker message type 0x{(byte)type:X2}"),
        };
    }

    private static SpectrumFrameMsg ParseSpectrumFrame(byte[] payload)
    {
        // Layout: sequence(8) centreHz(8) spanHz(8) binCount(4) bins(binCount*4)
        if (payload.Length < 8 + 8 + 8 + 4)
            throw new InvalidDataException("Spectrum frame header truncated");
        int o = 0;
        ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(o, 8)); o += 8;
        long  centreHz = BinaryPrimitives.ReadInt64BigEndian (payload.AsSpan(o, 8)); o += 8;
        long  spanHz   = BinaryPrimitives.ReadInt64BigEndian (payload.AsSpan(o, 8)); o += 8;
        int   binCount = BinaryPrimitives.ReadInt32BigEndian (payload.AsSpan(o, 4)); o += 4;
        int expected   = o + binCount * 4;
        if (payload.Length != expected)
            throw new InvalidDataException(
                $"Spectrum frame: expected {expected} bytes for {binCount} bins, got {payload.Length}");
        var bins = new float[binCount];
        for (int i = 0; i < binCount; i++)
        {
            bins[i] = BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(o, 4));
            o += 4;
        }
        return new SpectrumFrameMsg(sequence, centreHz, spanHz, bins);
    }

    private async ValueTask<bool> ReadExactlyAsync(byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) return false;  // EOF — clean close
            read += n;
        }
        return true;
    }
}
