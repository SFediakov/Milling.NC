using System.Numerics;
using Miller.Core.Native;

namespace Miller.Core.Toolpaths;

// Decides the rate used and the G-code word: G0 for Rapid, G1 otherwise.
public enum MoveKind
{
    Rapid,
    Feed,
    Plunge,
}

// Straight tool-tip movement. FeedRate (mm/min) is what the post-processor emits for Feed and Plunge;
// rapids carry the rapid rate only for time estimation and simulation.
public readonly record struct ToolpathSegment(Vector3 Start, Vector3 End, MoveKind Kind, float FeedRate)
{
    public float Length => Vector3.Distance(Start, End);

    // The vector feed of this move when its XY travel may run at rateXy and its Z travel at rateZ (T-221,
    // native mn_segment_rate, the rule the generation gives its moves).
    public unsafe float RateAt(float rateXy, float rateZ)
    {
        var native = CoreNative.SegmentOf(this);
        return CoreNative.mn_segment_rate(&native, rateXy, rateZ);
    }

    public Vector3 Direction
    {
        get
        {
            var delta = End - Start;
            var length = delta.Length();
            return length > 0 ? delta / length : Vector3.Zero;
        }
    }
}
