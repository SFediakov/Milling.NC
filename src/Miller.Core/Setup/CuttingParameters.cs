using System.Text.Json.Serialization;

namespace Miller.Core.Setup;

// Units: mm, mm/min, rpm. Stepover spaces the nodes of "Z layer by layer", Stepdown is its layer height.
public sealed class CuttingParameters : IJsonOnDeserialized
{
    public const float DefaultFeedRate = 800f;
    public const float DefaultFeedRateZ = DefaultFeedRate;
    public const float DefaultTravelRate = DefaultFeedRate;
    public const float DefaultTravelRateZ = DefaultTravelRate;
    public const float DefaultRapidRate = 3000f;
    public const float DefaultSpindleRpm = 12000f;
    public const float DefaultStepover = 3f;
    public const float DefaultStepdown = 2f;
    public const float DefaultFarStepdown = 0f;
    public const float DefaultSafeHeight = 5f;
    public const float DefaultCellSize = 0.2f;
    public const float DefaultTolerance = 0.05f;
    public const float MaxFinishingPasses = 5f;

    private float _feedRateZ = DefaultFeedRateZ;
    private float _travelRate = DefaultTravelRate;
    private float _travelRateZ = DefaultTravelRateZ;
    private bool _feedRateZRead;
    private bool _travelRateRead;
    private bool _travelRateZRead;

    // The XY speed of feeds and plunges in contact with material; the .nc file sets it in its header (T-215).
    public float FeedRate { get; set; } = DefaultFeedRate;

    // The Z speed of the same moves (T-221); a move runs at the vector feed of both (mn_segment_rate).
    public float FeedRateZ
    {
        get => _feedRateZ;
        set
        {
            _feedRateZ = value;
            _feedRateZRead = true;
        }
    }

    // The XY speed of a feed or plunge that meets no material over its whole length (T-218); the .nc file
    // writes F only where the speed changes.
    public float TravelRate
    {
        get => _travelRate;
        set
        {
            _travelRate = value;
            _travelRateRead = true;
        }
    }

    // The Z speed of those moves (T-221).
    public float TravelRateZ
    {
        get => _travelRateZ;
        set
        {
            _travelRateZ = value;
            _travelRateZRead = true;
        }
    }

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

    // T-222: passes after the roughing over the floors it cut, each at every cut position's final level,
    // cleaning what the roughing left there; a whole number from 0 (none) to MaxFinishingPasses.
    public float FinishingPasses { get; set; }

    public FinishingPattern FinishingPattern { get; set; } = FinishingPattern.ShiftedRows;

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

    // Files written before T-218 have no travel rate and files before T-221 no Z rates: their moves ran at
    // the feed rate in every direction, and still do (the travel Z rate follows the file's travel rate).
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!_travelRateRead)
        {
            _travelRate = FeedRate;
        }

        if (!_feedRateZRead)
        {
            _feedRateZ = FeedRate;
        }

        if (!_travelRateZRead)
        {
            _travelRateZ = _travelRate;
        }
    }
}
