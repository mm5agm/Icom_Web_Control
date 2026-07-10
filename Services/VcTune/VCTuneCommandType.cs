namespace Yaesu_Web_Control.Services;

/// <summary>
/// Identifies the kind of VT CAT command represented by a <see cref="VCTuneCommand"/>.
/// </summary>
public enum VCTuneCommandType
{
    /// <summary>Engage VC Tune preselector. Wire character after P1 = '1' (P2 = 1).</summary>
    On,

    /// <summary>Disengage VC Tune preselector. Wire character after P1 = '0' (P2 = 0).</summary>
    Off,

    /// <summary>
    /// Run auto-tune routine. Wire character after P1 = '2' (P2 = 2). The radio
    /// sweeps the capacitor to find the peak coupling position and then reports
    /// On (P2 = 1) or Off (P2 = 0) in its next status response.
    /// </summary>
    Default,

    /// <summary>
    /// Step the capacitor by a fixed amount in a given direction.
    /// Wire format after P1 = P3 ('+'/'-') + P4 (digit 1–9).
    /// </summary>
    Step,

    /// <summary>
    /// Move the capacitor to the center position. Wire format after P1 = '+0'.
    /// Equivalent to a Step command with direction = Plus and amount = 0.
    /// </summary>
    Center,

    /// <summary>
    /// Read current status. Wire format = VT{P1};. The radio responds with all
    /// P1–P6 fields in the full VT response format.
    /// </summary>
    Read
}
