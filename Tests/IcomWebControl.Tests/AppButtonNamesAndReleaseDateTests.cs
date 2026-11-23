using Icom_Web_Control;
using Icom_Web_Control.Models;

namespace IcomWebControl.Tests;

// YWC #420 (pre28): a launch button saved with an empty name was a thin red
// line, and Diagnostics showed the planned date, not the release date.
public class AppButtonNamesAndReleaseDateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankButtonName_FallsBackToTheDefault(string? blank)
    {
        var s = new ApplicationSettings
        {
            App1Name = blank!, App2Name = blank!, App3Name = blank!,
            App4Name = blank!, App5Name = blank!, App6Name = blank!,
        };
        Assert.Equal("WSJT-X", s.App1Name);
        Assert.Equal("JTAlert", s.App2Name);
        Assert.Equal("Log4OM", s.App3Name);
        Assert.Equal("GridTracker", s.App4Name);
        Assert.Equal("Fldigi", s.App5Name);
        Assert.Equal("JS8Call", s.App6Name);
    }

    [Fact]
    public void BlankNameInSavedSettings_IsMendedOnLoad()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<ApplicationSettings>("{\"App3Name\":\"\"}")!;
        Assert.Equal("Log4OM", s.App3Name);
    }

    [Fact]
    public void ReleaseDate_IsTheBuildDayForACiBuild()
    {
        Assert.Equal("2026-11-23", AppVersion.ComputeReleaseDate("v1.7.0-pre25", "2026-11-23"));
        Assert.Equal(AppVersion.PlannedReleaseDate, AppVersion.ComputeReleaseDate("", "2026-11-23"));
    }
}
