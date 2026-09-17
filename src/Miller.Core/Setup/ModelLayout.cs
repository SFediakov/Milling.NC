using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.Core.Setup;

// Where every model of a project ends up in machine space. Placement (orientation, rotation about
// Z around the oriented center, offset) is per model; machine zero comes from the stock corner
// around the union of the placed bounds and is shared, so one model with a zero placement lands
// exactly where AxisSetup.ToMatrix used to put it.
public static class ModelLayout
{
    private const float DegToRad = MathF.PI / 180f;

    public static Matrix4x4 PlacementMatrix(AxisSetup axes, ModelPlacement placement, BoundingBox modelBounds)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(placement);
        var orientation = axes.ToOrientationMatrix();
        var center = AxisSetup.TransformBounds(modelBounds, orientation).Center;
        var turn = Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationZ(placement.RotationZ * DegToRad) * Matrix4x4.CreateTranslation(center);
        return orientation * turn * Matrix4x4.CreateTranslation(placement.Offset);
    }

    // Union of the placed bounds before machine zero.
    public static BoundingBox PlacedBounds(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        RequireAligned(project, modelBounds);
        var union = BoundingBox.Empty;
        for (var k = 0; k < modelBounds.Count; k++)
        {
            union = union.Union(AxisSetup.TransformBounds(modelBounds[k], PlacementMatrix(project.Axes, project.Models[k], modelBounds[k])));
        }

        return union;
    }

    public static Vector3 MachineZero(MillingProject project, BoundingBox placedBounds)
        => AxisSetup.StockCorner(placedBounds, project.Stock) + project.Axes.OriginOffset(project.Stock);

    // Model space to machine space, one matrix per model.
    public static IReadOnlyList<Matrix4x4> MachineMatrices(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        RequireAligned(project, modelBounds);
        var zero = MachineZero(project, PlacedBounds(project, modelBounds));
        var shift = Matrix4x4.CreateTranslation(-zero);
        var matrices = new Matrix4x4[modelBounds.Count];
        for (var k = 0; k < modelBounds.Count; k++)
        {
            matrices[k] = PlacementMatrix(project.Axes, project.Models[k], modelBounds[k]) * shift;
        }

        return matrices;
    }

    public static BoundingBox MachineBounds(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        var matrices = MachineMatrices(project, modelBounds);
        var union = BoundingBox.Empty;
        for (var k = 0; k < modelBounds.Count; k++)
        {
            union = union.Union(AxisSetup.TransformBounds(modelBounds[k], matrices[k]));
        }

        return union;
    }

    public static IReadOnlyList<Mesh> MachineMeshes(MillingProject project, IReadOnlyList<Mesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        var matrices = MachineMatrices(project, meshes.Select(m => m.Bounds).ToList());
        return meshes.Select((mesh, k) => mesh.Transform(matrices[k])).ToList();
    }

    // Every model's triangles in machine space as one mesh for the rasterizer.
    public static Mesh MergeMachineMeshes(MillingProject project, IReadOnlyList<Mesh> meshes)
        => new(MachineMeshes(project, meshes).SelectMany(m => m.Triangles));

    // Alignment converges by halving when another model anchors the union, so 32 rounds reach the
    // tolerance from any offset the stock can hold.
    public const int AlignIterations = 32;
    public const float AlignTolerance = 1e-4f;

    public static Vector3 CenteredOffset(MillingProject project, IReadOnlyList<BoundingBox> modelBounds, int index, int axis)
        => AlignedOffset(project, modelBounds, index, axis, StockAlignment.Center);

    // The offset that puts model index at the stock minimum, middle or maximum on one axis (0 = X,
    // 1 = Y, 2 = Z) while the other components stay. The stock corner sits around the union of all
    // models, so moving the model moves the stock too; the fixed point is found by repeating the
    // shift until it is negligible. When the stock follows this model on that axis (a lone model, or
    // the one that defines the union extreme the stock hangs on) no offset reaches the target, and
    // that is reported instead of an offset that changes nothing.
    public static Vector3 AlignedOffset(MillingProject project, IReadOnlyList<BoundingBox> modelBounds, int index, int axis, StockAlignment alignment)
    {
        RequireAligned(project, modelBounds);
        if (index < 0 || index >= modelBounds.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (axis < 0 || axis > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(axis));
        }

        var placement = project.Models[index];
        var original = placement.Offset;
        var offset = original;
        try
        {
            for (var round = 0; ; round++)
            {
                placement.Offset = offset;
                var shift = AlignmentShift(project, modelBounds, index, axis, alignment);
                if (MathF.Abs(shift) <= AlignTolerance)
                {
                    return offset;
                }

                if (round >= AlignIterations)
                {
                    throw new InvalidOperationException(
                        $"The stock follows this model on {AxisName(axis)}, so no offset moves the model to the stock {alignment.ToString().ToLowerInvariant()}; choose the stock alignment instead.");
                }

                offset = axis switch
                {
                    0 => new Vector3(offset.X + shift, offset.Y, offset.Z),
                    1 => new Vector3(offset.X, offset.Y + shift, offset.Z),
                    _ => new Vector3(offset.X, offset.Y, offset.Z + shift),
                };
            }
        }
        finally
        {
            placement.Offset = original;
        }
    }

    // Distance from the model's reference point to the stock's on one axis for the current offsets.
    private static float AlignmentShift(MillingProject project, IReadOnlyList<BoundingBox> modelBounds, int index, int axis, StockAlignment alignment)
    {
        var corner = AxisSetup.StockCorner(PlacedBounds(project, modelBounds), project.Stock);
        var size = AxisSetup.StockBoundingSize(project.Stock);
        var model = AxisSetup.TransformBounds(modelBounds[index], PlacementMatrix(project.Axes, project.Models[index], modelBounds[index]));
        var (stockPoint, modelPoint) = alignment switch
        {
            StockAlignment.Min => (corner, model.Min),
            StockAlignment.Center => (corner + size / 2, model.Center),
            StockAlignment.Max => (corner + size, model.Max),
            _ => throw new ArgumentException($"Unknown stock alignment {alignment}.", nameof(alignment)),
        };
        return Component(stockPoint - modelPoint, axis);
    }

    private static string AxisName(int axis) => axis switch { 0 => "X", 1 => "Y", _ => "Z" };

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static void RequireAligned(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(modelBounds);
        if (modelBounds.Count == 0)
        {
            throw new ArgumentException("At least one model is needed.", nameof(modelBounds));
        }

        if (modelBounds.Count != project.Models.Count)
        {
            throw new ArgumentException($"{modelBounds.Count} models for {project.Models.Count} placements.", nameof(modelBounds));
        }
    }
}
