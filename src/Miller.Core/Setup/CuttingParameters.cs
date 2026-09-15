namespace Miller.Core.Setup;

public enum MillingDirection
{
    Zigzag,
    OneWay,
}

// Units: mm, mm/min, rpm.
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

    public MillingDirection Direction { get; set; } = MillingDirection.Zigzag;

    public static CuttingParameters Default() => new();
}
