using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// A radio that ignores the ID request on the broadcast address, as Paul VK2AHB's
/// original IC-7300 did (IWC #128): his test got only our echo back for
/// FE FE 00 E0 19 00 FD, but a reply to FE FE 94 E0 03 FD. The replies here are a
/// recorded MkII's; nothing has been near Paul's radio.
/// </summary>
public class IdentifyFallbackTests
{
    [Fact]
    public async Task ItIsFoundByAskingAtItsOwnAddress()
    {
        var bus = new SimulatedRadioClient(0x94, ignoresBroadcast: true);
        var (controller, state, settings, _) = TestHost.NewControllerWithHub(bus);
        settings.Settings.RadioModel = "IC-7300";

        Assert.True(await controller.ConnectAsync());
        Assert.Equal("IC-7300", controller.ModelId);
        Assert.Equal("IC-7300", state.RadioModel);
        // The model chosen in Settings is asked first.
        Assert.Equal("FE FE 00 E0 19 00 FD", bus.SentHex.First());
        Assert.Equal("FE FE 94 E0 19 00 FD", bus.SentHex.Skip(1).First());
    }

    [Fact]
    public async Task ItIsFoundByAFrequencyReadWhenItAnswersNoIdRequest()
    {
        // The default model (MkII) is chosen, so B6 is asked before 94.
        var bus = new SimulatedRadioClient(0x94, ignoresBroadcast: true, answersId: false);
        var (controller, _) = TestHost.NewController(bus);

        Assert.True(await controller.ConnectAsync());
        Assert.Equal("IC-7300", controller.ModelId);
        Assert.Contains("FE FE 94 E0 03 FD", bus.SentHex);

        bus.Sent.Clear();
        await controller.SetAttenuatorAsync(true);
        Assert.Equal("FE FE 94 E0 11 20 FD", Assert.Single(bus.SentHex));
    }

    [Fact]
    public async Task ARadioThatAnswersBroadcastIsAskedNothingMore()
    {
        var bus = new SimulatedRadioClient(0x94);
        var (controller, _) = TestHost.NewController(bus);

        Assert.True(await controller.ConnectAsync());
        Assert.Equal("IC-7300", controller.ModelId);
        Assert.Single(bus.SentHex, f => f.Contains(" 19 00 "));
    }

    [Fact]
    public async Task AnUnknownAddressStillDoesNotConnect()
    {
        var bus = new SimulatedRadioClient(0x70, ignoresBroadcast: true);
        var (controller, _) = TestHost.NewController(bus);
        Assert.False(await controller.ConnectAsync());
    }

    [Fact]
    public void FindMyRadioOnlyReads()
    {
        var hex = CivRecording.Hex(RadioFinder.ProbeBurst());
        var frames = hex.Split(" FD", StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim() + " FD").ToList();

        Assert.Equal("FE FE 00 E0 19 00 FD", frames[0]);
        foreach (var address in new[] { "B6", "94", "88" })
        {
            Assert.Contains($"FE FE {address} E0 19 00 FD", frames);
            Assert.Contains($"FE FE {address} E0 03 FD", frames);
        }
        Assert.All(frames, f => Assert.Matches("^FE FE [0-9A-F]{2} E0 (19 00|03) FD$", f));
    }
}
