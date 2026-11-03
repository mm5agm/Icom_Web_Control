using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// A CI-V session recorded off the radio (Fixtures/*.civlog): what was sent,
/// the serial chunks exactly as they came back, and each request paired with
/// the radio's reply to it.
/// </summary>
internal sealed class CivRecording
{
    public const string ReadsFile = "ic7300mk2-reads-2026-10-25.civlog";

    public required List<byte[]> Sent { get; init; }
    public required List<byte[]> Chunks { get; init; }

    /// <summary>Every frame the radio sent, in order, framed by the real <see cref="CivFrameBuffer"/>.</summary>
    public required List<CivFrame> Received { get; init; }

    /// <summary>Request (hex) → the radio's reply, or null where it said nothing.</summary>
    public required Dictionary<string, CivFrame?> Replies { get; init; }

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static CivRecording Load(string name = ReadsFile)
    {
        var sent = new List<byte[]>();
        var chunks = new List<byte[]>();
        var received = new List<CivFrame>();
        var replies = new Dictionary<string, CivFrame?>();
        string? waiting = null;

        var buffer = new CivFrameBuffer();
        buffer.FrameReceived += (_, f) =>
        {
            received.Add(f);
            // The reply is the first frame after the request that is not a
            // scope sweep, which the radio sends unasked all the while.
            if (waiting is not null && !IsScope(f))
            {
                replies[waiting] = f;
                waiting = null;
            }
        };

        foreach (var line in File.ReadLines(FixturePath(name)))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var parts = line.Trim().Split(' ', 3);
            var bytes = Bytes(parts[2]);
            if (parts[1] == "TX")
            {
                if (waiting is not null) replies[waiting] = null;
                sent.Add(bytes);
                waiting = Hex(bytes);
            }
            else
            {
                chunks.Add(bytes);
                buffer.Append(bytes);
            }
        }
        if (waiting is not null) replies[waiting] = null;

        return new CivRecording { Sent = sent, Chunks = chunks, Received = received, Replies = replies };
    }

    public static bool IsScope(CivFrame f) => f.Cmd == CivProtocol.CmdScope && f.Data.Length > 0 && f.Data[0] == CivProtocol.SubScopeWaveform;

    public static byte[] Bytes(string hex)
        => hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

    public static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    /// <summary>The recorded reply to a request given as its body ("14 0A"), addressed to the radio.</summary>
    public CivFrame? ReplyTo(string body)
    {
        var frame = CivProtocol.BuildFrame(CivProtocol.DefaultRadioAddress, CivProtocol.ControllerAddress, Bytes(body));
        return Replies[Hex(frame)];
    }
}
