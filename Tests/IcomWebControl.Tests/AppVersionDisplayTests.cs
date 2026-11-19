using Icom_Web_Control;
using Xunit;

namespace IcomWebControl.Tests;

/// <summary>
/// AppVersion.Display is what the About page, the tray, the log, bug reports
/// and the update check use. CI bakes the git tag in; these pin the shapes it
/// can take. v1.7.0-pre13 to -pre15 all said "pre12" because the suffix was a
/// constant nobody updated, and the update check kept offering them pre15.
/// </summary>
public class AppVersionDisplayTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void LocalBuild_NoTag_FullRelease_ShowsCurrent(string tag, string pre)
        => Assert.Equal("1.7.0", AppVersion.ComputeDisplay("1.7.0", pre, tag));

    [Fact]
    public void LocalBuild_NoTag_UsesThePreReleaseConstant()
        => Assert.Equal("1.7.0-pre16", AppVersion.ComputeDisplay("1.7.0", "pre16", ""));

    [Theory]
    [InlineData("v1.7.0")]
    [InlineData("V1.7.0")]
    [InlineData("1.7.0")]
    public void FullRelease_TagMatchesCurrent_ShowsBareVersion(string tag)
        => Assert.Equal("1.7.0", AppVersion.ComputeDisplay("1.7.0", "pre12", tag));

    [Theory]
    [InlineData("v1.7.0-pre16", "1.7.0-pre16")]
    [InlineData(" v1.7.0-pre16 ", "1.7.0-pre16")]
    [InlineData("v1.7.0-rc1", "1.7.0-rc1")]
    public void PreRelease_TheTagWins_OverAStaleConstant(string tag, string expected)
        => Assert.Equal(expected, AppVersion.ComputeDisplay("1.7.0", "pre12", tag));

    [Theory]
    [InlineData("v1.8.0", "1.7.0 (v1.8.0)")]
    [InlineData("v1.7.01-pre1", "1.7.0 (v1.7.01-pre1)")]
    [InlineData("unstable-20261119", "1.7.0 (unstable-20261119)")]
    public void MismatchedTag_IsShownBesideCurrent_NotHidden(string tag, string expected)
        => Assert.Equal(expected, AppVersion.ComputeDisplay("1.7.0", "", tag));

    [Fact]
    public void Display_IsDerivedFromCurrentAndBuildTag()
        => Assert.Equal(
            AppVersion.ComputeDisplay(AppVersion.Current, AppVersion.PreRelease, AppVersion.BuildTag),
            AppVersion.Display);
}
