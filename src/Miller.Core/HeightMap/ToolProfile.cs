using Miller.Core.Setup;

namespace Miller.Core.HeightMaps;

// Grid offset (Dx, Dy) from the tool axis and the height Dz of the tool surface above the tip there.
public readonly record struct ProfileOffset(int Dx, int Dy, float Dz);

// The tool as seen by the grid: every cell offset under the cutter with the tool-bottom height above
// the tip (flat: 0; ball: r - sqrt(r^2 - d^2)), and the ring of cell offsets under the head only.
// Offsets are exact because a cell center displaced by (Dx, Dy) cells lies at distance
// CellSize * sqrt(Dx^2 + Dy^2) from the axis when the axis sits on a cell center.
public sealed class ToolProfile
{
    // Absolute slack on the radius comparisons so that d == r (float) counts as inside.
    public const float RadiusTolerance = 1e-5f;

    private ToolProfile(ToolDefinition tool, float cellSize, ProfileOffset[] offsets, ProfileOffset[] annulus)
    {
        Tool = tool;
        CellSize = cellSize;
        Offsets = offsets;
        AnnulusOffsets = annulus;
        RadiusCells = (int)MathF.Ceiling(tool.CutterRadius / cellSize);
        HeadRadiusCells = (int)MathF.Ceiling(tool.HeadRadius / cellSize);
    }

    public ToolDefinition Tool { get; }

    public float CellSize { get; }

    // Cutter footprint, always containing (0, 0, 0).
    public ProfileOffset[] Offsets { get; }

    // Head ring: cutter radius < d <= head radius; Dz is 0 and unused.
    public ProfileOffset[] AnnulusOffsets { get; }

    public int RadiusCells { get; }

    public int HeadRadiusCells { get; }

    public static ToolProfile Create(ToolDefinition tool, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!(cellSize > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        if (!(tool.CutterDiameter > 0))
        {
            throw new ArgumentException($"Cutter diameter must be positive, got {tool.CutterDiameter}.", nameof(tool));
        }

        var r = tool.CutterRadius;
        var headRadius = tool.HeadRadius;
        var reach = (int)MathF.Ceiling(MathF.Max(r, headRadius) / cellSize);
        var offsets = new List<ProfileOffset>();
        var annulus = new List<ProfileOffset>();
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var d = cellSize * MathF.Sqrt(dx * dx + dy * dy);
                if (d <= r + RadiusTolerance)
                {
                    offsets.Add(new ProfileOffset(dx, dy, BottomHeight(tool.TipType, r, d)));
                }
                else if (d <= headRadius + RadiusTolerance)
                {
                    annulus.Add(new ProfileOffset(dx, dy, 0f));
                }
            }
        }

        return new ToolProfile(tool, cellSize, offsets.ToArray(), annulus.ToArray());
    }

    // Height of the tool bottom above the tip at lateral distance d from the axis.
    public static float BottomHeight(TipType tipType, float radius, float d)
    {
        switch (tipType)
        {
            case TipType.Flat:
                return 0f;
            case TipType.Ball:
                var inside = MathF.Max(0f, radius * radius - d * d);
                return radius - MathF.Sqrt(inside);
            default:
                throw new ArgumentException($"Unknown tip type {tipType}.", nameof(tipType));
        }
    }
}
