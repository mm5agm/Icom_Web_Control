using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The frequencies, bands and modes that differ by radio. The IC-7100 figures are
/// from section 21 of its manual; the IC-7300 ones must stay what they were.
/// </summary>
public class RadioFrequenciesTests
{
    private const byte Ic7100 = 0x88;
    private const byte Ic7300 = 0x94;
    private const byte CmdVfoMode = 0x26;

    [Theory]
    [InlineData(29_999, false)]
    [InlineData(30_000, true)]
    [InlineData(74_800_000, true)]
    [InlineData(74_800_001, false)]
    [InlineData(145_000_000, false)]
    public void TheIc7300StopsAt74MHz(long hz, bool expected)
    {
        Assert.Equal(expected, RadioProfile.Ic7300.CanTune(hz));
        Assert.Equal(expected, RadioProfile.Ic7300Mk2.CanTune(hz));
    }

    [Theory]
    [InlineData(29_999, false)]
    [InlineData(30_000, true)]
    [InlineData(145_000_000, true)]
    [InlineData(199_999_999, true)]
    [InlineData(200_000_000, false)]   // the gap
    [InlineData(399_999_999, false)]
    [InlineData(400_000_000, true)]
    [InlineData(433_500_000, true)]
    [InlineData(470_000_000, true)]
    [InlineData(470_000_001, false)]
    public void TheIc7100CoversVhfAndUhfWithAGap(long hz, bool expected) =>
        Assert.Equal(expected, RadioProfile.Ic7100.CanTune(hz));

    [Fact]
    public void AnUnknownRadioKeepsTheOldLimits()
    {
        Assert.Same(RadioProfile.Ic7300Mk2, RadioProfile.OrDefault(null));
        Assert.Same(RadioProfile.Ic7300Mk2, RadioProfile.OrDefault("Icom(99)"));
        Assert.Same(RadioProfile.Ic7100, RadioProfile.OrDefault("IC-7100"));
    }

    [Theory]
    [InlineData("WFM")]
    [InlineData("DV")]
    public void OnlyTheIc7100HasWfmAndDv(string mode)
    {
        Assert.True(RadioProfile.Ic7100.HasMode(mode));
        Assert.False(RadioProfile.Ic7300.HasMode(mode));
        Assert.False(RadioProfile.Ic7300Mk2.HasMode(mode));
    }

    [Theory]
    [InlineData("USB")]
    [InlineData("DATA-FM")]
    public void EveryRadioHasTheOrdinaryModes(string mode)
    {
        Assert.True(RadioProfile.Ic7100.HasMode(mode));
        Assert.True(RadioProfile.Ic7300.HasMode(mode));
    }

    [Theory]
    [InlineData(Ic7100, "WFM", true)]
    [InlineData(Ic7100, "DV", true)]
    [InlineData(Ic7300, "WFM", false)]
    [InlineData(Ic7300, "DV", false)]
    public async Task ModeFramesOnlyGoToARadioWithThatMode(byte address, string mode, bool sent)
    {
        var bus = new SimulatedRadioClient(address);
        var (controller, _, _, _) = TestHost.NewControllerWithHub(bus);
        await controller.ConnectAsync();
        bus.Sent.Clear();

        await controller.SetModeAsync(RadioVfo.A, mode);

        byte modeByte = mode == "WFM" ? (byte)0x06 : (byte)0x17;
        bool went = bus.Sent.Any(f => f.Length > 7 && f[4] == CmdVfoMode && f[6] == modeByte);
        Assert.Equal(sent, went);
    }

    [Fact]
    public void Ic7300RigctldRangesAreUnchanged()
    {
        var lines = RigctldServer.DumpState(RadioProfile.Ic7300Mk2).Split('\n');
        Assert.Equal("30000.000000 74800000.000000 0x1ff -1 -1 0x10000003 0x3", lines[2]);
        Assert.Equal("0 0 0 0 0 0 0", lines[3]);
        Assert.Equal("1800000.000000 29700000.000000 0x1ff 2 100 0x10000003 0x3", lines[4]);
        Assert.Equal("50000000.000000 54000000.000000 0x1ff 2 100 0x10000003 0x3", lines[5]);
        Assert.Equal("0 0 0 0 0 0 0", lines[6]);
        Assert.Equal("0 0", lines[7]);
    }

    [Fact]
    public void Ic7100RigctldRangesCoverItsBands()
    {
        var dump = RigctldServer.DumpState(RadioProfile.Ic7100);
        Assert.Contains("30000.000000 199999999.000000 0x1ff -1 -1", dump);
        Assert.Contains("400000000.000000 470000000.000000 0x1ff -1 -1", dump);
        Assert.Contains("144000000.000000 148000000.000000 0x1ff 2 50 ", dump);
        Assert.Contains("430000000.000000 450000000.000000 0x1ff 2 35 ", dump);
    }

    [Fact]
    public void HamlibKnowsEachRadioByItsOwnNumber()
    {
        Assert.Equal(("IC-7100", 3070), RadioProfile.Ic7100.Hamlib);
        Assert.Equal(("IC-7300", 3073), RadioProfile.Ic7300.Hamlib);
        Assert.Equal(("IC-7300", 3073), RadioProfile.Ic7300Mk2.Hamlib);
    }
}
