namespace Miller.Core.Setup;

// How much of the stock the levels remove: everything the cutter can reach, or only the model
// surface plus the trench that frees the model from the surrounding stock (SeparationRegion).
public enum CutScope
{
    Everything,
    Separation,
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

    public static MillingProject Default() => new();
}
