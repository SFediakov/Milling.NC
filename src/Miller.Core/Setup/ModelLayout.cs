using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.Core.Setup;

// Where every model of a project ends up in machine space. Placement (orientation, rotation about
// Z around the oriented center, offset) is per model. The stock is anchored to the union of the
// models before their offsets (AnchorBounds), so an offset moves a model inside the stock and never
// drags the stock along; machine zero comes from the stock corner around that anchor and is shared,
// so one model with a zero placement lands exactly where AxisSetup.ToMatrix used to put it.
public static class ModelLayout
{
    private const float DegToRad = MathF.PI / 180f;

    // Orientation and the turn about Z around the oriented center; the offset comes on top of it.
    public static Matrix4x4 TurnMatrix(AxisSetup axes, ModelPlacement placement, BoundingBox modelBounds)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(placement);
        var orientation = axes.ToOrientationMatrix();
        var center = AxisSetup.TransformBounds(modelBounds, orientation).Center;
        var turn = Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationZ(placement.RotationZ * DegToRad) * Matrix4x4.CreateTranslation(center);
        return orientation * turn;
    }

    public static Matrix4x4 PlacementMatrix(AxisSetup axes, ModelPlacement placement, BoundingBox modelBounds)
        => TurnMatrix(axes, placement, modelBounds) * Matrix4x4.CreateTranslation(placement.Offset);

    // Union of the turned bounds without offsets: what the stock corner is aligned to.
    public static BoundingBox AnchorBounds(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        RequireAligned(project, modelBounds);
        var union = BoundingBox.Empty;
        for (var k = 0; k < modelBounds.Count; k++)
        {
            union = union.Union(AxisSetup.TransformBounds(modelBounds[k], TurnMatrix(project.Axes, project.Models[k], modelBounds[k])));
        }

        return union;
    }

    // Union of the placed bounds (offsets included) before machine zero.
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

    public static Vector3 MachineZero(MillingProject project, BoundingBox anchorBounds)
        => AxisSetup.StockCorner(anchorBounds, project.Stock) + project.Axes.OriginOffset(project.Stock);

    // The anchor in machine space; StockModel.Create aligns the stock to it.
    public static BoundingBox AnchorBoundsMachine(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        var anchor = AnchorBounds(project, modelBounds);
        var zero = MachineZero(project, anchor);
        return new BoundingBox(anchor.Min - zero, anchor.Max - zero);
    }

    // The stock's bounding box in machine space. Machine zero is the stock corner plus the origin
    // offset, so the corner sits at minus that offset whatever the models are.
    public static BoundingBox StockBoundsMachine(MillingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var corner = -project.Axes.OriginOffset(project.Stock);
        return new BoundingBox(corner, corner + AxisSetup.StockBoundingSize(project.Stock));
    }

    // Model space to machine space, one matrix per model.
    public static IReadOnlyList<Matrix4x4> MachineMatrices(MillingProject project, IReadOnlyList<BoundingBox> modelBounds)
    {
        var zero = MachineZero(project, AnchorBounds(project, modelBounds));
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

    public static Vector3 CenteredOffset(MillingProject project, IReadOnlyList<BoundingBox> modelBounds, int index, int axis)
        => AlignedOffset(project, modelBounds, index, axis, StockAlignment.Center);

    // The offset that puts model index at the stock minimum, middle or maximum on one axis (0 = X,
    // 1 = Y, 2 = Z) while the other components stay. The stock is anchored, so the shift from the
    // model's reference point to the stock's is the answer in one step.
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

        var corner = AxisSetup.StockCorner(AnchorBounds(project, modelBounds), project.Stock);
        var size = AxisSetup.StockBoundingSize(project.Stock);
        var model = AxisSetup.TransformBounds(modelBounds[index], PlacementMatrix(project.Axes, project.Models[index], modelBounds[index]));
        var (stockPoint, modelPoint) = alignment switch
        {
            StockAlignment.Min => (corner, model.Min),
            StockAlignment.Center => (corner + size / 2, model.Center),
            StockAlignment.Max => (corner + size, model.Max),
            _ => throw new ArgumentException($"Unknown stock alignment {alignment}.", nameof(alignment)),
        };
        var shift = Component(stockPoint - modelPoint, axis);
        var offset = project.Models[index].Offset;
        return axis switch
        {
            0 => new Vector3(offset.X + shift, offset.Y, offset.Z),
            1 => new Vector3(offset.X, offset.Y + shift, offset.Z),
            _ => new Vector3(offset.X, offset.Y, offset.Z + shift),
        };
    }

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
