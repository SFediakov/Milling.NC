using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

public sealed class ToolpathSimplifierTests
{
    private const float Tolerance = 0.05f;
    private const float CellSize = 0.25f;
    private const float Feed = 800f;
    private static readonly HeightMap Flat = new(0, 0, CellSize, 120, 120, 0f);

    private static Toolpath Chain(MoveKind kind, float rate, params Vector3[] points)
    {
        var path = new Toolpath();
        for (var k = 1; k < points.Length; k++)
        {
            path.Add(new ToolpathSegment(points[k - 1], points[k], kind, rate));
        }

        return path;
    }

    private static List<Vector3> Points(Toolpath path)
    {
        var points = new List<Vector3> { path.Segments[0].Start };
        points.AddRange(path.Segments.Select(s => s.End));
        return points;
    }

    private static float MaxDeviation(IReadOnlyList<Vector3> original, Toolpath simplified)
    {
        var worst = 0f;
        foreach (var p in original)
        {
            var nearest = simplified.Segments.Min(s => ToolpathSimplifier.DistanceToSegment(p, s.Start, s.End));
            worst = MathF.Max(worst, nearest);
        }

        return worst;
    }

    [Fact]
    public void CollinearChain_BecomesOneSegment_EndPointsKept()
    {
        var points = Enumerable.Range(0, 41).Select(k => new Vector3(1 + k * CellSize, 5, 2 + k * 0.1f)).ToArray();
        var simplified = ToolpathSimplifier.Simplify(Chain(MoveKind.Feed, Feed, points), Flat, Tolerance);
        var segment = Assert.Single(simplified.Segments);
        Assert.Equal(points[0], segment.Start);
        Assert.Equal(points[^1], segment.End);
        Assert.Equal(MoveKind.Feed, segment.Kind);
        Assert.Equal(Feed, segment.FeedRate);
    }

    [Fact]
    public void Spike_IsKept_AndFlatStretchesAroundItMerge()
    {
        var points = new[]
        {
            new Vector3(1, 5, 2), new Vector3(2, 5, 2), new Vector3(3, 5, 2), new Vector3(3.5f, 5, 4), new Vector3(4, 5, 4),
            new Vector3(4.5f, 5, 2), new Vector3(6, 5, 2), new Vector3(9, 5, 2),
        };
        var simplified = ToolpathSimplifier.Simplify(Chain(MoveKind.Feed, Feed, points), Flat, Tolerance);
        Assert.Equal(new[] { points[0], points[2], points[3], points[4], points[5], points[7] }, Points(simplified));
    }

    [Fact]
    public void Circle_IsReducedToChordsWithinTheTolerance()
    {
        const float radius = 8f;
        var center = new Vector2(15, 15);
        var count = (int)MathF.Ceiling(2 * MathF.PI * radius / CellSize);
        var points = Enumerable.Range(0, count + 1)
            .Select(k => 2 * MathF.PI * k / count)
            .Select(a => new Vector3(center.X + radius * MathF.Cos(a), center.Y + radius * MathF.Sin(a), 1f))
            .ToArray();
        var original = Chain(MoveKind.Feed, Feed, points);
        var simplified = ToolpathSimplifier.Simplify(original, Flat, Tolerance);

        Assert.True(simplified.Count * 3 <= original.Count, $"{simplified.Count} chords for {original.Count} polygon edges");
        // Sagitta rule: a chord of length c over radius R deviates by R - sqrt(R^2 - c^2 / 4).
        var longestChord = 2 * MathF.Sqrt(2 * radius * Tolerance - Tolerance * Tolerance);
        Assert.All(simplified.Segments, s => Assert.True(s.Length <= longestChord + 1e-3f, $"chord {s.Length} longer than {longestChord}"));
        Assert.True(MaxDeviation(points, simplified) <= Tolerance + 1e-4f);
        Assert.Equal(points[0], simplified.Segments[0].Start);
        Assert.Equal(points[^1], simplified.Segments[^1].End);
    }

    [Fact]
    public void ChordThatWouldGouge_IsSplitAlthoughWithinTolerance()
    {
        // A wall cell covering y in [5, 5.25): the path skirts it 0.02 mm below, inside the tolerance
        // band, while the straight chord from a to b at y = 5 passes over it.
        var map = Flat.Clone();
        var (wi, wj) = map.CellOf(5.1f, 5.1f);
        map[wi, wj] = 3f;
        var a = new Vector3(1, 5, 0);
        var corner1 = new Vector3(4.9f, 4.98f, 0);
        var corner2 = new Vector3(5.4f, 4.98f, 0);
        var b = new Vector3(9, 5, 0);
        Assert.Equal(wj - 1, map.CellOf(corner1.X, corner1.Y).J);
        var path = Chain(MoveKind.Feed, Feed, a, corner1, corner2, b);
        Assert.Empty(GougeChecker.Verify(path, map, Tolerance));
        Assert.False(GougeChecker.IsClear(new ToolpathSegment(a, b, MoveKind.Feed, Feed), map, Tolerance));

        var simplified = ToolpathSimplifier.Simplify(path, map, Tolerance);
        Assert.Empty(GougeChecker.Verify(simplified, map, Tolerance));
        Assert.True(simplified.Count > 1, "the chord over the wall cell must not survive");
        Assert.Equal(a, simplified.Segments[0].Start);
        Assert.Equal(b, simplified.Segments[^1].End);
    }

