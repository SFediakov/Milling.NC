using Miller.Core.Native;

namespace Miller.Core.HeightMaps;

// The drop cutter on a grid (native mn_tip_map). The tip may descend at cell (i, j) until any part of
// the tool bottom touches the model: tip[i,j] = max over the footprint of (model[i+dx, j+dy] - dz).
// Footprint cells outside the grid or holding NaN do not constrain the tip; a cell whose whole
// footprint is NaN or off-grid stays NaN.
public static class HeightMapDilation
{
    public static unsafe HeightMap ComputeTipMap(HeightMap model, ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        var tip = CoreNative.Empty(model, float.NaN);
        var grid = CoreNative.GridOf(model);
        var offsets = CoreNative.OffsetsOf(profile.Offsets);
        fixed (float* m = model.Z, t = tip.Z)
        fixed (CoreNative.Offset* o = offsets)
        {
            CoreNative.mn_tip_map(&grid, m, o, offsets.Length, t);
        }

        return tip;
    }

    // Material left once the tip has been everywhere the tip map allows (native mn_remaining): the
    // closing of the model by the footprint, min over the footprint of (tip[i+dx, j+dy] + dz). Never
    // below the model, higher than it wherever the cutter does not fit (strips beside walls, inner
    // corners). Positions outside the grid or with a NaN tip cannot hold the tool.
    public static unsafe HeightMap ComputeRemaining(HeightMap tip, ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(profile);
        var remaining = CoreNative.Empty(tip, float.NaN);
        var grid = CoreNative.GridOf(tip);
        var offsets = CoreNative.OffsetsOf(profile.Offsets);
        fixed (float* t = tip.Z, r = remaining.Z)
        fixed (CoreNative.Offset* o = offsets)
        {
            CoreNative.mn_remaining(&grid, t, o, offsets.Length, r);
        }

        return remaining;
    }
}
