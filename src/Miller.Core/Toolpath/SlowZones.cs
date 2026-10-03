using Miller.Core.Native;
using Miller.Solver;

namespace Miller.Core.Toolpaths;

// The turn fine realized in the toolpath (native mn_slow_zones_apply): a movement is a run of feed
// segments straight on in XY by the rule of TurnFine (sine of the change within StraightSine, no
// reversal); its first and last SlowZone millimetres of XY travel run at SlowSpeedFactor of the rate,
// and a movement shorter than two zones is slow over its whole length. Rapids, plunges, feeds without
// XY travel and every direction change end a movement, so every new coordinate set starts and ends
// slow, at route ends as well. The chords are split at the zone boundaries and the original vertices
// stay. The pipeline runs it right after the simplifier, so the statistics, the simulation and the
// .nc file carry the slow rates.
public static class SlowZones
{
    public static unsafe Toolpath Apply(Toolpath toolpath)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        var segments = CoreNative.SegmentsOf(toolpath);
        CoreNative.Segment* result = null;
        int count;
        fixed (CoreNative.Segment* s = segments)
        {
            CoreNative.Check(CoreNative.mn_slow_zones_apply(s, segments.Length, &result, &count));
        }

        try
        {
            return CoreNative.ToolpathOf(result, count);
        }
        finally
        {
            CoreNative.mn_free(result);
        }
    }

    // The rate of a slow piece of a segment at `rate`: one float product, as the native library forms it.
    public static float SlowRate(float rate) => rate * TurnFine.SlowSpeedFactor;
}
