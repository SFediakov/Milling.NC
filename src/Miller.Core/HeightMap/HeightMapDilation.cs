namespace Miller.Core.HeightMaps;

// The drop cutter on a grid. The tip may descend at cell (i, j) until any part of the tool bottom
// touches the model: tip[i,j] = max over the footprint of (model[i+dx, j+dy] - dz). Footprint cells
// outside the grid or holding NaN do not constrain the tip; a cell whose whole footprint is NaN or
// off-grid stays NaN.
public static class HeightMapDilation
{
    public static HeightMap ComputeTipMap(HeightMap model, ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        var tip = new HeightMap(model.OriginX, model.OriginY, model.CellSize, model.Width, model.Height, float.NaN);
        var offsets = profile.Offsets;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                var best = float.NaN;
                foreach (var o in offsets)
                {
                    var ii = i + o.Dx;
                    var jj = j + o.Dy;
                    if (!model.InBounds(ii, jj))
                    {
                        continue;
                    }

                    var z = model[ii, jj];
                    if (float.IsNaN(z))
                    {
                        continue;
                    }

                    var candidate = z - o.Dz;
                    if (float.IsNaN(best) || candidate > best)
                    {
                        best = candidate;
                    }
                }

                tip[i, j] = best;
            }
        }

        return tip;
    }

    // Material left once the tip has been everywhere the tip map allows: the closing of the model
    // by the footprint, min over the footprint of (tip[i+dx, j+dy] + dz). Never below the model,
    // higher than it wherever the cutter does not fit (strips beside walls, inner corners).
    // Positions outside the grid or with a NaN tip cannot hold the tool and do not lower the result.
    public static HeightMap ComputeRemaining(HeightMap tip, ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(profile);
        var remaining = new HeightMap(tip.OriginX, tip.OriginY, tip.CellSize, tip.Width, tip.Height, float.NaN);
        var offsets = profile.Offsets;
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                var best = float.NaN;
                foreach (var o in offsets)
                {
                    var ii = i + o.Dx;
                    var jj = j + o.Dy;
                    if (!tip.InBounds(ii, jj))
                    {
                        continue;
                    }

                    var t = tip[ii, jj];
                    if (float.IsNaN(t))
                    {
                        continue;
                    }

                    var candidate = t + o.Dz;
                    if (float.IsNaN(best) || candidate < best)
                    {
                        best = candidate;
                    }
                }

                remaining[i, j] = best;
            }
        }

        return remaining;
    }
}
