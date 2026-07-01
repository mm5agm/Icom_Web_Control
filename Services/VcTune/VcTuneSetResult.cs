namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable result returned by every SET operation in <see cref="VcTuneService"/>.
/// When <see cref="Success"/> is true, <see cref="ConfirmedStatus"/> reflects the
/// radio's actual state after the post-SET READ. When false, <see cref="ErrorCategory"/>
/// and <see cref="Message"/> describe the failure.
/// </summary>
/// <param name="Success">True when the command was sent and the radio confirmed the expected state.</param>
/// <param name="ErrorCategory">Named error category (empty on success). See VcTuneService XML docs.</param>
/// <param name="Message">User-facing explanation of success or failure.</param>
/// <param name="ConfirmedStatus">
/// Parsed radio state from the post-SET READ, or null when the read failed
/// or when the command was rejected before being sent.
/// </param>
/// <param name="CommandRejected">
/// True when the command was delivered to the radio but the radio's confirmed
/// state contradicts the requested change.
/// </param>
/// <param name="MeterAvailable">
/// False when the confirmation READ succeeded but P5 could not be read.
/// </param>
public sealed record VcTuneSetResult(
    bool Success,
    string ErrorCategory,
    string Message,
    VcTuneReadResult? ConfirmedStatus,
    bool CommandRejected = false,
    bool MeterAvailable = true)
{
    /// <summary>Creates a fully successful result with confirmed radio state.</summary>
    public static VcTuneSetResult Ok(VcTuneReadResult confirmed) =>
        new(true, string.Empty, string.Empty, confirmed);

    /// <summary>
    /// Creates a rejection result produced before any CAT command was sent
    /// (validation failure, motor protection, semaphore timeout, etc.).
    /// </summary>
    public static VcTuneSetResult Rejected(string category, string message) =>
        new(false, category, message, null);

    /// <summary>
    /// Creates a result where the SET command was sent but the confirmation
    /// READ returned no valid response.
    /// </summary>
    public static VcTuneSetResult SentUnconfirmed(string message) =>
        new(true, "ReadFailure", message, null, MeterAvailable: false);

    /// <summary>
    /// Creates a result where the SET was delivered but the radio's confirmed
    /// state contradicts the requested change.
    /// </summary>
    public static VcTuneSetResult RadioCommandRejected(VcTuneReadResult confirmed) =>
        new(false, "CommandRejected",
            "The radio rejected the VC Tune command. Status has been refreshed.",
            confirmed, CommandRejected: true);
}
