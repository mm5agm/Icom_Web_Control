using System.Text;
using Icom_Web_Control.Services.RadioMenu;

namespace IcomWebControl.Tests;

/// <summary>
/// The radio menu file: the 270-item IC-7300MK2 table, turning what the radio
/// answers into words and back, and a save then load that changes nothing.
/// </summary>
public class RadioMenuTests
{
    private static readonly IReadOnlyList<RadioMenuItem> Table = Ic7300Mk2Menu.Items;
    private static RadioMenuItem It(int n) => Table[n - 1];
    private static string Hex(byte[] d) => RadioMenuFile.Hex(d);
    private static byte[] B(string h) => RadioMenuLoad.Bytes(h)!;
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static readonly byte[] EdgeNow = B("00 30 00 00 60 01");
    private static readonly byte[] EdgeSaved = B("00 50 00 00 70 01");

    [Fact]
    public void TheTableHas270ItemsInOrder()
    {
        Assert.Equal(270, Table.Count);
        Assert.Equal(Enumerable.Range(1, 270), Table.Select(i => i.Number));
    }

    [Fact]
    public void EveryListedValueDecodesAndEncodesBack()
    {
        int listed = 0;
        foreach (var item in Table.Where(i => i.Codec.Values is not null))
        {
            var values = item.Codec.Values!;
            foreach (var v in values)
            {
                listed++;
                var shown = item.Decode(v);
                Assert.True(shown != Hex(v), $"{item.Address} {item.Function}: {Hex(v)} has no words");
                if (item.Fixed is not null) continue;
                var raw = RadioMenuLoad.Encode(item, shown, null, values[0], out var err);
                Assert.True(raw is not null && item.Decode(B(raw)) == shown, $"{item.Address} {item.Function}: '{shown}' -> {raw} {err}");
                Assert.True(RadioMenuLoad.CheckRaw(item, raw!, values[0], out err) is not null, $"{item.Address} CheckRaw {raw} {err}");
            }
            if (item.Fixed is null)
            {
                var choices = RadioMenuLoad.Choices(item, values[0])!;
                Assert.Equal(choices.Count, choices.Select(c => c.Label).Distinct().Count());
            }
        }
        Assert.True(listed > 1000, $"{listed} listed values");
    }

    [Fact]
    public void PercentHas101ChoicesAndKeepsTheCurrentBytes()
    {
        var beep = It(22);
        var choices = RadioMenuLoad.Choices(beep, B("01 28"))!;
        Assert.Equal(101, choices.Count);
        Assert.Contains(choices, c => c.Raw == "01 28");
        Assert.Equal("01 28", RadioMenuLoad.Encode(beep, "50", null, B("01 28"), out _));
        Assert.Equal("00 00", RadioMenuLoad.Encode(beep, "0%", null, B("01 28"), out _));
        Assert.Null(RadioMenuLoad.Encode(beep, "101%", null, B("01 28"), out var err));
        Assert.Contains("not a setting", err);
    }

    [Theory]
    [InlineData(29, "20", "00", "03")]                                   // TX Delay HF, unit left off
    [InlineData(26, "rf+sql", "00", "02")]                               // case does not count
    [InlineData(2, "-5", "05", "00")]
    [InlineData(2, "+5", "05", "10")]
    [InlineData(267, "1.5", "02", "15")]                                 // VOX delay
    [InlineData(217, "1234", "00 01", "12 34")]
    [InlineData(14, "300-2700 Hz", "30", "14")]
    [InlineData(1, "HPF Through / LPF Through", "01 24", "00 25")]
    public void ValuesAreReadLeniently(int number, string value, string current, string raw)
        => Assert.Equal(raw, RadioMenuLoad.Encode(It(number), value, null, B(current), out _));

    [Fact]
    public void AValueBetweenStepsIsRefused()
        => Assert.Null(RadioMenuLoad.Encode(It(267), "2.1", null, B("02"), out _));

    [Fact]
    public void RgbIsParsed()
    {
        var rgb = It(144);
        Assert.Equal("R 255 G 128 B 0", rgb.Decode(B("02 55 01 28 00 00")));
        Assert.Equal("00 00 00 10 02 55", RadioMenuLoad.Encode(rgb, "r 0 g 10 b 255", null, B("02 55 01 28 00 00"), out _));
        Assert.Null(RadioMenuLoad.Encode(rgb, "R 256 G 0 B 0", null, B("02 55 01 28 00 00"), out _));
    }

    [Fact]
    public void BandEdgesGoBackOnlyAsSaved()
    {
        var edge = It(152);
        Assert.NotEqual(Hex(EdgeSaved), edge.Decode(EdgeSaved));
        Assert.Equal(Hex(EdgeSaved), RadioMenuLoad.Encode(edge, edge.Decode(EdgeSaved), Hex(EdgeSaved), EdgeNow, out _));
        Assert.Null(RadioMenuLoad.Encode(edge, "0.100-0.200 MHz", Hex(EdgeSaved), EdgeNow, out _));
        Assert.Null(RadioMenuLoad.CheckRaw(edge, "FF FF FF FF FF FF", EdgeNow, out _));
        Assert.Null(RadioMenuLoad.CheckRaw(edge, "00 50 00", EdgeNow, out _));
    }

    [Theory]
    [InlineData(89)] [InlineData(90)] [InlineData(94)] [InlineData(100)] [InlineData(101)]
    [InlineData(107)] [InlineData(110)] [InlineData(114)] [InlineData(132)] [InlineData(133)]
    public void LinkAndClockItemsAreFixed(int number) => Assert.NotNull(It(number).Fixed);

