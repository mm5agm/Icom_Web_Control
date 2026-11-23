using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// Watches, from its own thread, whether IWC is getting time to run (IWC
    /// Discussion #51). Every second it hands the thread pool a tiny job and
    /// times how long the job waits. When the wait passes 2 s it logs what the
    /// pool and the PC were doing, then logs again when things recover. It also
    /// logs when the whole PC has been flat out for a while, and when IWC's own
    /// clock jumps because the PC was asleep. Its own thread keeps running
    /// when every pool thread is stuck, so a freeze can explain itself.
    /// </summary>
    public sealed class StarvationMonitor : IHostedService, IDisposable
    {
        internal const int ProbeEveryMs = 1000;
        internal const int LagWarnMs = 2000;
        internal const int PcBusyPercent = 90;
        internal const int PcBusyAfterSamples = 10;
        internal const int PcCalmPercent = 70;
        internal const int PcCalmAfterSamples = 5;
        internal const int AsleepGapMs = 10000;

        private readonly ILogger<StarvationMonitor> _logger;
        private readonly CancellationTokenSource _stop = new();
        private Thread? _thread;
        private long _probeDoneAt;

        public StarvationMonitor(ILogger<StarvationMonitor> logger) => _logger = logger;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "IWC starvation monitor" };
            _thread.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stop.Cancel();
            return Task.CompletedTask;
        }

        public void Dispose() => _stop.Dispose();

        private void Run()
        {
            try
            {
                ThreadPool.GetMinThreads(out int minWorkers, out _);
                _logger.LogInformation("[Starvation] Monitor on: {Cpus} processors, thread pool minimum {Min}",
                    Environment.ProcessorCount, minWorkers);

                var lag = new EpisodeTracker(startAfter: 1, endAfter: 1);
                var busy = new EpisodeTracker(startAfter: PcBusyAfterSamples, endAfter: PcCalmAfterSamples);
                var cpu = new CpuSampler();
                long worstLag = 0;
                long probeQueuedAt = 0;
                long lastTick = Environment.TickCount64;

                while (!_stop.Token.WaitHandle.WaitOne(ProbeEveryMs))
                {
                    long now = Environment.TickCount64;
                    long gap = now - lastTick;
                    lastTick = now;
                    cpu.Sample();

                    if (gap >= AsleepGapMs)
                    {
                        // Even this thread didn't run: the PC was asleep or paused.
                        _logger.LogWarning("[Starvation] IWC didn't run at all for {Seconds} s; the PC was asleep or paused", gap / 1000);
                        probeQueuedAt = 0;
                        continue;
                    }

                    // Thread pool: one probe at a time; it waits as long as the pool does.
                    long lagMs = -1;
                    if (probeQueuedAt == 0)
                    {
                        Interlocked.Exchange(ref _probeDoneAt, 0);
                        probeQueuedAt = now;
                        ThreadPool.UnsafeQueueUserWorkItem(_ => Interlocked.Exchange(ref _probeDoneAt, Environment.TickCount64), null);
                    }
                    else
                    {
                        long doneAt = Interlocked.Read(ref _probeDoneAt);
                        if (doneAt != 0)
                        {
                            lagMs = doneAt - probeQueuedAt;
                            probeQueuedAt = 0;
                        }
                        else
                        {
                            lagMs = now - probeQueuedAt;
                        }
                    }

                    if (lagMs >= 0)
                    {
                        worstLag = lag.Active ? Math.Max(worstLag, lagMs) : lagMs;
                        switch (lag.Observe(lagMs >= LagWarnMs, now))
                        {
                            case EpisodeChange.Started:
                                _logger.LogWarning("[Starvation] IWC's thread pool is starved: a job has waited {Lag} ms. {Pool}. {Cpu}",
                                    lagMs, ThreadPoolSnapshot.Describe(), cpu.Describe());
                                break;
                            case EpisodeChange.Ended:
                                _logger.LogInformation("[Starvation] Thread pool running again after {Seconds} s (longest wait {Worst} ms). {Pool}. {Cpu}",
                                    lag.LastDurationMs / 1000, worstLag, ThreadPoolSnapshot.Describe(), cpu.Describe());
                                break;
                        }
                    }

                    if (cpu.PcPercent is double pc)
                    {
                        bool high = busy.Active ? pc >= PcCalmPercent : pc >= PcBusyPercent;
                        switch (busy.Observe(high, now))
                        {
                            case EpisodeChange.Started:
                                _logger.LogWarning("[Starvation] The PC has been busy for {Seconds} s. {Cpu}", PcBusyAfterSamples, cpu.Describe());
                                break;
                            case EpisodeChange.Ended:
                                _logger.LogInformation("[Starvation] The PC is calm again after {Seconds} s. {Cpu}", busy.LastDurationMs / 1000, cpu.Describe());
                                break;
                        }
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Starvation] Monitor stopped");
            }
        }
    }

    internal enum EpisodeChange { None, Started, Ended }

    /// <summary>
    /// Turns a stream of yes/no samples into episodes: it starts after
    /// <c>startAfter</c> yeses in a row and ends after <c>endAfter</c> noes in
    /// a row, so each episode is logged once at each end.
    /// </summary>
    internal sealed class EpisodeTracker
    {
        private readonly int _startAfter;
        private readonly int _endAfter;
        private int _run;
        private long _startedAt;

        public EpisodeTracker(int startAfter, int endAfter)
        {
            _startAfter = Math.Max(1, startAfter);
            _endAfter = Math.Max(1, endAfter);
        }

        public bool Active { get; private set; }
        public long LastDurationMs { get; private set; }

        public EpisodeChange Observe(bool bad, long nowMs)
        {
            if (Active == bad) { _run = 0; return EpisodeChange.None; }
            _run++;
            if (!Active && _run >= _startAfter)
            {
                Active = true;
                _run = 0;
                _startedAt = nowMs;
                return EpisodeChange.Started;
            }
            if (Active && _run >= _endAfter)
            {
                Active = false;
                _run = 0;
                LastDurationMs = nowMs - _startedAt;
                return EpisodeChange.Ended;
            }
            return EpisodeChange.None;
        }
    }

    /// <summary>The thread pool's numbers, for the log.</summary>
    internal static class ThreadPoolSnapshot
    {
        public static string Describe()
        {
            ThreadPool.GetAvailableThreads(out int freeWorkers, out _);
            ThreadPool.GetMaxThreads(out int maxWorkers, out _);
            ThreadPool.GetMinThreads(out int minWorkers, out _);
            int busyWorkers = maxWorkers - freeWorkers;
            string process = "";
            try
            {
                using var p = Process.GetCurrentProcess();
                process = $", IWC threads {p.Threads.Count}, memory {p.WorkingSet64 / (1024 * 1024)} MB";
            }
            catch { }
            return $"Pool threads {ThreadPool.ThreadCount} ({busyWorkers} busy, minimum {minWorkers}), jobs waiting {ThreadPool.PendingWorkItemCount}" +
                   $"{process}, full GCs {GC.CollectionCount(2)}";
        }
    }

    /// <summary>
    /// How busy the whole PC and IWC itself are, measured between two samples.
    /// </summary>
    internal sealed class CpuSampler
    {
        private (ulong Idle, ulong Total)? _lastPc;
        private TimeSpan _lastProc;
        private long _lastWall;

        public double? PcPercent { get; private set; }
        public double? IwcPercent { get; private set; }

        public void Sample()
        {
            long wall = Environment.TickCount64;
            var pc = ReadPcTimes();
            if (pc is { } now && _lastPc is { } before)
                PcPercent = CpuMath.BusyPercent(before.Idle, before.Total, now.Idle, now.Total);
            _lastPc = pc;

            try
            {
                using var p = Process.GetCurrentProcess();
                TimeSpan proc = p.TotalProcessorTime;
                if (_lastWall != 0)
                    IwcPercent = CpuMath.ProcessPercent(proc - _lastProc, wall - _lastWall, Environment.ProcessorCount);
                _lastProc = proc;
                _lastWall = wall;
            }
            catch { }
        }

        public string Describe() =>
            $"PC CPU {Format(PcPercent)}, IWC CPU {Format(IwcPercent)} of all {Environment.ProcessorCount} processors";

        private static string Format(double? v) => v is double d ? $"{d:0}%" : "unknown";

        private static (ulong Idle, ulong Total)? ReadPcTimes()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
                    // Kernel time includes idle time.
                    return ((ulong)idle, (ulong)kernel + (ulong)user);
                }
                if (OperatingSystem.IsLinux())
                    return CpuMath.ParseProcStat(File.ReadLines("/proc/stat").FirstOrDefault());
            }
            catch { }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
    }

    internal static class CpuMath
    {
        /// <summary>Busy share of all processors between two idle/total readings.</summary>
        public static double? BusyPercent(ulong idleBefore, ulong totalBefore, ulong idleNow, ulong totalNow)
        {
            if (totalNow <= totalBefore || idleNow < idleBefore) return null;
            double total = totalNow - totalBefore;
            double idle = idleNow - idleBefore;
            return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
        }

        /// <summary>One process's share of all processors over a wall-clock interval.</summary>
        public static double? ProcessPercent(TimeSpan cpuUsed, long wallMs, int processors)
        {
            if (wallMs <= 0 || processors <= 0) return null;
            return Math.Clamp(100.0 * cpuUsed.TotalMilliseconds / (wallMs * (double)processors), 0, 100);
        }

        /// <summary>Linux /proc/stat "cpu" line → idle (idle + iowait) and total jiffies.</summary>
        public static (ulong Idle, ulong Total)? ParseProcStat(string? line)
        {
            if (line == null || !line.StartsWith("cpu ")) return null;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return null;
            ulong total = 0, idle = 0;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!ulong.TryParse(parts[i], out ulong v)) return null;
                // guest and guest_nice (fields 9, 10) are already counted in user and nice.
                if (i <= 8) total += v;
                if (i == 4 || i == 5) idle += v;
            }
            return (idle, total);
        }
    }
}
