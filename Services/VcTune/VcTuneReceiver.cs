namespace Yaesu_Web_Control.Services;

/// <summary>
/// VC Tune target receiver. Maps to the P1 field in the VT CAT command.
/// </summary>
public enum VcTuneReceiver
{
    /// <summary>MAIN receiver (P1 = 0). Always supported on FTdx101D and FTdx101MP.</summary>
    Main = 0,

    /// <summary>
    /// SUB receiver (P1 = 1). Requires the optional VC Tune board on the FTdx101MP.
    /// Confirmed available only when the radio returns P6 = 1.
    /// </summary>
    Sub = 1
}
