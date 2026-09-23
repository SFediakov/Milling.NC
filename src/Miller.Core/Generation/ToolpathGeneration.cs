using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Progress;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;

namespace Miller.Core.Generation;

// Everything one generation produced. HeadLimitedMask marks where the head, not the cutter, decides
// the depth; ShouldCut the stock the collision check found in the way of the head.
public sealed record GenerationResult(
    Mesh MachineMesh,
    StockGeometry Stock,
    HeightMap Model,
    HeightMap Tip,
    HeightMap EffectiveTip,
    HeightMap HeadLimit,
    bool[,] HeadLimitedMask,
    bool[,] ShouldCut,
    SlicePlan Plan,
    HeightMap Standing,
    Toolpath Toolpath,
    ToolpathStatistics Statistics,
    ToolProfile Profile,
    float Floor);

// The whole toolpath generation in one call of the native library (mn_generate): transform, stock,
// model map, reach map, head clearance, slicing and cut scope, routing, simplification and
// statistics. The placement of the models (machine matrices) and the stock box come from the setup
// classes, which the viewport shares; the progress reports carry the stage index of the pipeline
// table (1 transform .. 9 statistics).
public static class ToolpathGeneration
{
    public static unsafe GenerationResult Run(MillingProject project, IReadOnlyList<Mesh> meshes, Action<int, StepProgress>? progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(meshes);
        var strategy = project.RoutingStrategyId switch
        {
            ZLayerByLayerStrategy.StrategyId => NativeStrategy.ZLayer,
            ThreeAxisFreedomStrategy.StrategyId => NativeStrategy.ThreeAxisFreedom,
            _ => throw new ArgumentException($"Strategy '{project.RoutingStrategyId}' has no native implementation.", nameof(project)),
        };

        var bounds = meshes.Select(m => m.Bounds).ToList();
        var matrices = ModelLayout.MachineMatrices(project, bounds);
        var anchor = ModelLayout.AnchorBoundsMachine(project, bounds);
        if (anchor.IsEmpty)
        {
            throw new ArgumentException("Model bounds are empty.", nameof(meshes));
        }

        var size = AxisSetup.StockBoundingSize(project.Stock);
        if (!(size.X > 0 && size.Y > 0 && size.Z > 0))
        {
            throw new ArgumentException($"Stock size must be positive, got {size}.", nameof(project));
        }

        var corner = AxisSetup.StockCorner(anchor, project.Stock);
        var triangles = new float[Math.Max(meshes.Sum(m => m.TriangleCount) * 9, 1)];
        var n = 0;
        foreach (var mesh in meshes)
        {
            foreach (var t in mesh.Triangles)
            {
                triangles[n++] = t.A.X;
                triangles[n++] = t.A.Y;
                triangles[n++] = t.A.Z;
                triangles[n++] = t.B.X;
                triangles[n++] = t.B.Y;
                triangles[n++] = t.B.Z;
                triangles[n++] = t.C.X;
                triangles[n++] = t.C.Y;
                triangles[n++] = t.C.Z;
            }
        }

        var counts = meshes.Select(m => m.TriangleCount).ToArray();
        var rows = matrices.SelectMany(m => new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }).ToArray();
        var mirrored = matrices.Select(m => m.GetDeterminant() < 0 ? 1 : 0).ToArray();
        var p = project.Parameters;
        CoreNative.StageCallback? callback = progress is null ? null : (_, stage, step, steps, fraction) => progress(stage, new StepProgress(step, steps, fraction));
        var callbackPointer = callback is null ? IntPtr.Zero : System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(callback);
        using var cancel = new CoreNative.CancelFlag(cancellation);
        IntPtr result;
        fixed (float* tri = triangles, m = rows)
        fixed (int* c = counts, mir = mirrored)
        {
            var job = new CoreNative.Job
            {
                Triangles = tri,
                MeshTriangleCounts = c,
                Matrices = m,
                Mirrored = mir,
                MeshCount = meshes.Count,
                StockMinX = corner.X,
                StockMinY = corner.Y,
                StockMinZ = corner.Z,
                StockSizeX = size.X,
                StockSizeY = size.Y,
                StockSizeZ = size.Z,
                StockCylinder = project.Stock.Shape == StockShape.Cylinder ? 1 : 0,
                StockDiameter = project.Stock.Diameter,
                Tool = CoreNative.ToolOf(project.Tool),
                Parameters = CoreNative.ParametersOf(p),
                Strategy = strategy,
                CutScope = (int)project.CutScope,
                MinIslandVolume = project.MinIslandVolume,
                ReachPercent = project.ReachPercent,
            };
            CoreNative.Check(CoreNative.mn_generate(&job, callbackPointer, IntPtr.Zero, cancel.Pointer, &result), cancellation);
        }

        GC.KeepAlive(callback);
        try
        {
            return Read(result, project, new BoundingBox(corner, corner + size));
        }
        finally
        {
            CoreNative.mn_result_free(result);
        }
    }

    private static unsafe GenerationResult Read(IntPtr result, MillingProject project, BoundingBox stockBounds)
    {
        CoreNative.Grid read;
        float top, bottom, floor;
        CoreNative.mn_result_grid(result, &read);
        var grid = read;
        CoreNative.mn_result_numbers(result, &top, &bottom, &floor);
        var cells = grid.Width * grid.Height;
        var buffer = new float[cells];
        HeightMap Map(int which)
        {
            fixed (float* z = buffer)
            {
                CoreNative.mn_result_map(result, which, z);
                return CoreNative.MapOf(grid, z);
            }
        }

        var bytes = new byte[cells];
        bool[,] Mask(int which)
        {
            fixed (byte* b = bytes)
            {
                CoreNative.mn_result_mask(result, which, b);
            }

            return CoreNative.Mask(bytes, grid.Width, grid.Height);
        }

        var triangleCount = CoreNative.mn_result_triangle_count(result);
        var flat = new float[Math.Max(triangleCount * 9, 1)];
        fixed (float* t = flat)
        {
            CoreNative.mn_result_triangles(result, t);
        }

        var triangles = new Triangle[triangleCount];
        for (var k = 0; k < triangleCount; k++)
        {
            triangles[k] = new Triangle(
                new Vector3(flat[9 * k], flat[9 * k + 1], flat[9 * k + 2]),
                new Vector3(flat[9 * k + 3], flat[9 * k + 4], flat[9 * k + 5]),
                new Vector3(flat[9 * k + 6], flat[9 * k + 7], flat[9 * k + 8]));
        }

        var segmentCount = CoreNative.mn_result_segment_count(result);
        var segments = new CoreNative.Segment[Math.Max(segmentCount, 1)];
        Toolpath toolpath;
        fixed (CoreNative.Segment* s = segments)
        {
            CoreNative.mn_result_segments(result, s);
            toolpath = CoreNative.ToolpathOf(s, segmentCount);
        }

        CoreNative.Statistics statistics;
        CoreNative.mn_result_statistics(result, &statistics);
        var stock = new StockGeometry(Map(0), top, bottom, stockBounds);
        return new GenerationResult(
            new Mesh(triangles),
            stock,
            Map(1),
            Map(2),
            Map(3),
            Map(4),
            Mask(0),
            Mask(1),
            CoreNative.PlanOf(CoreNative.mn_result_plan(result), grid.Width, grid.Height),
            Map(5),
            toolpath,
            ToolpathStatistics.Of(statistics),
            ToolProfile.Create(project.Tool, project.Parameters.CellSize),
            floor);
    }
}
