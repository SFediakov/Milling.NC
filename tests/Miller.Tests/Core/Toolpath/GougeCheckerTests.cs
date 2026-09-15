using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class GougeCheckerTests
{
    private const float Tolerance = 0.05f;
    private static readonly HeightMap Flat = new(0, 0, 0.5f, 20, 20, 0f);

    private static Toolpath Path(params ToolpathSegment[] segments)
    {
        var path = new Toolpath();
        path.AddRange(segments);
        return path;
    }

    [Fact]
    public void SegmentOnTheSurface_HasNoViolation()
    {
        var path = Path(new ToolpathSegment(new Vector3(1, 1, 0), new Vector3(8, 1, 0), MoveKind.Feed, 800));
        Assert.Empty(GougeChecker.Verify(path, Flat, Tolerance));
        var withinTolerance = Path(new ToolpathSegment(new Vector3(1, 1, -0.04f), new Vector3(8, 1, -0.04f), MoveKind.Feed, 800));
        Assert.Empty(GougeChecker.Verify(withinTolerance, Flat, Tolerance));
    }

    [Fact]
    public void SegmentBelowTheSurface_IsFlaggedAtEverySample()
    {
        var path = Path(new ToolpathSegment(new Vector3(1, 1, -0.1f), new Vector3(3, 1, -0.1f), MoveKind.Feed, 800));
        var violations = GougeChecker.Verify(path, Flat, Tolerance);
        Assert.Equal(9, violations.Count); // 2 mm at 0.25 mm steps: 8 intervals, 9 samples
        Assert.All(violations, v => Assert.Equal(0, v.SegmentIndex));
        Assert.All(violations, v => Assert.Equal(0.1f, v.Depth, 4));
    }

    [Fact]
    public void Rapids_AreIgnored_PlungesAreChecked()
    {
        var rapid = Path(new ToolpathSegment(new Vector3(1, 1, 5), new Vector3(8, 1, -2), MoveKind.Rapid, 3000));
        Assert.Empty(GougeChecker.Verify(rapid, Flat, Tolerance));

        var plunge = Path(new ToolpathSegment(new Vector3(1, 1, 5), new Vector3(1, 1, -0.2f), MoveKind.Plunge, 200));
        var violations = GougeChecker.Verify(plunge, Flat, Tolerance);
        Assert.NotEmpty(violations);
        Assert.Equal(0.2f, violations.Max(v => v.Depth), 4);
    }

    [Fact]
    public void OffGridAndNaNCells_DoNotConstrain()
    {
        var map = Flat.Clone();
        map[2, 2] = float.NaN;
        var outside = Path(new ToolpathSegment(new Vector3(-5, 1, -1), new Vector3(-1, 1, -1), MoveKind.Feed, 800));
        Assert.Empty(GougeChecker.Verify(outside, map, Tolerance));
        var overNaN = Path(new ToolpathSegment(new Vector3(1.25f, 1.25f, -1), new Vector3(1.25f, 1.25f, -1), MoveKind.Feed, 800));
        Assert.Empty(GougeChecker.Verify(overNaN, map, Tolerance));
        var overMaterial = Path(new ToolpathSegment(new Vector3(1.75f, 1.25f, -1), new Vector3(1.75f, 1.25f, -1), MoveKind.Feed, 800));
        Assert.Single(GougeChecker.Verify(overMaterial, map, Tolerance));
    }

    [Fact]
    public void SegmentIndex_PointsAtTheOffendingSegment()
    {
        var path = Path(
            new ToolpathSegment(new Vector3(1, 1, 0), new Vector3(3, 1, 0), MoveKind.Feed, 800),
            new ToolpathSegment(new Vector3(3, 1, 0), new Vector3(3, 3, -1), MoveKind.Feed, 800));
        var violations = GougeChecker.Verify(path, Flat, Tolerance);
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal(1, v.SegmentIndex));
    }
}
