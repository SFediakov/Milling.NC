namespace Miller.Core.Setup;

public enum TipType
{
    Flat,
    Ball,
}

// The holder above the cutter: a cylinder of HeadDiameter, or a four-sided section (a frustum) that
// runs from HeadDiameter at its bottom to HeadTopDiameter over HeadLength and keeps the top
// diameter above that.
public enum HeadShape
{
    Cylinder,
    Frustum,
}

// CutterLength is the usable length from the tip to the underside of the head.
public sealed class ToolDefinition
{
    public const string DefaultName = "6 mm flat end mill";
    public const float DefaultCutterDiameter = 6f;
    public const float DefaultCutterLength = 20f;
    public const float DefaultHeadDiameter = 10f;
    public const float DefaultHeadTopDiameter = 20f;
    public const float DefaultHeadLength = 10f;

    public string Name { get; set; } = DefaultName;

    public float CutterDiameter { get; set; } = DefaultCutterDiameter;

    public float CutterLength { get; set; } = DefaultCutterLength;

    // The cylinder's diameter, or the frustum's bottom diameter.
    public float HeadDiameter { get; set; } = DefaultHeadDiameter;

    public HeadShape HeadShape { get; set; } = HeadShape.Cylinder;

    // Frustum only.
    public float HeadTopDiameter { get; set; } = DefaultHeadTopDiameter;

    // Frustum only: height from the bottom to the top diameter.
    public float HeadLength { get; set; } = DefaultHeadLength;

    public TipType TipType { get; set; } = TipType.Flat;

    public float CutterRadius => CutterDiameter / 2;

    // The widest head radius, the one collision checks reach to.
    public float HeadRadius => HeadShape == HeadShape.Frustum ? MathF.Max(HeadDiameter, HeadTopDiameter) / 2 : HeadDiameter / 2;

    public static ToolDefinition Default() => new();
}
