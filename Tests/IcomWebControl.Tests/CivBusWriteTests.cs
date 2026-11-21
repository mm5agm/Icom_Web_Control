using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Icom_Web_Control.Services.Civ;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// A serial write the USB driver never finishes must not freeze IWC. The write
/// used to run inside the port lock that IsOpen, the reader and every
/// transaction also took, so one stuck write tied up a thread for every
/// browser and rigctld request until the thread pool ran dry. The page froze
/// and only pulling the USB cable brought IWC back (D#51, Paul VK2AHB).
/// </summary>
public class CivBusWriteTests
{
    [Fact]
    public async Task AWriteThatNeverEndsGivesUp()
    {
        using var release = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();

        Assert.False(await CivBusService.WriteFinishesWithinAsync(() => release.Wait(), 200));
        Assert.InRange(clock.ElapsedMilliseconds, 150, 5000);
        release.Set();
    }

    [Fact]
    public async Task AWriteThatEndsSaysSo()
        => Assert.True(await CivBusService.WriteFinishesWithinAsync(() => { }, 5000));

    [Fact]
    public async Task AWriteThatFailsStillThrows()
        => await Assert.ThrowsAsync<IOException>(
            () => CivBusService.WriteFinishesWithinAsync(() => throw new IOException("port gone"), 5000));

    [Fact]
    public void TheStuckLimitIsWellPastThePortsOwnWriteTimeout()
        => Assert.InRange(CivBusService.WriteStuckMs, 1000, 5000);

    [Fact]
    public async Task IsOpenAnswersWhileThePortLockIsHeld()
    {
        var bus = new CivBusService(NullLogger<CivBusService>.Instance);
        var portLock = typeof(CivBusService)
            .GetField("_portLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(bus)!;

        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() => { lock (portLock) { held.Set(); release.Wait(); } }) { IsBackground = true };
        holder.Start();
        held.Wait();
        try
        {
            var ask = Task.Run(() => bus.IsOpen);
            Assert.True(await CivBusService.FinishesWithinAsync(ask, 1000), "IsOpen waited on the port lock");
            Assert.False(await ask);
            Assert.Null(bus.PortName);
        }
        finally
        {
            release.Set();
            holder.Join();
            bus.Dispose();
        }
    }

    /// <summary>
    /// Every write goes through WriteWithinAsync, never straight to the port
    /// and never inside <c>lock (_portLock)</c>.
    /// </summary>
    [Fact]
    public void ThePortIsOnlyWrittenThroughTheTimedWrite()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "Civ", "CivBusService.cs"));
        var writes = Regex.Matches(source, @"\b\w+\.Write\(").Select(m => m.Index).ToList();
        Assert.Single(writes);

        var method = source.IndexOf("private async Task<bool> WriteWithinAsync(", StringComparison.Ordinal);
        Assert.True(method >= 0, "WriteWithinAsync is gone");
        var next = source.IndexOf("internal static async Task<bool> WriteFinishesWithinAsync", method, StringComparison.Ordinal);
        Assert.InRange(writes[0], method, next);
    }

    /// <summary>
    /// The poll loop never awaits a broadcast to the browsers: a frozen browser
    /// holds such a send until SignalR drops it, minutes later.
    /// </summary>
    [Fact]
    public void TheControllerNeverAwaitsAHubSend()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "CivRadioController.cs"));
        Assert.DoesNotMatch(@"await\s+_hubContext\.", source);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Icom_Web_Control.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException($"No Icom_Web_Control.csproj above {AppContext.BaseDirectory}.");
    }
}
