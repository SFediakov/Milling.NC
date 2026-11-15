namespace Miller.Core.Toolpaths;

// The speeds a shown path is colored on (T-224, user request: red for the slowest move, green for the
// fastest). A feed or plunge runs at its own rate, the F of its G1 line; a rapid runs at the machine's
// rapid rate, because G0 has no F word and Grbl moves it at its maximum rates (mn_statistics_of times
// rapids the same way). Slowest and Fastest span the moves with a known speed (positive and finite); a
// move without one (a G1 before any F word, a rapid rate that is being typed) sits at the slow end and
// sets no bound.
public sealed class ToolpathSpeedScale
{
    public ToolpathSpeedScale(float slowest, float fastest, float rapidRate)
    {
        if (!IsKnown(slowest) || !IsKnown(fastest) || fastest < slowest)
        {
            throw new ArgumentException($"A speed scale needs 0 < slowest <= fastest, got {slowest} and {fastest} mm/min.");
        }

        Slowest = slowest;
        Fastest = fastest;
        RapidRate = rapidRate;
    }

    // mm/min
    public float Slowest { get; }

    public float Fastest { get; }

    public float RapidRate { get; }

    public bool IsSingleSpeed => Fastest == Slowest;

    public static float SpeedOf(ToolpathSegment segment, float rapidRate)
        => segment.Kind == MoveKind.Rapid ? rapidRate : segment.FeedRate;

    public static bool IsKnown(float speed) => float.IsFinite(speed) && speed > 0;

    // Null when no move has a known speed, an empty path among them.
    public static ToolpathSpeedScale? Of(IReadOnlyList<ToolpathSegment> segments, float rapidRate)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var slowest = float.PositiveInfinity;
        var fastest = float.NegativeInfinity;
        for (var k = 0; k < segments.Count; k++)
        {
            var speed = SpeedOf(segments[k], rapidRate);
            if (IsKnown(speed))
            {
                slowest = MathF.Min(slowest, speed);
                fastest = MathF.Max(fastest, speed);
            }
        }

        return slowest <= fastest ? new ToolpathSpeedScale(slowest, fastest, rapidRate) : null;
    }

    // Where a speed lies on the scale: 0 at Slowest, 1 at Fastest, linear between and clamped; 0 when
    // every move has the same speed (the first color of a range, as slicer previews do) and for a speed
    // that is not known.
    public float Fraction(float speed)
    {
        if (!IsKnown(speed) || !(speed > Slowest))
        {
            return 0f;
        }

        return speed >= Fastest ? 1f : (speed - Slowest) / (Fastest - Slowest);
    }

    public float Fraction(ToolpathSegment segment) => Fraction(SpeedOf(segment, RapidRate));

    // `count` speeds from Slowest to Fastest in equal steps, the first and the last exactly the bounds;
    // one speed when every move has the same speed.
    public IReadOnlyList<float> Ticks(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        if (IsSingleSpeed)
        {
            return new[] { Slowest };
        }

        var ticks = new float[count];
        for (var k = 0; k < count - 1; k++)
        {
            ticks[k] = Slowest + (Fastest - Slowest) * k / (count - 1);
        }

        ticks[count - 1] = Fastest;
        return ticks;
    }
}
