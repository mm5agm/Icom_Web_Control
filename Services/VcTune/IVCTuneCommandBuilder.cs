namespace Yaesu_Web_Control.Services;

/// <summary>
/// Contract for the VC Tune CAT command builder.
/// Constructs fully formatted VT command strings and returns them as
/// immutable <see cref="VCTuneCommand"/> records.
/// Does not send commands, parse responses, or check radio state.
/// </summary>
public interface IVCTuneCommandBuilder
{
    /// <summary>
    /// Builds a SET ON command: <c>VT{P1}1;</c>
    /// </summary>
    /// <param name="band">Target receiver (MAIN or SUB).</param>
    /// <param name="subInstalled">
    /// Pass <see langword="false"/> when the SUB option board is known not to be
    /// fitted; the builder will throw before constructing any string.
    /// Ignored when <paramref name="band"/> is <see cref="VCTuneBand.Main"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="band"/> is not a defined enum value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="band"/> is <see cref="VCTuneBand.Sub"/> and
    /// <paramref name="subInstalled"/> is <see langword="false"/>.
    /// </exception>
    VCTuneCommand BuildSetOn(VCTuneBand band, bool subInstalled = true);

    /// <summary>
    /// Builds a SET OFF command: <c>VT{P1}0;</c>
    /// </summary>
    /// <inheritdoc cref="BuildSetOn"/>
    VCTuneCommand BuildSetOff(VCTuneBand band, bool subInstalled = true);

    /// <summary>
    /// Builds a SET DEFAULT (auto-tune) command: <c>VT{P1}2;</c>
    /// </summary>
    /// <inheritdoc cref="BuildSetOn"/>
    VCTuneCommand BuildSetDefault(VCTuneBand band, bool subInstalled = true);

    /// <summary>
    /// Builds a STEP command: <c>VT{P1}{direction}{amount};</c>
    /// </summary>
    /// <param name="band">Target receiver (MAIN or SUB).</param>
    /// <param name="direction">Motor step direction.</param>
    /// <param name="amount">Step amount 0–9.</param>
    /// <param name="subInstalled">
    /// Pass <see langword="false"/> when the SUB option board is known not to be fitted.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="band"/> or <paramref name="direction"/> is not a
    /// defined enum value, or when <paramref name="amount"/> is outside 0–9.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="band"/> is <see cref="VCTuneBand.Sub"/> and
    /// <paramref name="subInstalled"/> is <see langword="false"/>.
    /// </exception>
    VCTuneCommand BuildSetStep(VCTuneBand band, VCTuneDirection direction, int amount, bool subInstalled = true);

    /// <summary>
    /// Builds a CENTER command: <c>VT{P1}+0;</c>
    /// Equivalent to a step with direction = Plus and amount = 0.
    /// </summary>
    /// <inheritdoc cref="BuildSetOn"/>
    VCTuneCommand BuildSetCenter(VCTuneBand band, bool subInstalled = true);

    /// <summary>
    /// Builds a READ STATUS command: <c>VT{P1};</c>
    /// The radio responds with the full VT status frame containing P1–P6.
    /// </summary>
    /// <inheritdoc cref="BuildSetOn"/>
    VCTuneCommand BuildReadStatus(VCTuneBand band, bool subInstalled = true);
}
