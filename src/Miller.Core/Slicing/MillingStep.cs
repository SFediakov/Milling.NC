namespace Miller.Core.Slicing;

// One Z level of the job and the cells whose tool position takes part in it. Mask is indexed
// [i, j] like the heightmaps it was derived from.
public sealed class MillingStep
{
    public MillingStep(float level, bool[,] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        Level = level;
        Mask = mask;
        var count = 0;
        foreach (var cell in mask)
        {
            if (cell)
            {
                count++;
            }
        }

        MaskCount = count;
    }

    public float Level { get; }

    public bool[,] Mask { get; }

    public int MaskCount { get; }

    public int Width => Mask.GetLength(0);

    public int Height => Mask.GetLength(1);
}
