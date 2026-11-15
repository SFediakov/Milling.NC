using System.Numerics;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// T-224: the speed scale the shown path is colored on. A feed or plunge runs at its own rate, a rapid at
// the rapid rate whatever rate it carries (a program file's G0 carries the modal F).
public sealed class ToolpathSpeedScaleTests
{
    private const float RapidRate = 3000f;

    private static ToolpathSegment Move(MoveKind kind, float rate)
        => new(Vector3.Zero, new Vector3(10, 0, 0), kind, rate);

    [Fact]
    public void Speed_FeedAndPlungeAtTheirRate_RapidAtTheRapidRate()
    {
        Assert.Equal(800f, ToolpathSpeedScale.SpeedOf(Move(MoveKind.Feed, 800), RapidRate));
        Assert.Equal(300f, ToolpathSpeedScale.SpeedOf(Move(MoveKind.Plunge, 300), RapidRate));
        Assert.Equal(RapidRate, ToolpathSpeedScale.SpeedOf(Move(MoveKind.Rapid, 100), RapidRate));
        Assert.Equal(RapidRate, ToolpathSpeedScale.SpeedOf(Move(MoveKind.Rapid, 0), RapidRate));
    }

    [Fact]
    public void Of_SpansTheSlowestAndTheFastestMove()
    {
        var scale = ToolpathSpeedScale.Of(new[]
        {
            Move(MoveKind.Rapid, 100), Move(MoveKind.Feed, 800), Move(MoveKind.Plunge, 282.84f), Move(MoveKind.Feed, 1500),
        }, RapidRate);

        Assert.NotNull(scale);
        Assert.Equal(282.84f, scale.Slowest);
        Assert.Equal(RapidRate, scale.Fastest);
        Assert.Equal(RapidRate, scale.RapidRate);
        Assert.False(scale.IsSingleSpeed);
    }

    [Fact]
    public void Of_WithoutAMoveOfKnownSpeed_IsNull()
    {
        Assert.Null(ToolpathSpeedScale.Of(Array.Empty<ToolpathSegment>(), RapidRate));
        // A G1 before any F word carries 0; a rapid rate being typed can be 0 or NaN.
        Assert.Null(ToolpathSpeedScale.Of(new[] { Move(MoveKind.Feed, 0), Move(MoveKind.Plunge, 0) }, RapidRate));
        Assert.Null(ToolpathSpeedScale.Of(new[] { Move(MoveKind.Rapid, 800) }, float.NaN));
        Assert.Null(ToolpathSpeedScale.Of(new[] { Move(MoveKind.Rapid, 800) }, 0f));
    }

    [Fact]
    public void UnknownSpeeds_SetNoBound_AndSitAtTheSlowEnd()
    {
        var unknown = new[] { 0f, -5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
        var segments = unknown.Select(rate => Move(MoveKind.Feed, rate)).Append(Move(MoveKind.Feed, 500)).Append(Move(MoveKind.Feed, 1000)).ToArray();

        var scale = ToolpathSpeedScale.Of(segments, float.NaN);

        Assert.NotNull(scale);
        Assert.Equal(500f, scale.Slowest);
        Assert.Equal(1000f, scale.Fastest);
        foreach (var rate in unknown)
        {
            Assert.Equal(0f, scale.Fraction(Move(MoveKind.Feed, rate)));
        }

        Assert.Equal(0f, scale.Fraction(Move(MoveKind.Rapid, 800)));
    }

    [Fact]
    public void Fraction_IsExactAtTheBounds_LinearBetween_AndClamped()
    {
        var scale = new ToolpathSpeedScale(500, 1500, RapidRate);

        Assert.Equal(0f, scale.Fraction(500));
        Assert.Equal(1f, scale.Fraction(1500));
        Assert.Equal(0.5f, scale.Fraction(1000));
        Assert.Equal(0.25f, scale.Fraction(750));
        Assert.Equal(0f, scale.Fraction(100));
        Assert.Equal(1f, scale.Fraction(5000));
        Assert.Equal(1f, scale.Fraction(Move(MoveKind.Rapid, 100)));
        Assert.Equal(0.3f, scale.Fraction(Move(MoveKind.Feed, 800)), 6);
    }

    [Fact]
    public void OneSpeed_TakesTheSlowEnd_AndHasOneTick()
    {
        var scale = ToolpathSpeedScale.Of(new[] { Move(MoveKind.Feed, 800), Move(MoveKind.Plunge, 800) }, float.NaN);

        Assert.NotNull(scale);
        Assert.True(scale.IsSingleSpeed);
        Assert.Equal(0f, scale.Fraction(800));
        Assert.Equal(new[] { 800f }, scale.Ticks(5));
    }

    [Fact]
    public void Ticks_AreEqualSteps_FromTheSlowestToTheFastest()
    {
        var scale = new ToolpathSpeedScale(800, 3000, RapidRate);

        Assert.Equal(new[] { 800f, 1350f, 1900f, 2450f, 3000f }, scale.Ticks(5));
        Assert.Equal(new[] { 800f, 3000f }, scale.Ticks(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => scale.Ticks(1));

        // The last tick is the bound itself, not a sum that rounds next to it.
        var odd = new ToolpathSpeedScale(282.84271f, 1131.3708f, RapidRate);
        var ticks = odd.Ticks(7);
        Assert.Equal(odd.Slowest, ticks[0]);
        Assert.Equal(odd.Fastest, ticks[^1]);
        Assert.True(ticks.Zip(ticks.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [Theory]
    [InlineData(5f, 1f)]
    [InlineData(0f, 1f)]
    [InlineData(-1f, 1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.PositiveInfinity)]
    public void Constructor_RefusesAnInvertedOrUnknownRange(float slowest, float fastest)
    {
        Assert.Throws<ArgumentException>(() => new ToolpathSpeedScale(slowest, fastest, RapidRate));
    }

    [Fact]
    public void RapidRate_MovesOnlyTheRapids()
    {
        var segments = new[] { Move(MoveKind.Feed, 800), Move(MoveKind.Rapid, 800), Move(MoveKind.Feed, 1900) };

        var slow = ToolpathSpeedScale.Of(segments, 3000);
        var fast = ToolpathSpeedScale.Of(segments, 6000);

        Assert.NotNull(slow);
        Assert.NotNull(fast);
        Assert.Equal(0.5f, slow.Fraction(segments[2]));
        Assert.Equal(1100f / 5200f, fast.Fraction(segments[2]), 6);
        Assert.Equal(1f, fast.Fraction(segments[1]));
        Assert.Equal(800f, fast.Slowest);
        Assert.Equal(6000f, fast.Fastest);
    }
}
