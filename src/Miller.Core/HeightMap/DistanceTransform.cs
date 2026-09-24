using Miller.Core.Native;

namespace Miller.Core.HeightMaps;

// Exact Euclidean distance from every cell center to the nearest cell center inside a mask, in
// world units: the separable lower envelope of parabolas (Felzenszwalb and Huttenlocher), one pass
// along the columns and one along the rows on squared distances, O(cells) (native mn_distances).
// Infinity everywhere when the mask is empty.
public static class DistanceTransform
{
    public static unsafe float[,] Distances(bool[,] mask, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (!(cellSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        var bytes = CoreNative.Bytes(mask);
        var flat = new float[Math.Max(width * height, 1)];
        fixed (byte* m = bytes)
        fixed (float* d = flat)
        {
            CoreNative.Check(CoreNative.mn_distances(m, width, height, cellSize, d));
        }

        var result = new float[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                result[i, j] = flat[j * width + i];
            }
        }

        return result;
    }
}
