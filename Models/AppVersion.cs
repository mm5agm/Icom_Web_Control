using System.Reflection;

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
    public const string Current = "1.7.0";

    /// <summary>
    /// Pre-release suffix without the leading hyphen ("pre4"), or empty for a
    /// full release. Set by finish-release.ps1 from the tag. Only a local build
    /// shows it now: a CI build shows <see cref="BuildTag"/> instead, because
    /// v1.7.0-pre13, -pre14 and -pre15 were released without the script and
    /// all said "pre12", and the update check kept offering them pre15.
    ///
    /// This exists because v1.0.6-pre1, -pre2 and -pre3 all reported themselves
    /// as plain "v1.0.6": a tester could not tell which build they were running,
    /// and neither could we when reading their bug report.
    /// </summary>
    public const string PreRelease = "pre29";

    /// <summary>
    /// The git tag CI built from ("v1.7.0-pre16", "v1.7.0"), or "" for a local
    /// build. The workflow passes it as <c>-p:IwcBuildTag=</c> and the csproj
    /// bakes it in as assembly metadata, so nothing has to be edited by hand.
    /// </summary>
    public static readonly string BuildTag =
        typeof(AppVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "IwcBuildTag")?.Value?.Trim() ?? "";

    /// <summary>
    /// What the user is shown, and the only version string that should appear in
    /// the UI, the tray, the logs or a bug report: "1.0.6" or "1.0.6-pre4".
    /// </summary>
    public static readonly string Display = ComputeDisplay(Current, PreRelease, BuildTag);

    /// <summary>
    /// With no tag (a local build): Current plus the PreRelease constant. With a
    /// tag: "1.7.0" for v1.7.0, "1.7.0-pre16" for v1.7.0-pre16, and a tag that
    /// does not belong to Current at all shown beside it ("1.7.0 (v1.8.0)")
    /// rather than hidden, because that is a release-prep mistake worth seeing.
    /// </summary>
    internal static string ComputeDisplay(string current, string preRelease, string buildTag)
    {
        if (string.IsNullOrWhiteSpace(buildTag))
            return preRelease.Length == 0 ? current : $"{current}-{preRelease}";

        string tag = buildTag.Trim();
        string bare = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;

        if (bare == current)
            return current;
        if (bare.StartsWith(current + "-", StringComparison.Ordinal))
            return bare;
        return $"{current} ({tag})";
    }

    /// <summary>Date this version was released, ISO format. A CI build (one
    /// with a tag) uses the day it was built, which is the day it was
    /// released; before, every pre-release showed the planned date below
    /// (YWC #420). A local build shows <see cref="PlannedReleaseDate"/>.</summary>
    public static readonly string ReleaseDate = ComputeReleaseDate(BuildTag,
        typeof(AppVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "IwcBuildDate")?.Value?.Trim() ?? "");

    /// <summary>The planned ship date, for local builds. Bump on release.</summary>
    public const string PlannedReleaseDate = "2026-11-05";

    internal static string ComputeReleaseDate(string buildTag, string buildDate) =>
        !string.IsNullOrWhiteSpace(buildTag) && System.Text.RegularExpressions.Regex.IsMatch(buildDate, @"^\d{4}-\d{2}-\d{2}$")
            ? buildDate
            : PlannedReleaseDate;

    /// <summary>
    /// Firmware version(s) of the developer's bench radio at the time this IWC
    /// build was cut. Shown on the About page and included in the diagnostics
    /// block so bug reporters can compare against the firmware IWC was tested
    /// on. Some behaviours can depend on the radio's firmware, so having this
    /// visible lets a user spot "I'm on different firmware to Colin, that might
    /// be why my behaviour differs".
    ///
    /// Find your own value:
    ///   IC-7300 MkII: MENU -> SET -> Others -> Information -> Version (front panel).
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
