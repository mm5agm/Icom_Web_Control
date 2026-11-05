using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

/// <summary>
/// The controls that differ by radio and band: power, preamp, attenuator, and
/// the ones an IC-7100 does not have. Its figures are from its manual. On an
/// IC-7300 nothing here may change what it has always done.
/// </summary>
public class RadioBandControlsTests
{
    [Theory]
    [InlineData(14_074_000, 100)]
    [InlineData(50_313_000, 100)]
    [InlineData(70_100_000, 50)]
    [InlineData(145_500_000, 50)]
    [InlineData(433_500_000, 35)]
    [InlineData(100_000_000, 100)]   // receive only: nothing goes out
    public void Ic7100PowerDependsOnTheBand(long hz, int watts) =>
        Assert.Equal(watts, RadioProfile.Ic7100.MaxWattsAt(hz));

    [Theory]
    [InlineData(433_500_000, 35, 100)]
    [InlineData(433_500_000, 5, 14)]
    [InlineData(145_500_000, 25, 50)]
    [InlineData(14_074_000, 40, 40)]
    public void Ic7100WattsBecomeAPercentOfTheBandsMost(long hz, int watts, int percent) =>
        Assert.Equal(percent, RadioProfile.Ic7100.PercentFor(hz, watts));

    [Theory]
    [InlineData(433_500_000, 100, 35)]
    [InlineData(145_500_000, 50, 25)]
    [InlineData(14_074_000, 40, 40)]
    public void Ic7100PercentBecomesWatts(long hz, int percent, int watts) =>
        Assert.Equal(watts, RadioProfile.Ic7100.WattsFor(hz, percent));

    [Theory]
    [InlineData(1_800_000)]
    [InlineData(14_074_000)]
    [InlineData(70_100_000)]
    public void Ic7300WattsAreStillThePercent(long hz)
    {
        for (int watts = 0; watts <= 100; watts++)
        {
            Assert.Equal(watts, RadioProfile.Ic7300Mk2.PercentFor(hz, watts));
            Assert.Equal(watts, RadioProfile.Ic7300.WattsFor(hz, watts));
        }
    }

    [Theory]
    [InlineData(14_074_000, 2)]
    [InlineData(50_313_000, 2)]
    [InlineData(70_100_000, 2)]
    [InlineData(144_000_000, 1)]
    [InlineData(433_500_000, 1)]
    public void Ic7100HasOnePreampOnVhfAndUhf(long hz, int steps) =>
        Assert.Equal(steps, RadioProfile.Ic7100.PreampSteps(hz));

    [Fact]
    public void Ic7300AlwaysHasTwoPreamps() =>
        Assert.Equal(2, RadioProfile.Ic7300.PreampSteps(70_100_000));

    [Fact]
    public void TheAttenuatorSizeIsTheRadiosOwn()
    {
        Assert.Equal(12, RadioProfile.Ic7100.AttenuatorDb);
        Assert.Equal(20, RadioProfile.Ic7300.AttenuatorDb);
        Assert.Equal(20, RadioProfile.Ic7300Mk2.AttenuatorDb);
    }

    [Fact]
    public void Ic7100HasNoApfOrDeltaTx()
    {
        Assert.False(RadioProfile.Ic7100.HasApf);
        Assert.False(RadioProfile.Ic7100.HasDeltaTx);
    }

    [Fact]
    public void Ic7300HasBoth()
    {
        Assert.True(RadioProfile.Ic7300.HasApf);
        Assert.True(RadioProfile.Ic7300Mk2.HasDeltaTx);
    }
}
