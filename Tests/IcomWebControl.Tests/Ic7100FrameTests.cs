using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// What an IC-7100 is sent. The bytes are checked at the bus, below the guard,
/// so these see what would reach the radio. The replies are a recorded MkII's;
/// nothing here has been near a real 7100.
/// </summary>
public class Ic7100FrameTests
{
    private const byte Address = 0x88;

    /// <summary>The body of a frame: what follows the controller's address.</summary>
    private static string Body(string frame) => frame[(frame.IndexOf(" E0 ") + 4)..];

    [Fact]
    public async Task ItIsNeverSentACommandItDoesNotHave()
    {
        // Written out rather than taken from the profile, so that a slip in the
        // allow-list fails here.
        string[] forbidden =
        {
            "27 ",      // scope: no such command
            "28 ",      // voice keyer
            "1A 05 ",   // menu items: every number means something else
            "11 20 ",   // 20 dB attenuator: it has 12 dB
            "16 32 ",   // APF
            "21 02 ",   // delta TX
        };

        var actions = await RadioActionRecorder.RecordAll(Address);
        Assert.Contains(actions, a => a.Frames.Count > 0);
        foreach (var action in actions)
            foreach (var frame in action.Frames)
                Assert.False(forbidden.Any(Body(frame).StartsWith), $"{action.Label} sent {frame}");

        // Memories have their own layout, and may only be read: 1A 00, bank 1 to 5, 00, channel.
        var memoryFrames = actions.SelectMany(a => a.Frames.Select(f => (a.Label, Body: Body(f))))
            .Where(x => x.Body.StartsWith("1A 00 ")).ToList();
        Assert.NotEmpty(memoryFrames);
        foreach (var (label, body) in memoryFrames)
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(body, "^1A 00 0[1-5] 00 [0-9]{2} FD$"),
                $"{label} sent {body}");
    }

    [Fact]
    public async Task ItIsAddressedAs88OnceIdentified()
    {
        var actions = await RadioActionRecorder.RecordAll(Address);
        var connect = actions[0].Frames;
        Assert.Equal("FE FE 00 E0 19 00 FD", connect[0]);
        Assert.All(connect.Skip(1), f => Assert.Contains(" 88 E0 ", f));
        Assert.All(actions.Skip(1).SelectMany(a => a.Frames), f => Assert.Contains(" 88 E0 ", f));
    }

    [Fact]
    public async Task ItIsIdentifiedAndConnects()
    {
        var (controller, state) = TestHost.NewController(new SimulatedRadioClient(Address));
        Assert.True(await controller.ConnectAsync());
        Assert.Equal("IC-7100", controller.ModelId);
        Assert.Equal("IC-7100", state.RadioModel);
    }

    [Theory]
    [InlineData((byte)0x88, "FE FE 88 E0 11 12 FD")]
    [InlineData((byte)0xB6, "FE FE B6 E0 11 20 FD")]
    [InlineData((byte)0x94, "FE FE 94 E0 11 20 FD")]
    public async Task TheAttenuatorIsTheSizeTheRadioHas(byte address, string expected)
    {
        var bus = new SimulatedRadioClient(address);
        var (controller, _) = TestHost.NewController(bus);
        await controller.ConnectAsync();
        bus.Sent.Clear();
        await controller.SetAttenuatorAsync(true);
        Assert.Equal(expected, Assert.Single(bus.SentHex));
    }

    [Theory]
    [InlineData(new byte[] { 0x1C, 0x00, 0x00 }, true)]         // on its table
    [InlineData(new byte[] { 0x14, 0x0A, 0x01, 0x28 }, true)]   // on its table
    [InlineData(new byte[] { 0x1A, 0x05, 0x00, 0x08 }, false)]  // on a 7100 this is another setting
    [InlineData(new byte[] { 0x28, 0x00, 0x01 }, false)]
    [InlineData(new byte[] { 0x27, 0x10, 0x01 }, false)]
    [InlineData(new byte[] { 0x7F }, false)]                    // nothing IWC knows about
    public async Task AVoiceMacroGetsTheSameCheck(byte[] body, bool reachesTheRadio)
    {
        var bus = new SimulatedRadioClient(Address);
        var (controller, _) = TestHost.NewController(bus);
        await controller.ConnectAsync();
        bus.Sent.Clear();
        await controller.SendRawCommandAsync(body);
        Assert.Equal(reachesTheRadio, bus.Sent.Count > 0);
    }

    [Theory]
    [InlineData("FE FE 88 E0 11 FD", true)]                 // read the attenuator
    [InlineData("FE FE FE FE FE 88 E0 18 01 FD", true)]     // power on, with the wake-up preamble
    [InlineData("FE FE 88 E0 FD", false)]                   // no command
    [InlineData("FE FE 88 E0 03", false)]                   // no end byte
    [InlineData("88 E0 03 FD", false)]                      // no preamble
    [InlineData("FE FE 88 E0 16 FD", false)]                // 16 is nothing without a sub-command
    public void TheListReadsTheWholeFrame(string hex, bool allowed) =>
        Assert.Equal(allowed, RadioProfile.Ic7100.Allows(CivRecording.Bytes(hex)));

    [Fact]
    public void AnIc7300IsNeverFiltered()
    {
        Assert.True(RadioProfile.Ic7300Mk2.Allows(CivRecording.Bytes("FE FE B6 E0 28 00 01 FD")));
        Assert.True(RadioProfile.Ic7300.Allows(CivRecording.Bytes("FE FE 94 E0 28 00 01 FD")));
    }

    [Fact]
    public void ARadioWithNoTableIsNotFiltered() => Assert.Null(RadioProfile.ForModel("Icom(A4)"));

    [Theory]
    [InlineData((byte)0xB6, "IC-7300MK2")]
    [InlineData((byte)0x94, "IC-7300")]
    [InlineData((byte)0x88, "IC-7100")]
    [InlineData((byte)0xA4, "Icom(A4)")]
    public void TheIdByteNamesTheModel(byte id, string model) =>
        Assert.Equal(model, RadioProfile.ModelNameForId(id));
}
