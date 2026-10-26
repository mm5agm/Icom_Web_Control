using Icom_Web_Control.Hubs;
using Icom_Web_Control.Models;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Civ;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// Builds a <see cref="CivRadioController"/> on a <see cref="ReplayCivClient"/>
/// with in-memory settings and a radio state kept in a temp folder, so no test
/// touches the user's files in %APPDATA%.
/// </summary>
internal static class TestHost
{
    public static (CivRadioController Controller, ReplayCivClient Bus, RadioStateService State) NewController(CivRecording? recording = null)
    {
        var bus = new ReplayCivClient(recording ?? CivRecording.Load());
        var dir = Path.Combine(Path.GetTempPath(), "IcomWebControl.Tests", Guid.NewGuid().ToString("N"));
        var persistence = new RadioStatePersistenceService(NullLogger<RadioStatePersistenceService>.Instance, dir);
        var hub = new SilentHubContext();
        var state = new RadioStateService(NullLogger<RadioStateService>.Instance, persistence, hub, new UnknownBandPlan());
        var controller = new CivRadioController(
            state, bus, new MemorySettings(), hub,
            new RadioFinder(NullLogger<RadioFinder>.Instance),
            NullLogger<CivRadioController>.Instance);
        return (controller, bus, state);
    }

    internal sealed class MemorySettings : ISettingsService
    {
        public ApplicationSettings Settings { get; } = new() { SerialPort = "COM8", BaudRate = 19200 };
        public int Saves { get; private set; }
        public Task<ApplicationSettings> GetSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(ApplicationSettings settings) { Saves++; return Task.CompletedTask; }
        public string GetSettingsFilePath() => "(memory)";
        public void InvalidateCache() { }
    }

    internal sealed class SilentHubContext : IHubContext<RadioHub>
    {
        public IHubClients Clients { get; } = new SilentClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class SilentClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new SilentProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class SilentProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class UnknownBandPlan : IBandPlanService
    {
        public string CurrentRegion => "Region1";
        public IReadOnlyList<BandEdge> CurrentEdges => Array.Empty<BandEdge>();
        public string BandForFrequency(long hz) => "Unknown";
    }
}
