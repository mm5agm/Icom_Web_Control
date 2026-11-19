using System.Diagnostics;
using System.Text.RegularExpressions;
using Icom_Web_Control.Services.Civ;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// Closing the CI-V port must never stop the poll loop. When the radio went
/// quiet and the loop dropped the link, the close ran inside the port lock and
/// never came back: IWC stopped trying to reconnect, sat at 0% CPU, and every
/// exit had to be forced (IWC #128, D#51).
/// </summary>
public class CivBusCloseTests
{
    [Fact]
    public async Task AWaitOnACloseThatNeverEndsGivesUp()
    {
        var never = new TaskCompletionSource().Task;
        var clock = Stopwatch.StartNew();

        Assert.False(await CivBusService.FinishesWithinAsync(never, 200));
        Assert.InRange(clock.ElapsedMilliseconds, 150, 5000);
    }

    [Fact]
    public async Task AWaitOnACloseThatEndsSaysSo()
        => Assert.True(await CivBusService.FinishesWithinAsync(Task.Delay(20), 5000));

    [Fact]
    public void TheTimeoutIsShortEnoughForTheLoopToCarryOn()
        => Assert.InRange(CivBusService.CloseTimeoutMs, 500, 5000);

    [Fact]
    public async Task ClosingABusThatWasNeverOpenedReturnsAtOnce()
    {
        var bus = new CivBusService(NullLogger<CivBusService>.Instance);
        var close = bus.CloseAsync();

        Assert.True(await CivBusService.FinishesWithinAsync(close, 1000));
        Assert.False(bus.IsOpen);
        bus.Dispose();
    }

    [Fact]
    public async Task OpeningAPortThatIsNotThereFailsWithoutHanging()
    {
        var bus = new CivBusService(NullLogger<CivBusService>.Instance);
        var open = bus.OpenAsync("COM_IWC_TEST_NOT_PRESENT", 19200);

        Assert.True(await CivBusService.FinishesWithinAsync(open, 5000));
        Assert.False(await open);
        Assert.False(bus.IsOpen);
        bus.Dispose();
    }

    /// <summary>
    /// The one place a SerialPort is closed is CloseQuietly, which runs on a
    /// worker with no lock held. A Close() call anywhere else in the bus, and in
    /// particular inside <c>lock (_portLock)</c>, brings the hang back.
    /// </summary>
    [Fact]
    public void ThePortIsOnlyEverClosedInCloseQuietly()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "Civ", "CivBusService.cs"));
        var closes = Regex.Matches(source, @"\b\w+\.Close\(\)").Select(m => m.Index).ToList();
        Assert.Single(closes);

        var method = source.IndexOf("private void CloseQuietly(SerialPort port)", StringComparison.Ordinal);
        Assert.True(method >= 0, "CloseQuietly is gone");
        var next = source.IndexOf("internal static async Task<bool> FinishesWithinAsync", method, StringComparison.Ordinal);
        Assert.InRange(closes[0], method, next);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Icom_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Icom_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
