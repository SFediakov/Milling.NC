using System.Runtime.CompilerServices;

namespace Miller.Solver;

public readonly record struct RoutePoint(float X, float Y, float Z);

// The polyline the tool follows from one point to another without dipping under the plateau of
// any cell it crosses: the tool rises in place when it starts under its own plateau, every
// crossing of a cell edge is lifted to the highest plateau touching the crossing point (never
// below the straight line between the end points), and the tool descends in place over the end
// point. Two consecutive polyline points lie in one cell at or above its plateau, so the segment
// between them does too, which is the plateau model of the gouge checker. A point on a grid line
// belongs to whichever neighbouring cell the checker assigns it to, so every cell touching the
// point counts: two at an edge, four at a corner.
public static class SurfacePath
{
    // A coordinate this close to a grid line (in cells) counts as on it.
    private const float LineEpsilon = 1e-4f;

    // Appends the polyline after `a` (excluded) up to `b` (included) to `points` when it is given and
    // returns the vertical travel along the whole polyline. The cell walk is a DDA over the grid
    // lines; `remaining` bounds it against float drift at the far end.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Trace(RouteGrid grid, RoutePoint a, RoutePoint b, List<RoutePoint>? points)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var floor = grid.Floor;
        var width = grid.Width;
        var height = grid.Height;
        var cell = grid.CellSize;
        var i = grid.CellI(a.X);
        var j = grid.CellJ(a.Y);
        var iEnd = grid.CellI(b.X);
        var jEnd = grid.CellJ(b.Y);
        var z = a.Z;
        var climb = 0f;
        var current = Plateau(floor, width, height, i, j);
        var start = Max(z, current, Touching(grid, a.X, a.Y));
        if (start > z)
        {
            climb += start - z;
            z = start;
            points?.Add(new RoutePoint(a.X, a.Y, z));
        }

        if (i != iEnd || j != jEnd)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var stepI = dx > 0 ? 1 : dx < 0 ? -1 : 0;
            var stepJ = dy > 0 ? 1 : dy < 0 ? -1 : 0;
            var tDeltaX = stepI == 0 ? float.PositiveInfinity : cell / MathF.Abs(dx);
            var tDeltaY = stepJ == 0 ? float.PositiveInfinity : cell / MathF.Abs(dy);
            var tMaxX = stepI == 0 ? float.PositiveInfinity : (grid.OriginX + (stepI > 0 ? i + 1 : i) * cell - a.X) / dx;
            var tMaxY = stepJ == 0 ? float.PositiveInfinity : (grid.OriginY + (stepJ > 0 ? j + 1 : j) * cell - a.Y) / dy;
            var remaining = Math.Abs(iEnd - i) + Math.Abs(jEnd - j) + 2;
            while ((i != iEnd || j != jEnd) && remaining-- > 0)
            {
                float t;
                if (tMaxX < tMaxY)
                {
                    t = tMaxX;
                    i += stepI;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY < tMaxX)
                {
                    t = tMaxY;
                    j += stepJ;
                    tMaxY += tDeltaY;
                }
                else
                {
                    t = tMaxX;
                    i += stepI;
                    j += stepJ;
                    tMaxX += tDeltaX;
                    tMaxY += tDeltaY;
                }

                if (t > 1f)
                {
                    break;
                }

                var px = a.X + dx * t;
                var py = a.Y + dy * t;
                var next = Plateau(floor, width, height, i, j);
                var lifted = Max(Max(a.Z + (b.Z - a.Z) * t, current, next), Touching(grid, px, py));
                climb += MathF.Abs(lifted - z);
                z = lifted;
                points?.Add(new RoutePoint(px, py, z));
                current = next;
            }
        }

        var above = Max(Max(b.Z, current, Plateau(floor, width, height, iEnd, jEnd)), Touching(grid, b.X, b.Y));
        climb += MathF.Abs(above - z);
        z = above;
        if (z > b.Z)
        {
            points?.Add(new RoutePoint(b.X, b.Y, z));
            climb += z - b.Z;
        }

        points?.Add(b);
        return climb;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Plateau(float[] floor, int width, int height, int i, int j)
        => i >= 0 && i < width && j >= 0 && j < height ? floor[j * width + i] : float.NaN;

    // Highest plateau among the cells touching the point: its own cell, and the cell on the other
    // side of every grid line the point lies on.
    private static float Touching(RouteGrid grid, float x, float y)
    {
        var fi = (x - grid.OriginX) / grid.CellSize;
        var fj = (y - grid.OriginY) / grid.CellSize;
        var i = (int)MathF.Floor(fi);
        var j = (int)MathF.Floor(fj);
        var onVertical = MathF.Abs(fi - MathF.Round(fi)) < LineEpsilon;
        var onHorizontal = MathF.Abs(fj - MathF.Round(fj)) < LineEpsilon;
        var i0 = onVertical ? (int)MathF.Round(fi) - 1 : i;
        var i1 = onVertical ? (int)MathF.Round(fi) : i;
        var j0 = onHorizontal ? (int)MathF.Round(fj) - 1 : j;
        var j1 = onHorizontal ? (int)MathF.Round(fj) : j;
        var best = float.NaN;
        for (var jj = j0; jj <= j1; jj++)
        {
            for (var ii = i0; ii <= i1; ii++)
            {
                var value = Plateau(grid.Floor, grid.Width, grid.Height, ii, jj);
                if (float.IsNaN(best) || value > best)
                {
                    best = value;
                }
            }
        }

        return best;
    }

    // `first` must be a number; NaN candidates never win a comparison and are ignored.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Max(float first, float second, float third) => Max(Max(first, second), third);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Max(float first, float second) => second > first ? second : first;
}
