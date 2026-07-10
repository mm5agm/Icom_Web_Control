namespace Yaesu_Web_Control.Services;

/// <summary>
/// Unified result returned by <see cref="VCTuneModule.ExecuteCommandAsync"/> and
/// <see cref="VCTuneModule.RefreshStatusAsync"/>. Provides a single result shape
/// regardless of whether the underlying operation was a SET or a READ.
/// </summary>
/// <param name="Success">
/// <see langword="true"/> when the operation completed without error and the radio
/// confirmed the expected outcome.
/// </param>
/// <param name="ErrorCategory">
/// Matches one of the <see cref="VCTuneErrorType"/> member names, or an empty string
/// on success. Suitable for programmatic branching without string parsing.
/// </param>
/// <param name="Message">
/// User-facing description of the outcome. Empty on success (callers may substitute
/// their own positive feedback); populated with a friendly explanation on failure.
/// </param>
/// <param name="UpdatedSnapshot">
/// The <see cref="VCTuneStateSnapshot"/> from the state machine immediately after the
/// operation completed, or <see langword="null"/> when the operation failed before any
/// state update could be applied.
/// </param>
public sealed record VCTuneCommandResult(
    bool Success,
    string ErrorCategory,
    string Message,
    VCTuneStateSnapshot? UpdatedSnapshot)
{
    /// <summary>
    /// Creates a successful result carrying the post-operation state snapshot.
    /// </summary>
    public static VCTuneCommandResult Ok(VCTuneStateSnapshot snapshot) =>
        new(true, string.Empty, string.Empty, snapshot);

    /// <summary>
    /// Creates a failure result with an error category and user-facing message.
    /// </summary>
    public static VCTuneCommandResult Fail(string category, string message) =>
        new(false, category, message, null);
}
