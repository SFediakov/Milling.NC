namespace Miller.Core.Slicing;

// Roughing steps from the stock top downward, then the finishing step.
public sealed class SlicePlan
{
    public SlicePlan(IReadOnlyList<MillingStep> steps, float lowestLevel)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Steps = steps;
        LowestLevel = lowestLevel;
        RoughingLevels = steps.Count(s => s.Operation == MillingOperation.Roughing);
        HasFinishing = steps.Any(s => s.Operation == MillingOperation.Finishing);
    }

    public IReadOnlyList<MillingStep> Steps { get; }

    public int RoughingLevels { get; }

    public bool HasFinishing { get; }

    // Lowest effective tip height: where the deepest cut ends.
    public float LowestLevel { get; }

    public IEnumerable<MillingStep> RoughingSteps => Steps.Where(s => s.Operation == MillingOperation.Roughing);

    public MillingStep? FinishingStep => Steps.FirstOrDefault(s => s.Operation == MillingOperation.Finishing);

    // Cells the finishing strategies cover; every material cell unless the cut scope narrowed it.
    public bool[,] FinishingMask => (FinishingStep ?? throw new InvalidOperationException("The plan has no finishing step.")).Mask;
}
