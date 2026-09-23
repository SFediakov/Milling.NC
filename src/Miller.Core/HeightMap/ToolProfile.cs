using Miller.Core.Native;
using Miller.Core.Setup;

namespace Miller.Core.HeightMaps;

// Grid offset (Dx, Dy) from the tool axis and the height Dz of the tool surface there: above the tip
// under the cutter, above the head bottom under the head.
public readonly record struct ProfileOffset(int Dx, int Dy, float Dz);

// The tool as seen by the grid (native mn_profile_create): every cell offset under the cutter with
// the tool-bottom height above the tip (flat: 0; ball: r - sqrt(r^2 - d^2)), and the ring of cell
// offsets under the head only, with the height of the head underside above the head bottom (0 for a
// cylinder; for a frustum 0 inside its bottom radius and, when it widens, rising linearly to its
// length at the top radius). Offsets are exact because a cell center displaced by (Dx, Dy) cells
// lies at distance CellSize * sqrt(Dx^2 + Dy^2) from the axis when the axis sits on a cell center.
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

    // Head ring: cutter radius < d <= head radius; Dz is the head underside above the head bottom.
    public ProfileOffset[] AnnulusOffsets { get; }

    public int RadiusCells { get; }

    public int HeadRadiusCells { get; }

    public static unsafe ToolProfile Create(ToolDefinition tool, float cellSize)
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

        var native = CoreNative.ToolOf(tool);
        CoreNative.Offset* offsets = null;
        CoreNative.Offset* annulus = null;
        int offsetCount, annulusCount;
        CoreNative.Check(CoreNative.mn_profile_create(&native, cellSize, &offsets, &offsetCount, &annulus, &annulusCount));
        try
        {
            return new ToolProfile(tool, cellSize, CoreNative.ProfileOffsetsOf(offsets, offsetCount), CoreNative.ProfileOffsetsOf(annulus, annulusCount));
        }
        finally
        {
            CoreNative.mn_free(offsets);
            CoreNative.mn_free(annulus);
        }
    }

    // Height of the tool bottom above the tip at lateral distance d from the axis.
    public static float BottomHeight(TipType tipType, float radius, float d)
    {
        if (tipType != TipType.Flat && tipType != TipType.Ball)
        {
            throw new ArgumentException($"Unknown tip type {tipType}.", nameof(tipType));
        }

        return CoreNative.mn_bottom_height((int)tipType, radius, d);
    }
}
