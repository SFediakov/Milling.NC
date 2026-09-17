using Miller.Core.Simulation;
using Xunit;

namespace Miller.Tests.Core.Simulation;

public sealed class SimulationClockTests
{
    [Theory]
    [InlineData(0.01f, 0.1f)]
    [InlineData(0f, 0.1f)]
    [InlineData(-5f, 0.1f)]
    [InlineData(0.1f, 0.1f)]
    [InlineData(1f, 1f)]
    [InlineData(1000f, 1000f)]
    [InlineData(5000f, 1000f)]
    [InlineData(float.PositiveInfinity, 1000f)]
    [InlineData(float.NaN, 0.1f)]
    public void SpeedFactor_IsClampedToTheRange(float requested, float expected)
    {
        var clock = new SimulationClock { SpeedFactor = requested };
        Assert.Equal(expected, clock.SpeedFactor);
    }

    [Fact]
    public void Defaults_AreOneTimesPausedAndZero()
    {
        var clock = new SimulationClock();
        Assert.Equal(1f, clock.SpeedFactor);
        Assert.False(clock.IsPlaying);
        Assert.Equal(0, clock.ElapsedSimulated);
        Assert.Equal(0.1f, SimulationClock.MinSpeedFactor);
        Assert.Equal(1000f, SimulationClock.MaxSpeedFactor);
    }

    [Fact]
    public void Advance_WhilePaused_ReturnsZeroAndKeepsElapsed()
    {
        var clock = new SimulationClock { SpeedFactor = 10f };
        Assert.Equal(0, clock.Advance(3));
        Assert.Equal(0, clock.ElapsedSimulated);
        clock.Play();
        clock.Advance(1);
        clock.Pause();
        Assert.Equal(0, clock.Advance(1));
        Assert.Equal(10, clock.ElapsedSimulated, 9);
    }

    [Fact]
    public void Advance_TwoSecondsAtThousandTimes_YieldsTwoThousand()
    {
        var clock = new SimulationClock { SpeedFactor = 1000f };
        clock.Play();
        Assert.Equal(2000, clock.Advance(2), 9);
        Assert.Equal(2000, clock.ElapsedSimulated, 9);
        // The factor is a float: 0.1f times 5 carries float rounding into the double.
        clock.SpeedFactor = 0.1f;
        Assert.Equal(0.5, clock.Advance(5), 6);
        Assert.Equal(2000.5, clock.ElapsedSimulated, 6);
    }

    [Fact]
    public void Advance_NegativeOrNaN_ReturnsZero()
    {
        var clock = new SimulationClock();
        clock.Play();
        Assert.Equal(0, clock.Advance(-1));
        Assert.Equal(0, clock.Advance(double.NaN));
        Assert.Equal(0, clock.ElapsedSimulated);
    }

    [Fact]
    public void Reset_StopsAndZeroesElapsed()
    {
        var clock = new SimulationClock { SpeedFactor = 2f };
        clock.Play();
        clock.Advance(4);
        clock.Reset();
        Assert.False(clock.IsPlaying);
        Assert.Equal(0, clock.ElapsedSimulated);
        Assert.Equal(2f, clock.SpeedFactor);
    }
}
