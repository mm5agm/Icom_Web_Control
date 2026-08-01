using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services
{
    /// <summary>
    /// No-op <see cref="ICatClient"/> — the placeholder that replaced the Yaesu
    /// CAT transport (CatMultiplexerService / CatMessageDispatcher /
    /// MultiplexedCatClient) when IWC was carved off YWC.
    ///
    /// IWC talks to the IC-7300 through the semantic <see cref="IRadioController"/>
    /// seam (CI-V, via CivRadioController). Most control paths in CatController,
    /// MemoryController and the voice IntentDispatcher have been migrated to that
    /// seam; the ones that have NOT yet been ported still call
    /// <c>_catClient.SendCommandAsync(...)</c> with raw Yaesu CAT strings.
    ///
    /// Rather than delete those call sites (and lose the UI wiring) before their
    /// CI-V replacements exist, they resolve this stub: every send is a logged
    /// no-op and every read returns an empty/zero value. The features it backs
    /// are therefore inert — not broken — until each is re-implemented on the
    /// CI-V seam in its own block. When the last vestigial call site is gone,
    /// this class and <see cref="ICatClient"/> can be deleted outright.
    /// </summary>
    public sealed class NullCatClient : ICatClient
    {
        private readonly ILogger<NullCatClient> _logger;

        public NullCatClient(ILogger<NullCatClient> logger) => _logger = logger;

        public bool IsConnected => false;

        public Task<bool> ConnectAsync(string portName, int baudRate = 38400) => Task.FromResult(false);
        public Task DisconnectAsync() => Task.CompletedTask;
        public void Dispose() { }

        public Task<string> SendCommandAsync(string command, string clientId, CancellationToken cancellationToken = default, int timeoutMs = 150)
        {
            _logger.LogDebug("[NullCatClient] dropped legacy CAT send (no CI-V equivalent yet): {Command}", command?.Trim());
            return Task.FromResult(string.Empty);
        }

        // VFO-A (Main)
        public Task<long> ReadFrequencyAsync() => Task.FromResult(0L);
        public Task<long> ReadFrequencyAAsync() => Task.FromResult(0L);
        public Task<bool> SetFrequencyAAsync(long frequencyHz) => Task.FromResult(false);
        public Task<int> ReadSMeterAsync() => Task.FromResult(0);
        public Task<int> ReadSMeterMainAsync() => Task.FromResult(0);
        public Task<string> ReadModeAsync() => Task.FromResult(string.Empty);
        public Task<string> ReadModeMainAsync() => Task.FromResult(string.Empty);
        public Task<bool> SetModeMainAsync(string mode) => Task.FromResult(false);
        public Task<long> QueryFrequencyAAsync(string clientId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        // VFO-B (Sub)
        public Task<long> ReadFrequencyBAsync() => Task.FromResult(0L);
        public Task<bool> SetFrequencyBAsync(long frequencyHz) => Task.FromResult(false);
        public Task<int> ReadSMeterSubAsync() => Task.FromResult(0);
        public Task<string> ReadModeSubAsync() => Task.FromResult(string.Empty);
        public Task<bool> SetModeSubAsync(string mode) => Task.FromResult(false);
        public Task<long> QueryFrequencyBAsync(string clientId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        // Common
        public Task<bool> ReadTransmitStatusAsync() => Task.FromResult(false);
    }
}
