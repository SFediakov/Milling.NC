using System.Buffers;
using System.Globalization;
using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

// Receives every cell a check found entered, with the height of the tool surface there (the footprint
// bottom of a rapid, the head underside) that the material stands above.
public delegate void ContactHandler(int i, int j, float surface);

// Checks the current stock (not the model) at one tool position (native mn_check_tip): a rapid whose
// footprint sits below the stock surface, or stock under the head annulus higher than the head
// underside there (the head bottom plus the annulus Dz, which is 0 under a cylinder and rises along a
// widening frustum).
public static class CollisionDetector
{
    // Float slack so a tip resting exactly on a surface is not a collision.
    public const float Tolerance = 1e-4f;

    public static SimulationEvent? Check(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segmentIndex)
        => Check(stock, profile, cutterLength, tip, kind, segmentIndex, null);

    // Every offset is examined, so the handler sees all entered cells in offset order; the event
    // describes the first entered cell, a rapid footprint before the head.
    public static unsafe SimulationEvent? Check(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segmentIndex, ContactHandler? contact)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(profile);
        var grid = CoreNative.GridOf(stock);
        var offsets = profile.NativeOffsets;
        var annulus = profile.NativeAnnulus;
        var capacity = offsets.Length + annulus.Length;
        var cells = ArrayPool<int>.Shared.Rent(capacity);
        var surfaces = ArrayPool<float>.Shared.Rent(capacity);
        try
        {
            var point = stackalloc float[] { tip.X, tip.Y, tip.Z };
            int entered;
            int firstKind;
            float firstStock;
            float firstSurface;
            fixed (float* z = stock.Z)
            fixed (CoreNative.Offset* o = offsets, a = annulus)
            fixed (int* c = cells)
            fixed (float* s = surfaces)
            {
                entered = CoreNative.mn_check_tip(z, &grid, o, offsets.Length, a, annulus.Length, cutterLength, Tolerance, point, kind == MoveKind.Rapid ? 1 : 0, c, s, capacity, &firstKind, &firstStock, &firstSurface);
            }

            if (entered < 0)
            {
                CoreNative.Check(entered);
            }

            if (entered == 0)
            {
                return null;
            }

            var found = firstKind == CoreNative.EventRapid
                ? new SimulationEvent(SimulationEventKind.RapidIntoMaterial, segmentIndex, tip, RapidMessage(segmentIndex, tip, firstStock))
                : new SimulationEvent(SimulationEventKind.HeadCollision, segmentIndex, tip, HeadMessage(segmentIndex, tip, firstStock, firstSurface));
            if (contact is not null)
            {
                for (var k = 0; k < entered; k++)
                {
                    contact(cells[k] % stock.Width, cells[k] / stock.Width, surfaces[k]);
                }
            }

            return found;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(cells);
            ArrayPool<float>.Shared.Return(surfaces);
        }
    }

    public static string RapidMessage(int segmentIndex, Vector3 tip, float stockZ)
        => string.Create(CultureInfo.InvariantCulture, $"Rapid move in segment {segmentIndex} enters material at {tip.X:0.###}, {tip.Y:0.###}, {tip.Z:0.###} (stock {stockZ:0.###}).");

    public static string HeadMessage(int segmentIndex, Vector3 tip, float stockZ, float underside)
        => string.Create(CultureInfo.InvariantCulture, $"Head touches the stock in segment {segmentIndex} at {tip.X:0.###}, {tip.Y:0.###}, {tip.Z:0.###} (stock {stockZ:0.###} above the head underside {underside:0.###}).");
}
