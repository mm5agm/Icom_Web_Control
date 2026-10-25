using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// Which frames off the wire the bus takes as replies. Our own echo must
/// never count: taking the identify echo as the answer made the radio's
/// address E0, after which nothing answered (issues #2, #5).
/// </summary>
public class CivBusServiceTests
{
    private static readonly CivRecording Recording = CivRecording.Load();

    private static CivFrame Parse(byte[] frame) => new() { To = frame[2], From = frame[3], Body = frame[4..^1] };

    [Fact]
    public void EveryRecordedReplyIsForUs()
        => Assert.All(Recording.Received, f => Assert.True(CivBusService.IsForController(f), f.ToString()));

    [Fact]
    public void TheEchoOfEveryRecordedRequestIsNot()
        => Assert.All(Recording.Sent, s => Assert.False(CivBusService.IsForController(Parse(s)), CivRecording.Hex(s)));

    [Fact]
    public void TheBroadcastIdentifyEchoIsNot()
    {
        // Sent to 00 so any radio answers; its echo is To 00, so only From tells it apart.
        var identify = Recording.Sent.First(s => s[4] == CivProtocol.CmdReadId);
        Assert.Equal(CivProtocol.BroadcastAddress, identify[2]);
        Assert.False(CivBusService.IsForController(Parse(identify)));
    }

    [Fact]
    public void AFrameForAnotherControllerIsNot()
        => Assert.False(CivBusService.IsForController(new CivFrame { To = 0xE1, From = 0xB6, Body = new byte[] { 0x03 } }));

    [Fact]
    public void ABroadcastFromTheRadioIs()
        => Assert.True(CivBusService.IsForController(new CivFrame { To = 0x00, From = 0xB6, Body = new byte[] { 0x00 } }));
}
