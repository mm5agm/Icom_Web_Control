using System.Text;
using Icom_Web_Control.Services;
using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The rigctl conversation as Hamlib's NET rigctl backend (netrigctl.c) sees it.
/// Paul VK2AHB's fldigi kept hanging up after 4.5 s; these pin down what a
/// Hamlib client reads from IWC's port 4532.
/// </summary>
public class RigctldProtocolTests
{
    public static TheoryData<string> Radios => new() { "IC-7300MK2", "IC-7300", "IC-7100", "IC-9700" };

    private static RadioProfile ProfileFor(string model) => RadioProfile.ForModel(model)!;

    // Reads a protocol-0 dump_state the way netrigctl_open does, line by line,
    // and returns how many receive ranges it found. Throws if it runs out of
    // lines or has lines left over.
    private static (int Region, int RxRanges, int TxRanges) ParseLikeHamlib(string dump)
    {
        var lines = new Queue<string>(dump.Split('\n'));
        string Next() => lines.Count > 0 ? lines.Dequeue() : throw new InvalidOperationException("ran out of lines");

        Assert.Equal("0", Next());              // protocol version
        Assert.Equal("2", Next());              // rig model
        var region = int.Parse(Next());         // ITU region: must be a plain number

        int Ranges()
        {
            var n = 0;
            while (true)
            {
                var f = Next().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(7, f.Length);
                if (double.Parse(f[0]) == 0 && double.Parse(f[1]) == 0) return n;
                n++;
            }
        }
        var rx = Ranges();
        var tx = Ranges();

        void Pairs()
        {
            while (true)
            {
                var f = Next().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, f.Length);
                if (f[0] == "0" && f[1] == "0") return;
            }
        }
        Pairs();                                // tuning steps
        Pairs();                                // filters

        // max_rit, max_xit, max_ifshift, announces, preamp, attenuator,
        // has_get_func, has_set_func, has_get_level, has_set_level,
        // has_get_parm, has_set_parm
        for (int i = 0; i < 12; i++) Next();

        Assert.Empty(lines);
        return (region, rx, tx);
    }

    [Theory]
    [MemberData(nameof(Radios))]
    public void DumpState_ParsesLineForLineLikeHamlib(string model)
    {
        var profile = ProfileFor(model);
        var (_, rx, tx) = ParseLikeHamlib(RigctldServer.DumpState(profile));
        Assert.Equal(profile.Frequencies.Receive.Count(), rx);
        Assert.Equal(profile.Frequencies.Transmit.Count(), tx);
    }

    [Fact]
    public void DumpState_HasTheItuRegionLine_BeforeTheRanges()
    {
        var lines = RigctldServer.DumpState(RadioProfile.Ic7300Mk2).Split('\n');
        Assert.Equal("0", lines[2]);
        Assert.StartsWith("30000.000000 ", lines[3]);
    }

    [Fact]
    public void TakeLines_SplitsSeveralCommandsInOneRead()
    {
        var pending = new StringBuilder();
        Assert.Equal(new[] { "f", "m", "t" }, RigctldServer.TakeLines(pending, "f\nm\nt\n"));
        Assert.Equal(0, pending.Length);
    }

    [Fact]
    public void TakeLines_KeepsACommandSplitAcrossTwoReads()
    {
        var pending = new StringBuilder();
        Assert.Equal(new[] { "f" }, RigctldServer.TakeLines(pending, "f\nF 1407"));
        Assert.Equal("F 1407", pending.ToString());
        Assert.Equal(new[] { "F 14074000" }, RigctldServer.TakeLines(pending, "4000\n"));
        Assert.Equal(0, pending.Length);
    }

    [Fact]
    public void TakeLines_ReturnsNothingUntilANewlineArrives()
    {
        var pending = new StringBuilder();
        Assert.Empty(RigctldServer.TakeLines(pending, "\\dump_st"));
        Assert.Equal(new[] { "\\dump_state" }, RigctldServer.TakeLines(pending, "ate\n"));
    }

    [Fact]
    public void TakeLines_DropsAnEndlessLineInsteadOfGrowingForever()
    {
        var pending = new StringBuilder();
        for (int i = 0; i < 10; i++)
            Assert.Empty(RigctldServer.TakeLines(pending, new string('x', 1000)));
        Assert.True(pending.Length <= 5000);
    }

    // What fldigi set to Hamlib's IC-7300 model, with localhost:4532 as the
    // device, actually sent (captured 2026-11-20): read frequency, then
    // read the transceive setting, as raw CI-V.
    [Theory]
    [InlineData(new byte[] { 0xFE, 0xFE, 0x94, 0xE0, 0x03, 0xFD })]
    [InlineData(new byte[] { 0xFE, 0xFE, 0x94, 0xE0, 0x25, 0x00, 0xFD })]
    public void LooksLikeCiv_SpotsAProgramSetToTheRadiosOwnModel(byte[] sent)
        => Assert.True(RigctldServer.LooksLikeCiv(sent));

    [Theory]
    [InlineData("\\get_powerstat\n\\chk_vfo\n\\dump_state\n")]
    [InlineData("f\nm\nF 14074000.000000\nM PKTUSB 3000\n")]
    public void LooksLikeCiv_IgnoresRigctlText(string sent)
        => Assert.False(RigctldServer.LooksLikeCiv(Encoding.ASCII.GetBytes(sent)));
}
