using Icom_Web_Control.Services.Civ;
using Icom_Web_Control.Services.Cw;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// Until the DTR and RTS lines have been measured off Windows, IWC must not raise
/// them or open ports by itself there. These only assert anything on the Linux and
/// macOS CI runs; on Windows those features are in use, so the tests return early.
/// </summary>
public class OffWindowsSafetyTests
{
    [Fact]
    public void DtrCannotBeRaised()
    {
        if (OperatingSystem.IsWindows()) return;
        var bus = new CivBusService(NullLogger<CivBusService>.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() => bus.SetDtr(true));
        Assert.Contains("isn't available", ex.Message);
    }

    [Fact]
    public void NoKeyPortsAreOffered()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Empty(SerialCwKeyLine.Ports());
    }

    [Fact]
    public async Task FindMyRadioOpensNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await new RadioFinder(NullLogger<RadioFinder>.Instance).FindAsync(null, CancellationToken.None);
        Assert.False(result.Found);
        Assert.Empty(result.PortsProbed);
        Assert.Empty(result.PortsBusy);
    }
}
