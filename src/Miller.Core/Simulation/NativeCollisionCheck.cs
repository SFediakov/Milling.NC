using Miller.Core.Generation;
using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;

namespace Miller.Core.Simulation;

// The dynamic collision check of the native library on its own (mn_collision_check): the toolpath
// over a clone of the stock, the head ring and the rapid footprint against the stock as it stands.
// The generation runs the same check at the end of every pass; this facade serves the tests and
// any caller with a toolpath of its own.
public static class NativeCollisionCheck
{
    // Status receives Model per cell and Collision on every entered cell; Contacts and the events
    // follow the rule of the simulation panel (one event per segment and kind, Model outranks Stock).
    public static unsafe CollisionReport Run(Toolpath toolpath, HeightMap stock, HeightMap model, float floor, ToolDefinition tool, float tolerance, out CellStatus[] status)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tool);
        if (!stock.SameGridAs(model))
        {
            throw new ArgumentException("Stock and model must share the same grid.", nameof(model));
        }

        var grid = CoreNative.GridOf(stock);
        var native = CoreNative.ToolOf(tool);
        var segments = CoreNative.SegmentsOf(toolpath);
        var bytes = new byte[stock.CellCount];
        CoreNative.Collision* events = null;
        int count;
        fixed (CoreNative.Segment* s = segments)
        fixed (float* z = stock.Z, m = model.Z)
        fixed (byte* b = bytes)
        {
            CoreNative.Check(CoreNative.mn_collision_check(s, segments.Length, &grid, z, m, floor, &native, tolerance, b, &events, &count));
        }

        status = new CellStatus[bytes.Length];
        var contacts = new CollisionContact[bytes.Length];
        for (var k = 0; k < bytes.Length; k++)
        {
            status[k] = (CellStatus)bytes[k];
        }

        try
        {
            // The facade keeps no per-cell contact split: an entered model cell counts as Model when
            // the model stands above the floor there, as Stock otherwise.
            for (var k = 0; k < bytes.Length; k++)
            {
                if ((status[k] & CellStatus.Collision) != 0)
                {
                    contacts[k] = (status[k] & CellStatus.Model) != 0 ? CollisionContact.Model : CollisionContact.Stock;
                }
            }

            return ToolpathGeneration.ReportOf(events, count, contacts);
        }
        finally
        {
            CoreNative.mn_free(events);
        }
    }
}
