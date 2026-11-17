using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// A radio still streaming scope data from an earlier session when IWC opens the
/// port, as an original IC-7300 on CI-V USB Port = Link to [REMOTE] was after IWC
/// had been killed (IWC #128). While it streams it drops every question, as that
/// radio did, and it ignores the first stop request. The replies are a recorded
/// MkII's; no real radio or port is involved.
/// </summary>
public class LeftoverScopeStreamTests
{
    private const string StopOutput = "FE FE 94 E0 27 11 00 FD";

    [Fact]
    public async Task TheStreamIsStoppedBeforeTheRadioIsAskedAnything()
    {
        await using var bus = new StreamingRadio(streaming: true, ignoredStops: 1);
        var (controller, _) = TestHost.NewController(bus);
        controller.LeftoverScopeListenMs = 150;

        Assert.True(await controller.ConnectAsync());
        Assert.Equal("IC-7300", controller.ModelId);
        Assert.False(bus.Streaming);

        var sent = bus.Inner.SentHex.ToList();
        Assert.Equal(StopOutput, sent[0]);
        Assert.Equal(StopOutput, sent[1]);
        Assert.Equal("FE FE 00 E0 19 00 FD", sent[2]);
    }

    [Fact]
    public async Task AQuietRadioIsNotSentAStop()
    {
        await using var bus = new StreamingRadio(streaming: false, ignoredStops: 0);
        var (controller, _) = TestHost.NewController(bus);
        controller.LeftoverScopeListenMs = 150;

        Assert.True(await controller.ConnectAsync());
        Assert.Equal("FE FE 00 E0 19 00 FD", bus.Inner.SentHex.First());
    }

    [Fact]
    public async Task ExitStopsTheStream()
    {
        await using var bus = new StreamingRadio(streaming: false, ignoredStops: 0);
        var (controller, _) = TestHost.NewController(bus);
        Assert.True(await controller.ConnectAsync());

        bus.Inner.Sent.Clear();
        await controller.StopAsync(CancellationToken.None);
        Assert.Equal(StopOutput, Assert.Single(bus.Inner.SentHex));
    }

    private sealed class StreamingRadio : ICivClient, IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private int _ignoredStops;
        private Task? _pump;

        public StreamingRadio(bool streaming, int ignoredStops)
        {
            Streaming = streaming;
            _ignoredStops = ignoredStops;
        }

        public SimulatedRadioClient Inner { get; } = new(0x94);
        public volatile bool Streaming;

        public bool IsOpen => Inner.IsOpen;

        private EventHandler<CivFrame>? _frames;
        public event EventHandler<CivFrame>? UnsolicitedFrame
        {
            add { _frames += value; Inner.UnsolicitedFrame += value; }
            remove { _frames -= value; Inner.UnsolicitedFrame -= value; }
        }

        public async Task<bool> OpenAsync(string portName, int baudRate)
        {
            bool ok = await Inner.OpenAsync(portName, baudRate);
            _pump ??= Task.Run(async () =>
            {
                // A middle part of an 11-part sweep: seen as scope data, never a whole sweep.
                var part = new CivFrame { To = CivProtocol.ControllerAddress, From = 0x94, Body = new byte[] { 0x27, 0x00, 0x00, 0x05, 0x11, 0x10, 0x20 } };
                while (!_cts.IsCancellationRequested)
                {
                    if (Streaming) _frames?.Invoke(this, part);
                    try { await Task.Delay(20, _cts.Token); } catch (OperationCanceledException) { }
                }
            });
            return ok;
        }

        public Task CloseAsync() => Inner.CloseAsync();

        public Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
        {
            if (Streaming)
            {
                Inner.Sent.Add(frame);
                return Task.FromResult<CivFrame?>(null);
            }
            return Inner.TransactAsync(frame, expectedCmd, timeoutMs, cancellationToken);
        }

        public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
        {
            if (CivRecording.Hex(bytes) == StopOutput && _ignoredStops-- <= 0)
                Streaming = false;
            return Inner.SendRawAsync(bytes, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_pump != null) await _pump;
            _cts.Dispose();
        }
    }
}
