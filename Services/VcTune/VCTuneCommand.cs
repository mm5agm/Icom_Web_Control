namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable representation of a fully constructed VT CAT command string,
/// produced by <see cref="IVCTuneCommandBuilder"/>.
/// </summary>
/// <param name="RawCommand">
/// The fully formatted CAT command string ready to pass to
/// <c>ICatClient.SendCommandAsync</c>. Examples: <c>"VT01;"</c>, <c>"VT0+3;"</c>, <c>"VT0;"</c>.
/// </param>
/// <param name="Type">The kind of operation this command performs.</param>
/// <param name="Band">Which receiver this command targets.</param>
/// <param name="Direction">
/// Step direction for <see cref="VCTuneCommandType.Step"/> and
/// <see cref="VCTuneCommandType.Center"/> commands; <see langword="null"/> for all others.
/// </param>
/// <param name="StepAmount">
/// Step amount (0–9) for <see cref="VCTuneCommandType.Step"/> and
/// <see cref="VCTuneCommandType.Center"/> commands; <see langword="null"/> for all others.
/// Center commands always carry amount = 0.
/// </param>
public sealed record VCTuneCommand(
    string RawCommand,
    VCTuneCommandType Type,
    VCTuneBand Band,
    VCTuneDirection? Direction = null,
    int? StepAmount = null);
