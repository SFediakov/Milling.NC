namespace Miller.Core.Setup;

// Units: mm, mm/min, rpm. Stepover spaces the nodes of "Z layer by layer", Stepdown is its layer height.
public sealed class CuttingParameters
{
    public const float DefaultFeedRate = 800f;
    public const float DefaultRapidRate = 3000f;
    public const float DefaultSpindleRpm = 12000f;
    public const float DefaultStepover = 3f;
    public const float DefaultStepdown = 2f;
    public const float DefaultFarStepdown = 0f;
    public const float DefaultSafeHeight = 5f;
    public const float DefaultCellSize = 0.2f;
    public const float DefaultTolerance = 0.05f;

    // The one speed of feeds and plunges; the .nc file sets it once in its header (T-215).
    public float FeedRate { get; set; } = DefaultFeedRate;

    public float RapidRate { get; set; } = DefaultRapidRate;

    public float SpindleRpm { get; set; } = DefaultSpindleRpm;

    public float Stepover { get; set; } = DefaultStepover;

    public float Stepdown { get; set; } = DefaultStepdown;

    // "Z layer by layer" only: material at least the Stepover away from everything that stays is cut
    // this much deeper in one step, the band nearer to it level by level at the Stepdown; a whole
    // multiple of the Stepdown of at least twice it, 0 for none.
    public float FarStepdown { get; set; } = DefaultFarStepdown;

    // Clearance above the stock top for rapid moves; the absolute rapid Z is stock top + SafeHeight.
    public float SafeHeight { get; set; } = DefaultSafeHeight;

    public float CellSize { get; set; } = DefaultCellSize;

    public float Tolerance { get; set; } = DefaultTolerance;

    // Schema 2 field (zigzag or one way): read for migration, never written back; the route solver
    // decides the direction of every move.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; }

    // The node spacing of "3 axis freedom", removed in T-214: read for migration, never written back.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? FinishingStepover { get; set; }

    // The plunge speed, removed in T-215 (plunges run at the feed rate): read for migration, never written back.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? PlungeRate { get; set; }

    public static CuttingParameters Default() => new();
}
