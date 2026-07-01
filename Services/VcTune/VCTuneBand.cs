namespace Yaesu_Web_Control.Services;

/// <summary>
/// Target receiver / band selector for VC Tune commands.
/// Maps to the P1 field in the VT CAT command.
/// This is the command-builder layer's representation of the receiver;
/// <see cref="VcTuneReceiver"/> is the equivalent type used in the service layer.
/// Both enums share the same integer values (Main = 0, Sub = 1) so conversion
/// is a simple cast: <c>(VCTuneBand)(int)receiver</c>.
/// </summary>
public enum VCTuneBand
{
    /// <summary>MAIN receiver. P1 = '0'. Always supported on FTdx101D and FTdx101MP.</summary>
    Main = 0,

    /// <summary>
    /// SUB receiver. P1 = '1'. Requires the VC Tune option board on the FTdx101MP.
    /// Hardware presence is confirmed at runtime via the P6 field in VT responses;
    /// the command builder accepts a <c>subInstalled</c> hint from the caller.
    /// </summary>
    Sub = 1
}
