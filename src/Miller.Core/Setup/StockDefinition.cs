using System.Numerics;

namespace Miller.Core.Setup;

public enum StockShape
{
    Box,
    Cylinder,
}

public enum StockPlacement
{
    AutoFitWithMargin,
    Explicit,
}

public sealed class StockDefinition
{
    public const float DefaultSizeX = 100f;
    public const float DefaultSizeY = 100f;
    public const float DefaultSizeZ = 30f;
    public const float DefaultDiameter = 100f;
    public const float DefaultHeight = 30f;
    public const float DefaultMargin = 5f;

    public StockShape Shape { get; set; } = StockShape.Box;

    public float SizeX { get; set; } = DefaultSizeX;

    public float SizeY { get; set; } = DefaultSizeY;

    public float SizeZ { get; set; } = DefaultSizeZ;

    public float Diameter { get; set; } = DefaultDiameter;

    public float Height { get; set; } = DefaultHeight;

    public StockPlacement Placement { get; set; } = StockPlacement.AutoFitWithMargin;

    public float Margin { get; set; } = DefaultMargin;

    // Offset of the stock's minimum corner (min X, min Y, bottom Z of its bounding box) from the
    // oriented model's minimum corner; used only with Placement = Explicit. See AxisSetup.StockCorner.
    public Vector3 ExplicitOrigin { get; set; } = Vector3.Zero;

    public static StockDefinition Default() => new();
}
