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
        private readonly Func<string?> _chosenModel;
        private readonly HashSet<string> _logged = new();

        /// <param name="chosenModel">
        /// The model chosen in Settings. A frame that transmits is refused when
        /// the radio that answered is under test (<see cref="RadioProfile.TransmitBlocked"/>),
        /// or, when IWC didn't recognise it, this one is: an IC-9700 whose CI-V
        /// address has been changed still can't be keyed.
        /// </param>
        public CivFrameGuard(ICivClient inner, Func<string?> model, ILogger logger, Func<string?>? chosenModel = null)
        {
            _inner = inner;
            _model = model;
            _logger = logger;
            _chosenModel = chosenModel ?? (() => null);
        }

        public bool IsOpen => _inner.IsOpen;
        public string? PortName => _inner.PortName;
        public string RecentReceivedHex() => _inner.RecentReceivedHex();
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
            // A radio IWC recognised decides; one it didn't (a changed CI-V
            // address) falls back to the model chosen in Settings.
            var blockedModel = profile != null ? model : _chosenModel();
            if (RadioProfile.TransmitBlockedFor(blockedModel) && RadioProfile.IsTransmitFrame(frame))
            {
                _logger.LogWarning("[CivFrameGuard] Transmit refused: {Model} is under test, and IWC doesn't transmit on it yet: {Frame}",
                    blockedModel, Convert.ToHexString(frame));
                return false;
            }
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
