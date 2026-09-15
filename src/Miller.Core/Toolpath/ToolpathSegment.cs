using System.Numerics;

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
