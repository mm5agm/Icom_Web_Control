using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// Sweep reassembly from the 27 00 frames the radio pushed while recording:
/// eleven frames a sweep, a header then ten segments of bins.
/// </summary>
public class CivScopeAssemblerTests
{
    private static readonly CivRecording Recording = CivRecording.Load();
    private static readonly List<CivFrame> Scope = Recording.Received.Where(CivRecording.IsScope).ToList();

    [Fact]
    public void RecordedSweepsAssemble()
    {
        var assembler = new CivScopeAssembler();
        var sweeps = Scope.Select(assembler.Add).OfType<ScopeSweep>().ToList();

        int headers = Scope.Count(f => f.Data[2] == 0x01);
        Assert.True(sweeps.Count >= headers - 2, $"{sweeps.Count} sweeps from {headers} headers");
        Assert.Equal(sweeps.Count, assembler.SweepsCompleted);
        Assert.All(sweeps, s =>
        {
            Assert.Equal(CivScopeAssembler.BinCount, s.BinsDb.Length);
            // The radio's centre is the USB carrier point, 1.5 kHz above the dial (14.074.000).
            Assert.Equal(14_075_500, s.CentreHz);
            // The span field is the +/- half width, 27 15 read 50 kHz; the sweep is twice that.
            Assert.Equal(100_000, s.SpanHz);
            Assert.Equal(CivProtocol.ScopeModeCenter, s.Mode);
            Assert.False(s.OutOfRange);
        });
    }

    [Fact]
    public void TheRecordedSegmentsCarry475Bins()
    {
        // Frames 02-10 carry 50 bins, frame 11 the last 25.
        var first = Scope.SkipWhile(f => f.Data[2] != 0x01).Take(11).ToList();
        Assert.Equal(Enumerable.Range(1, 11), first.Select(f => CivProtocol.BcdByte(f.Data[2])));
        Assert.Equal(475, first.Skip(1).Sum(f => f.Data.Length - 4));
    }

    [Fact]
    public void JoiningMidSweepIsNotADiscard()
    {
        var assembler = new CivScopeAssembler();
        var midway = Scope.SkipWhile(f => f.Data[2] != 0x05).ToList();
        var sweeps = midway.Select(assembler.Add).OfType<ScopeSweep>().ToList();
        Assert.NotEmpty(sweeps);
        Assert.Equal(0, assembler.SweepsDiscarded);
    }

    [Fact]
    public void ADroppedSegmentDiscardsThatSweepOnly()
    {
        var assembler = new CivScopeAssembler();
        var start = Scope.FindIndex(f => f.Data[2] == 0x01);
        var frames = Scope.Skip(start).Take(22).ToList();
        frames.RemoveAt(5);   // lose segment 06 of the first sweep
        var sweeps = frames.Select(assembler.Add).OfType<ScopeSweep>().ToList();
        Assert.Single(sweeps);
        Assert.Equal(1, assembler.SweepsDiscarded);
    }

    [Fact]
    public void NonScopeFramesAreIgnored()
    {
        var assembler = new CivScopeAssembler();
        Assert.All(Recording.Received.Where(f => !CivRecording.IsScope(f)), f => Assert.Null(assembler.Add(f)));
        Assert.Equal(0, assembler.SweepsCompleted);
    }
}