    [Theory]
    [InlineData(22)] [InlineData(128)] [InlineData(135)] [InlineData(136)] [InlineData(144)] [InlineData(152)]
    public void OtherItemsAreNot(int number) => Assert.Null(It(number).Fixed);

    [Fact]
    public void CivTransceiveCannotBeLoaded()
    {
        // Changing it from a file would cut the link IWC is using.
        Assert.Null(RadioMenuLoad.Encode(It(89), "ON", "01", B("00"), out var err));
        Assert.Contains("CI-V", err);
        Assert.Null(RadioMenuLoad.CheckRaw(It(89), "01", B("00"), out _));
        Assert.Null(RadioMenuLoad.Choices(It(89), B("00")));
    }

    [Fact]
    public void MyCallIsUpperCaseAndPaddedLikeTheRadio()
    {
        var call = It(128);
        Assert.Equal("MM5AGM/P", RadioMenuLoad.Encode(call, "mm5agm/p", null, Ascii("MM5AGM    "), out _));
        // The recording shows the radio pads My Call to 10 with spaces.
        Assert.Equal(Hex(Ascii("MM5AGM/P  ")), Hex(RadioMenuLoad.CheckRaw(call, "mm5agm/p", Ascii("MM5AGM    "), out _)!));
        Assert.Equal(Hex(Ascii("MM5AGM/P")), Hex(RadioMenuLoad.CheckRaw(call, "mm5agm/p", Ascii("MM5AGM"), out _)!));
        Assert.True(RadioMenuLoad.ReadBackMatches(call, Ascii("MM5AGM/P  "), Ascii("MM5AGM/P")));
    }

    [Theory]
    [InlineData("MM5AGM!")]
    [InlineData("ABCDEFGHIJK")]
    [InlineData("(blank)")]
    public void ABadCallIsRefused(string value)
    {
        Assert.Null(RadioMenuLoad.CheckRaw(It(128), value, Ascii("MM5AGM"), out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void NtpServerKeepsItsCase()
    {
        var data = RadioMenuLoad.CheckRaw(It(135), "pool.ntp.org", B("41"), out _);
        Assert.Equal("pool.ntp.org", Encoding.ASCII.GetString(data!));
    }

    [Fact]
    public void SaveThenLoadChangesNothing()
    {
        var rng = new Random(7);
        var readings = Table.Select(i =>
        {
            byte[] d = i.Codec.Values is { } v ? v[rng.Next(v.Count)]
                : i.IsText ? Ascii("MM5AGM    ")
                : i.Codec.Parse is not null ? B("02 55 01 28 00 00")
                : i.Number is >= 152 and <= 203 ? EdgeSaved
                : i.Number is 34 or 35 ? B("00 10 00 00")
                : i.Number == 136 ? B("01 00 00")
                : i.Number == 132 ? B("20 26 10 25")
                : i.Number == 133 ? B("12 34")
                : B("00");
            return new RadioMenuReading(i, d);
        }).ToList();

        var parsed = RadioMenuLoad.Parse(RadioMenuFile.ToIni("IC-7300MK2", new DateTime(2026, 10, 25, 12, 0, 0), readings));
        Assert.Equal("IWC", parsed.Program);
        Assert.Equal("IC-7300MK2", parsed.Radio);
        Assert.Equal(270, parsed.Items.Count);

        int same = 0;
        foreach (var line in parsed.Items)
        {
            var item = RadioMenuLoad.Find(Table, line);
            Assert.NotNull(item);
            var now = readings[item!.Number - 1].Data!;
            var raw = RadioMenuLoad.Encode(item, line.Value, line.SavedRaw, now, out var err);
            if (raw is null)
            {
                Assert.True(item.Fixed is not null, $"{item.Address} {item.Function} '{line.Value}' refused: {err}");
                continue;
            }
            var data = RadioMenuLoad.CheckRaw(item, raw, now, out err);
            Assert.True(data is not null && RadioMenuLoad.ReadBackMatches(item, data, now), $"{item.Address} {item.Function} {raw} {err}");
            same++;
        }
        Assert.Equal(270 - Table.Count(i => i.Fixed is not null), same);
    }

    [Fact]
    public void AnItemIsFoundByNameWhenTheCommentIsGone()
    {
        var line = RadioMenuLoad.Parse("[SET > Function > TX Delay]\nHF = 25 ms\n").Items[0];
        Assert.Equal(29, RadioMenuLoad.Find(Table, line)?.Number);
    }

    [Fact]
    public void ARealSavedFileLoads()
    {
        // Saved from the radio on 2026-10-25, before My Call was set.
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ic7300mk2-radio-menu-2026-10-25.ini"));
        var parsed = RadioMenuLoad.Parse(text);
        Assert.Equal("IWC", parsed.Program);
        Assert.Equal("IC-7300MK2", parsed.Radio);
        Assert.Equal(270, parsed.Items.Count);

        var found = parsed.Items.Select(l => (Line: l, Item: RadioMenuLoad.Find(Table, l))).ToList();
        Assert.All(found, f => Assert.True(f.Item is not null, $"line {f.Line.Line} {f.Line.Name}"));
        Assert.Equal(Enumerable.Range(1, 270), found.Select(f => f.Item!.Number).OrderBy(n => n));

        var unanswered = found.Where(f => f.Line.Value == RadioMenuLoad.NoAnswer).Select(f => f.Item!.Address).ToList();
        Assert.Equal(new[] { "01 30", "02 27" }, unanswered);

        // HPF/LPF SSB agrees with what the radio answered while recording.
        Assert.Equal("13 25", found.Single(f => f.Item!.Number == 1).Line.SavedRaw);
        Assert.Equal("(blank)", found.Single(f => f.Item!.Number == 128).Line.Value);
    }
}
