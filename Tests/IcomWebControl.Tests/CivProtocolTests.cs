using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>Frame building and BCD, checked against bytes recorded off the radio.</summary>
public class CivProtocolTests
{
    private static readonly CivRecording Recording = CivRecording.Load();

    [Fact]
    public void BuildFrameMakesTheRecordedRequests()
    {
        foreach (var sent in Recording.Sent)
        {
            var body = sent[4..^1];
            Assert.Equal(CivRecording.Hex(sent), CivRecording.Hex(CivProtocol.BuildFrame(sent[2], sent[3], body)));
        }
    }

    [Fact]
    public void OperatingFrequencyDecodes()
    {
        // 03 00 40 07 14 00
        Assert.Equal(14_074_000, CivProtocol.DecodeBcd(Recording.ReplyTo("03")!.Data));
    }

    [Theory]
    [InlineData("25 00", 14_074_000)]
    [InlineData("25 01", 7_110_400)]
    public void VfoFrequenciesDecode(string request, long hz)
    {
        var reply = Recording.ReplyTo(request)!;
        Assert.Equal(hz, CivProtocol.DecodeBcd(reply.Data.AsSpan(1, 5)));
    }

    [Theory]
    [InlineData(14_074_000)]
    [InlineData(7_110_400)]
    [InlineData(1)]
    [InlineData(9_999_999_999)]
    public void EncodeBcdUndoesDecode(long hz)
    {
        Assert.Equal(hz, CivProtocol.DecodeBcd(CivProtocol.EncodeBcd(hz, 5)));
    }

    [Fact]
    public void EncodeBcdMakesTheRadiosBytes()
    {
        Assert.Equal("00 40 07 14 00", CivRecording.Hex(CivProtocol.EncodeBcd(14_074_000, 5)));
    }

    [Theory]
    [InlineData(0x00, 0)]
    [InlineData(0x28, 28)]
    [InlineData(0x55, 55)]
    [InlineData(0x99, 99)]
    public void BcdByteDecodes(byte b, int value) => Assert.Equal(value, CivProtocol.BcdByte(b));

    [Theory]
    [InlineData(19200, 25)]
    [InlineData(9600, 13)]
    [InlineData(4800, 7)]
    [InlineData(1200, 7)]
    public void PowerOnWakeBurstMatchesTheManual(int baud, int count)
        => Assert.Equal(count, CivProtocol.PowerOnWakePreambleCount(baud));

    [Fact]
    public void ARefusalIsAnNgFrame()
    {
        // 1A 05 01 30 (Display Language) and a menu item that does not exist both came back FA.
        Assert.Equal(CivProtocol.AckNg, Recording.ReplyTo("1A 05 01 30")!.Cmd);
        Assert.Equal(CivProtocol.AckNg, Recording.ReplyTo("1A 05 09 99")!.Cmd);
        Assert.Empty(Recording.ReplyTo("1A 05 09 99")!.Data);
    }
}
