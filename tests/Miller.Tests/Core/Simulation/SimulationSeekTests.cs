using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Simulation;

// Seeking by distance composes with stepping by time: the stock after SeekTo equals the stock after
// the steps that cover the same length.
public sealed class SimulationSeekTests
{
    private const float Cell = 0.5f;

    private static HeightMap Stock() => new(0, 0, Cell, 20, 20, 5f);

    private static ToolProfile Profile() => ToolProfile.Create(new ToolDefinition { CutterDiameter = 2f, CutterLength = 20, HeadDiameter = 6f }, Cell);

    // Rapid 1 mm at 3000, plunge 5 mm at 200, feed 7 mm at 800, feed 4 mm at 400, rapid up 5 mm.
    private static Toolpath Path()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0.25f, 5.25f, 8), new Vector3(1.25f, 5.25f, 8), MoveKind.Rapid, 3000));
        path.Add(new ToolpathSegment(new Vector3(1.25f, 5.25f, 8), new Vector3(1.25f, 5.25f, 3), MoveKind.Plunge, 200));
        path.Add(new ToolpathSegment(new Vector3(1.25f, 5.25f, 3), new Vector3(8.25f, 5.25f, 3), MoveKind.Feed, 800));
        path.Add(new ToolpathSegment(new Vector3(8.25f, 5.25f, 3), new Vector3(8.25f, 9.25f, 3), MoveKind.Feed, 400));
        path.Add(new ToolpathSegment(new Vector3(8.25f, 9.25f, 3), new Vector3(8.25f, 9.25f, 8), MoveKind.Rapid, 3000));
        return path;
    }

    private const float Total = 1 + 5 + 7 + 4 + 5;
    private const double TotalSeconds = 1 / 50.0 + 5 / (200 / 60.0) + 7 / (800 / 60.0) + 4 / (400 / 60.0) + 5 / 50.0;

    [Fact]
    public void SeekTo_MidSegment_MatchesSteppingByTime()
    {
        // 9.5 mm: rapid, plunge and 3.5 mm of the first feed = 0.02 + 1.5 + 0.2625 s.
        var sought = Stock();
        var seek = new SimulationEngine(Path(), sought, Profile());
        var result = seek.SeekTo(9.5f);
        Assert.Equal(2, result.SegmentsCompleted);
        Assert.False(result.Finished);
        Assert.Equal(new Vector3(4.75f, 5.25f, 3), result.ToolPosition);
        Assert.Equal(9.5f, seek.CoveredLength, 4);
        Assert.Equal(Total, seek.TotalLength, 4);
        Assert.Equal(9.5f / Total, seek.Progress, 4);
        Assert.Equal(0.02 + 1.5 + 0.2625, seek.ElapsedSeconds, 4);

        var stepped = Stock();
        var step = new SimulationEngine(Path(), stepped, Profile());
        step.Step(0.02 + 1.5 + 0.2625);
        Assert.Equal(stepped.Z, sought.Z);
        Assert.Equal(step.ToolPosition.X, seek.ToolPosition.X, 3);
        Assert.False(result.Dirty.IsEmpty);
    }

    [Fact]
    public void SeekTo_Forward_ContinuesFromTheCurrentPosition_AndToTheEndFinishes()
    {
        var stock = Stock();
        var engine = new SimulationEngine(Path(), stock, Profile());
        engine.SeekTo(9.5f);
        var result = engine.SeekTo(15f);
        Assert.Equal(new Vector3(8.25f, 7.25f, 3), result.ToolPosition);
        Assert.Equal(3, result.SegmentsCompleted);

        var end = engine.SeekTo(Total + 10f);
        Assert.True(end.Finished);
        Assert.Equal(1f, engine.Progress);
        Assert.Equal(TotalSeconds, engine.ElapsedSeconds, 4);
        var whole = Stock();
        new SimulationEngine(Path(), whole, Profile()).RunToEnd(TestContext.Current.CancellationToken);
        Assert.Equal(whole.Z, stock.Z);
    }

    [Fact]
    public void SeekTo_Backward_Throws_UntilReset()
    {
        var engine = new SimulationEngine(Path(), Stock(), Profile());
        engine.SeekTo(10f);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SeekTo(9f));
        var fresh = Stock();
        engine.Reset(fresh);
        Assert.Equal(0f, engine.CoveredLength);
        Assert.Equal(0, engine.ElapsedSeconds);
        engine.SeekTo(9f);
        Assert.Equal(9f, engine.CoveredLength, 4);
        Assert.Same(fresh, engine.Stock);
    }

    [Fact]
    public void ElapsedSeconds_FollowsStepsAndSeeks()
    {
        var engine = new SimulationEngine(Path(), Stock(), Profile());
        engine.Step(1.0);
        Assert.Equal(1.0, engine.ElapsedSeconds, 4);
        engine.SeekTo(6f);
        Assert.Equal(0.02 + 1.5, engine.ElapsedSeconds, 4);
        engine.Step(0.1);
        Assert.Equal(0.02 + 1.5 + 0.1, engine.ElapsedSeconds, 4);
    }

    [Fact]
    public void Clock_Seek_SetsTheElapsedTimeAndKeepsThePlayState()
    {
        var clock = new SimulationClock();
        clock.Play();
        clock.Advance(2);
        clock.Seek(0.5);
        Assert.Equal(0.5, clock.ElapsedSimulated);
        Assert.True(clock.IsPlaying);
        clock.Advance(1);
        Assert.Equal(1.5, clock.ElapsedSimulated);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Seek(-1));
    }
}
