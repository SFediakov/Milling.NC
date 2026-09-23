using System.Globalization;
using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

// Checks the current stock (not the model) at one tool position: a rapid whose footprint sits below
// the stock surface, or stock under the head annulus higher than the head underside there (the head
// bottom plus the annulus Dz, which is 0 under a cylinder and rises along a widening frustum).
public static class CollisionDetector
{
    // Float slack so a tip resting exactly on a surface is not a collision.
    public const float Tolerance = 1e-4f;

    public static SimulationEvent? Check(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        if (kind == MoveKind.Rapid)
        {
            foreach (var offset in profile.Offsets)
            {
                var z = Cell(stock, ci + offset.Dx, cj + offset.Dy);
                if (z > tip.Z + offset.Dz + Tolerance)
                {
                    return new SimulationEvent(SimulationEventKind.RapidIntoMaterial, segmentIndex, tip,
                        string.Create(CultureInfo.InvariantCulture, $"Rapid move in segment {segmentIndex} enters material at {tip.X:0.###}, {tip.Y:0.###}, {tip.Z:0.###} (stock {z:0.###})."));
                }
            }
        }

        var headBottom = tip.Z + cutterLength;
        foreach (var offset in profile.AnnulusOffsets)
        {
            var z = Cell(stock, ci + offset.Dx, cj + offset.Dy);
            if (z > headBottom + offset.Dz + Tolerance)
            {
                return new SimulationEvent(SimulationEventKind.HeadCollision, segmentIndex, tip,
                    string.Create(CultureInfo.InvariantCulture, $"Head touches the stock in segment {segmentIndex} at {tip.X:0.###}, {tip.Y:0.###}, {tip.Z:0.###} (stock {z:0.###} above the head underside {headBottom + offset.Dz:0.###})."));
            }
        }

        return null;
    }

    // NaN for cells outside the grid or without material, so comparisons stay false.
    private static float Cell(HeightMap stock, int i, int j) => stock.InBounds(i, j) ? stock[i, j] : float.NaN;
}
