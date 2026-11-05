using System.Text;
using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The IC-7100's memory channels, from page 20-16 of its manual. No real radio
/// has answered these yet. IWC may read them and must never be able to write them.
/// </summary>
public class Ic7100MemoryTests
{
    // One whole record: 00, bank, channel, split, then two 47-byte blocks and the name.
    private static byte[] Record(byte bank, byte chLo, byte split, byte[] hzBcd, byte mode, byte filter,
        byte data, byte[] txHzBcd, byte txMode, string name)
    {
        var d = new List<byte> { 0x00, bank, 0x00, chLo, split };
        void Block(byte[] hz, byte m, byte f, byte dm)
        {
            d.AddRange(hz);
            d.Add(m); d.Add(f); d.Add(dm);
            d.AddRange(new byte[15]);                      // duplex, tones, DTCS, offset
            d.AddRange(Encoding.ASCII.GetBytes(new string(' ', 24)));   // three call signs
        }
        Block(hzBcd, mode, filter, data);
        Block(txHzBcd, txMode, filter, data);
        d.AddRange(Encoding.ASCII.GetBytes(name.PadRight(16)));
        return d.ToArray();
    }

    private static readonly byte[] Hz145500 = { 0x00, 0x00, 0x50, 0x45, 0x01 };
    private static readonly byte[] Hz145000 = { 0x00, 0x00, 0x00, 0x45, 0x01 };
    private static readonly byte[] Hz14074 = { 0x00, 0x40, 0x07, 0x14, 0x00 };

    [Fact]
    public void AWholeRecordIs115Bytes() =>
        Assert.Equal(115, Record(1, 0x01, 0, Hz14074, 1, 1, 0, Hz14074, 1, "").Length);

    [Theory]
    [InlineData(1, "A-01", 1, 0x01)]
    [InlineData(99, "A-99", 1, 0x99)]
    [InlineData(100, "B-01", 2, 0x01)]
    [InlineData(198, "B-99", 2, 0x99)]
    [InlineData(495, "E-99", 5, 0x99)]
    public void ChannelsNumberThroughTheBanks(int channel, string label, byte bank, byte chLo)
    {
        Assert.Equal(label, Ic7100MemoryCodec.Label(channel));
        Assert.Equal(new byte[] { 0x00, bank, 0x00, chLo }, Ic7100MemoryCodec.ReadData(channel));
    }

    [Fact]
    public void AProgrammedChannelReads()
    {
        var c = Ic7100MemoryCodec.Parse(100, Record(2, 0x01, 0x10, Hz145500, 0x05, 0x02, 0, Hz145000, 0x05, "2m call"))!;
        Assert.False(c.IsEmpty);
        Assert.Equal(100, c.Channel);
        Assert.Equal(145_500_000, c.FrequencyHz);
        Assert.Equal("FM", c.Mode);
        Assert.Equal(2, c.Filter);
        Assert.True(c.SplitOn);
        Assert.Equal(145_000_000, c.TxFrequencyHz);
        Assert.Equal("2m call", c.Name);
    }

    [Fact]
    public void TheDataModeByteCounts()
    {
        var c = Ic7100MemoryCodec.Parse(1, Record(1, 0x01, 0, Hz14074, 0x01, 0x01, 0x01, Hz14074, 0x01, ""))!;
        Assert.Equal("DATA-U", c.Mode);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x01, 0xFF })]
    public void AnEmptyChannelReadsAsEmpty(byte[] reply) =>
        Assert.True(Ic7100MemoryCodec.Parse(1, reply)!.IsEmpty);

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01 })]                         // too short
    [InlineData(new byte[] { 0x00, 0x02, 0x00, 0x01 })]             // another bank
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x02 })]             // another channel
    [InlineData(new byte[] { 0x03, 0x01, 0x00, 0x01 })]             // another sub-command
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 })] // cut off in the frequency
    public void AnythingElseIsAMissNotAnEmptyChannel(byte[] reply) =>
        Assert.Null(Ic7100MemoryCodec.Parse(1, reply));

    private static byte[] Frame(params byte[] body)
    {
        var f = new List<byte> { 0xFE, 0xFE, 0x88, 0xE0 };
        f.AddRange(body);
        f.Add(0xFD);
        return f.ToArray();
    }

    [Fact]
    public void AReadMayBeSent() =>
        Assert.True(RadioProfile.Ic7100.Allows(Frame(0x1A, 0x00, 0x01, 0x00, 0x01)));

    [Fact]
    public void AClearMayNot() =>
        Assert.False(RadioProfile.Ic7100.Allows(Frame(0x1A, 0x00, 0x01, 0x00, 0x01, 0xFF)));

    [Fact]
    public void AWriteMayNot()
    {
        var body = new List<byte> { 0x1A };
        body.AddRange(Record(1, 0x01, 0, Hz14074, 1, 1, 0, Hz14074, 1, "x"));
        Assert.False(RadioProfile.Ic7100.Allows(Frame(body.ToArray())));
    }

    [Theory]
    [InlineData(0x00, 0x00, 0x01, 0xFF)]   // a 7300's clear, the same length as a read
    [InlineData(0x00, 0x06, 0x00, 0x01)]   // no bank 6
    [InlineData(0x00, 0x00, 0x00, 0x01)]   // no bank 0
    [InlineData(0x00, 0x01, 0x01, 0x06)]   // call channels aren't read
    [InlineData(0x00, 0x01, 0x00, 0x00)]   // no channel 0
    [InlineData(0x00, 0x01, 0x00, 0x1F)]   // not BCD
    public void FramesThatOnlyLookLikeAReadMayNot(byte sub, byte bank, byte hi, byte lo) =>
        Assert.False(RadioProfile.Ic7100.Allows(Frame(0x1A, sub, bank, hi, lo)));

    [Fact]
    public void A7300StyleReadOrWriteMayNot()
    {
        Assert.False(RadioProfile.Ic7100.Allows(Frame(0x1A, 0x00, 0x00, 0x01)));
        Assert.False(RadioProfile.Ic7100.Allows(Frame(0x1A, 0x00)));
    }

    [Fact]
    public void Only7100WritesAreRefused()
    {
        Assert.False(RadioProfile.Ic7100.CanWriteMemories);
        Assert.True(RadioProfile.Ic7300.CanWriteMemories);
        Assert.True(RadioProfile.Ic7300Mk2.CanWriteMemories);
    }

    [Fact]
    public void OtherMenuReadsStillPass() =>
        Assert.True(RadioProfile.Ic7100.Allows(Frame(0x1A, 0x03)));

    [Fact]
    public void Item05StaysBlocked() =>
        Assert.False(RadioProfile.Ic7100.Allows(Frame(0x1A, 0x05, 0x00, 0x01)));

    [Fact]
    public void ChannelCounts()
    {
        Assert.Equal(495, RadioProfile.Ic7100.MemoryChannels);
        Assert.Equal(99, RadioProfile.Ic7300.MemoryChannels);
        Assert.Equal("CH 007", RadioProfile.Ic7300.ChannelLabel(7));
        Assert.Equal("A-07", RadioProfile.Ic7100.ChannelLabel(7));
    }
}
