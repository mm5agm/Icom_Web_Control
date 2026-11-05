using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The recorded MkII answering as a different radio: it identifies with the
/// given address and replies from it. The replies are a MkII's, so this proves
/// what the controller sends another model, not how that model answers. No real
/// radio or port is involved.
/// </summary>
internal sealed class SimulatedRadioClient : ICivClient
{
    private const byte RecordedAddress = 0xB6;
    private readonly ReplayCivClient _inner = new(CivRecording.Load());
    private readonly byte _address;
    private readonly bool _plainUsb;

    /// <param name="address">The address it identifies with and replies from.</param>
    /// <param name="plainUsb">
    /// Report the mode as USB rather than the recording's DATA mode, which is the
    /// only way to reach the actions that depend on it, such as RX tone.
    /// </param>
    public SimulatedRadioClient(byte address, bool plainUsb = false)
    {
        _address = address;
        _plainUsb = plainUsb;
    }

    public List<byte[]> Sent { get; } = new();
    public IEnumerable<string> SentHex => Sent.Select(CivRecording.Hex);
    public bool IsOpen => _inner.IsOpen;

    public event EventHandler<CivFrame>? UnsolicitedFrame
    {
        add => _inner.UnsolicitedFrame += value;
        remove => _inner.UnsolicitedFrame -= value;
    }

    public Task<bool> OpenAsync(string portName, int baudRate) => _inner.OpenAsync(portName, baudRate);
    public Task CloseAsync() => _inner.CloseAsync();

    public async Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
    {
        Sent.Add(frame);
        if (frame[3] == CivProtocol.ControllerAddress && frame[4] == CivProtocol.CmdReadId)
            return new CivFrame { To = CivProtocol.ControllerAddress, From = _address, Body = new byte[] { CivProtocol.CmdReadId, 0x00, _address } };

        if (_plainUsb && CivRecording.Hex(frame[4..]) == "26 00 FD")
            return new CivFrame { To = CivProtocol.ControllerAddress, From = _address, Body = new byte[] { 0x26, 0x00, 0x01, 0x00, 0x01 } };

        var asRecorded = (byte[])frame.Clone();
        if (asRecorded[2] == _address) asRecorded[2] = RecordedAddress;
        var reply = await _inner.TransactAsync(asRecorded, expectedCmd, timeoutMs, cancellationToken);
        return reply is null ? null : new CivFrame { To = reply.To, From = _address, Body = reply.Body };
    }

    public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        Sent.Add(bytes);
        return Task.CompletedTask;
    }
}
