using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// A CI-V bus that answers from a recording instead of a radio. A request the
/// radio was sent while recording gets the reply it gave; anything else
/// (every set and write, since none were recorded) gets no reply, as a radio
/// that times out, unless a test scripts one with <see cref="Answer"/>. Every
/// frame the controller sends is kept in <see cref="Sent"/>, so a test can
/// say exactly what went to the radio.
/// </summary>
internal sealed class ReplayCivClient : ICivClient
{
    private readonly CivRecording _recording;
    private readonly Dictionary<string, CivFrame> _scripted = new();

    public ReplayCivClient(CivRecording recording) => _recording = recording;

    public List<byte[]> Sent { get; } = new();
    public bool IsOpen { get; private set; }

    public event EventHandler<CivFrame>? UnsolicitedFrame;

    /// <summary>Answer <paramref name="frameHex"/> (a whole frame) with <paramref name="reply"/>.</summary>
    public void Answer(string frameHex, CivFrame reply) => _scripted[frameHex] = reply;

    public IEnumerable<string> SentHex => Sent.Select(CivRecording.Hex);

    /// <summary>Frames sent that the recording has no entry for: sets, writes, or a read never recorded.</summary>
    public IEnumerable<string> SentNotRecorded => SentHex.Where(h => !_recording.Replies.ContainsKey(h));

    public Task<bool> OpenAsync(string portName, int baudRate) { IsOpen = true; return Task.FromResult(true); }
    public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

    public Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
    {
        Sent.Add(frame);
        var hex = CivRecording.Hex(frame);
        CivFrame? reply = _scripted.TryGetValue(hex, out var s) ? s
            : _recording.Replies.TryGetValue(hex, out var r) ? r
            : null;
        // CivBusService completes a transaction on the expected command or an NG.
        if (reply is not null && reply.Cmd != expectedCmd && reply.Cmd != CivProtocol.AckNg)
            reply = null;
        return Task.FromResult(reply);
    }

    public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        Sent.Add(bytes);
        return Task.CompletedTask;
    }

    /// <summary>Hand the controller frames the radio pushed unasked, as the bus does.</summary>
    public void Push(IEnumerable<CivFrame> frames)
    {
        foreach (var f in frames) UnsolicitedFrame?.Invoke(this, f);
    }
}
