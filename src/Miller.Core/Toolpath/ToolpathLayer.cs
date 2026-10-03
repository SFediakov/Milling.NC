namespace Miller.Core.Toolpaths;

// One layer of a generated toolpath: a run of consecutive routes at one plan level, in path order
// (the Z layer strategy visits caves depth first, so a level can return later). FirstSegment is the
// index of its first segment; the first layer starts at 0 and a layer ends where the next one starts.
public readonly record struct ToolpathLayer(int FirstSegment, float Level);
