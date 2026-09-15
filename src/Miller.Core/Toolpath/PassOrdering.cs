using System.Numerics;

namespace Miller.Core.Toolpaths;

// Orders the passes of one group so the tool travels as little as possible between them: greedy
// nearest neighbour from the current position, measured in XY. An open pass may be reversed when
// its end is the nearer point and the direction is free (zigzag); one-way milling keeps every pass
// as produced. A closed loop keeps its direction (climb stays climb) and is rotated to start at the
// vertex nearest to the tool. Fewer long moves between passes means fewer retracts.
public static class PassOrdering
{
    public static List<Toolpath> Order(IReadOnlyList<Toolpath> passes, Vector3? start, bool allowReverse)
    {
        ArgumentNullException.ThrowIfNull(passes);
        var pending = passes.Where(p => p.Count > 0).ToList();
        var ordered = new List<Toolpath>(pending.Count);
        var position = start;
        while (pending.Count > 0)
        {
            var bestIndex = 0;
            var bestReverse = false;
            var bestRotation = 0;
            var bestDistance = float.MaxValue;
            if (position is Vector3 from)
            {
                for (var k = 0; k < pending.Count; k++)
                {
                    var pass = pending[k];
                    if (IsClosed(pass))
                    {
                        var (vertex, distance) = NearestVertex(pass, from);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestIndex = k;
                            bestReverse = false;
                            bestRotation = vertex;
                        }
                    }
                    else
                    {
                        var toStart = Planar(pass.Segments[0].Start, from);
                        var toEnd = Planar(pass.Segments[^1].End, from);
                        if (toStart < bestDistance)
                        {
                            bestDistance = toStart;
                            bestIndex = k;
                            bestReverse = false;
                            bestRotation = 0;
                        }

                        if (allowReverse && toEnd < bestDistance)
                        {
                            bestDistance = toEnd;
                            bestIndex = k;
                            bestReverse = true;
                            bestRotation = 0;
                        }
                    }
                }
            }

            var chosen = pending[bestIndex];
            pending.RemoveAt(bestIndex);
            var oriented = bestReverse ? Reverse(chosen) : bestRotation > 0 ? Rotate(chosen, bestRotation) : chosen;
            ordered.Add(oriented);
            position = oriented.Segments[^1].End;
        }

        return ordered;
    }

    public static bool IsClosed(Toolpath pass)
        => pass.Count > 1 && pass.Segments[0].Start == pass.Segments[^1].End;

    public static Toolpath Reverse(Toolpath pass)
    {
        var result = new Toolpath();
        for (var k = pass.Count - 1; k >= 0; k--)
        {
            var s = pass.Segments[k];
            result.Add(s with { Start = s.End, End = s.Start });
        }

        return result;
    }

    // The loop restarted at segment index first; the segment order and directions stay.
    public static Toolpath Rotate(Toolpath loop, int first)
    {
        var result = new Toolpath();
        for (var k = 0; k < loop.Count; k++)
        {
            result.Add(loop.Segments[(first + k) % loop.Count]);
        }

        return result;
    }

    private static (int Vertex, float Distance) NearestVertex(Toolpath loop, Vector3 from)
    {
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var k = 0; k < loop.Count; k++)
        {
            var d = Planar(loop.Segments[k].Start, from);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k;
            }
        }

        return (best, bestDistance);
    }

    private static float Planar(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
