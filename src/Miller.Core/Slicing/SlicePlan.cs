namespace Miller.Core.Slicing;

// The levels from the stock top downward with their masks, and the coverage mask: the cells whose
// tool position the surface-following strategy visits (every material cell unless the cut scope
// narrowed it).
public sealed class SlicePlan
{
    public SlicePlan(IReadOnlyList<MillingStep> steps, bool[,] coverage, float lowestLevel)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(coverage);
        Steps = steps;
        Coverage = coverage;
        LowestLevel = lowestLevel;
    }

    public IReadOnlyList<MillingStep> Steps { get; }

    public bool[,] Coverage { get; }

    public int Levels => Steps.Count;

    // Lowest effective tip height: where the deepest cut ends.
    public float LowestLevel { get; }
}
