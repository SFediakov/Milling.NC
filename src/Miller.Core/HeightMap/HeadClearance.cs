using Miller.Core.Native;

namespace Miller.Core.HeightMaps;

// The head (holder) is wider than the cutter and sits CutterLength above the tip, its underside Dz
// higher over each ring cell (ToolProfile.AnnulusOffsets). At tip height z the material under the
// ring must stay at or below z + CutterLength + Dz, so the tip cannot go lower than
// limit[i,j] = max over the ring of (material - Dz) - CutterLength (native mn_head_limit). The
// simulation's CollisionDetector checks the current stock with the same ring.
public static class HeadClearance
{
    // NaN where no ring cell holds material: the head is unconstrained there.
    public static unsafe HeightMap ComputeHeadLimit(HeightMap model, ToolProfile profile, float cutterLength)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        if (!(cutterLength > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cutterLength), cutterLength, "Cutter length must be positive.");
        }

        var limit = CoreNative.Empty(model, float.NaN);
        var grid = CoreNative.GridOf(model);
        var annulus = CoreNative.OffsetsOf(profile.AnnulusOffsets);
        fixed (float* m = model.Z, l = limit.Z)
        fixed (CoreNative.Offset* o = annulus)
        {
            CoreNative.mn_head_limit(&grid, m, o, annulus.Length, cutterLength, l);
        }

        return limit;
    }

    // Effective tip = max(tip, limit); a NaN tip stays NaN, a NaN limit does not constrain.
    public static unsafe HeightMap ApplyHeadLimit(HeightMap tip, HeightMap limit)
    {
        RequireSameGrid(tip, limit);
        var effective = CoreNative.Empty(tip, float.NaN);
        fixed (float* t = tip.Z, l = limit.Z, e = effective.Z)
        {
            CoreNative.mn_apply_head_limit(t, l, tip.CellCount, e);
        }

        return effective;
    }

    // True where the head, not the cutter, decides the depth: limit > tip + tolerance.
    public static unsafe bool[,] HeadLimitedMask(HeightMap tip, HeightMap limit, float tolerance)
    {
        RequireSameGrid(tip, limit);
        var mask = new byte[tip.CellCount];
        fixed (float* t = tip.Z, l = limit.Z)
        fixed (byte* m = mask)
        {
            CoreNative.mn_head_limited_mask(t, l, tip.CellCount, tolerance, m);
        }

        return CoreNative.Mask(mask, tip.Width, tip.Height);
    }

    private static void RequireSameGrid(HeightMap tip, HeightMap limit)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(limit);
        if (!tip.SameGridAs(limit))
        {
            throw new ArgumentException("Tip map and head-limit map must share the same grid.", nameof(limit));
        }
    }
}
