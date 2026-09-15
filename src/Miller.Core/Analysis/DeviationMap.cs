using Miller.Core.HeightMaps;

namespace Miller.Core.Analysis;

// Ok, RestMaterial, Gouge and NoModel come from the final stock against the model; Overhang,
// HeadLimited and CornerLimited explain why material stays and are laid over by the analysis view.
public enum CellCategory
{
    Ok,
    RestMaterial,
    Gouge,
    NoModel,
    Overhang,
    HeadLimited,
    CornerLimited,
}

// Per-cell difference between the final stock and the model (NaN where the model is at the floor)
// with a category per cell, both on the model grid.
public sealed class DeviationMap
{
    public DeviationMap(HeightMap values, CellCategory[] categories)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
        Categories = categories ?? throw new ArgumentNullException(nameof(categories));
        if (categories.Length != values.CellCount)
        {
            throw new ArgumentException($"{categories.Length} categories for {values.CellCount} cells.", nameof(categories));
        }
    }

    public HeightMap Values { get; }

    public CellCategory[] Categories { get; }

    public int Count(CellCategory category)
    {
        var count = 0;
        foreach (var c in Categories)
        {
            if (c == category)
            {
                count++;
            }
        }

        return count;
    }
}
