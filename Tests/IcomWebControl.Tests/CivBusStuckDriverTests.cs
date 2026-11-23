using System.Diagnostics;
using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// A USB serial driver that stops answering can hold an open or close
/// forever (D#51). Those calls run on a thread of their own, never the
/// thread pool's, and the bus stops waiting after a time limit.
/// </summary>
public class CivBusStuckDriverTests
{
    [Fact]
    public async Task RunOnOwnThread_IsNotAPoolThread()
    {
        bool? pool = null;
        await CivBusService.RunOnOwnThread(() => pool = Thread.CurrentThread.IsThreadPoolThread);
        Assert.False(pool);
    }

    [Fact]
    public async Task AStuckCall_IsGivenUpOn_AndPinsNoPoolThread()
    {
        using var release = new ManualResetEventSlim(false);
        ThreadPool.GetAvailableThreads(out var before, out _);

        var sw = Stopwatch.StartNew();
        var stuck = CivBusService.RunOnOwnThread(() => release.Wait());
        Assert.False(await CivBusService.FinishesWithinAsync(stuck, 200));
        Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);

        // The stuck call holds its own thread, not one of the pool's.
        ThreadPool.GetAvailableThreads(out var during, out _);
        Assert.True(during >= before - 2, $"pool threads available {before} -> {during}");

        release.Set();
        await stuck;
    }

    [Fact]
    public async Task AFailedCall_StillThrowsToTheCaller()
    {
        var failing = CivBusService.RunOnOwnThread(() => throw new UnauthorizedAccessException("port in use"));
        Assert.True(await CivBusService.FinishesWithinAsync(failing, 2000));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => failing);
    }

    [Fact]
    public void OpenTimeout_IsLongerThanAHealthyOpen_AndShortEnoughToKeepPolling()
        => Assert.InRange(CivBusService.OpenTimeoutMs, 1000, 5000);
}
