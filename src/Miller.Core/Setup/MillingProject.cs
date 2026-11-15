namespace Miller.Core.Setup;

// How much of the stock the levels remove: everything the cutter can reach, or only the model
// surface plus the trench that frees the model from the surrounding stock (SeparationRegion).
public enum CutScope
{
    Everything,
    Separation,
}

// How the generation handles the cutter head. Backwards (T-169): a check from the lowest level up
// leaves out every position whose head would meet material only cutting the model frees, and reports
// the share of those unreached positions against the model cells. Backwards_GPU (T-174, user request;
// the name the user gave it): Backwards with its data-parallel processes on the discrete GPU (OpenCL).
// Recursion and One run were removed in T-214 (user request); files naming them load as Backwards
// (CollisionModeConverter). The values equal the native MN_COLLISION_* numbers.
public enum CollisionMode
{
    Backwards = 0,
    Backwards_GPU = 1,
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

    public const float DefaultBridgeCount = 4f;
    public const float DefaultBridgeWidth = 0.5f;
    public const float DefaultBridgeHeight = 0.3f;

    // The native library's MN_MAX_BRIDGES.
    public const int MaxBridgeCount = 32;

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

    // Separation scope only: holding bridges per part the trench frees at the floor (a whole number,
    // 0 for none), their width (mm) and the material they keep above the floor (mm).
    public float BridgeCount { get; set; } = DefaultBridgeCount;

    public float BridgeWidth { get; set; } = DefaultBridgeWidth;

    public float BridgeHeight { get; set; } = DefaultBridgeHeight;

    // Reach rule (ReachMap): the share of the footprint cells that must be free of model for the
    // tool to enter a position, in percent. 50 is the majority rule, 100 never cuts the model.
    public float ReachPercent { get; set; } = HeightMaps.ReachMap.DefaultPercent;

    public CollisionMode CollisionMode { get; set; } = CollisionMode.Backwards;

    // Ratios of the collision modes removed in T-214: read for migration, never written back.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? RecursionRatio { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public float? OneRunRatio { get; set; }

    public static MillingProject Default() => new();
}
