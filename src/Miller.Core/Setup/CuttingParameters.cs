namespace Miller.Core.Setup;

// Units: mm, mm/min, rpm. Stepover spaces the nodes of "Z layer by layer", FinishingStepover those
// of "3 axis precise", Stepdown is the layer height of "Z layer by layer".
public sealed class CuttingParameters
{
    public const float DefaultFeedRate = 800f;
    public const float DefaultPlungeRate = 200f;
    public const float DefaultRapidRate = 3000f;
    public const float DefaultSpindleRpm = 12000f;
    public const float DefaultStepover = 3f;
    public const float DefaultFinishingStepover = 0.5f;
    public const float DefaultStepdown = 2f;
    public const float DefaultSafeHeight = 5f;
    public const float DefaultCellSize = 0.2f;
    public const float DefaultTolerance = 0.05f;

    public float FeedRate { get; set; } = DefaultFeedRate;

    public float PlungeRate { get; set; } = DefaultPlungeRate;

    public float RapidRate { get; set; } = DefaultRapidRate;

    public float SpindleRpm { get; set; } = DefaultSpindleRpm;

    public float Stepover { get; set; } = DefaultStepover;

    public float FinishingStepover { get; set; } = DefaultFinishingStepover;

    public float Stepdown { get; set; } = DefaultStepdown;

    // Clearance above the stock top for rapid moves; the absolute rapid Z is stock top + SafeHeight.
    public float SafeHeight { get; set; } = DefaultSafeHeight;

    public float CellSize { get; set; } = DefaultCellSize;

    public float Tolerance { get; set; } = DefaultTolerance;

    // Schema 2 field (zigzag or one way): read for migration, never written back; the route solver
    // decides the direction of every move.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; }

    public static CuttingParameters Default() => new();
}
