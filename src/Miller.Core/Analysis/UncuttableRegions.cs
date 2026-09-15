using Miller.Core.Geometry;
using Miller.Core.HeightMaps;

namespace Miller.Core.Analysis;

public sealed record UncuttableResult(
    bool[,] Overhang,
    bool[,] HeadLimited,
    bool[,] CornerLimited,
    int OverhangCells,
    int HeadLimitedCells,
    int CornerLimitedCells);

// What a 3-axis mill cannot produce: Overhang (a downward face below the top surface, hidden from
// +Z), HeadLimited (the head keeps the tip up, from the pipeline mask) and CornerLimited (the
// material the cutter leaves, the closing of the tip map, stands above the model where the head is
// not the cause: the cutter radius is larger than the local concave radius).
public static class UncuttableRegions
{
    public static UncuttableResult Compute(Mesh machineMesh, HeightMap model, HeightMap effectiveTip, bool[,] headLimited, ToolProfile profile, float floor, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(machineMesh);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        ArgumentNullException.ThrowIfNull(headLimited);
        ArgumentNullException.ThrowIfNull(profile);
        if (!model.SameGridAs(effectiveTip) || headLimited.GetLength(0) != model.Width || headLimited.GetLength(1) != model.Height)
        {
            throw new ArgumentException("Model, effective tip and head-limited mask must share the same grid.", nameof(effectiveTip));
        }

        var down = new HeightMap(model.OriginX, model.OriginY, model.CellSize, model.Width, model.Height, float.NaN);
        MeshRasterizer.RasterizeDownwardFacing(machineMesh, down);
        var remaining = HeightMapDilation.ComputeRemaining(effectiveTip, profile);

        var overhang = new bool[model.Width, model.Height];
        var head = new bool[model.Width, model.Height];
        var corner = new bool[model.Width, model.Height];
        int overhangCells = 0, headCells = 0, cornerCells = 0;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                var m = model[i, j];
                if (float.IsNaN(m))
                {
                    continue;
                }

                var hasModel = m > floor + FinalModelAnalyzer.FloorTolerance;
                var d = down[i, j];
                if (hasModel && !float.IsNaN(d) && d < m - tolerance && d > floor + tolerance)
                {
                    overhang[i, j] = true;
                    overhangCells++;
                }

                if (headLimited[i, j])
                {
                    head[i, j] = true;
                    headCells++;
                    continue;
                }

                var r = remaining[i, j];
                if (!float.IsNaN(r) && r - m > tolerance)
                {
                    corner[i, j] = true;
                    cornerCells++;
                }
            }
        }

        return new UncuttableResult(overhang, head, corner, overhangCells, headCells, cornerCells);
    }
}
