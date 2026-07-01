namespace Yaesu_Web_Control.Services;

/// <summary>
/// VC Tune hardware availability state. Maps directly to the P6 field
/// returned in VT CAT responses.
/// </summary>
public enum VcTuneAvailability
{
    /// <summary>P6 = 0. The VC Tune option board is not fitted on this receiver.</summary>
    NotInstalled = 0,

    /// <summary>P6 = 1. The option board is fitted and the current frequency is within range.</summary>
    Available = 1,

    /// <summary>P6 = 2. The option board is fitted but the current frequency is outside its range.</summary>
    UnavailableFrequency = 2
}
