using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

public class CivMacroCodecTests
{
    [Fact]
    public void SpacesAndSeparatorsDoNotMatter()
    {
        Assert.True(CivMacroCodec.TryParse("16 40 01; 1640 00;;", out var cmds, out var err), err);
        Assert.Equal(new[] { "16 40 01", "16 40 00" }, cmds.Select(CivMacroCodec.Describe));
    }

    [Theory]
    [InlineData("", "no CI-V command")]
    [InlineData(" ; ", "no CI-V command")]
    [InlineData("16 4", "odd number")]
    [InlineData("16 4G", "isn't hex")]
    [InlineData("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10", "longer than 16")]
    public void BadMacrosSayWhy(string text, string why)
    {
        Assert.False(CivMacroCodec.TryParse(text, out _, out var err));
        Assert.Contains(why, err);
    }
}
