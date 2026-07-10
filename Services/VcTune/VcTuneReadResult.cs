namespace Yaesu_Web_Control.Services;

/// <summary>
/// Immutable parsed result of a VT{P1}; READ command.
/// Returned by <see cref="IVcTuneService.ReadVCTuneStatusAsync"/> and by
/// every post-SET confirmation read inside <see cref="VcTuneService"/>.
/// </summary>
/// <param name="Receiver">Which receiver this result describes (MAIN or SUB).</param>
/// <param name="IsOn">True when P2 = 1 (VC Tune active).</param>
/// <param name="IsDefault">True when P2 = 2 (last command was DEFAULT; not yet resolved to On/Off).</param>
/// <param name="LastDirection">P3: last step direction reported by the radio ('+' or '-').</param>
/// <param name="LastStepAmount">P4: last step amount reported by the radio (0–9).</param>
/// <param name="Meter">P5: preselector coupling meter value (0–255). -1 when unavailable.</param>
/// <param name="Availability">P6: hardware availability state.</param>
/// <param name="RawResponse">Full raw CAT response string, including semicolon. Empty when no response.</param>
/// <param name="IsValid">True when the response was received and all fields parsed successfully.</param>
public sealed record VcTuneReadResult(
    VcTuneReceiver Receiver,
    bool IsOn,
    bool IsDefault,
    char LastDirection,
    int LastStepAmount,
    int Meter,
    VcTuneAvailability Availability,
    string RawResponse,
    bool IsValid)
{
    /// <summary>
    /// Returns a sentinel result representing a missing or unparseable response
    /// for the given receiver.
    /// </summary>
    public static VcTuneReadResult NoResponse(VcTuneReceiver receiver) =>
        new(receiver, false, false, '+', 0, -1,
            VcTuneAvailability.NotInstalled, string.Empty, false);
}
