using Icom_Web_Control.Models;
using Icom_Web_Control.Services.Civ;

namespace IcomWebControl.Tests;

public class ScopeRefineTests
{
    [Fact]
    public void Step_0_is_the_raw_sweeps_and_each_step_is_smoother()
    {
        Assert.Equal(1.0, ScopeRefine.WeightFor(0));
        for (int n = 1; n <= ScopeRefine.MaxLevel; n++)
            Assert.True(ScopeRefine.WeightFor(n) < ScopeRefine.WeightFor(n - 1));
    }

    [Fact]
    public void No_step_lags_more_than_about_a_second_at_three_sweeps_a_second()
    {
        // A weight w follows a change in about 1/(sweeps a second × w) seconds.
        double lag = 1 / (3.2 * ScopeRefine.WeightFor(ScopeRefine.MaxLevel));
        Assert.InRange(lag, 0, 1.1);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(99, 5)]
    public void A_step_out_of_range_is_clamped(int level, int expected)
    {
        Assert.Equal(expected, ScopeRefine.Clamp(level));
        Assert.Equal(ScopeRefine.Weights[expected], ScopeRefine.WeightFor(level));
    }

    [Fact]
    public void New_settings_start_at_the_default()
    {
        Assert.Equal(ScopeRefine.DefaultLevel, new ApplicationSettings().ScopeRefineLevel);
        Assert.Equal(0.5, ScopeRefine.WeightFor(ScopeRefine.DefaultLevel));
    }
}
