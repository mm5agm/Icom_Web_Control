using Icom_Web_Control.Models;
using Icom_Web_Control.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IcomWebControl.Tests;

/// <summary>
/// The server names the band from the operator's own region. Until v1.7.0-pre10
/// it never read the region at all: the "checked recently" test overflowed on the
/// first call, so every operator got Region 1 and a Region 2 IC-9700 showed out of
/// band above 146 MHz (IWC #137). These run against the built-in edges (no web
/// root), which match bandplan.default.json for the bands used here.
/// </summary>
public class BandPlanRegionTests
{
    private static BandPlanService Service(string bandPlan)
    {
        var settings = new TestHost.MemorySettings();
        settings.Settings.BandPlan = bandPlan;
        return new BandPlanService(new NoWebRoot(), settings, NullLogger<BandPlanService>.Instance);
    }

    [Theory]
    [InlineData("Region1", "Region1")]
    [InlineData("Region2", "Region2")]
    [InlineData("Region3", "Region3")]
    [InlineData("USA", "Region2")]
    [InlineData("UK", "Region1")]
    public void TheConfiguredRegionIsUsedFromTheFirstCall(string setting, string expected)
    {
        Assert.Equal(expected, Service(setting).CurrentRegion);
    }

    [Theory]
    [InlineData("Region2", 146_200_000L, "2m")]
    [InlineData("Region2", 3_900_000L, "80m")]
    [InlineData("Region1", 146_200_000L, BandPlanService.UnknownBand)]
    [InlineData("Region1", 3_900_000L, BandPlanService.UnknownBand)]
    public void TheBandComesFromTheConfiguredRegion(string setting, long hz, string expected)
    {
        Assert.Equal(expected, Service(setting).BandForFrequency(hz));
    }

    private sealed class NoWebRoot : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }
}
