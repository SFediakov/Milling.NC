using Miller.Core.HeightMaps;

namespace Miller.Core.Native;

// The processes Backwards_GPU runs on the GPU (T-177), callable on their own so that each can be held
// against its CPU counterpart: the closing (HeightMapDilation.ComputeRemaining on the CPU), the route
// floor of one route (RouteFloor on the CPU) and the start tours of the route solver.
public static class GpuProcesses
{
    public static unsafe HeightMap Remaining(HeightMap tip, ToolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(profile);
        var remaining = CoreNative.Empty(tip, float.NaN);
        var grid = CoreNative.GridOf(tip);
        var offsets = CoreNative.OffsetsOf(profile.Offsets);
        fixed (float* t = tip.Z, r = remaining.Z)
        fixed (CoreNative.Offset* o = offsets)
        {
            CoreNative.Check(CoreNative.mn_gpu_remaining(&grid, t, o, offsets.Length, r));
        }

        return remaining;
    }

    // Over uncut stock (the floor not below the stock) the route floor rises to `safe` and the touch floor
    // stays above what the tip map keeps; the result holds the route floor, then the touch floor.
    public static unsafe float[] RouteFloor(float[] stock, float[] tip, float[] floor, float safe)
    {
        Check(stock, tip, floor);
        var lifted = new float[2 * floor.Length];
        fixed (float* s = stock, t = tip, f = floor, l = lifted)
        {
            CoreNative.Check(CoreNative.mn_route_floor(s, t, f, floor.Length, safe, l));
        }

        return lifted;
    }

    public static unsafe float[] GpuRouteFloor(float[] stock, float[] tip, float[] floor, float safe)
    {
        Check(stock, tip, floor);
        var lifted = new float[2 * floor.Length];
        fixed (float* s = stock, t = tip, f = floor, l = lifted)
        {
            CoreNative.Check(CoreNative.mn_gpu_route_floor(s, t, f, floor.Length, safe, l));
        }

        return lifted;
    }

    // The `keep` shortest of `tours` nearest-neighbour tours from `start` (the fixed `end` last, -1 for
    // none), shortest first.
    public static unsafe IReadOnlyList<int[]> StartTours(float[] x, float[] y, int start, int end, int tours, int keep)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Length != y.Length)
        {
            throw new ArgumentException($"{x.Length} x and {y.Length} y coordinates.", nameof(y));
        }

        var n = x.Length;
        var orders = new int[Math.Max(keep, 1) * n];
        int kept;
        fixed (float* px = x, py = y)
        fixed (int* o = orders)
        {
            CoreNative.Check(CoreNative.mn_gpu_start_tours(px, py, n, start, end, tours, keep, o, &kept));
        }

        return Enumerable.Range(0, kept).Select(t => orders.AsSpan(t * n, n).ToArray()).ToArray();
    }

    private static void Check(float[] stock, float[] tip, float[] floor)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(floor);
        if (stock.Length != floor.Length || tip.Length != floor.Length)
        {
            throw new ArgumentException($"Stock {stock.Length}, tip {tip.Length} and floor {floor.Length} cells must match.", nameof(floor));
        }
    }
}
