using System;
using System.Threading;
using System.Threading.Tasks;

namespace Icom_Web_Control.Services.Civ
{
    /// <summary>
    /// The CI-V transport seam: owns the serial port, frames the byte stream,
    /// and matches request frames to their replies. Icom analogue of the Yaesu
    /// <c>ICatClient</c>. Deliberately protocol-light — callers hand it fully
    /// built frames (see <see cref="CivProtocol.BuildFrame"/>) and get parsed
    /// reply frames back; semantic meaning lives above, in
    /// <see cref="CivRadioController"/>.
    /// </summary>
    public interface ICivClient
    {
        bool IsOpen { get; }

        /// <summary>The CI-V port's name while it is open, else null.</summary>
        string? PortName => null;

        /// <summary>The last bytes that came in off the wire, as hex, for the log when a radio doesn't answer (IWC #128).</summary>
        string RecentReceivedHex() => "";

        /// <summary>
        /// Raises or drops DTR on the open CI-V port: the CW Paddle's key line
        /// on an original IC-7300, whose one USB port carries both CI-V and
        /// USB Keying (CW). Throws if the port is not open.
        /// </summary>
        void SetDtr(bool on) => throw new NotSupportedException("This CI-V client has no DTR line.");

        Task<bool> OpenAsync(string portName, int baudRate);
        Task CloseAsync();

        /// <summary>
        /// Send <paramref name="frame"/> and await the radio's reply whose
        /// command byte equals <paramref name="expectedCmd"/> (or an
        /// <see cref="CivProtocol.AckNg"/> rejection). Returns null on timeout.
        /// Transactions are serialized so one poll's reply can't be mistaken
        /// for another's.
        /// </summary>
        Task<CivFrame?> TransactAsync(byte[] frame, byte expectedCmd, int timeoutMs = 500, CancellationToken cancellationToken = default);

        /// <summary>
        /// Write raw bytes to the port with no reply matching. Exists for the
        /// power-ON wake-up burst (a run of 0xFE bytes plus the 18 01 frame),
        /// which the asleep radio may not acknowledge in the normal way.
        /// Serialised against <see cref="TransactAsync"/> so it can never
        /// interleave with a pending request. No-op if the port is closed.
        /// </summary>
        Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken = default);

        /// <summary>
        /// Frames addressed to us that did not match a pending transaction
        /// (radio-initiated transceive pushes, spontaneous state reports). Not
        /// used in Phase 2 but exposed so Phase 3's transceive handling has a
        /// home without reworking the transport.
        /// </summary>
        event EventHandler<CivFrame>? UnsolicitedFrame;
    }
}
