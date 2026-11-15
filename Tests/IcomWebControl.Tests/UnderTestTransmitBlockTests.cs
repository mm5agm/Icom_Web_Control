using Icom_Web_Control.Services.Civ;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcomWebControl.Tests;

/// <summary>
/// The under-test IC-7100 is never sent a frame that transmits: no PTT on, no
/// tuner tune, no CW text. PTT off and CW stop still go, so IWC can always let
/// go. The IC-7300s and the IC-9700 (receive confirmed, IWC #137) transmit.
/// </summary>
public class UnderTestTransmitBlockTests
{
    private static string Body(string frame) => frame[(frame.IndexOf(" E0 ") + 4)..];

    private static bool Transmits(string body) =>
        body.StartsWith("1C 00 01") || body.StartsWith("1C 01 02")
        || (body.StartsWith("17 ") && body != "17 FF FD");

    [Fact]
    public async Task NoActionSendsTheIc7100AFrameThatTransmits()
    {
        var actions = await RadioActionRecorder.RecordAll(0x88);
        foreach (var action in actions)
            foreach (var frame in action.Frames)
                Assert.False(Transmits(Body(frame)), $"{action.Label} sent {frame}");
        // Letting go still reaches the radio.
        Assert.Contains(actions, a => a.Frames.Any(f => Body(f) == "1C 00 00 FD"));
    }

    [Theory]
    [InlineData(0xB6)] // IC-7300 MkII
    [InlineData(0xA2)] // IC-9700
    public async Task TheseRadiosAreSentTransmitFrames(byte address)
    {
        var actions = await RadioActionRecorder.RecordAll(address);
        Assert.Contains(actions, a => a.Frames.Any(f => Body(f) == "1C 00 01 FD"));
        Assert.Contains(actions, a => a.Frames.Any(f => Body(f).StartsWith("17 ") && Body(f) != "17 FF FD"));
    }

    [Theory]
    [InlineData(new byte[] { 0x1C, 0x00, 0x01 }, true)]
    [InlineData(new byte[] { 0x1C, 0x00, 0x00 }, false)]
    [InlineData(new byte[] { 0x1C, 0x00 }, false)]
    [InlineData(new byte[] { 0x1C, 0x01, 0x02 }, true)]
    [InlineData(new byte[] { 0x1C, 0x01, 0x01 }, false)]
    [InlineData(new byte[] { 0x17, 0x43, 0x51 }, true)]
    [InlineData(new byte[] { 0x17, 0xFF }, false)]
    [InlineData(new byte[] { 0x03 }, false)]
    public void TransmitFramesAreRecognised(byte[] body, bool transmits) =>
        Assert.Equal(transmits, RadioProfile.IsTransmitFrame(CivProtocol.BuildFrame(0xA2, CivProtocol.ControllerAddress, body)));

    [Fact]
    public void OnlyTheUnderTestModelsAreBlocked()
    {
        Assert.False(RadioProfile.Ic9700.TransmitBlocked);
        Assert.True(RadioProfile.Ic7100.TransmitBlocked);
        Assert.False(RadioProfile.Ic7300Mk2.TransmitBlocked);
        Assert.False(RadioProfile.Ic7300.TransmitBlocked);
    }

    [Theory]
    [InlineData(null, "IC-7100", false)]          // not identified yet: Settings decides
    [InlineData("Icom(A4)", "IC-7100", false)]    // a changed CI-V address: Settings decides
    [InlineData("IC-7300MK2", "IC-7100", true)]   // a recognised radio decides
    [InlineData("IC-7100", "IC-7300MK2", false)]
    [InlineData("IC-9700", "IC-7100", true)]      // the IC-9700 transmits now
    [InlineData(null, "IC-9700", true)]
    [InlineData(null, "IC-7300MK2", true)]
    public async Task TheGuardRefusesPttOnByModel(string? answered, string chosen, bool sent)
    {
        var inner = new SimulatedRadioClient(0xA2);
        var guard = new CivFrameGuard(inner, () => answered, NullLogger.Instance, () => chosen);
        await guard.TransactAsync(CivProtocol.BuildFrame(0xA2, CivProtocol.ControllerAddress, 0x1C, 0x00, 0x01), CivProtocol.AckOk);
        Assert.Equal(sent, inner.Sent.Count > 0);
    }
}
