namespace Miller.Solver;

// Uniform buckets over the XY extent of the nodes, about four nodes per bucket, for nearest
// searches by expanding rings of buckets: a node in ring r + 1 lies at least r bucket sides away,
// which bounds when a ring search may stop.
internal sealed class SpatialBuckets
{
    private const float TargetPerBucket = 4f;

    private readonly RouteProblem _problem;
    private readonly float _minX;
    private readonly float _minY;
    private readonly float _side;
    private readonly int _cols;
    private readonly int _rows;
    private readonly int[] _start;
    private readonly int[] _items;
    private readonly int[] _alive;
    private readonly List<(float Distance, int Node)> _scratch = new();

    public SpatialBuckets(RouteProblem problem)
    {
        _problem = problem;
        var n = problem.Count;
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        for (var k = 0; k < n; k++)
        {
            minX = MathF.Min(minX, problem.X[k]);
            maxX = MathF.Max(maxX, problem.X[k]);
            minY = MathF.Min(minY, problem.Y[k]);
            maxY = MathF.Max(maxY, problem.Y[k]);
        }

        var extentX = MathF.Max(maxX - minX, problem.Grid.CellSize);
        var extentY = MathF.Max(maxY - minY, problem.Grid.CellSize);
        _side = MathF.Max(MathF.Sqrt(extentX * extentY * TargetPerBucket / n), problem.Grid.CellSize);
        _minX = minX;
        _minY = minY;
        _cols = (int)(extentX / _side) + 1;
        _rows = (int)(extentY / _side) + 1;
        var buckets = _cols * _rows;
        _start = new int[buckets + 1];
        _items = new int[n];
        _alive = new int[buckets];
        for (var k = 0; k < n; k++)
        {
            _start[BucketOf(k) + 1]++;
        }

        for (var b = 0; b < buckets; b++)
        {
            _start[b + 1] += _start[b];
            _alive[b] = _start[b + 1] - _start[b];
        }

        var fill = new int[buckets];
        for (var k = 0; k < n; k++)
        {
            var b = BucketOf(k);
            _items[_start[b] + fill[b]++] = k;
        }
    }

    public int BucketOf(int node) => Bucket(_problem.X[node], _problem.Y[node]);

    // The k nearest other nodes by planar distance, nearest first; fewer when the problem is small.
    public int Nearest(int node, int k, Span<int> nodes, Span<float> distances)
    {
        var x = _problem.X[node];
        var y = _problem.Y[node];
        var col = Col(x);
        var row = Row(y);
        _scratch.Clear();
        for (var r = 0; ; r++)
        {
            if (!Ring(col, row, r, x, y, node, alive: null))
            {
                break;
            }

            if (_scratch.Count >= k)
            {
                _scratch.Sort(static (p, q) => p.Distance != q.Distance ? p.Distance.CompareTo(q.Distance) : p.Node.CompareTo(q.Node));
                if (r * _side >= _scratch[k - 1].Distance)
                {
                    break;
                }
            }
        }

        _scratch.Sort(static (p, q) => p.Distance != q.Distance ? p.Distance.CompareTo(q.Distance) : p.Node.CompareTo(q.Node));
        var count = Math.Min(k, _scratch.Count);
        for (var m = 0; m < count; m++)
        {
            nodes[m] = _scratch[m].Node;
            distances[m] = _scratch[m].Distance;
        }

        return count;
    }

    // The unvisited node with the smallest lower-bound cost from the point, or -1 when none is left.
    public int NearestAlive(in RoutePoint from, bool[] visited)
    {
        var col = Col(from.X);
        var row = Row(from.Y);
        var best = -1;
        var bestCost = float.PositiveInfinity;
        for (var r = 0; ; r++)
        {
            _scratch.Clear();
            if (!Ring(col, row, r, from.X, from.Y, -1, visited))
            {
                break;
            }

            foreach (var (_, node) in _scratch)
            {
                var cost = RouteCost.LowerBound(from, _problem.Node(node));
                if (cost < bestCost || (cost == bestCost && node < best))
                {
                    bestCost = cost;
                    best = node;
                }
            }

            if (best >= 0 && r * _side / RouteCost.XySpeedFactor >= bestCost)
            {
                break;
            }
        }

        return best;
    }

    public void Remove(int node)
    {
        _alive[BucketOf(node)]--;
    }

    // Appends the nodes of ring r (excluding `self` and, with `alive`, visited nodes) to the scratch
    // list; false when all four sides of the ring lie outside the bucket grid.
    private bool Ring(int col, int row, int r, float x, float y, int self, bool[]? alive)
    {
        var c0 = col - r;
        var c1 = col + r;
        var r0 = row - r;
        var r1 = row + r;
        if (c0 < 0 && r0 < 0 && c1 >= _cols && r1 >= _rows)
        {
            return false;
        }

        for (var br = Math.Max(r0, 0); br <= Math.Min(r1, _rows - 1); br++)
        {
            var edgeRow = br == r0 || br == r1;
            for (var bc = Math.Max(c0, 0); bc <= Math.Min(c1, _cols - 1); bc++)
            {
                if (!edgeRow && bc != c0 && bc != c1)
                {
                    continue;
                }

                var b = br * _cols + bc;
                if (alive is not null && _alive[b] == 0)
                {
                    continue;
                }

                for (var m = _start[b]; m < _start[b + 1]; m++)
                {
                    var node = _items[m];
                    if (node == self || (alive is not null && alive[node]))
                    {
                        continue;
                    }

                    var dx = _problem.X[node] - x;
                    var dy = _problem.Y[node] - y;
                    _scratch.Add((MathF.Sqrt(dx * dx + dy * dy), node));
                }
            }
        }

        return true;
    }

    private int Bucket(float x, float y) => Row(y) * _cols + Col(x);

    private int Col(float x) => Math.Clamp((int)((x - _minX) / _side), 0, _cols - 1);

    private int Row(float y) => Math.Clamp((int)((y - _minY) / _side), 0, _rows - 1);
}
