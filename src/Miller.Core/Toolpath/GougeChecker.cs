using System.Numerics;
using Miller.Core.HeightMaps;

namespace Miller.Core.Toolpaths;

// One sample of a feed or plunge segment that sits below the effective tip map by more than the
// tolerance. Depth is how far below (positive).
public readonly record struct GougeViolation(int SegmentIndex, Vector3 Position, float Depth);

// Discrete model: every cell is a flat plateau at its tip value, and a sample is judged by the cell it
// falls in. Strategies are written to be safe under this model, and the material-removal simulation
// uses the same cells, so the two agree. Rapids are not checked here; the simulation reports rapids
// through material.
public static class GougeChecker
{
    public static IReadOnlyList<GougeViolation> Verify(Toolpath toolpath, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var violations = new List<GougeViolation>();
        for (var index = 0; index < toolpath.Count; index++)
        {
            var segment = toolpath.Segments[index];
            if (segment.Kind == MoveKind.Rapid)
            {
                continue;
            }

            foreach (var p in Samples(segment, effectiveTip.CellSize / 2))
            {
                var depth = Depth(p, effectiveTip);
                if (depth > tolerance)
                {
                    violations.Add(new GougeViolation(index, p, depth));
                }
            }
        }

        return violations;
    }

    // True when a feed or plunge along this segment never dips below the tip map by more than the
    // tolerance. Strategies use it to decide whether two passes may be joined without a retract.
    public static bool IsClear(ToolpathSegment segment, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(effectiveTip);
        foreach (var p in Samples(segment, effectiveTip.CellSize / 2))
        {
            if (Depth(p, effectiveTip) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<Vector3> Samples(ToolpathSegment segment, float step)
    {
        var length = segment.Length;
        var samples = length > 0 ? (int)MathF.Ceiling(length / step) : 0;
        for (var k = 0; k <= samples; k++)
        {
            yield return samples == 0 ? segment.Start : Vector3.Lerp(segment.Start, segment.End, (float)k / samples);
        }
    }

    // How far the sample sits below the tip; negative infinity where nothing constrains it.
    private static float Depth(Vector3 p, HeightMap effectiveTip)
    {
        var (i, j) = effectiveTip.CellOf(p.X, p.Y);
        if (!effectiveTip.InBounds(i, j))
        {
            return float.NegativeInfinity;
        }

        var limit = effectiveTip[i, j];
        return float.IsNaN(limit) ? float.NegativeInfinity : limit - p.Z;
    }
}
