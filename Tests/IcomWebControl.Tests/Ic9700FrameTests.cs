using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// What an IC-9700 is sent. The bytes are checked at the bus, below the guard,
/// so these see what would reach the radio. The replies are a recorded MkII's;
/// nothing here has been near a real 9700.
/// </summary>
public class Ic9700FrameTests
{
    private const byte Address = 0xA2;

    /// <summary>The body of a frame: what follows the controller's address.</summary>
    private static string Body(string frame) => frame[(frame.IndexOf(" E0 ") + 4)..];

    [Fact]
    public async Task ItIsNeverSentACommandItDoesNotHave()
    {
        // Written out rather than taken from the profile, so that a slip in the
        // allow-list fails here.
        string[] forbidden =
        {
            "28 ",      // voice keyer: not until tested
            "07 B0 ",   // on a 9700 this swaps Main and Sub
            "07 D1 ",   // Sub band: IWC drives Main only
            "11 20 ",   // 20 dB attenuator: it has 10 dB
            "16 32 ",   // APF
            "16 5A ",   // satellite mode
            "1A 00 ",   // memories: a layout IWC doesn't read yet
            "1C 01 ",   // tuner: it has none
            "21 02 ",   // delta TX
        };

        var actions = await RadioActionRecorder.RecordAll(Address);
        Assert.Contains(actions, a => a.Frames.Count > 0);
        foreach (var action in actions)
            foreach (var frame in action.Frames)
                Assert.False(forbidden.Any(Body(frame).StartsWith), $"{action.Label} sent {frame}");

        // Menu items: only the USB SEND and keying ones, 120 to 122, and only read.
        var menuFrames = actions.SelectMany(a => a.Frames.Select(f => (a.Label, Body: Body(f))))
            .Where(x => x.Body.StartsWith("1A 05 ")).ToList();
        foreach (var (label, body) in menuFrames)
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(body, "^1A 05 01 2[0-2] FD$"),
                $"{label} sent {body}");
    }

    [Fact]
    public async Task ItIsIdentifiedAndConnects()
    {
        var (controller, state) = TestHost.NewController(new SimulatedRadioClient(Address));
        Assert.True(await controller.ConnectAsync());
        Assert.Equal("IC-9700", controller.ModelId);
        Assert.Equal("IC-9700", state.RadioModel);
    }

    [Fact]
    public async Task ConnectSelectsTheMainBandAndScope()
    {
        var bus = new SimulatedRadioClient(Address);
        var (controller, _) = TestHost.NewController(bus);
        await controller.ConnectAsync();
        Assert.Contains("FE FE A2 E0 07 D0 FD", bus.SentHex);
        Assert.Contains("FE FE A2 E0 27 12 00 FD", bus.SentHex);
    }

    [Fact]
    public async Task A7300IsNotSentTheMainBandSelect()
    {
        var bus = new SimulatedRadioClient(0xB6);
        var (controller, _) = TestHost.NewController(bus);
        await controller.ConnectAsync();
        Assert.DoesNotContain(bus.SentHex, f => f.Contains(" 07 D0 ") || f.Contains(" 27 12 "));
    }

    [Fact]
    public async Task TheAttenuatorIs10dB()
    {
        var bus = new SimulatedRadioClient(Address);
        var (controller, _) = TestHost.NewController(bus);
        await controller.ConnectAsync();
        bus.Sent.Clear();
        await controller.SetAttenuatorAsync(true);
        Assert.Equal("FE FE A2 E0 11 10 FD", Assert.Single(bus.SentHex));
    }

    [Theory]
    [InlineData("FE FE A2 E0 1A 05 01 20 FD", true)]    // USB SEND
    [InlineData("FE FE A2 E0 1A 05 01 22 FD", true)]    // USB Keying (RTTY)
    [InlineData("FE FE A2 E0 1A 05 00 96 FD", false)]   // the MkII's USB SEND: something else here
    [InlineData("FE FE A2 E0 1A 05 01 FD", false)]      // item cut short
    [InlineData("FE FE A2 E0 1A 00 01 00 01 FD", false)] // memory read: not yet
    [InlineData("FE FE A2 E0 27 15 00 00 25 00 00 FD", true)] // scope span
    [InlineData("FE FE A2 E0 27 19 00 00 00 FD", false)] // scope reference level: not used yet
    [InlineData("FE FE A2 E0 07 D0 FD", true)]          // select Main
    [InlineData("FE FE A2 E0 07 B0 FD", false)]         // swap Main and Sub
    public void TheListReadsTheWholeFrame(string hex, bool allowed) =>
        Assert.Equal(allowed, RadioProfile.Ic9700.Allows(CivRecording.Bytes(hex)));

    [Fact]
    public void ItsProfileSaysWhatItIs()
    {
        var p = RadioProfile.Ic9700;
        Assert.Same(p, RadioProfile.ForModel("IC-9700"));
        Assert.Equal("IC-9700", RadioProfile.ModelNameForId(0xA2));
        Assert.Contains((byte)0xA2, RadioProfile.AddressesToTry(null));
        Assert.Equal(0xA2, RadioProfile.AddressesToTry("IC-9700")[0]);
        Assert.Equal(("IC-9700", 3081), p.Hamlib);
        Assert.True(p.HasScope);
        Assert.True(p.HasSubBand);
        Assert.False(RadioProfile.Ic7300Mk2.HasSubBand);
        Assert.False(p.HasRadioMemories);
        Assert.False(p.HasApf);
        Assert.False(p.HasDeltaTx);
        Assert.Equal(10, p.AttenuatorDb);
        Assert.Equal(new[] { 120, 121, 122 }, p.UsbLineItems.Select(i => i.Item));
    }

    [Theory]
    [InlineData(145_500_000L, true)]
    [InlineData(432_200_000L, true)]
    [InlineData(1_296_200_000L, true)]
    [InlineData(14_074_000L, false)]
    [InlineData(200_000_000L, false)]
    public void ItTunesOnlyItsOwnBands(long hz, bool canTune) =>
        Assert.Equal(canTune, RadioProfile.Ic9700.CanTune(hz));

    [Theory]
    [InlineData(144_174_000L, 100)]
    [InlineData(432_174_000L, 75)]
    [InlineData(1_296_174_000L, 10)]
    public void PowerFollowsTheBand(long hz, int watts) =>
        Assert.Equal(watts, RadioProfile.Ic9700.MaxWattsAt(hz));

    [Fact]
    public void ItHasOnePreampEverywhere()
    {
        Assert.Equal(1, RadioProfile.Ic9700.PreampSteps(144_174_000));
        Assert.Equal(1, RadioProfile.Ic9700.PreampSteps(1_296_174_000));
    }

    [Fact]
    public void ItsBandButtonsAreItsOwn()
    {
        Assert.Equal(new[] { "2m", "70cm", "23cm" }, RadioProfile.Ic9700.Frequencies.Bands);
        Assert.Equal(14, RadioProfile.Ic7100.Frequencies.Bands.Count);
        Assert.Equal(12, RadioProfile.Ic7300Mk2.Frequencies.Bands.Count);
    }

    [Fact]
    public void ItHasDvButNotWfm()
    {
        Assert.True(RadioProfile.Ic9700.HasMode("DV"));
        Assert.False(RadioProfile.Ic9700.HasMode("WFM"));
    }
}
