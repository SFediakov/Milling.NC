using System.Numerics;
using Miller.Core.HeightMaps;

namespace Miller.Core.Toolpaths;

// One sample of a feed or plunge segment that sits below the effective tip map by more than the
// tolerance. Depth is how far below (positive).
public readonly record struct GougeViolation(int SegmentIndex, Vector3 Position, float Depth);

// Every strategy test and the analysis run this. Rapids are not checked here; the simulation's
// CollisionDetector reports rapids through material.
public static class GougeChecker
{
    public static IReadOnlyList<GougeViolation> Verify(Toolpath toolpath, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var violations = new List<GougeViolation>();
        var step = effectiveTip.CellSize / 2;
        for (var index = 0; index < toolpath.Count; index++)
        {
            var segment = toolpath.Segments[index];
            if (segment.Kind == MoveKind.Rapid)
            {
                continue;
            }

            var length = segment.Length;
            var samples = length > 0 ? (int)MathF.Ceiling(length / step) : 0;
            for (var k = 0; k <= samples; k++)
            {
                var p = samples == 0 ? segment.Start : Vector3.Lerp(segment.Start, segment.End, (float)k / samples);
                var (i, j) = effectiveTip.CellOf(p.X, p.Y);
                if (!effectiveTip.InBounds(i, j))
                {
                    continue;
                }

                var limit = effectiveTip[i, j];
                if (float.IsNaN(limit))
                {
                    continue;
                }

                var depth = limit - p.Z;
                if (depth > tolerance)
                {
                    violations.Add(new GougeViolation(index, p, depth));
                }
            }
        }

        return violations;
    }
}
