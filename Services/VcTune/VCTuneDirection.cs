namespace Yaesu_Web_Control.Services;

/// <summary>
/// Step direction for VC Tune motor commands. Maps to the P3 field in the VT CAT command.
/// <see cref="Plus"/> produces the character <c>'+'</c>; <see cref="Minus"/> produces <c>'-'</c>.
/// </summary>
public enum VCTuneDirection
{
    /// <summary>Move the capacitor forward. P3 = '+'.</summary>
    Plus = 0,

    /// <summary>Move the capacitor backward. P3 = '-'.</summary>
    Minus = 1
}
