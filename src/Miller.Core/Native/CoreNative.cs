using System.Numerics;
using System.Runtime.InteropServices;
using Miller.Core.HeightMaps;
using Miller.Core.Progress;
using Miller.Core.Setup;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;

namespace Miller.Core.Native;

// The generation functions of the native library (src/Miller.Native/include/miller_native.h) and the
// conversions between its row-major arrays and the C# types: maps are HeightMap.Z as they are, masks
// bool[width, height] become bytes in j * width + i order.
internal static unsafe class CoreNative
{
    private const string Library = "miller_native";

    public const int Ok = 0;
    public const int ErrorArgument = -1;
    public const int ErrorOutOfRange = -2;
    public const int ErrorMemory = -4;
    public const int ErrorCancelled = -5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Grid
    {
        public float OriginX;
        public float OriginY;
        public float CellSize;
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tool
    {
        public float CutterDiameter;
        public float CutterLength;
        public float HeadDiameter;
        public float HeadTopDiameter;
        public float HeadLength;
        public int TipType;
        public int HeadShape;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Offset
    {
        public int Dx;
        public int Dy;
        public float Dz;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Segment
    {
        public float StartX;
        public float StartY;
        public float StartZ;
        public float EndX;
        public float EndY;
        public float EndZ;
        public int Kind;
        public float Rate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Parameters
    {
        public float FeedRate;
        public float PlungeRate;
        public float RapidRate;
        public float Stepover;
        public float FinishingStepover;
        public float Stepdown;
        public float SafeHeight;
        public float CellSize;
        public float Tolerance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Statistics
    {
        public float RapidLength;
        public float FeedLength;
        public float PlungeLength;
        public int SegmentCount;
        public float EstimatedMinutes;
        public int RetractCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Context
    {
        public Grid Grid;
        public float* Model;
        public float* Tip;
        public float* EffectiveTip;
        public float* HeadLimit;
        public float* Stock;
        public byte* ShouldCut;
        public IntPtr Plan;
        public Tool Tool;
        public Parameters Parameters;
        public float StockTop;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Job
    {
        public float* Triangles;
        public int* MeshTriangleCounts;
        public float* Matrices;
        public int* Mirrored;
        public int MeshCount;
        public float StockMinX;
        public float StockMinY;
        public float StockMinZ;
        public float StockSizeX;
        public float StockSizeY;
        public float StockSizeZ;
        public int StockCylinder;
        public float StockDiameter;
        public Tool Tool;
        public Parameters Parameters;
        public int Strategy;
        public int CutScope;
        public float MinIslandVolume;
        public float ReachPercent;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ProgressCallback(IntPtr context, int step, int steps, float fraction);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StageCallback(IntPtr context, int stage, int step, int steps, float fraction);

    [DllImport(Library)] public static extern IntPtr mn_last_error();
    [DllImport(Library)] public static extern void mn_free(void* pointer);

    [DllImport(Library)] public static extern int mn_profile_create(Tool* tool, float cellSize, Offset** offsets, int* offsetCount, Offset** annulus, int* annulusCount);
    [DllImport(Library)] public static extern float mn_bottom_height(int tipType, float radius, float distance);

    [DllImport(Library)] public static extern void mn_transform_points(float* points, int pointCount, float* matrix, float* result);
    [DllImport(Library)] public static extern int mn_grid_for(float minX, float minY, float maxX, float maxY, float cellSize, Grid* grid);
    [DllImport(Library)] public static extern void mn_rasterize(float* triangles, int triangleCount, Grid* grid, float* z);
    [DllImport(Library)] public static extern int mn_stock_map(float cornerX, float cornerY, float sizeX, float sizeY, float top, int cylinder, float diameter, float cellSize, Grid* grid, float** z);

    [DllImport(Library)] public static extern void mn_tip_map(Grid* grid, float* model, Offset* offsets, int count, float* tip);
    [DllImport(Library)] public static extern void mn_remaining(Grid* grid, float* tip, Offset* offsets, int count, float* remaining);
    [DllImport(Library)] public static extern int mn_reach_map(Grid* grid, float* model, float* stock, Offset* offsets, int count, float floor, float percent, float tolerance, IntPtr progress, IntPtr context, float* reach);
    [DllImport(Library)] public static extern double mn_reach_round_percent(float percent, int round);
    [DllImport(Library)] public static extern int mn_reach_rank(int n, float percent);
    [DllImport(Library)] public static extern float mn_reach_select(float* values, int n, int rank);
    [DllImport(Library)] public static extern float mn_reach_select_threshold(float* values, byte* excluded, int n, double percent);
    [DllImport(Library)] public static extern int mn_reachable_at(float reachFloor, float level);
    [DllImport(Library)] public static extern void mn_head_limit(Grid* grid, float* remaining, Offset* annulus, int count, float cutterLength, float* limit);
    [DllImport(Library)] public static extern void mn_apply_head_limit(float* tip, float* limit, int cells, float* effective);
    [DllImport(Library)] public static extern void mn_head_limited_mask(float* tip, float* limit, int cells, float tolerance, byte* mask);
    [DllImport(Library)] public static extern int mn_distances(byte* mask, int width, int height, float cellSize, float* distances);

    [DllImport(Library)] public static extern float mn_ceil_to_level(float z, float stockTop, float stepdown);
    [DllImport(Library)] public static extern void mn_ceil_to_levels(float* z, int cells, float stockTop, float stepdown, float* result);
    [DllImport(Library)] public static extern int mn_levels(float stockTop, float lowest, float stepdown, float** levels, int* count);
    [DllImport(Library)] public static extern int mn_slice(Grid* grid, float* effectiveTip, float* stock, float stepdown, IntPtr* plan);
    [DllImport(Library)] public static extern int mn_plan_create(Grid* grid, int levelCount, float* levels, byte* masks, byte* coverage, float lowest, IntPtr* plan);
    [DllImport(Library)] public static extern int mn_plan_level_count(IntPtr plan);
    [DllImport(Library)] public static extern float mn_plan_lowest(IntPtr plan);
    [DllImport(Library)] public static extern void mn_plan_read(IntPtr plan, float* levels, byte* masks, byte* coverage);
    [DllImport(Library)] public static extern void mn_plan_free(IntPtr plan);
    [DllImport(Library)] public static extern void mn_model_region(float* effectiveTip, int cells, float floor, byte* region);
    [DllImport(Library)] public static extern int mn_head_reach(float* levels, int count, int k, float stockTop, float cutterLength);
    [DllImport(Library)] public static extern void mn_obstacles(float* effectiveTip, int cells, float level, byte* obstacles);
    [DllImport(Library)] public static extern int mn_within(byte* marked, int width, int height, float cellSize, float radius, byte* result);
    [DllImport(Library)] public static extern float mn_adjacency_margin(float cellSize);
    [DllImport(Library)] public static extern float mn_head_margin(float cellSize, float tolerance);
    [DllImport(Library)] public static extern int mn_separation(IntPtr plan, float* effectiveTip, float* stock, Tool* tool, float tolerance, float stockTop, float floor, float minIslandVolume, float* standing, int** islandCells, int** islandOffsets, float** islandVolumes, int* islandCount);
    [DllImport(Library)] public static extern int mn_islands_find(Grid* grid, float* standing, float* effectiveTip, float* stock, float tolerance, int** islandCells, int** islandOffsets, float** islandVolumes, int* islandCount);
    [DllImport(Library)] public static extern int mn_islands_remove_below(int* islandCells, int* islandOffsets, float* islandVolumes, int islandCount, float minVolume, int levelCount, int cells, byte* allowed, byte* masks, byte* fullCoverage, byte* coverage, float* standing, int* removed, int* removedCount);
    [DllImport(Library)] public static extern int mn_label(byte* mask, int width, int height, int* labels, int** componentCells, int** componentOffsets, int* componentCount);
    [DllImport(Library)] public static extern int mn_caves(byte* masks, int levelCount, int width, int height, int* labels, int** caveLevels, int** caveIds, int** caveCells, int** caveCellOffsets, int** caveChildren, int** caveChildOffsets, int** roots, int* caveCount, int* rootCount);

    [DllImport(Library)] public static extern int mn_step_cells(float spacing, float cellSize);
    [DllImport(Library)] public static extern int mn_on_lattice(int index, int count, int step);
    [DllImport(Library)] public static extern int mn_lattice_nodes(byte* inside, int width, int height, int step, int** nodes, int* count);
    [DllImport(Library)] public static extern int mn_is_outline(byte* inside, int width, int height, int i, int j);

    [DllImport(Library)] public static extern int mn_writer_create(Parameters* parameters, float safeZ, IntPtr* writer);
    [DllImport(Library)] public static extern int mn_writer_travel(IntPtr writer, float* to, Grid* grid, float* floor);
    [DllImport(Library)] public static extern int mn_writer_follow(IntPtr writer, float* to, Grid* grid, float* floor);
    [DllImport(Library)] public static extern int mn_writer_position(IntPtr writer, float* position);
    [DllImport(Library)] public static extern int mn_writer_count(IntPtr writer);
    [DllImport(Library)] public static extern int mn_writer_finish(IntPtr writer, Segment** segments, int* count);
    [DllImport(Library)] public static extern void mn_writer_free(IntPtr writer);

    [DllImport(Library)] public static extern void mn_level_map(float* tip, int cells, float level, float* result);
    [DllImport(Library)] public static extern int mn_is_step(Grid* grid, float* map, int i, int j, float tolerance);
    [DllImport(Library)] public static extern int mn_strategy_generate(int strategy, Context* context, IntPtr progress, IntPtr progressContext, int* cancel, Segment** segments, int* count);
    [DllImport(Library)] public static extern int mn_gouge_verify(Segment* segments, int count, Grid* grid, float* effectiveTip, float tolerance, int** segmentIndex, float** positions, float** depths, int* violationCount);
    [DllImport(Library)] public static extern int mn_gouge_is_clear(Segment* segment, Grid* grid, float* effectiveTip, float tolerance);
    [DllImport(Library)] public static extern int mn_simplify(Segment* segments, int count, Grid* grid, float* effectiveTip, float tolerance, Segment** result, int* resultCount);
    [DllImport(Library)] public static extern int mn_kept_indices(float* points, int count, Grid* grid, float* effectiveTip, float tolerance, float feedRate, int** kept, int* keptCount);
    [DllImport(Library)] public static extern float mn_distance_to_segment(float* p, float* a, float* b);
    [DllImport(Library)] public static extern int mn_statistics_compute(Segment* segments, int count, float rapidRate, Statistics* statistics);

    [DllImport(Library)] public static extern int mn_generate(Job* job, IntPtr progress, IntPtr context, int* cancel, IntPtr* result);
    [DllImport(Library)] public static extern void mn_result_grid(IntPtr result, Grid* grid);
    [DllImport(Library)] public static extern void mn_result_numbers(IntPtr result, float* stockTop, float* stockBottom, float* floor);
    [DllImport(Library)] public static extern void mn_result_map(IntPtr result, int which, float* z);
    [DllImport(Library)] public static extern void mn_result_mask(IntPtr result, int which, byte* mask);
    [DllImport(Library)] public static extern IntPtr mn_result_plan(IntPtr result);
    [DllImport(Library)] public static extern int mn_result_triangle_count(IntPtr result);
    [DllImport(Library)] public static extern void mn_result_triangles(IntPtr result, float* triangles);
    [DllImport(Library)] public static extern int mn_result_segment_count(IntPtr result);
    [DllImport(Library)] public static extern void mn_result_segments(IntPtr result, Segment* segments);
    [DllImport(Library)] public static extern void mn_result_statistics(IntPtr result, Statistics* statistics);
    [DllImport(Library)] public static extern void mn_result_profile(IntPtr result, int* offsetCount, int* annulusCount);
    [DllImport(Library)] public static extern void mn_result_profile_read(IntPtr result, Offset* offsets, Offset* annulus);
    [DllImport(Library)] public static extern void mn_result_free(IntPtr result);

    public static string LastError => Marshal.PtrToStringAnsi(mn_last_error()) ?? "Unknown native failure.";

    public static void Check(int status, CancellationToken cancellation = default)
    {
        switch (status)
        {
            case Ok:
                return;
            case ErrorCancelled:
                throw new OperationCanceledException(cancellation);
            case ErrorOutOfRange:
                throw new ArgumentOutOfRangeException(null, LastError);
            case ErrorArgument:
                throw new ArgumentException(LastError);
            case ErrorMemory:
                throw new OutOfMemoryException(LastError);
            default:
                throw new InvalidOperationException(LastError);
        }
    }

    public static Grid GridOf(HeightMap map) => new()
    {
        OriginX = map.OriginX,
        OriginY = map.OriginY,
        CellSize = map.CellSize,
        Width = map.Width,
        Height = map.Height,
    };

    public static HeightMap MapOf(in Grid grid, float* z)
    {
        var map = new HeightMap(grid.OriginX, grid.OriginY, grid.CellSize, grid.Width, grid.Height, 0f);
        new ReadOnlySpan<float>(z, map.CellCount).CopyTo(map.Z);
        return map;
    }

    public static HeightMap Empty(HeightMap like, float fill) => new(like.OriginX, like.OriginY, like.CellSize, like.Width, like.Height, fill);

    public static byte[] Bytes(bool[,] mask)
    {
        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        var bytes = new byte[width * height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                bytes[j * width + i] = mask[i, j] ? (byte)1 : (byte)0;
            }
        }

        return bytes;
    }

    public static bool[,] Mask(ReadOnlySpan<byte> bytes, int width, int height)
    {
        var mask = new bool[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                mask[i, j] = bytes[j * width + i] != 0;
            }
        }

        return mask;
    }

    public static void CopyInto(ReadOnlySpan<byte> bytes, bool[,] mask)
    {
        var width = mask.GetLength(0);
        var height = mask.GetLength(1);
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                mask[i, j] = bytes[j * width + i] != 0;
            }
        }
    }

    public static Tool ToolOf(ToolDefinition tool) => new()
    {
        CutterDiameter = tool.CutterDiameter,
        CutterLength = tool.CutterLength,
        HeadDiameter = tool.HeadDiameter,
        HeadTopDiameter = tool.HeadTopDiameter,
        HeadLength = tool.HeadLength,
        TipType = (int)tool.TipType,
        HeadShape = (int)tool.HeadShape,
    };

    public static Parameters ParametersOf(CuttingParameters p) => new()
    {
        FeedRate = p.FeedRate,
        PlungeRate = p.PlungeRate,
        RapidRate = p.RapidRate,
        Stepover = p.Stepover,
        FinishingStepover = p.FinishingStepover,
        Stepdown = p.Stepdown,
        SafeHeight = p.SafeHeight,
        CellSize = p.CellSize,
        Tolerance = p.Tolerance,
    };

    public static Offset[] OffsetsOf(ProfileOffset[] offsets)
    {
        var result = new Offset[offsets.Length];
        for (var k = 0; k < offsets.Length; k++)
        {
            result[k] = new Offset { Dx = offsets[k].Dx, Dy = offsets[k].Dy, Dz = offsets[k].Dz };
        }

        return result;
    }

    public static ProfileOffset[] ProfileOffsetsOf(Offset* offsets, int count)
    {
        var result = new ProfileOffset[count];
        for (var k = 0; k < count; k++)
        {
            result[k] = new ProfileOffset(offsets[k].Dx, offsets[k].Dy, offsets[k].Dz);
        }

        return result;
    }

    public static Segment SegmentOf(in ToolpathSegment s) => new()
    {
        StartX = s.Start.X,
        StartY = s.Start.Y,
        StartZ = s.Start.Z,
        EndX = s.End.X,
        EndY = s.End.Y,
        EndZ = s.End.Z,
        Kind = (int)s.Kind,
        Rate = s.FeedRate,
    };

    public static Segment[] SegmentsOf(Toolpath toolpath)
    {
        var result = new Segment[toolpath.Count];
        for (var k = 0; k < result.Length; k++)
        {
            result[k] = SegmentOf(toolpath.Segments[k]);
        }

        return result;
    }

    public static Toolpath ToolpathOf(Segment* segments, int count)
    {
        var toolpath = new Toolpath();
        for (var k = 0; k < count; k++)
        {
            var s = segments[k];
            toolpath.Add(new ToolpathSegment(new Vector3(s.StartX, s.StartY, s.StartZ), new Vector3(s.EndX, s.EndY, s.EndZ), (MoveKind)s.Kind, s.Rate));
        }

        return toolpath;
    }

    // A native copy of a C# plan; Dispose frees it.
    public sealed class NativePlan : IDisposable
    {
        public NativePlan(SlicePlan plan, HeightMap grid)
        {
            var native = GridOf(grid);
            var cells = grid.CellCount;
            var levels = plan.Steps.Select(s => s.Level).ToArray();
            var masks = new byte[Math.Max(levels.Length * cells, 1)];
            for (var k = 0; k < levels.Length; k++)
            {
                Bytes(plan.Steps[k].Mask).CopyTo(masks, k * cells);
            }

            var coverage = Bytes(plan.Coverage);
            IntPtr handle;
            fixed (float* l = levels)
            fixed (byte* m = masks, c = coverage)
            {
                Check(mn_plan_create(&native, levels.Length, l, m, c, plan.LowestLevel, &handle));
            }

            Handle = handle;
        }

        public IntPtr Handle { get; private set; }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                mn_plan_free(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    public static SlicePlan PlanOf(IntPtr plan, int width, int height)
    {
        var count = mn_plan_level_count(plan);
        var cells = width * height;
        var levels = new float[Math.Max(count, 1)];
        var masks = new byte[Math.Max(count * cells, 1)];
        var coverage = new byte[cells];
        fixed (float* l = levels)
        fixed (byte* m = masks, c = coverage)
        {
            mn_plan_read(plan, l, m, c);
        }

        var steps = new List<MillingStep>(count);
        for (var k = 0; k < count; k++)
        {
            steps.Add(new MillingStep(levels[k], Mask(masks.AsSpan(k * cells, cells), width, height)));
        }

        return new SlicePlan(steps, Mask(coverage, width, height), mn_plan_lowest(plan));
    }

    // Keeps the delegate alive while native code may call it; Pointer is null without a progress.
    public sealed class Progress : IDisposable
    {
        private readonly ProgressCallback? _callback;
        private readonly GCHandle _handle;

        public Progress(IProgress<StepProgress>? progress)
        {
            if (progress is null)
            {
                return;
            }

            _callback = (_, step, steps, fraction) => progress.Report(new StepProgress(step, steps, fraction));
            _handle = GCHandle.Alloc(_callback);
            Pointer = Marshal.GetFunctionPointerForDelegate(_callback);
        }

        public IntPtr Pointer { get; }

        public void Dispose()
        {
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }

    // A flag the native side polls; set when the token is cancelled.
    public sealed class CancelFlag : IDisposable
    {
        private readonly int[] _flag = new int[1];
        private readonly GCHandle _handle;
        private readonly CancellationTokenRegistration _registration;

        public CancelFlag(CancellationToken cancellation)
        {
            _handle = GCHandle.Alloc(_flag, GCHandleType.Pinned);
            _registration = cancellation.Register(() => Volatile.Write(ref _flag[0], 1));
        }

        public int* Pointer => (int*)_handle.AddrOfPinnedObject();

        public void Dispose()
        {
            _registration.Dispose();
            _handle.Free();
        }
    }
}
