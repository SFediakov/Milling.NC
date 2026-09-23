using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Native;

namespace Miller.Core.Toolpaths;

// One sample of a feed or plunge segment that sits below the effective tip map by more than the
// tolerance. Depth is how far below (positive).
public readonly record struct GougeViolation(int SegmentIndex, Vector3 Position, float Depth);

// Discrete model (native mn_gouge_verify, mn_gouge_is_clear): every cell is a flat plateau at its tip
// value, and a sample (every half cell along the segment) is judged by the cell it falls in.
// Strategies are written to be safe under this model, and the material-removal simulation uses the
// same cells, so the two agree. Rapids are not checked here; the simulation reports rapids through
// material.
public static class GougeChecker
{
    public static unsafe IReadOnlyList<GougeViolation> Verify(Toolpath toolpath, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var segments = CoreNative.SegmentsOf(toolpath);
        var grid = CoreNative.GridOf(effectiveTip);
        int* indices = null;
        float* positions = null;
        float* depths = null;
        int count;
        fixed (CoreNative.Segment* s = segments)
        fixed (float* t = effectiveTip.Z)
        {
            CoreNative.Check(CoreNative.mn_gouge_verify(s, segments.Length, &grid, t, tolerance, &indices, &positions, &depths, &count));
        }

        try
        {
            var violations = new List<GougeViolation>(count);
            for (var k = 0; k < count; k++)
            {
                violations.Add(new GougeViolation(indices[k], new Vector3(positions[3 * k], positions[3 * k + 1], positions[3 * k + 2]), depths[k]));
            }

            return violations;
        }
        finally
        {
            CoreNative.mn_free(indices);
            CoreNative.mn_free(positions);
            CoreNative.mn_free(depths);
        }
    }

    // True when a feed or plunge along this segment never dips below the tip map by more than the
    // tolerance.
    public static unsafe bool IsClear(ToolpathSegment segment, HeightMap effectiveTip, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var native = CoreNative.SegmentOf(segment);
        var grid = CoreNative.GridOf(effectiveTip);
        fixed (float* t = effectiveTip.Z)
        {
            return CoreNative.mn_gouge_is_clear(&native, &grid, t, tolerance) != 0;
        }
    }
}
