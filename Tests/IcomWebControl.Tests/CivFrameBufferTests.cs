using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The framer, fed the serial chunks recorded off the radio: scope sweeps
/// several frames to a chunk, and one reply sharing a chunk with a sweep.
/// </summary>
public class CivFrameBufferTests
{
    private static readonly CivRecording Recording = CivRecording.Load();

    private static List<CivFrame> Frame(IEnumerable<byte[]> chunks)
    {
        var frames = new List<CivFrame>();
        var buffer = new CivFrameBuffer();
        buffer.FrameReceived += (_, f) => frames.Add(f);
        foreach (var c in chunks) buffer.Append(c);
        return frames;
    }

    [Fact]
    public void EveryRecordedFrameIsFound()
    {
        // FD only ends a frame: BCD digits, ASCII text and scope bins (0-A0) never reach it.
        int terminators = Recording.Chunks.Sum(c => c.Count(b => b == CivProtocol.End));
        Assert.True(terminators > 800, $"{terminators} frames recorded");
        Assert.Equal(terminators, Recording.Received.Count);
    }

    [Fact]
    public void EveryRecordedFrameIsFromTheRadioToUs()
    {
        Assert.All(Recording.Received, f =>
        {
            Assert.Equal(CivProtocol.ControllerAddress, f.To);
            Assert.Equal(CivProtocol.DefaultRadioAddress, f.From);
        });
    }

    [Fact]
    public void ByteAtATimeGivesTheSameFrames()
    {
        var oneByOne = Frame(Recording.Chunks.SelectMany(c => c.Select(b => new[] { b })));
        Assert.Equal(Recording.Received.Select(f => f.ToString()), oneByOne.Select(f => f.ToString()));
    }

    [Fact]
    public void AllInOneChunkGivesTheSameFrames()
    {
        var whole = Frame(new[] { Recording.Chunks.SelectMany(c => c).ToArray() });
        Assert.Equal(Recording.Received.Select(f => f.ToString()), whole.Select(f => f.ToString()));
    }

    [Fact]
    public void AReplySharingAChunkWithAScopeFrameIsFound()
    {
        // The Monitor Level answer (14 15) arrived in the same chunk as a sweep segment.
        var chunk = Recording.Chunks.Single(c => CivRecording.Hex(c).Contains("FE FE E0 B6 14 15 "));
        var frames = Frame(new[] { chunk });
        Assert.True(frames.Count >= 2);
        Assert.Contains(frames, CivRecording.IsScope);
        var reply = Assert.Single(frames, f => f.Cmd == CivProtocol.CmdSetLevel);
        Assert.Equal("15-01-28", BitConverter.ToString(reply.Data));
    }

    [Fact]
    public void NoiseBeforeThePreambleIsDropped()
    {
        var reply = CivRecording.Bytes("FE FE E0 B6 03 00 40 07 14 00 FD");
        var frames = Frame(new[] { new byte[] { 0x00, 0x13, 0xFE }, reply });
        var f = Assert.Single(frames);
        Assert.Equal(14_074_000, CivProtocol.DecodeBcd(f.Data));
    }

    [Fact]
    public void AFrameSplitAnywhereIsJoined()
    {
        var reply = CivRecording.Bytes("FE FE E0 B6 1A 05 01 28 4D 4D 35 41 47 4D 20 20 20 20 FD");
        for (int cut = 1; cut < reply.Length; cut++)
        {
            var f = Assert.Single(Frame(new[] { reply[..cut], reply[cut..] }));
            Assert.Equal("1A-05-01-28-4D-4D-35-41-47-4D-20-20-20-20", BitConverter.ToString(f.Body));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(25)]
    public void ExtraPreambleBytesAreNotTheAddress(int extra)
    {
        var reply = CivRecording.Bytes("FE FE E0 B6 03 00 40 07 14 00 FD");
        var f = Assert.Single(Frame(new[] { Enumerable.Repeat(CivProtocol.Preamble, extra).Concat(reply).ToArray() }));
        Assert.Equal(CivProtocol.ControllerAddress, f.To);
        Assert.Equal(CivProtocol.DefaultRadioAddress, f.From);
    }

    [Fact]
    public void ARunOfPreambleSplitAcrossChunksIsJoined()
    {
        var f = Assert.Single(Frame(new[] { CivRecording.Bytes("FE FE FE"), CivRecording.Bytes("FE E0 B6 03 00 40 07 14 00 FD") }));
        Assert.Equal(CivProtocol.ControllerAddress, f.To);
    }

    [Fact]
    public void ATooShortFrameIsDropped()
    {
        Assert.Empty(Frame(new[] { new byte[] { 0xFE, 0xFE, 0xE0, 0xFD } }));
    }
}
