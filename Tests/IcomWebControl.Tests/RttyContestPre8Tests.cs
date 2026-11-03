using Icom_Web_Control.Models;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Rtty;

namespace IcomWebControl.Tests;

/// <summary>
/// YWC v2.5.4-pre8's RTTY Contest additions, ported: a page named on the
/// command line, and an IF width set by hand kept for its mode.
/// </summary>
public class RttyContestPre8Tests
{
    [Fact]
    public void LaunchPageOpensANamedPageUnderTheRootOrElseStart()
    {
        LaunchPage.ReadFrom(new[] { "--urls=x", "RTTY-Contest" });
        Assert.Equal("https://localhost:8443/rtty-contest", LaunchPage.At("https://localhost:8443/"));

        LaunchPage.ReadFrom(new[] { "/classic" });
        Assert.Equal("http://localhost:5000/", LaunchPage.At("http://localhost:5000"));

        LaunchPage.ReadFrom(new[] { "nonsense" });
        Assert.Null(LaunchPage.Path);
        Assert.Equal("http://localhost:5000/start", LaunchPage.At("http://localhost:5000"));
    }

    [Fact]
    public void AKeptWidthIsForItsModelAndMode()
    {
        var s = new ApplicationSettings { RadioModel = "IC-7300" };
        s.RttyTunerWidths["IC-7300|RTTY-L"] = 350;
        Assert.Equal(350, RttyTunerModeService.KeptWidth(s, "RTTY-L"));
        Assert.Null(RttyTunerModeService.KeptWidth(s, "DATA-L"));
        s.RadioModel = "IC-7300MK2";
        Assert.Null(RttyTunerModeService.KeptWidth(s, "RTTY-L"));
    }
}
