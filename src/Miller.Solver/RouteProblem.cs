namespace Miller.Solver;

// The nodes one route must visit, each at the height the tool works at there, over the surface the
// moves between them must clear.
public sealed class RouteProblem
{
    public RouteProblem(RouteGrid grid, float[] x, float[] y, float[] z)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(z);
        if (x.Length != y.Length || x.Length != z.Length)
        {
            throw new ArgumentException($"Coordinate arrays differ in length: {x.Length}, {y.Length}, {z.Length}.", nameof(x));
        }

        for (var k = 0; k < z.Length; k++)
        {
            if (float.IsNaN(x[k]) || float.IsNaN(y[k]) || float.IsNaN(z[k]))
            {
                throw new ArgumentException($"Node {k} has a NaN coordinate.", nameof(z));
            }
        }

        Grid = grid;
        X = x;
        Y = y;
        Z = z;
    }

    public RouteGrid Grid { get; }

    public float[] X { get; }

    public float[] Y { get; }

    public float[] Z { get; }

    public int Count => X.Length;

    public RoutePoint Node(int k) => new(X[k], Y[k], Z[k]);
}
