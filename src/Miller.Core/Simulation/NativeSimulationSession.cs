using Miller.Core.Generation;
using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

// The simulation's whole segments on the collision check's banded walk over the CPU workers (T-207,
// mn_sim_open/walk/close): the footprint, the annulus, the segments and the worker pool are taken once
// for a loaded toolpath and kept until Dispose, so a walk only reads the stock it is given. The stamps
// and the checks follow the per-sample engine's rules, and the events are one per check position from
// its first entered cell, so a walk leaves the stock and finds the events SimulationEngine finds.
public sealed unsafe class NativeSimulationSession : IDisposable
{
    private IntPtr _session;

    public NativeSimulationSession(Toolpath toolpath, HeightMap stock, ToolProfile profile, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.CellSize != stock.CellSize)
        {
            throw new ArgumentException($"Profile cell size {profile.CellSize} differs from the stock cell size {stock.CellSize}.", nameof(profile));
        }

        var grid = CoreNative.GridOf(stock);
        var segments = CoreNative.SegmentsOf(toolpath);
        var offsets = profile.NativeOffsets;
        var annulus = profile.NativeAnnulus;
        Count = segments.Length;
        IntPtr session;
        fixed (CoreNative.Segment* s = segments)
        fixed (CoreNative.Offset* o = offsets, a = annulus)
        {
            CoreNative.Check(CoreNative.mn_sim_open(&grid, o, offsets.Length, a, annulus.Length, profile.Tool.CutterRadius, profile.Tool.CutterLength, tolerance, s, segments.Length, &session));
        }

        _session = session;
    }

    public int Count { get; }

    // Segments [first, last) over the stock in place; the rectangle holds every changed cell.
    public DirtyRect Walk(HeightMap stock, HeightMap model, float floor, int first, int last, out IReadOnlyList<SimulationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(model);
        ObjectDisposedException.ThrowIf(_session == IntPtr.Zero, this);
        if (!stock.SameGridAs(model))
        {
            throw new ArgumentException("Stock and model must share the same grid.", nameof(model));
        }

        CoreNative.Collision* found = null;
        int count;
        CoreNative.Dirty dirty;
        fixed (float* z = stock.Z, m = model.Z)
        {
            CoreNative.Check(CoreNative.mn_sim_walk(_session, first, last, z, m, floor, &found, &count, &dirty));
        }

        try
        {
            events = ToolpathGeneration.ReportOf(found, count, Array.Empty<CollisionContact>()).Events;
        }
        finally
        {
            CoreNative.mn_free(found);
        }

        return new DirtyRect(dirty.I0, dirty.J0, dirty.I1, dirty.J1);
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero)
        {
            CoreNative.mn_sim_close(_session);
            _session = IntPtr.Zero;
        }
    }
}
