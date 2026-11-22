using Icom_Web_Control.Services.Civ;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// Who waits for the CI-V bus and for how long (IWC Discussion #51): the poll
/// loop as long as it takes, the browser and rigctld 1.5 s, and nobody but the
/// poll loop while the radio isn't answering.
/// </summary>
public class CivBusGateTests
{
    private sealed class Clock
    {
        public long Now = 1_000_000;
        public long Read() => Now;
    }

    private sealed class ListLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private static CivBusGate MakeSilent(Clock clock, ILogger? logger = null)
    {
        var gate = new CivBusGate(logger ?? NullLogger.Instance, clock.Read);
        clock.Now += CivBusGate.SilentAfterMs;
        for (int i = 0; i < CivBusGate.SilentAfterTimeouts; i++) gate.TimedOut();
        Assert.True(gate.RadioSilent);
        return gate;
    }

    // Runs in its own flow, so the mark stays there.
    private static Task<T> AsPollLoop<T>(Func<Task<T>> work) => Task.Run(() =>
    {
        CivBusGate.MarkPollLoop();
        return work();
    });

    [Fact]
    public async Task ABrowserCallGivesUpWhenTheBusStaysBusy()
    {
        using var gate = new CivBusGate(NullLogger.Instance);
        Assert.True(await gate.EnterAsync(true, default));
        var started = Environment.TickCount64;
        Assert.False(await gate.EnterAsync(true, default));
        Assert.InRange(Environment.TickCount64 - started, CivBusGate.BusWaitMs - 100, CivBusGate.BusWaitMs + 2000);
        gate.Release();
        Assert.True(await gate.EnterAsync(true, default));
        gate.Release();
    }

    [Fact]
    public async Task ThePollLoopWaitsAsLongAsItTakes()
    {
        using var gate = new CivBusGate(NullLogger.Instance);
        Assert.True(await gate.EnterAsync(true, default));
        var poll = AsPollLoop(() => gate.EnterAsync(true, default));
        await Task.Delay(CivBusGate.BusWaitMs + 500);
        Assert.False(poll.IsCompleted);
        Assert.Equal(1, gate.Waiting);
        gate.Release();
        Assert.True(await poll);
        gate.Release();
    }

    [Fact]
    public async Task TheMarkStaysInThePollLoopsFlow()
    {
        Assert.True(await AsPollLoop(() => Task.FromResult(CivBusGate.IsPollLoop)));
        Assert.False(CivBusGate.IsPollLoop);
    }

    [Fact]
    public async Task WhileTheRadioIsSilentBrowserCallsAreSkippedAtOnce()
    {
        var clock = new Clock();
        using var gate = MakeSilent(clock);
        Assert.False(await gate.EnterAsync(true, default));
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task WhileTheRadioIsSilentThePollLoopStillGetsThrough()
    {
        var clock = new Clock();
        using var gate = MakeSilent(clock);
        Assert.True(await AsPollLoop(() => gate.EnterAsync(true, default)));
        gate.Release();
    }

    [Fact]
    public async Task WhileTheRadioIsSilentARawWriteStillGoes()
    {
        // The power-on wake-up from the browser is for exactly this radio.
        var clock = new Clock();
        using var gate = MakeSilent(clock);
        Assert.True(await gate.EnterAsync(false, default));
        gate.Release();
    }

    [Fact]
    public async Task OneAnswerEndsTheSilence()
    {
        var clock = new Clock();
        var log = new ListLogger();
        using var gate = MakeSilent(clock, log);
        gate.Answered();
        Assert.False(gate.RadioSilent);
        Assert.True(await gate.EnterAsync(true, default));
        gate.Release();
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("hasn't answered"));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Information && l.Text.Contains("answering again"));
    }

    [Fact]
    public void TimeoutsSoonAfterAnAnswerAreNotSilence()
    {
        // A run of feature probes the radio doesn't support, while it answers the poll.
        var clock = new Clock();
        using var gate = new CivBusGate(NullLogger.Instance, clock.Read);
        gate.Answered();
        clock.Now += CivBusGate.SilentAfterMs - 1;
        for (int i = 0; i < CivBusGate.SilentAfterTimeouts * 3; i++) gate.TimedOut();
        Assert.False(gate.RadioSilent);
    }

    [Fact]
    public void TooFewTimeoutsAreNotSilence()
    {
        var clock = new Clock();
        using var gate = new CivBusGate(NullLogger.Instance, clock.Read);
        clock.Now += CivBusGate.SilentAfterMs * 10;
        for (int i = 0; i < CivBusGate.SilentAfterTimeouts - 1; i++) gate.TimedOut();
        Assert.False(gate.RadioSilent);
    }

    [Fact]
    public void ANewPortForgetsTheSilence()
    {
        var clock = new Clock();
        using var gate = MakeSilent(clock);
        gate.Reset();
        Assert.False(gate.RadioSilent);
        gate.TimedOut();
        Assert.False(gate.RadioSilent);
    }

    [Fact]
    public async Task AGiveUpIsLoggedWithTheQueue()
    {
        var log = new ListLogger();
        using var gate = new CivBusGate(log);
        Assert.True(await gate.EnterAsync(true, default));
        Assert.False(await gate.EnterAsync(true, default));
        gate.Release();
        var line = Assert.Single(log.Lines, l => l.Text.Contains("Slow bus"));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("1 in the queue", line.Text);
        Assert.Contains("1 gave up", line.Text);
    }

    [Fact]
    public async Task SkipsAreReportedAtMostEveryTenSeconds()
    {
        var clock = new Clock();
        var log = new ListLogger();
        using var gate = MakeSilent(clock, log);
        for (int i = 0; i < 20; i++) await gate.EnterAsync(true, default);
        Assert.Single(log.Lines, l => l.Text.Contains("Slow bus"));
        clock.Now += CivBusGate.LogEveryMs;
        await gate.EnterAsync(true, default);
        Assert.Contains(log.Lines, l => l.Text.Contains("20 skipped"));
    }

    [Fact]
    public async Task CancellingTheWaitStillThrows()
    {
        // As the bare lock did: the poll loop stops on it at shutdown.
        using var gate = new CivBusGate(NullLogger.Instance);
        Assert.True(await gate.EnterAsync(true, default));
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsPollLoop(() => gate.EnterAsync(true, cts.Token)));
        Assert.Equal(0, gate.Waiting);
        gate.Release();
    }
}
