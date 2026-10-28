using Icom_Web_Control.Models;
using Icom_Web_Control.Services.Rtty;

namespace IcomWebControl.Tests;

/// <summary>
/// The operator's RTTY mode: what the tuner switches to and holds. YWC's
/// tests, ported with the setting: a tuner that always put the radio back in
/// RTTY would take an AFSK operator out of DATA.
/// </summary>
public class RttyTunerModeChoiceTests
{
    [Theory]
    [InlineData("RTTY-L", "RTTY-L")]
    [InlineData("rtty-u", "RTTY-U")]
    [InlineData(" DATA-L ", "DATA-L")]
    [InlineData("DATA-U", "DATA-U")]
    [InlineData("USB", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyTheFourModesAreChoices(string? given, string? expected)
    {
        Assert.Equal(expected, RttyTunerModeService.Normalise(given));
    }

    [Theory]
    [InlineData("DATA-L", "DATA-L")]
    [InlineData("CW-U", "RTTY-L")]
    [InlineData("", "RTTY-L")]
    public void AnythingElseIsRttyL(string chosen, string expected)
    {
        var s = new ApplicationSettings { RttyTunerMode = chosen };
        Assert.Equal(expected, RttyTunerModeService.TargetMode(s));
    }

    [Fact]
    public void TheDefaultIsRttyL()
    {
        Assert.Equal("RTTY-L", RttyTunerModeService.TargetMode(new ApplicationSettings()));
    }

    [Theory]
    // RTTY chosen: either FSK mode is left alone, as before.
    [InlineData("RTTY-L", "RTTY-L", false)]
    [InlineData("RTTY-U", "RTTY-L", false)]
    [InlineData("DATA-L", "RTTY-L", true)]
    [InlineData("USB", "RTTY-L", true)]
    [InlineData("CW-U", "RTTY-L", true)]
    // DATA chosen: either DATA mode is left alone, and RTTY is switched.
    [InlineData("DATA-L", "DATA-L", false)]
    [InlineData("DATA-U", "DATA-L", false)]
    [InlineData("RTTY-L", "DATA-L", true)]
    [InlineData("LSB", "DATA-L", true)]
    [InlineData("DATA-FM", "DATA-U", true)]
    [InlineData(null, "DATA-L", true)]
    public void SwitchesOnlyOutOfAnotherKind(string? mode, string target, bool switches)
    {
        Assert.Equal(switches, RttyTunerModeService.NeedsSwitching(mode, target));
    }

    [Theory]
    // RTTY's ladder stops at 2700; SSB's, which DATA uses, at 3600.
    [InlineData(269, RttyTunerModeService.RttyWidestHz, 300)]
    [InlineData(1250, RttyTunerModeService.RttyWidestHz, 1300)]
    [InlineData(3000, RttyTunerModeService.RttyWidestHz, 2700)]
    [InlineData(3000, RttyTunerModeService.SsbWidestHz, 3000)]
    [InlineData(4000, RttyTunerModeService.SsbWidestHz, 3600)]
    public void RungsStopAtTheModesWidest(int wanted, int widest, int expected)
    {
        Assert.Equal(expected, RttyTunerModeService.RungAtOrAbove(wanted, widest));
    }

    [Theory]
    // The menu mark is the low end of the pair in both modes (DDK9 and a
    // noise sweep, 2026-10-28); the tuner's RTTY-R mark is the high end.
    [InlineData("RTTY-L", 2125, 170, 2125)]
    [InlineData("RTTY-U", 2125, 170, 2295)]
    [InlineData("RTTY-U", 2125, 425, 2550)]
    [InlineData("DATA-U", 2125, 425, 2125)]
    public void RttyRMarkIsTheTopOfTheMenuPair(string mode, int menuMark, int shift, int tunerMark)
    {
        Assert.Equal(tunerMark, RttyTunerModeService.TunerMarkFromMenu(mode, menuMark, shift));
        Assert.Equal(menuMark, RttyTunerModeService.MenuMarkFromTuner(mode, tunerMark, shift));
    }

    [Theory]
    // Auto never writes the menu shift (the radio transmits FSK on it); it says
    // when a signal's shift is not the menu's.
    [InlineData(451, 170, true)]
    [InlineData(200, 170, true)]
    [InlineData(176, 170, false)]
    [InlineData(445, 425, false)]
    [InlineData(451, null, false)]
    public void AutoSaysWhenTheShiftIsNotTheMenus(int signal, int? menu, bool said)
    {
        Assert.Equal(said, RttyAutoService.ShiftNote(signal, menu) is not null);
    }
}
