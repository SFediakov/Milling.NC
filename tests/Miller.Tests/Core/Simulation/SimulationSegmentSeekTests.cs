using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Simulation;

// Seeking to a segment start covers whole segments exactly: the stock, the elapsed time and the
// position equal a seek by the summed length, and a target behind the tool needs a reset first.
public sealed class SimulationSegmentSeekTests
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

    [Fact]
    public void SeekToSegment_StopsExactlyAtTheSegmentStart()
    {
        var stock = Stock();
        var engine = new SimulationEngine(Path(), stock, Profile());
        Assert.True(engine.AtSegmentStart);

        var result = engine.SeekToSegment(3);
        Assert.Equal(3, result.SegmentsCompleted);
        Assert.Equal(3, engine.CurrentSegmentIndex);
        Assert.True(engine.AtSegmentStart);
        Assert.False(result.Finished);
        Assert.Equal(new Vector3(8.25f, 5.25f, 3), engine.ToolPosition);
        Assert.Equal(1 + 5 + 7, engine.CoveredLength, 4);
        Assert.Equal(1 / 50.0 + 5 / (200 / 60.0) + 7 / (800 / 60.0), engine.ElapsedSeconds, 4);
        Assert.False(result.Dirty.IsEmpty);

        var byLength = Stock();
        new SimulationEngine(Path(), byLength, Profile()).SeekTo(13f);
        Assert.Equal(byLength.Z, stock.Z);

        Assert.True(engine.SeekToSegment(3).Dirty.IsEmpty, "a seek to the current start covers nothing");

        var end = engine.SeekToSegment(99);
        Assert.True(end.Finished);
        Assert.True(engine.AtSegmentStart);
        Assert.Equal(5, engine.CurrentSegmentIndex);
        Assert.Equal(1f, engine.Progress);
        var whole = Stock();
        new SimulationEngine(Path(), whole, Profile()).RunToEnd(TestContext.Current.CancellationToken);
        Assert.Equal(whole.Z, stock.Z);
    }

    [Fact]
    public void SeekToSegment_Backward_Throws_UntilReset()
    {
        var engine = new SimulationEngine(Path(), Stock(), Profile());
        engine.SeekTo(9.5f);
        Assert.Equal(2, engine.CurrentSegmentIndex);
        Assert.False(engine.AtSegmentStart);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SeekToSegment(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SeekToSegment(1));

        Assert.Equal(3, engine.SeekToSegment(3).SegmentsCompleted);
        Assert.True(engine.AtSegmentStart);

        engine.Reset(Stock());
        Assert.Equal(0, engine.SeekToSegment(0).SegmentsCompleted);
        Assert.Equal(2, engine.SeekToSegment(2).SegmentsCompleted);
        Assert.Equal(new Vector3(1.25f, 5.25f, 3), engine.ToolPosition);
    }
}
