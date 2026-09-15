using System.Numerics;
using Miller.Core.HeightMaps;

namespace Miller.Core.Toolpaths;

// Iso-contours on the grid of cell centers. Nodes outside the grid and NaN nodes count as below any
// level, so every contour is a closed loop even where material touches the grid border. Loops are
// returned with the first point repeated at the end.
public static class MarchingSquares
{
    // Fraction of an edge by which mask contours move from the cell boundary into the inside cell.
    public const float MaskBias = 0.05f;

    private enum VertexKind
    {
        HorizontalEdge,
        VerticalEdge,
        Elbow,
    }

    // A loop vertex: a crossing on the edge starting at node (I, J) towards +X or +Y, or the elbow
    // of a square (I, J) displaced from its center by (Dx, Dy) times the bias.
    private readonly record struct VertexKey(int I, int J, VertexKind Kind, int Dx, int Dy);

    // Level contours of a heightmap with linear interpolation along grid edges.
    public static IReadOnlyList<IReadOnlyList<Vector2>> Contours(HeightMap map, float level)
    {
        ArgumentNullException.ThrowIfNull(map);
        float Value(int i, int j)
        {
            if (!map.InBounds(i, j))
            {
                return float.NegativeInfinity;
            }

            var z = map[i, j];
            return float.IsNaN(z) ? float.NegativeInfinity : z;
        }

        return Trace(map, Value, level, maskMode: false);
    }

    // Outline of the allowed cells, pulled MaskBias into the allowed side so that every point of the
    // loop and every segment between points lies in allowed cells: saddles are split, and a square
    // with three allowed corners gets an elbow so the loop hugs the forbidden cell instead of
    // cutting across it.
    public static IReadOnlyList<IReadOnlyList<Vector2>> MaskContours(bool[,] mask, HeightMap grid)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(grid);
        if (mask.GetLength(0) != grid.Width || mask.GetLength(1) != grid.Height)
        {
            throw new ArgumentException("Mask and grid sizes differ.", nameof(mask));
        }

