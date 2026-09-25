namespace Miller.Core.Setup;

// How much of the stock the levels remove: everything the cutter can reach, or only the model
// surface plus the trench that frees the model from the surrounding stock (SeparationRegion).
public enum CutScope
{
    Everything,
    Separation,
}

// How the generation handles the cutter head (T-147 to T-150). Recursion: the toolpath is generated
// without any head consideration, checked dynamically against the stock as it stands, the blocking
// stock is marked should be removed and the positions whose head meets the model are forbidden
// unless the X ratio lets them through, then the generation is repeated while the collisions
// decrease. OneRun: the route solver evaluates every node before its route with the Y ratio.
public enum CollisionMode
{
    Recursion,
    OneRun,
}

// Everything a .miller.json file contains.
public sealed class MillingProject
{
    // Schema 3 holds one routing strategy; schema 2 files with a roughing and a finishing strategy
    // and a milling direction, and schema 1 files with one StlPath, are migrated on load.
    public const int CurrentSchemaVersion = 3;
    public const int OldestSchemaVersion = 1;
    public const string DefaultRoutingStrategyId = "z-layer-by-layer";
    public const string DefaultPostProcessorId = "grbl";

    // X and Y: a position whose head meets model cells is achieved only when the model cells that
    // only it finishes outnumber the met ones by more than the ratio times.
    public const float DefaultCollisionRatio = 10f;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<ModelPlacement> Models { get; set; } = new();

    // Schema 1 field: read for migration, never written back.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StlPath { get; set; }

    public ToolDefinition Tool { get; set; } = ToolDefinition.Default();

    public StockDefinition Stock { get; set; } = StockDefinition.Default();

    public AxisSetup Axes { get; set; } = AxisSetup.Default();

    public CuttingParameters Parameters { get; set; } = CuttingParameters.Default();

    public string RoutingStrategyId { get; set; } = DefaultRoutingStrategyId;

    // Schema 2 fields: read for migration, never written back.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RoughingStrategyId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishingStrategyId { get; set; }

    public string PostProcessorId { get; set; } = DefaultPostProcessorId;

    public CutScope CutScope { get; set; } = CutScope.Everything;

    // Separation scope only: the smallest island of standing stock (enclosed by the trench, mm3)
    // that stays; smaller islands are milled out. 0 keeps every island.
    public float MinIslandVolume { get; set; }

    // Reach rule (ReachMap): the share of the footprint cells that must be free of model for the
    // tool to enter a position, in percent. 50 is the majority rule, 100 never cuts the model.
    public float ReachPercent { get; set; } = HeightMaps.ReachMap.DefaultPercent;

    public CollisionMode CollisionMode { get; set; } = CollisionMode.Recursion;

    // X of the recursion mode.
    public float RecursionRatio { get; set; } = DefaultCollisionRatio;

    // Y of the one run mode.
    public float OneRunRatio { get; set; } = DefaultCollisionRatio;

    public static MillingProject Default() => new();
}