    [Fact]
    public void RapidsPlungesAndRateChanges_AreNeverMerged()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(1, 1, 10), new Vector3(1, 1, 0), MoveKind.Plunge, 200));
        path.Add(new ToolpathSegment(new Vector3(1, 1, 0), new Vector3(2, 1, 0), MoveKind.Feed, 800));
        path.Add(new ToolpathSegment(new Vector3(2, 1, 0), new Vector3(3, 1, 0), MoveKind.Feed, 800));
        path.Add(new ToolpathSegment(new Vector3(3, 1, 0), new Vector3(4, 1, 0), MoveKind.Feed, 400));
        path.Add(new ToolpathSegment(new Vector3(4, 1, 0), new Vector3(5, 1, 0), MoveKind.Feed, 400));
        path.Add(new ToolpathSegment(new Vector3(5, 1, 0), new Vector3(5, 1, 10), MoveKind.Rapid, 3000));
        path.Add(new ToolpathSegment(new Vector3(5, 1, 10), new Vector3(9, 1, 10), MoveKind.Rapid, 3000));
        path.Add(new ToolpathSegment(new Vector3(9, 1, 10), new Vector3(9, 1, 0), MoveKind.Plunge, 200));
        path.Add(new ToolpathSegment(new Vector3(9, 1, 0), new Vector3(9, 5, 0), MoveKind.Feed, 800));
        path.Add(new ToolpathSegment(new Vector3(9, 6, 0), new Vector3(9, 8, 0), MoveKind.Feed, 800));

        var simplified = ToolpathSimplifier.Simplify(path, Flat, Tolerance);
        Assert.Equal(
            new[] { MoveKind.Plunge, MoveKind.Feed, MoveKind.Feed, MoveKind.Rapid, MoveKind.Rapid, MoveKind.Plunge, MoveKind.Feed, MoveKind.Feed },
            simplified.Segments.Select(s => s.Kind));
        Assert.Equal(new Vector3(3, 1, 0), simplified.Segments[1].End);
        Assert.Equal(800f, simplified.Segments[1].FeedRate);
        Assert.Equal(new Vector3(5, 1, 0), simplified.Segments[2].End);
        Assert.Equal(400f, simplified.Segments[2].FeedRate);
        Assert.Equal(path.Segments[5], simplified.Segments[3]);
        Assert.Equal(path.Segments[7], simplified.Segments[5]);
        // A gap between two feeds is not a chain, so both stay.
        Assert.Equal(path.Segments[8], simplified.Segments[6]);
        Assert.Equal(path.Segments[9], simplified.Segments[7]);
    }

    [Fact]
    public void EmptyToolpath_AndZeroTolerance_Work()
    {
        Assert.Equal(0, ToolpathSimplifier.Simplify(new Toolpath(), Flat, Tolerance).Count);
        var points = new[] { new Vector3(1, 1, 0), new Vector3(2, 1, 0), new Vector3(3, 1, 0), new Vector3(3, 2, 0) };
        var exact = ToolpathSimplifier.Simplify(Chain(MoveKind.Feed, Feed, points), Flat, 0f);
        Assert.Equal(new[] { points[0], points[2], points[3] }, Points(exact));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolpathSimplifier.Simplify(new Toolpath(), Flat, -1f));
    }

    [Fact]
    public void SlopedRow_FromTheFinishingStrategy_IsOneInteriorSegment()
    {
        var map = new HeightMap(0, 0, CellSize, 60, 1, 0f);
        for (var i = 0; i < map.Width; i++)
        {
            map[i, 0] = 0.5f * map.CellCenter(i, 0).X;
        }

        var points = RasterFinishingStrategy.RowPoints(map, 0, map.Width - 1, 0);
        // Half-cell lead-in from the first center, one line through the boundary points, flat lead-out.
        Assert.Equal(4, points.Count);
        Assert.Equal(map[0, 0], points[0].Z);
        Assert.Equal(map[map.Width - 1, 0], points[^1].Z);
        var row = Chain(MoveKind.Feed, Feed, points.ToArray());
        // Float slack only: the line meets every plateau exactly at the cell's left edge.
        Assert.Empty(GougeChecker.Verify(row, map, 1e-4f));
        var simplified = ToolpathSimplifier.Simplify(row, map, Tolerance);
        Assert.True(simplified.Count <= 3);
        Assert.Empty(GougeChecker.Verify(simplified, map, Tolerance));
    }
    [Fact]
    public void ZeroLengthSegmentsInsideARun_AreAbsorbed_AndASinglePointRunStays()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(1, 1, 0), new Vector3(2, 1, 0), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(2, 1, 0), new Vector3(2, 1, 0), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(2, 1, 0), new Vector3(3, 1, 0), MoveKind.Feed, Feed));
        path.Add(new ToolpathSegment(new Vector3(3, 1, 0), new Vector3(3, 1, 10), MoveKind.Rapid, 3000));
        path.Add(new ToolpathSegment(new Vector3(7, 7, 0), new Vector3(7, 7, 0), MoveKind.Feed, Feed));
        var simplified = ToolpathSimplifier.Simplify(path, Flat, Tolerance);
        Assert.Equal(3, simplified.Count);
        Assert.Equal(new Vector3(1, 1, 0), simplified.Segments[0].Start);
        Assert.Equal(new Vector3(3, 1, 0), simplified.Segments[0].End);
        Assert.Equal(path.Segments[3], simplified.Segments[1]);
        Assert.Equal(path.Segments[4], simplified.Segments[2]);
    }
}
