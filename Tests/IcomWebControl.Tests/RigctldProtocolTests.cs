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

    // YWC #387, ported: Log4OM, set to Hamlib NET rigctl with VFO MODE ticked, sends
    // ";V ?" and then polls "+\\get_vfo_info VFOA" and "VFOB". The expected
    // replies are what Hamlib 4.6.2's own rigctld (Log4OM's bundled copy,
    // dummy rig) sent back on 2026-11-21.

    // Answers like a rig on 7.074 MHz USB, VFO B on 14.074, recording calls.
    private static (Func<string, string[], string?, Task<string>> Plain, List<string> Calls) FakeRig()
    {
        var calls = new List<string>();
        Task<string> Plain(string name, string[] args, string? vfo)
        {
            calls.Add($"{name}|{string.Join(' ', args)}|{vfo}");
            var onB = RigctldServer.IsVfoB(vfo ?? (name == "get_vfo_info" ? args.FirstOrDefault() : null));
            return Task.FromResult(name switch
            {
                "get_freq"     => onB ? "14074000" : "7074000",
                "get_mode"     => "USB\n0",
                "set_vfo"      => args.FirstOrDefault() == "?" ? "VFOA VFOB \nRPRT 0" : "RPRT 0",
                "get_vfo_info" => RigctldServer.VfoInfo(onB ? "14074000" : "7074000", "USB", false),
                "get_split_vfo"=> "0\nVFOA",
                "get_level"    => "-54",
                "set_freq"     => onB ? "RPRT -1" : "RPRT 0",
                _              => "RPRT -1",
            });
        }
        return (Plain, calls);
    }

    [Theory]
    [InlineData(";V ?",                 "set_vfo: ?;VFOA VFOB \nRPRT 0")]
    [InlineData("+\\get_vfo_info VFOA", "get_vfo_info: VFOA\nFreq: 7074000\nMode: USB\nWidth: 0\nSplit: 0\nSatMode: 0\nRPRT 0")]
    [InlineData("+\\get_vfo_info VFOB", "get_vfo_info: VFOB\nFreq: 14074000\nMode: USB\nWidth: 0\nSplit: 0\nSatMode: 0\nRPRT 0")]
    [InlineData(";f",                   "get_freq:;Frequency: 7074000;RPRT 0")]
    [InlineData(";\\get_freq",          "get_freq:;Frequency: 7074000;RPRT 0")]
    [InlineData("+f",                   "get_freq:\nFrequency: 7074000\nRPRT 0")]
    [InlineData("|f",                   "get_freq:|Frequency: 7074000|RPRT 0")]
    [InlineData(",f",                   "get_freq:,Frequency: 7074000,RPRT 0")]
    [InlineData(";m",                   "get_mode:;Mode: USB;Passband: 0;RPRT 0")]
    [InlineData(";s",                   "get_split_vfo:;Split: 0;TX VFO: VFOA;RPRT 0")]
    [InlineData(";l STRENGTH",          "get_level: STRENGTH;-54;RPRT 0")]
    [InlineData(";F 14074000",          "set_freq: 14074000;RPRT 0")]
    [InlineData(";t",                   "get_ptt:;RPRT -1")]
    [InlineData(";q",                   "RPRT 0")]
    public async Task ExtendedCommands_AreAnsweredInHamlibsFormat(string line, string expected)
        => Assert.Equal(expected, await RigctldServer.AnswerLineAsync(line, FakeRig().Plain));

    [Theory]
    [InlineData("f", "7074000")]
    [InlineData("\\get_freq", "7074000")]
    [InlineData("V ?", "VFOA VFOB \nRPRT 0")]
    [InlineData("\\get_vfo_info VFOA", "7074000\nUSB\n0\n0\n0")]
    [InlineData("s", "0\nVFOA")]
    public async Task PlainCommands_AreUnchanged(string line, string expected)
        => Assert.Equal(expected, await RigctldServer.AnswerLineAsync(line, FakeRig().Plain));

    // Log4OM tunes with "F VFOA 8074000": the VFO comes first in VFO mode.
    [Theory]
    [InlineData("F VFOA 8074000", "set_freq|8074000|VFOA")]
    [InlineData("\\set_freq VFOA 8074000", "set_freq|8074000|VFOA")]
    [InlineData("f VFOA", "get_freq||VFOA")]
    [InlineData("M VFOA USB 2400", "set_mode|USB 2400|VFOA")]
    [InlineData("F 14074000", "set_freq|14074000|")]
    [InlineData("S 1 VFOB", "set_split_vfo|1 VFOB|")]
    [InlineData("V VFOB", "set_vfo|VFOB|")]
    [InlineData("\\get_vfo_info VFOB", "get_vfo_info|VFOB|")]
    public async Task TheVfoArgument_IsTakenOffBeforeTheValue(string line, string call)
    {
        var (plain, calls) = FakeRig();
        await RigctldServer.AnswerLineAsync(line, plain);
        Assert.Equal(call, Assert.Single(calls));
    }

    [Fact]
    public async Task SettingVfoB_IsRefused_NotAppliedToA()
        => Assert.Equal("RPRT -1", await RigctldServer.AnswerLineAsync("F VFOB 14074000", FakeRig().Plain));

    // Hamlib's one-letter names, which netrigctl (WSJT-X, fldigi) sends.
    [Theory]
    [InlineData("s", "get_split_vfo")] [InlineData("S 1 VFOB", "set_split_vfo")]
    [InlineData("i", "get_split_freq")] [InlineData("I 14076000", "set_split_freq")]
    [InlineData("x", "get_split_mode")] [InlineData("X USB 2400", "set_split_mode")]
    [InlineData("j", "get_rit")] [InlineData("J 100", "set_rit")]
    [InlineData("z", "get_xit")] [InlineData("Z 100", "set_xit")]
    [InlineData("_", "get_info")]
    public async Task ShortLetters_MeanWhatTheyMeanInHamlib(string line, string name)
    {
        var (plain, calls) = FakeRig();
        await RigctldServer.AnswerLineAsync(line, plain);
        Assert.StartsWith(name + "|", Assert.Single(calls));
    }

    [Theory]
    [InlineData("r")] [InlineData("c")] [InlineData("y")]
    public async Task LettersWithNoCommandHere_GetRprtMinus1(string line)
        => Assert.Equal("RPRT -1", await RigctldServer.AnswerLineAsync(line, FakeRig().Plain));
}
