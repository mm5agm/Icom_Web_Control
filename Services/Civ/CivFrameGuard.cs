using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// Sits between the controller and the bus and refuses any frame the connected
    /// radio's <see cref="RadioProfile"/> does not allow. Every feature and every
    /// voice macro reaches the radio through here, so no single feature has to
    /// remember. A refused request reads as a radio that did not answer, which the
    /// controller already treats as "not available".
    ///
    /// Until the radio has identified itself there is no profile to check against,
    /// so frames pass. The first frame on a connection is the 19 00 that identifies it.
    /// </summary>
    public sealed class CivFrameGuard : ICivClient
    {
        private readonly ICivClient _inner;
        private readonly Func<string?> _model;
        private readonly ILogger _logger;
        private readonly HashSet<string> _logged = new();

        public CivFrameGuard(ICivClient inner, Func<string?> model, ILogger logger)
        {
            _inner = inner;
            _model = model;
            _logger = logger;
        }

        public bool IsOpen => _inner.IsOpen;
        public string? PortName => _inner.PortName;
        public void SetDtr(bool on) => _inner.SetDtr(on);

        public event EventHandler<CivFrame>? UnsolicitedFrame
        {
            add => _inner.UnsolicitedFrame += value;
            remove => _inner.UnsolicitedFrame -= value;
        }

        public Task<bool> OpenAsync(string portName, int baudRate) => _inner.OpenAsync(portName, baudRate);
        public Task CloseAsync() => _inner.CloseAsync();

        public Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default)
            => Allowed(frame)
                ? _inner.TransactAsync(frame, expectedCmd, timeoutMs, cancellationToken)
                : Task.FromResult<CivFrame?>(null);

        public Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default)
            => Allowed(bytes) ? _inner.SendRawAsync(bytes, cancellationToken) : Task.CompletedTask;

        private bool Allowed(byte[] frame)
        {
            var model = _model();
            var profile = RadioProfile.ForModel(model);
            if (profile == null || profile.Allows(frame)) return true;

            // Say so once per frame, not on every poll.
            var hex = Convert.ToHexString(frame);
            bool first;
            lock (_logged) first = _logged.Add(hex);
            if (first)
                _logger.LogInformation("[CivFrameGuard] Not sent to {Model}, which has no such command: {Frame}", model, hex);
            return false;
        }
    }
}
