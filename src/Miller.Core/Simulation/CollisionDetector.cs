using System.Globalization;
using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

// Receives every cell a check found entered, with the height of the tool surface there (the footprint
// bottom of a rapid, the head underside) that the material stands above.
public delegate void ContactHandler(int i, int j, float surface);

// Checks the current stock (not the model) at one tool position: a rapid whose footprint sits below
// the stock surface, or stock under the head annulus higher than the head underside there (the head
// bottom plus the annulus Dz, which is 0 under a cylinder and rises along a widening frustum).
public static class CollisionDetector
{
    // Float slack so a tip resting exactly on a surface is not a collision.
    public const float Tolerance = 1e-4f;

    public static SimulationEvent? Check(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segmentIndex)
        => Check(stock, profile, cutterLength, tip, kind, segmentIndex, null);

    // Every offset is examined, so the handler sees all entered cells; the event describes the first
    // entered cell, a rapid footprint before the head.
    public static SimulationEvent? Check(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segmentIndex, ContactHandler? contact)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        SimulationEvent? found = null;
        if (kind == MoveKind.Rapid)
        {
            foreach (var offset in profile.Offsets)
            {
                var i = ci + offset.Dx;
                var j = cj + offset.Dy;
                var z = Cell(stock, i, j);
                var surface = tip.Z + offset.Dz;
                if (z > surface + Tolerance)
                {
                    found ??= new SimulationEvent(SimulationEventKind.RapidIntoMaterial, segmentIndex, tip, RapidMessage(segmentIndex, tip, z));
                    contact?.Invoke(i, j, surface);
                }
            }
        }

        var headBottom = tip.Z + cutterLength;
        foreach (var offset in profile.AnnulusOffsets)
        {
            var i = ci + offset.Dx;
            var j = cj + offset.Dy;
            var z = Cell(stock, i, j);
            var underside = headBottom + offset.Dz;
            if (z > underside + Tolerance)
            {
                found ??= new SimulationEvent(SimulationEventKind.HeadCollision, segmentIndex, tip, HeadMessage(segmentIndex, tip, z, underside));
                contact?.Invoke(i, j, underside);
            }
        }

        return found;
    }

    public static string RapidMessage(int segmentIndex, Vector3 tip, float stockZ)
        => string.Create(CultureInfo.InvariantCulture, $"Rapid move in segment {segmentIndex} enters material at {tip.X:0.###}, {tip.Y:0.###}, {tip.Z:0.###} (stock {stockZ:0.###}).");

    public static string HeadMessage(int segmentIndex, Vector3 tip, float stockZ, float underside)
        => string.Create(CultureInfo.InvariantCulture, $"Head touches the stock in segment {segmentIndex} at {tip.X:0.###}, {tip.Y:0.###}, {tip.Z:0.###} (stock {stockZ:0.###} above the head underside {underside:0.###}).");

    // NaN for cells outside the grid or without material, so comparisons stay false.
    private static float Cell(HeightMap stock, int i, int j) => stock.InBounds(i, j) ? stock[i, j] : float.NaN;
}
