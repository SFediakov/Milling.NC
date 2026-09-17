namespace Miller.Core.Setup;

public enum TipType
{
    Flat,
    Ball,
}

// CutterLength is the usable length from the tip to the underside of the head.
public sealed class ToolDefinition
{
    public const string DefaultName = "6 mm flat end mill";
    public const float DefaultCutterDiameter = 6f;
    public const float DefaultCutterLength = 20f;
    public const float DefaultHeadDiameter = 10f;

    public string Name { get; set; } = DefaultName;

    public float CutterDiameter { get; set; } = DefaultCutterDiameter;

    public float CutterLength { get; set; } = DefaultCutterLength;

    public float HeadDiameter { get; set; } = DefaultHeadDiameter;

    public TipType TipType { get; set; } = TipType.Flat;

    public float CutterRadius => CutterDiameter / 2;

    public float HeadRadius => HeadDiameter / 2;

    public static ToolDefinition Default() => new();
}
