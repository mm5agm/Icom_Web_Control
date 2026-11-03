using Icom_Web_Control.Services.Civ;
using Icom_Web_Control.Services.Cw;
using Xunit;

namespace IcomWebControl.Tests;

/// <summary>
/// The CW Paddle's key line on the CI-V port (an original IC-7300 keys by DTR
/// on its one USB port). No serial port is opened: a fake CI-V client records
/// what DTR was set to.
/// </summary>
public class CwKeyLineTests
{
    private sealed class FakeCiv : ICivClient
    {
        public string? Port = "COM5";
        public List<bool> Dtr { get; } = new();
        public bool IsOpen => Port is not null;
        public string? PortName => Port;
        public void SetDtr(bool on)
        {
            if (Port is null) throw new InvalidOperationException("closed");
            Dtr.Add(on);
        }
        public Task<bool> OpenAsync(string portName, int baudRate) => Task.FromResult(true);
        public Task CloseAsync() => Task.CompletedTask;
        public Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
            => Task.FromResult<CivFrame?>(null);
        public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event EventHandler<CivFrame>? UnsolicitedFrame { add { } remove { } }
    }

    [Fact]
    public void Choosing_the_line_drops_DTR_and_keying_follows_the_key()
    {
        var civ = new FakeCiv();
        var line = new CivDtrCwKeyLine(civ, "COM5");
        line.SetKey(true);
        line.SetKey(false);
        Assert.Equal(new[] { false, true, false }, civ.Dtr);
    }

    [Fact]
    public void Closing_the_line_drops_DTR()
    {
        var civ = new FakeCiv();
        var line = new CivDtrCwKeyLine(civ, "COM5");
        line.SetKey(true);
        line.Dispose();
        Assert.False(civ.Dtr[^1]);
    }

    [Fact]
    public void Keying_after_the_CI_V_port_has_moved_or_closed_throws_so_the_service_drops_to_dry_run()
    {
        var civ = new FakeCiv();
        var line = new CivDtrCwKeyLine(civ, "COM5");
        civ.Port = "COM6";
        Assert.Throws<InvalidOperationException>(() => line.SetKey(true));
        civ.Port = null;
        Assert.Throws<InvalidOperationException>(() => line.SetKey(true));
        Assert.DoesNotContain(true, civ.Dtr);
        line.Dispose();   // must not throw with the port gone
    }

    [Fact]
    public void A_CI_V_client_without_a_DTR_line_refuses()
    {
        ICivClient replay = new ReplayCivClient(null!);
        Assert.Null(replay.PortName);
        Assert.Throws<NotSupportedException>(() => replay.SetDtr(true));
    }
}
