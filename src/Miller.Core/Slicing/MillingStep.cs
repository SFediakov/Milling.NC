namespace Miller.Core.Slicing;

public enum MillingOperation
{
    Roughing,
    Finishing,
}

// One step of the job: a Z level, the operation and the cells that take part. Mask is indexed
// [i, j] like the heightmaps it was derived from.
public sealed class MillingStep
{
    public MillingStep(float level, MillingOperation operation, bool[,] mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        Level = level;
        Operation = operation;
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

    public MillingOperation Operation { get; }

    public bool[,] Mask { get; }

    public int MaskCount { get; }

    public int Width => Mask.GetLength(0);

    public int Height => Mask.GetLength(1);
}
