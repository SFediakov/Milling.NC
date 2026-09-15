using System.Numerics;
using Miller.Core.Geometry;

namespace Miller.Core.Setup;

public enum ModelAxis
{
    X,
    Y,
    Z,
}

public enum OriginMode
{
    StockCornerMinXYMinZ,
    StockCornerMinXYTopZ,
    StockCenterTopZ,
    Custom,
}

// One transform from model to machine coordinates: axis permutation with signs, then rotation
// about X, Y, Z, then the translation that puts the origin-mode point of the stock at (0, 0, 0).
// The stock position relative to the model is the same rule StockModel applies later to the
// machine-space bounds, so both agree by construction.
public sealed class AxisSetup
{
    private const float DegreesToRadians = MathF.PI / 180f;

    public ModelAxis MapX { get; set; } = ModelAxis.X;

    public ModelAxis MapY { get; set; } = ModelAxis.Y;

    public ModelAxis MapZ { get; set; } = ModelAxis.Z;

    public bool FlipX { get; set; }

    public bool FlipY { get; set; }

    public bool FlipZ { get; set; }

    public float RotationX { get; set; }

    public float RotationY { get; set; }

    public float RotationZ { get; set; }

    public OriginMode OriginMode { get; set; } = OriginMode.StockCornerMinXYMinZ;

    // Machine zero relative to the stock's minimum corner; used only with OriginMode.Custom.
    public Vector3 CustomOffset { get; set; } = Vector3.Zero;

    public static AxisSetup Default() => new();

    // Each model axis used exactly once.
    public bool IsPermutation => MapX != MapY && MapY != MapZ && MapX != MapZ;

    public Matrix4x4 ToOrientationMatrix()
    {
        if (!IsPermutation)
        {
            throw new ArgumentException($"Axis mapping must be a permutation, got X={MapX}, Y={MapY}, Z={MapZ}.");
        }

        var permutation = Matrix4x4.Identity;
        permutation.M11 = permutation.M22 = permutation.M33 = 0;
        SetPermutationEntry(ref permutation, MapX, 0, FlipX);
        SetPermutationEntry(ref permutation, MapY, 1, FlipY);
        SetPermutationEntry(ref permutation, MapZ, 2, FlipZ);

        return permutation
            * Matrix4x4.CreateRotationX(RotationX * DegreesToRadians)
            * Matrix4x4.CreateRotationY(RotationY * DegreesToRadians)
            * Matrix4x4.CreateRotationZ(RotationZ * DegreesToRadians);
    }

    public Matrix4x4 ToMatrix(BoundingBox modelBounds, StockDefinition stock)
    {
        ArgumentNullException.ThrowIfNull(stock);
        var orientation = ToOrientationMatrix();
        var oriented = TransformBounds(modelBounds, orientation);
        var machineZero = StockCorner(oriented, stock) + OriginOffset(stock);
        return orientation * Matrix4x4.CreateTranslation(-machineZero);
    }

    // Size of the stock's axis-aligned bounding box.
    public static Vector3 StockBoundingSize(StockDefinition stock)
        => stock.Shape == StockShape.Cylinder
            ? new Vector3(stock.Diameter, stock.Diameter, stock.Height)
            : new Vector3(stock.SizeX, stock.SizeY, stock.SizeZ);

    // Minimum corner of the stock's bounding box in the coordinate system of the given model bounds.
    public static Vector3 StockCorner(BoundingBox modelBounds, StockDefinition stock)
    {
        var size = StockBoundingSize(stock);
        return stock.Placement == StockPlacement.AutoFitWithMargin
            ? new Vector3(
                modelBounds.Center.X - size.X / 2,
                modelBounds.Center.Y - size.Y / 2,
                modelBounds.Max.Z - size.Z)
            : modelBounds.Min + stock.ExplicitOrigin;
    }

    // Machine zero relative to the stock's minimum corner.
    public Vector3 OriginOffset(StockDefinition stock)
    {
        var size = StockBoundingSize(stock);
        return OriginMode switch
        {
            OriginMode.StockCornerMinXYMinZ => Vector3.Zero,
            OriginMode.StockCornerMinXYTopZ => new Vector3(0, 0, size.Z),
            OriginMode.StockCenterTopZ => new Vector3(size.X / 2, size.Y / 2, size.Z),
            OriginMode.Custom => CustomOffset,
            _ => throw new ArgumentException($"Unknown origin mode {OriginMode}."),
        };
    }

    public static BoundingBox TransformBounds(BoundingBox bounds, Matrix4x4 matrix)
    {
        if (bounds.IsEmpty)
        {
            return bounds;
        }

        var result = BoundingBox.Empty;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
            result = result.Include(Vector3.Transform(corner, matrix));
        }

        return result;
    }

    // Row vectors: machine component 'machineColumn' = sign * model component 'source'.
    private static void SetPermutationEntry(ref Matrix4x4 m, ModelAxis source, int machineColumn, bool flip)
        => m[(int)source, machineColumn] = flip ? -1f : 1f;
}
