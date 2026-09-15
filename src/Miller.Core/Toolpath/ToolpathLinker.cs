using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;

namespace Miller.Core.Toolpaths;

// Joins passes into one toolpath. A pass is a chain of feed segments a strategy produced. Between
// passes whose end and start are farther apart than one cell, or whose straight join would gouge
// the effective tip map, the tool retracts to safe Z, rapids over and plunges; otherwise the passes
// are joined by one feed. The program starts above the first pass at safe Z and ends with a retract.
public static class ToolpathLinker
{
    public static Toolpath Link(IReadOnlyList<Toolpath> passes, CuttingParameters parameters, float safeZ, HeightMap effectiveTip)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(effectiveTip);
        var result = new Toolpath();
        var joinDistance = parameters.CellSize + ToolProfileEpsilon;
        Vector3? position = null;
        foreach (var pass in passes)
        {
            if (pass.Count == 0)
            {
                continue;
            }

            RequireBelow(pass, safeZ);
            var first = pass.Segments[0].Start;
            if (position is null)
            {
                result.Add(new ToolpathSegment(new Vector3(first.X, first.Y, safeZ), first, MoveKind.Plunge, parameters.PlungeRate));
            }
            else if (Vector3.Distance(position.Value, first) <= joinDistance
                && GougeChecker.IsClear(new ToolpathSegment(position.Value, first, MoveKind.Feed, parameters.FeedRate), effectiveTip, parameters.Tolerance))
            {
                if (position.Value != first)
                {
                    result.Add(new ToolpathSegment(position.Value, first, MoveKind.Feed, parameters.FeedRate));
                }
            }
            else
            {
                var up = new Vector3(position.Value.X, position.Value.Y, safeZ);
                var over = new Vector3(first.X, first.Y, safeZ);
                result.Add(new ToolpathSegment(position.Value, up, MoveKind.Rapid, parameters.RapidRate));
                result.Add(new ToolpathSegment(up, over, MoveKind.Rapid, parameters.RapidRate));
                result.Add(new ToolpathSegment(over, first, MoveKind.Plunge, parameters.PlungeRate));
            }

            result.AddRange(pass.Segments);
            position = pass.Segments[^1].End;
        }

        if (position is Vector3 last)
        {
            result.Add(new ToolpathSegment(last, new Vector3(last.X, last.Y, safeZ), MoveKind.Rapid, parameters.RapidRate));
        }

        return result;
    }

    private const float ToolProfileEpsilon = 1e-6f;

    private static void RequireBelow(Toolpath pass, float safeZ)
    {
        foreach (var s in pass.Segments)
        {
            if (s.Start.Z > safeZ || s.End.Z > safeZ)
            {
                throw new ArgumentException($"Pass point above safe Z {safeZ}: {s.Start} -> {s.End}.", nameof(pass));
            }
        }
    }
}
