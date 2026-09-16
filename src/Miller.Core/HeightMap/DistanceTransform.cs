namespace Miller.Core.HeightMaps;

// Exact Euclidean distance from every cell center to the nearest cell center inside a mask, in
// world units: the separable lower envelope of parabolas (Felzenszwalb and Huttenlocher), one pass
// along the columns and one along the rows on squared distances, O(cells). Infinity everywhere
// when the mask is empty.
public static class DistanceTransform
{
    public static float[,] Distances(bool[,] mask, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (!(cellSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        var squared = new float[width, height];
        var line = new float[Math.Max(width, height)];
        var output = new float[line.Length];
        var vertices = new int[line.Length];
        var boundaries = new float[line.Length + 1];

        for (var i = 0; i < width; i++)
        {
            for (var j = 0; j < height; j++)
            {
                line[j] = mask[i, j] ? 0f : float.PositiveInfinity;
            }

            Transform(line, height, output, vertices, boundaries);
            for (var j = 0; j < height; j++)
            {
                squared[i, j] = output[j];
            }
        }

        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                line[i] = squared[i, j];
            }

            Transform(line, width, output, vertices, boundaries);
            for (var i = 0; i < width; i++)
            {
                squared[i, j] = output[i] == float.PositiveInfinity ? float.PositiveInfinity : MathF.Sqrt(output[i]) * cellSize;
            }
        }

        return squared;
    }

    // d[q] = min over p of (f[p] + (q - p)^2) for one line of n samples.
    private static void Transform(float[] f, int n, float[] d, int[] v, float[] z)
    {
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            if (f[q] == float.PositiveInfinity)
            {
                continue;
            }

            float s;
            while (true)
            {
                var p = v[k];
                if (f[p] == float.PositiveInfinity)
                {
                    s = float.NegativeInfinity;
                }
                else
                {
                    s = ((f[q] + q * (float)q) - (f[p] + p * (float)p)) / (2f * (q - p));
                }

                if (s > z[k] || k == 0)
                {
                    break;
                }

                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        if (f[v[0]] == float.PositiveInfinity && k == 0)
        {
            Array.Fill(d, float.PositiveInfinity, 0, n);
            return;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            var p = v[k];
            d[q] = f[p] == float.PositiveInfinity ? float.PositiveInfinity : f[p] + (q - p) * (float)(q - p);
        }
    }
}
