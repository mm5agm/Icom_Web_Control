namespace Icom_Web_Control;

public static class AppVersion
{
    // Fresh IWC version line — IWC starts its own numbering at 0.x (pre-alpha),
    // NOT continued from the inherited YWC 2.4.x lineage. "-alpha" signals to
    // first-time Facebook downloaders that this is early-preview software.
    public const string Current = "0.1.0-alpha";

    /// <summary>Date this version was released, ISO format.
    /// Bump on actual release; current value reflects the planned ship date.</summary>
    public const string ReleaseDate = "2026-07-29";

    /// <summary>
    /// Firmware version(s) of the developer's bench radio at the time this IWC
    /// build was cut. Shown on the About page and included in the diagnostics
    /// block so bug reporters can compare against the firmware IWC was tested
    /// on. Some behaviours can depend on the radio's firmware, so having this
    /// visible lets a user spot "I'm on different firmware to Colin, that might
    /// be why my behaviour differs".
    ///
    /// Find your own value:
    ///   IC-7300 MkII: MENU -> SET -> Others -> Version Information (front panel).
    /// Update this dictionary whenever the bench radio's firmware moves.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TestedFirmware =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["IC-7300 MkII"] = new Dictionary<string, string>
            {
                ["Main CPU"]     = "1.02",
                ["Front CPU"]    = "1.01",
                ["DSP Program"]  = "1.01",
                ["DSP Data"]     = "1.00",
                ["FPGA"]         = "1.01",
            },
        };
}
