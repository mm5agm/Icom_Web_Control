namespace Yaesu_Web_Control.Services;

/// <summary>
/// Constructs VT CAT command strings for the FTdx101 VC Tune preselector.
/// <para>
/// All methods are pure string-formatting operations: they validate parameters,
/// assemble the correct character sequence, and return an immutable
/// <see cref="VCTuneCommand"/> record. No I/O is performed and no radio state
/// is read or written.
/// </para>
/// <para>
/// <b>Wire format (confirmed against live radio 2026-07-01)</b><br/>
/// SET commands use the full <c>VT VCT P1,P2,P3,P4;</c> form with literal
/// spaces and commas. P3/P4 are always required even for ON/OFF/DEFAULT;
/// omitting them causes the radio to silently ignore the command.<br/>
/// <c>VT VCT {P1},0,+,0;</c> — OFF<br/>
/// <c>VT VCT {P1},1,+,0;</c> — ON<br/>
/// <c>VT VCT {P1},2,+,0;</c> — DEFAULT / CENTER<br/>
/// <c>VT VCT {P1},1,{dir},{amount};</c> — STEP<br/>
/// READ command: <c>VT{P1};</c> (short form, no spaces)
/// </para>
/// <para>
/// Register as a singleton in DI:
/// <c>services.AddSingleton&lt;IVCTuneCommandBuilder, VCTuneCommandBuilder&gt;();</c>
/// </para>
/// </summary>
public sealed class VCTuneCommandBuilder : IVCTuneCommandBuilder
{
    // ══════════════════════════════════════════════════════════════════════
    // SET commands
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public VCTuneCommand BuildSetOn(VCTuneBand band, bool subInstalled = true)
    {
        GuardBand(band, subInstalled);
        return new VCTuneCommand(
            $"VT VCT {P1Char(band)},1,+,0;",
            VCTuneCommandType.On,
            band);
    }

    /// <inheritdoc/>
    public VCTuneCommand BuildSetOff(VCTuneBand band, bool subInstalled = true)
    {
        GuardBand(band, subInstalled);
        return new VCTuneCommand(
            $"VT VCT {P1Char(band)},0,+,0;",
            VCTuneCommandType.Off,
            band);
    }

    /// <inheritdoc/>
    public VCTuneCommand BuildSetDefault(VCTuneBand band, bool subInstalled = true)
    {
        GuardBand(band, subInstalled);
        return new VCTuneCommand(
            $"VT VCT {P1Char(band)},2,+,0;",
            VCTuneCommandType.Default,
            band);
    }

    /// <inheritdoc/>
    public VCTuneCommand BuildSetStep(
        VCTuneBand band,
        VCTuneDirection direction,
        int amount,
        bool subInstalled = true)
    {
        GuardBand(band, subInstalled);
        GuardDirection(direction);
        GuardStepAmount(amount);

        char dir = DirectionChar(direction);
        return new VCTuneCommand(
            $"VT VCT {P1Char(band)},1,{dir},{amount};",
            VCTuneCommandType.Step,
            band,
            direction,
            amount);
    }

    /// <inheritdoc/>
    public VCTuneCommand BuildSetCenter(VCTuneBand band, bool subInstalled = true)
    {
        GuardBand(band, subInstalled);

        // CENTER uses the DEFAULT frame — the auto-tune sweep re-centres the
        // preselector at the current frequency.
        return new VCTuneCommand(
            $"VT VCT {P1Char(band)},2,+,0;",
            VCTuneCommandType.Center,
            band,
            VCTuneDirection.Plus,
            StepAmount: 0);
    }

    // ══════════════════════════════════════════════════════════════════════
    // READ command
    // ══════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public VCTuneCommand BuildReadStatus(VCTuneBand band, bool subInstalled = true)
    {
        GuardBand(band, subInstalled);

        // READ = VT{P1}; — short form, no P2/P3/P4 needed.
        // Response: VT{P1}{P2}{P5P5P5}{P6} (8 chars, no semicolon).
        return new VCTuneCommand(
            $"VT{P1Char(band)};",
            VCTuneCommandType.Read,
            band);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Private helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the P1 character for the given band.
    /// MAIN → '0', SUB → '1'.
    /// </summary>
    private static char P1Char(VCTuneBand band) =>
        band == VCTuneBand.Sub ? '1' : '0';

    /// <summary>
    /// Returns the P3 character for the given direction.
    /// Plus → '+', Minus → '-'.
    /// </summary>
    private static char DirectionChar(VCTuneDirection direction) =>
        direction == VCTuneDirection.Plus ? '+' : '-';

    /// <summary>
    /// Validates a <see cref="VCTuneBand"/> value and enforces the
    /// SUB-installed precondition when applicable.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="band"/> is not a defined enum member.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="band"/> is <see cref="VCTuneBand.Sub"/> and
    /// <paramref name="subInstalled"/> is <see langword="false"/>.
    /// </exception>
    private static void GuardBand(VCTuneBand band, bool subInstalled)
    {
        if (!Enum.IsDefined(band))
            throw new ArgumentOutOfRangeException(
                nameof(band), band,
                $"'{band}' is not a valid VCTuneBand value.");

        if (band == VCTuneBand.Sub && !subInstalled)
            throw new InvalidOperationException(
                "Cannot build a SUB VC Tune command: the caller has indicated that " +
                "the SUB VC Tune option board is not installed on this receiver.");
    }

    /// <summary>
    /// Validates a <see cref="VCTuneDirection"/> value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="direction"/> is not a defined enum member.
    /// </exception>
    private static void GuardDirection(VCTuneDirection direction)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(
                nameof(direction), direction,
                $"'{direction}' is not a valid VCTuneDirection value.");
    }

    /// <summary>
    /// Validates a step amount value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="amount"/> is outside the range 0–9.
    /// </exception>
    private static void GuardStepAmount(int amount)
    {
        if (amount < 0 || amount > 9)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount,
                "Step amount must be in the range 0–9.");
    }
}
