namespace Icom_Web_Control;

public static class AppVersion
{
    // Fresh IWC version line — IWC starts its own numbering at 1.0.0, NOT
    // continued from the inherited YWC 2.4.x lineage.
    //
    // Current is the NUMERIC CORE only, with no pre-release suffix. Keep it that
    // way: finish-release.ps1 checks it against the same three-part number in
    // installer.nsi and the csproj, and those two cannot carry a suffix at all
    // (NSIS and AssemblyVersion both want X.Y.Z). Put the suffix in PreRelease.
    public const string Current = "1.0.6";

    /// <summary>
    /// Pre-release suffix without the leading hyphen ("pre4"), or empty for a
    /// full release. Set by finish-release.ps1 from the tag, so it cannot drift
    /// from what was actually shipped.
    ///
    /// This exists because v1.0.6-pre1, -pre2 and -pre3 all reported themselves
    /// as plain "v1.0.6": a tester could not tell which build they were running,
    /// and neither could we when reading their bug report.
    /// </summary>
    public const string PreRelease = "pre4";

    /// <summary>
    /// What the user is shown, and the only version string that should appear in
    /// the UI, the tray, the logs or a bug report: "1.0.6" or "1.0.6-pre4".
    /// </summary>
    public static string Display =>
        PreRelease.Length == 0 ? Current : $"{Current}-{PreRelease}";

    /// <summary>Date this version was released, ISO format.
    /// Bump on actual release; current value reflects the planned ship date.</summary>
    public const string ReleaseDate = "2026-08-10";

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
