namespace Miller.Core.HeightMaps;

// The head (holder) is wider than the cutter and sits CutterLength above the tip. At tip height z the
// model under the head ring must stay at or below z + CutterLength, so the tip cannot go lower than
// limit[i,j] = max over the annulus of model - CutterLength. This is checked against the finished
// surface (model map); the simulation's CollisionDetector checks the current stock separately.
public static class HeadClearance
{
    // NaN where no annulus cell holds model material: the head is unconstrained there.
    public static HeightMap ComputeHeadLimit(HeightMap model, ToolProfile profile, float cutterLength)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(profile);
        if (!(cutterLength > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cutterLength), cutterLength, "Cutter length must be positive.");
        }

        var limit = new HeightMap(model.OriginX, model.OriginY, model.CellSize, model.Width, model.Height, float.NaN);
        var annulus = profile.AnnulusOffsets;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                var highest = float.NaN;
                foreach (var o in annulus)
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

                    if (float.IsNaN(highest) || z > highest)
                    {
                        highest = z;
                    }
                }

                limit[i, j] = float.IsNaN(highest) ? float.NaN : highest - cutterLength;
            }
        }

        return limit;
    }

    // Effective tip = max(tip, limit); a NaN tip stays NaN, a NaN limit does not constrain.
    public static HeightMap ApplyHeadLimit(HeightMap tip, HeightMap limit)
    {
        RequireSameGrid(tip, limit);
        var effective = tip.Clone();
        for (var k = 0; k < effective.Z.Length; k++)
        {
            var t = tip.Z[k];
            var l = limit.Z[k];
            if (float.IsNaN(t) || float.IsNaN(l))
            {
                continue;
            }

            if (l > t)
            {
                effective.Z[k] = l;
            }
        }

        return effective;
    }

    // True where the head, not the cutter, decides the depth: limit > tip + tolerance.
    public static bool[,] HeadLimitedMask(HeightMap tip, HeightMap limit, float tolerance)
    {
        RequireSameGrid(tip, limit);
        var mask = new bool[tip.Width, tip.Height];
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                var t = tip[i, j];
                var l = limit[i, j];
                mask[i, j] = !float.IsNaN(t) && !float.IsNaN(l) && l > t + tolerance;
            }
        }

        return mask;
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
