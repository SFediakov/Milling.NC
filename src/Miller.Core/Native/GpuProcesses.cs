using Miller.Core.HeightMaps;

namespace Miller.Core.Native;

// The processes Backwards_GPU runs on the GPU (T-177), callable on their own so that each can be held
// against its CPU counterpart: the closing (HeightMapDilation.ComputeRemaining on the CPU) and the start
// tours of the route solver.
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
}
