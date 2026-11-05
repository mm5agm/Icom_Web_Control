using System.Text.Json;
using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// What picking an IC-7100 in Settings brings with it: the meter table from its
/// manual, and radio memories refused until their layout has been written.
/// </summary>
public class Ic7100SettingsTests
{
    private static JsonElement Meter(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "wwwroot", "calibration.default.IC-7100.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "wwwroot", "calibration.default.IC-7100.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("meters").EnumerateArray()
            .First(m => m.GetProperty("name").GetString() == name).Clone();
    }

    [Theory]
    [InlineData("PWR", "50", 143)]
    [InlineData("PWR", "100", 213)]
    [InlineData("SWR", "1.5", 48)]
    [InlineData("SWR", "2.0", 80)]
    [InlineData("SWR", "3.0", 120)]
    [InlineData("Compression", "15", 130)]
    [InlineData("Compression", "30", 241)]
    [InlineData("ALC", "100", 120)]
    [InlineData("IDD", "25", 241)]
    [InlineData("VPA", "16", 241)]
    [InlineData("S-Meter", "S9", 120)]
    [InlineData("S-Meter", "+60", 241)]
    public void TheMeterTableHasTheManualsFigures(string meter, string reading, int raw)
    {
        var point = Meter(meter).GetProperty("points").EnumerateArray()
            .First(p => p.GetProperty("Radio").GetString() == reading);
        Assert.Equal(raw, point.GetProperty("raw").GetInt32());
    }

    [Theory]
    [InlineData("PWR")]
    [InlineData("SWR")]
    [InlineData("Compression")]
    [InlineData("ALC")]
    [InlineData("IDD")]
    [InlineData("VPA")]
    [InlineData("S-Meter")]
    public void EveryMeterRisesFromZero(string meter)
    {
        var raws = Meter(meter).GetProperty("points").EnumerateArray()
            .Select(p => p.GetProperty("raw").GetInt32()).ToList();
        Assert.Equal(0, raws[0]);
        Assert.Equal(raws.OrderBy(r => r).Distinct(), raws);
    }

    [Fact]
    public void Ic7100MemoriesAreRefusedForNow() =>
        Assert.False(RadioProfile.Ic7100.HasRadioMemories);

    [Fact]
    public void Ic7300MemoriesStillWork()
    {
        Assert.True(RadioProfile.Ic7300.HasRadioMemories);
        Assert.True(RadioProfile.Ic7300Mk2.HasRadioMemories);
    }
}