        float Value(int i, int j) => grid.InBounds(i, j) && mask[i, j] ? 1f : 0f;
        return Trace(grid, Value, 0.5f, maskMode: true);
    }

    private static IReadOnlyList<IReadOnlyList<Vector2>> Trace(HeightMap grid, Func<int, int, float> value, float level, bool maskMode)
    {
        var segments = new List<(VertexKey A, VertexKey B)>();
        for (var j = -1; j < grid.Height; j++)
        {
            for (var i = -1; i < grid.Width; i++)
            {
                var va = value(i, j);
                var vb = value(i + 1, j);
                var vc = value(i + 1, j + 1);
                var vd = value(i, j + 1);
                var code = (va >= level ? 1 : 0) | (vb >= level ? 2 : 0) | (vc >= level ? 4 : 0) | (vd >= level ? 8 : 0);
                if (code == 0 || code == 15)
                {
                    continue;
                }

                var bottom = new VertexKey(i, j, VertexKind.HorizontalEdge, 0, 0);
                var right = new VertexKey(i + 1, j, VertexKind.VerticalEdge, 0, 0);
                var top = new VertexKey(i, j + 1, VertexKind.HorizontalEdge, 0, 0);
                var left = new VertexKey(i, j, VertexKind.VerticalEdge, 0, 0);
                switch (code)
                {
                    case 1: segments.Add((left, bottom)); break;
                    case 2: segments.Add((bottom, right)); break;
                    case 3: segments.Add((left, right)); break;
                    case 4: segments.Add((right, top)); break;
                    case 6: segments.Add((bottom, top)); break;
                    case 8: segments.Add((top, left)); break;
                    case 9: segments.Add((bottom, top)); break;
                    case 12: segments.Add((right, left)); break;
                    case 7: AddThreeInside(segments, maskMode, left, top, i, j, 1, -1); break;
                    case 11: AddThreeInside(segments, maskMode, right, top, i, j, -1, -1); break;
                    case 13: AddThreeInside(segments, maskMode, bottom, right, i, j, -1, 1); break;
                    case 14: AddThreeInside(segments, maskMode, left, bottom, i, j, 1, 1); break;
                    case 5:
                        if (CenterInside(va, vb, vc, vd, level, maskMode))
                        {
                            segments.Add((bottom, right));
                            segments.Add((top, left));
                        }
                        else
                        {
                            segments.Add((left, bottom));
                            segments.Add((right, top));
                        }

                        break;
                    case 10:
                        if (CenterInside(va, vb, vc, vd, level, maskMode))
                        {
                            segments.Add((left, bottom));
                            segments.Add((right, top));
                        }
                        else
                        {
                            segments.Add((bottom, right));
                            segments.Add((top, left));
                        }

                        break;
                }
            }
        }

        return Link(segments, key => Position(grid, value, level, key, maskMode));
    }

    // Three allowed corners: the geometric contour is one chord; the mask contour goes through an
    // elbow displaced from the square center towards the corner opposite the forbidden one.
    private static void AddThreeInside(List<(VertexKey, VertexKey)> segments, bool maskMode, VertexKey from, VertexKey to, int i, int j, int dx, int dy)
    {
        if (!maskMode)
        {
            segments.Add((from, to));
            return;
        }

        var elbow = new VertexKey(i, j, VertexKind.Elbow, dx, dy);
        segments.Add((from, elbow));
        segments.Add((elbow, to));
    }

    private static bool CenterInside(float va, float vb, float vc, float vd, float level, bool maskMode)
    {
        if (maskMode || float.IsInfinity(va) || float.IsInfinity(vb) || float.IsInfinity(vc) || float.IsInfinity(vd))
        {
            return false;
        }

        return (va + vb + vc + vd) / 4 >= level;
    }

    private static Vector2 Position(HeightMap grid, Func<int, int, float> value, float level, VertexKey key, bool maskMode)
    {
        if (key.Kind == VertexKind.Elbow)
        {
            var center = grid.CellCenter(key.I, key.J) + new Vector2(grid.CellSize / 2, grid.CellSize / 2);
            return center + new Vector2(key.Dx, key.Dy) * (MaskBias * grid.CellSize);
        }

        var horizontal = key.Kind == VertexKind.HorizontalEdge;
        var i1 = horizontal ? key.I + 1 : key.I;
        var j1 = horizontal ? key.J : key.J + 1;
        var v0 = value(key.I, key.J);
        var v1 = value(i1, j1);
        float t;
        if (maskMode)
        {
            t = v0 >= level ? 0.5f - MaskBias : 0.5f + MaskBias;
        }
        else if (!float.IsInfinity(v0) && !float.IsInfinity(v1) && v0 != v1)
        {
            t = Math.Clamp((level - v0) / (v1 - v0), 0f, 1f);
        }
        else
        {
            t = 0.5f;
        }

        return Vector2.Lerp(grid.CellCenter(key.I, key.J), grid.CellCenter(i1, j1), t);
    }

    private static IReadOnlyList<IReadOnlyList<Vector2>> Link(List<(VertexKey A, VertexKey B)> segments, Func<VertexKey, Vector2> position)
    {
        var byVertex = new Dictionary<VertexKey, List<int>>();
        for (var k = 0; k < segments.Count; k++)
        {
            Register(byVertex, segments[k].A, k);
            Register(byVertex, segments[k].B, k);
        }

        var used = new bool[segments.Count];
        var loops = new List<IReadOnlyList<Vector2>>();
        for (var start = 0; start < segments.Count; start++)
        {
            if (used[start])
            {
                continue;
            }

            var loop = new List<Vector2>();
            var current = start;
            var vertex = segments[start].A;
            var first = vertex;
            while (true)
            {
                used[current] = true;
                loop.Add(position(vertex));
                var (a, b) = segments[current];
                vertex = a.Equals(vertex) ? b : a;
                if (vertex.Equals(first))
                {
                    break;
                }

                var next = byVertex[vertex].FirstOrDefault(k => !used[k], -1);
                if (next < 0)
                {
                    throw new InvalidOperationException($"Contour did not close at vertex {vertex}.");
                }

                current = next;
            }

            loop.Add(loop[0]);
            loops.Add(loop);
        }

        return loops;
    }

    private static void Register(Dictionary<VertexKey, List<int>> byVertex, VertexKey key, int index)
    {
        if (!byVertex.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            byVertex[key] = list;
        }

        list.Add(index);
    }
}
