using System.Runtime.InteropServices;

namespace Miller.Solver.Native;

// The solver functions of the native generation library (src/Miller.Native/include/miller_native.h).
// Status codes follow the header; Check turns a failure into the matching .NET exception.
internal static unsafe class SolverNative
{
    private const string Library = "miller_native";

    public const int Ok = 0;
    public const int ErrorArgument = -1;
    public const int ErrorOutOfRange = -2;
    public const int ErrorState = -3;
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

        public static Grid Of(RouteGrid grid) => new()
        {
            OriginX = grid.OriginX,
            OriginY = grid.OriginY,
            CellSize = grid.CellSize,
            Width = grid.Width,
            Height = grid.Height,
        };
    }

    [DllImport(Library)]
    public static extern IntPtr mn_last_error();

    [DllImport(Library)]
    public static extern void mn_free(void* pointer);

    [DllImport(Library)]
    public static extern float mn_surface_trace(Grid* grid, float* floor, float* a, float* b, float** points, int* pointCount);

    [DllImport(Library)]
    public static extern float mn_route_exact(Grid* grid, float* floor, float* a, float* b);

    [DllImport(Library)]
    public static extern float mn_route_lower_bound(float* a, float* b);

    [DllImport(Library)]
    public static extern float mn_route_planar(float* a, float* b);

    [DllImport(Library)]
    public static extern int mn_turn_is_fined(float* x, float* y, float cellSize, int p, int a, int b, int c, int d);

    [DllImport(Library)]
    public static extern float mn_turn_slow_length(float* x, float* y, float cellSize, int* order, int count);

    [DllImport(Library)]
    public static extern float mn_turn_fine(float* x, float* y, float cellSize, int* order, int count);

    [DllImport(Library)]
    public static extern float mn_turn_overlap(int zonesA, int zonesB, float gap);

    [DllImport(Library)]
    public static extern long mn_budget_share(long remaining, int nodes, long nodesLeft);

    [DllImport(Library)]
    public static extern int mn_route_solve(Grid* grid, float* floor, float* x, float* y, float* z, int count, int start, long allowance, int* cancel, int* order, long* evaluations);

    [DllImport(Library)]
    public static extern float mn_route_path_cost(Grid* grid, float* floor, float* x, float* y, float* z, int* order, int count);

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

    // A flag the native side polls while the token is not cancelled; Dispose unregisters it.
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
