using System.Numerics;
using System.Runtime.InteropServices;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Solver;

namespace Miller.Core.Toolpaths;

// Turns a sequence of tool positions into segments (native mn_writer). A cut between two positions
// follows the surface polyline of SurfacePath over the clearance grid: level, rising and gently
// descending parts are feeds, a descent steeper than MaxRampSlope is a feed at the higher height to
// over the lower point and then a plunge, so the tool never drops down a wall at feed rate and never
// dips under a plateau. A travel between two positions takes the same polyline or a retract to safe
// Z, a rapid and a plunge, whichever the rates make faster; the program starts with a plunge from
// safe Z above the first position and ends with a retract.
public sealed class RouteWriter
{
    // Height differences below this are one level; float noise in a plateau is not a step.
    public const float LevelEpsilon = 1e-5f;

    // Steepest descent (drop per unit of XY travel) still cut as a ramp at feed rate; about 63
    // degrees. A drop down a wall crosses one cell and is far steeper, so it becomes a plunge.
    public const float MaxRampSlope = 2f;

    private readonly Handle _writer;
    private readonly float _safeZ;

    public unsafe RouteWriter(CuttingParameters parameters, float safeZ)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!(parameters.FeedRate > 0) || !(parameters.PlungeRate > 0) || !(parameters.RapidRate > 0))
        {
            throw new ArgumentException("Feed, plunge and rapid rates must be positive.", nameof(parameters));
        }

        var native = CoreNative.ParametersOf(parameters);
        IntPtr writer;
        CoreNative.Check(CoreNative.mn_writer_create(&native, safeZ, &writer));
        _writer = new Handle(writer);
        _safeZ = safeZ;
    }

    public unsafe Vector3? Position
    {
        get
        {
            var position = stackalloc float[3];
            var has = CoreNative.mn_writer_position(_writer.DangerousGetHandle(), position) != 0;
            GC.KeepAlive(_writer);
            return has ? new Vector3(position[0], position[1], position[2]) : null;
        }
    }

    public int Count
    {
        get
        {
            var count = CoreNative.mn_writer_count(_writer.DangerousGetHandle());
            GC.KeepAlive(_writer);
            return count;
        }
    }

    public unsafe void Travel(RoutePoint to, RouteGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (to.Z > _safeZ)
        {
            throw new ArgumentException($"Position {to} lies above safe Z {_safeZ}.", nameof(to));
        }

        var point = stackalloc float[] { to.X, to.Y, to.Z };
        var native = GridOf(grid);
        fixed (float* floor = grid.Floor)
        {
            CoreNative.Check(CoreNative.mn_writer_travel(_writer.DangerousGetHandle(), point, &native, floor));
        }

        GC.KeepAlive(_writer);
    }

    public unsafe void Follow(RoutePoint to, RouteGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (Position is null)
        {
            throw new InvalidOperationException("Travel to the first position before following the surface.");
        }

        var point = stackalloc float[] { to.X, to.Y, to.Z };
        var native = GridOf(grid);
        fixed (float* floor = grid.Floor)
        {
            CoreNative.Check(CoreNative.mn_writer_follow(_writer.DangerousGetHandle(), point, &native, floor));
        }

        GC.KeepAlive(_writer);
    }

    public unsafe Toolpath Finish()
    {
        CoreNative.Segment* segments = null;
        int count;
        CoreNative.Check(CoreNative.mn_writer_finish(_writer.DangerousGetHandle(), &segments, &count));
        GC.KeepAlive(_writer);
        try
        {
            return CoreNative.ToolpathOf(segments, count);
        }
        finally
        {
            CoreNative.mn_free(segments);
        }
    }

    private static CoreNative.Grid GridOf(RouteGrid grid) => new()
    {
        OriginX = grid.OriginX,
        OriginY = grid.OriginY,
        CellSize = grid.CellSize,
        Width = grid.Width,
        Height = grid.Height,
    };

    private sealed class Handle : SafeHandle
    {
        public Handle(IntPtr writer)
            : base(IntPtr.Zero, true)
        {
            SetHandle(writer);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            CoreNative.mn_writer_free(handle);
            return true;
        }
    }
}
