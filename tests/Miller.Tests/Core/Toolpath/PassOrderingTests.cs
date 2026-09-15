using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class PassOrderingTests
{
    private const float SafeZ = 20f;
    private static readonly CuttingParameters Parameters = new() { CellSize = 0.5f, FeedRate = 800, PlungeRate = 200, RapidRate = 3000, Tolerance = 0.05f };
    private static readonly HeightMap Clear = new(0, 0, 0.5f, 80, 80, 0f);

    private static Toolpath Row(float x0, float x1, float y, float z = 0f)
    {
        var pass = new Toolpath();
        pass.Add(new ToolpathSegment(new Vector3(x0, y, z), new Vector3(x1, y, z), MoveKind.Feed, 800));
        return pass;
    }

    private static Toolpath Square(float x, float y, float size, float z = 0f)
    {
        var pass = new Toolpath();
        var corners = new[] { new Vector3(x, y, z), new Vector3(x + size, y, z), new Vector3(x + size, y + size, z), new Vector3(x, y + size, z) };
        for (var k = 0; k < 4; k++)
        {
            pass.Add(new ToolpathSegment(corners[k], corners[(k + 1) % 4], MoveKind.Feed, 800));
        }

        return pass;
    }

    private static int Retracts(Toolpath linked) => ToolpathStatistics.Compute(linked, Parameters).RetractCount;

    [Fact]
    public void TwoRegionsGivenInterleaved_AreLinkedWithOneRetractBetweenThem()
    {
        // Left region rows at x 0..10, right region rows at x 30..40, interleaved row by row.
        var passes = new List<Toolpath>();
        for (var r = 0; r < 4; r++)
        {
            passes.Add(Row(0, 10, r * 0.5f));
            passes.Add(Row(30, 40, r * 0.5f));
        }

        var linked = ToolpathLinker.Link(passes, Parameters, SafeZ, Clear);
        // One retract between the regions plus the final retract; the naive order needs seven.
        Assert.Equal(2, Retracts(linked));
        var feeds = linked.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        Assert.True(feeds.Take(feeds.Count / 2).All(s => s.Start.X <= 10.01f), "the first half stays in one region");
    }

    [Fact]
    public void OpenPass_IsReversedWhenItsEndIsNearer()
    {
        var first = Row(0, 10, 0);
        var second = Row(0, 10, 0.5f);
        var ordered = PassOrdering.Order(new[] { first, second }, new Vector3(0, 0, 0), allowReverse: true);
        var oneWay = PassOrdering.Order(new[] { first, second }, new Vector3(0, 0, 0), allowReverse: false);
        Assert.Equal(new Vector3(0, 0.5f, 0), oneWay[1].Segments[0].Start);
        Assert.Equal(new Vector3(0, 0, 0), ordered[0].Segments[0].Start);
        Assert.Equal(new Vector3(10, 0.5f, 0), ordered[1].Segments[0].Start);
        Assert.Equal(new Vector3(0, 0.5f, 0), ordered[1].Segments[^1].End);
    }

    [Fact]
    public void ClosedLoop_KeepsItsDirection_AndStartsAtTheNearestVertex()
    {
        var loop = Square(10, 10, 4);
        var ordered = PassOrdering.Order(new[] { loop }, new Vector3(14.2f, 14.1f, 0), allowReverse: true);
        var rotated = ordered[0];
        Assert.True(PassOrdering.IsClosed(rotated));
        Assert.Equal(new Vector3(14, 14, 0), rotated.Segments[0].Start);
        Assert.Equal(SignedArea(loop), SignedArea(rotated), 3);
        Assert.Equal(4, rotated.Count);
    }

    [Fact]
    public void Groups_NeverInterleave_EvenWhenTheNextGroupIsCloser()
    {
        var level1 = new[] { Row(0, 10, 0, 3f), Row(30, 40, 0, 3f) };
        var level2 = new[] { Row(0, 10, 0.5f, 1f) };
        var linked = ToolpathLinker.Link(new IReadOnlyList<Toolpath>[] { level1, level2 }, Parameters, SafeZ, Clear);
        var feedZ = linked.Segments.Where(s => s.Kind == MoveKind.Feed).Select(s => s.Start.Z).ToList();
        Assert.Equal(new[] { 3f, 3f, 1f }, feedZ);
    }

    [Fact]
    public void EmptyPasses_AreSkipped_AndNoStart_KeepsTheGivenOrder()
    {
        var a = Row(0, 10, 0);
        var b = Row(20, 30, 5);
        var ordered = PassOrdering.Order(new[] { new Toolpath(), b, a }, null, allowReverse: true);
        Assert.Equal(2, ordered.Count);
        Assert.Same(b, ordered[0]);
        Assert.Equal(new Vector3(30, 5, 0), ordered[0].Segments[^1].End);
        // From (30, 5) the row a is nearer at its end, so it comes reversed.
        Assert.Equal(new Vector3(10, 0, 0), ordered[1].Segments[0].Start);
    }

    [Fact]
    public void Statistics_CountRetracts()
    {
        var linked = ToolpathLinker.Link(new[] { Row(0, 10, 0), Row(30, 40, 0) }, Parameters, SafeZ, Clear);
        var stats = ToolpathStatistics.Compute(linked, Parameters);
        Assert.Equal(2, stats.RetractCount);
        Assert.Equal(0, ToolpathStatistics.Compute(new Toolpath(), Parameters).RetractCount);
    }

    private static float SignedArea(Toolpath loop)
    {
        var area = 0f;
        foreach (var s in loop.Segments)
        {
            area += s.Start.X * s.End.Y - s.End.X * s.Start.Y;
        }

        return area / 2;
    }
}
