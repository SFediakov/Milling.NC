namespace Miller.Solver;

// A route read as up to four pieces of a base order, each forward or reversed: the route a candidate
// move would produce, without building it. A chord inside a piece is read from the XY lengths of the
// base chords; a join between two pieces is measured.
internal sealed class PathView
{
    private const int MaxPieces = 4;

    private readonly int[] _first = new int[MaxPieces];
    private readonly int[] _length = new int[MaxPieces];
    private readonly bool[] _reversed = new bool[MaxPieces];
    private readonly int[] _at = new int[MaxPieces];
    private readonly RouteProblem _problem;
    private int[] _base = Array.Empty<int>();
    private float[] _lengths = Array.Empty<float>();
    private int _pieces;

    public PathView(RouteProblem problem)
    {
        _problem = problem;
    }

    public int Count { get; private set; }

    public int Pieces => _pieces;

    // `lengths[k]` is the XY length of the base chord from position k to k + 1.
    public void Reset(int[] baseOrder, float[] lengths)
    {
        _base = baseOrder;
        _lengths = lengths;
        _pieces = 0;
        Count = 0;
    }

    // Appends base positions first .. last (inclusive); an empty range adds nothing.
    public void Add(int first, int last, bool reversed)
    {
        if (last < first)
        {
            return;
        }

        _first[_pieces] = first;
        _length[_pieces] = last - first + 1;
        _reversed[_pieces] = reversed;
        _at[_pieces] = Count;
        Count += last - first + 1;
        _pieces++;
    }

    // View position of the last node of a piece; the edge after it joins two pieces.
    public int PieceEnd(int piece) => _at[piece] + _length[piece] - 1;

    public int BasePosition(int p) => BasePosition(p, Piece(p));

    // View position of a base position, or -1 when no piece holds it.
    public int PositionOf(int basePosition)
    {
        for (var k = 0; k < _pieces; k++)
        {
            var offset = basePosition - _first[k];
            if (offset >= 0 && offset < _length[k])
            {
                return _reversed[k] ? _at[k] + _length[k] - 1 - offset : _at[k] + offset;
            }
        }

        return -1;
    }

    public int Node(int p) => _base[BasePosition(p)];

    // XY length of the chord from view position p to p + 1.
    public float Length(int p)
    {
        var piece = Piece(p);
        var from = BasePosition(p, piece);
        if (p + 1 < _at[piece] + _length[piece])
        {
            return _lengths[_reversed[piece] ? from - 1 : from];
        }

        var a = _base[from];
        var b = Node(p + 1);
        var dx = _problem.X[a] - _problem.X[b];
        var dy = _problem.Y[a] - _problem.Y[b];
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public bool IsFinedAt(int p)
        => p > 0 && p < Count - 1
        && TurnFine.IsFined(_problem, p >= 2 ? Node(p - 2) : -1, Node(p - 1), Node(p), Node(p + 1), p + 2 < Count ? Node(p + 2) : -1);

    private int Piece(int p)
    {
        var k = 0;
        while (p >= _at[k] + _length[k])
        {
            k++;
        }

        return k;
    }

    private int BasePosition(int p, int piece)
    {
        var offset = p - _at[piece];
        return _reversed[piece] ? _first[piece] + _length[piece] - 1 - offset : _first[piece] + offset;
    }
}

// The part of a route's slow length that depends on a set of changed nodes, the nodes whose fined
// status or adjoining chords may differ between routes that agree everywhere else: 2 x SlowZone for
// every changed node that is fined, less the overlap of every two consecutive events whose stretch
// holds a changed node. The difference of the term over two routes is the difference of their slow
// lengths, since every other pair of events and every other zone is the same in both.
//
// Every route compared is made of the changed nodes and the unchanged stretches of the base order
// between them, each stretch forward or reversed, the first starting at the route start and the last
// ending at the route end. Prepare walks each stretch once from both ends: to the first fined node
// (or route end) within Reach, or through the whole stretch when it holds none and is shorter; beyond
// Reach no two events overlap. Term then reads those results in the order the route passes them.
internal sealed class FineWindow
{
    private const float Reach = 2f * TurnFine.SlowZone;

    private readonly bool[] _fined;
    private int[] _base = Array.Empty<int>();
    private float[] _lengths = Array.Empty<float>();
    private int _count;
    private int[] _changed = new int[32];
    private int _changedCount;

    // Stretch k lies between _changed[k - 1] and _changed[k]; stretch 0 before the first changed
    // position, stretch _changedCount after the last. Low is the walk up from the lower bound, High the
    // walk down from the upper one; Span is the length of a stretch passed through without an event.
    private Walk[] _low = new Walk[33];
    private Walk[] _high = new Walk[33];
    private float[] _span = new float[33];
    private bool[] _through = new bool[33];
    private int[] _positions = new int[32];
    private int[] _stretches = new int[32];

    public FineWindow(bool[] fined)
    {
        _fined = fined;
    }

    // `fined` describes the base order; `count` is its length and `changed` holds base positions.
    public void Prepare(int[] baseOrder, float[] lengths, int count, int[] changed, int changedCount)
    {
        _base = baseOrder;
        _lengths = lengths;
        _count = count;
        if (_changed.Length < changedCount)
        {
            _changed = new int[changedCount];
            _positions = new int[changedCount];
            _stretches = new int[changedCount];
            _low = new Walk[changedCount + 1];
            _high = new Walk[changedCount + 1];
            _span = new float[changedCount + 1];
            _through = new bool[changedCount + 1];
        }

        _changedCount = 0;
        for (var k = 0; k < changedCount; k++)
        {
            var b = changed[k];
            var at = _changedCount;
            while (at > 0 && _changed[at - 1] > b)
            {
                at--;
            }

            if (at > 0 && _changed[at - 1] == b)
            {
                continue;
            }

            for (var m = _changedCount; m > at; m--)
            {
                _changed[m] = _changed[m - 1];
            }

            _changed[at] = b;
            _changedCount++;
        }

        if (_changed[0] > 0)
        {
            _high[0] = Down(_changed[0], -1, out _, out _);
        }

        for (var k = 1; k < _changedCount; k++)
        {
            var a = _changed[k - 1];
            var b = _changed[k];
            if (b == a + 1)
            {
                continue;
            }

            _low[k] = Up(a, b, out _through[k], out _span[k]);
            if (!_through[k])
            {
                _high[k] = Down(b, a, out _, out _);
            }
        }

        var last = _changed[_changedCount - 1];
        if (last < _count - 1)
        {
            _low[_changedCount] = Up(last, _count, out _, out _);
        }
    }

    // The term over a route made of the prepared stretches. With `fresh` the status of a changed node
    // is computed from the view; otherwise it is read from `fined`, which then describes the view. A
    // changed position the view does not hold (a shorter route of the same base order) is left out.
    public float Term(PathView view, bool fresh)
    {
        var n = 0;
        for (var k = 0; k < _changedCount; k++)
        {
            var p = view.PositionOf(_changed[k]);
            if (p < 0)
            {
                continue;
            }

            var at = n;
            while (at > 0 && _positions[at - 1] > p)
            {
                _positions[at] = _positions[at - 1];
                _stretches[at] = _stretches[at - 1];
                at--;
            }

            _positions[at] = p;
            _stretches[at] = k;
            n++;
        }

        var end = view.Count - 1;
        var total = 0f;
        var s = 0f;
        var last = _positions[0] == 0 ? new Walk(0f, 0, false) : _high[0].Behind();
        for (var i = 0; i < n; i++)
        {
            var p = _positions[i];
            if (p == end)
            {
                total -= last.OverlapWith(0, s);
                break;
            }

            if (p > 0 && (fresh ? view.IsFinedAt(p) : _fined[view.Node(p)]))
            {
                total += 2f * TurnFine.SlowZone - last.OverlapWith(1, s);
                last = new Walk(s, 1, false);
            }

            if (i + 1 == n)
            {
                var after = _low[_changedCount];
                if (!after.Far)
                {
                    total -= last.OverlapWith(after.Zones, s + after.Distance);
                }

                break;
            }

            var next = _positions[i + 1];
            if (next == p + 1)
            {
                s += view.Length(p);
                continue;
            }

            var from = _stretches[i];
            var to = _stretches[i + 1];
            var stretch = Math.Max(from, to);
            if (_through[stretch])
            {
                s += _span[stretch];
                continue;
            }

            var entry = from < to ? _low[stretch] : _high[stretch];
            if (!entry.Far)
            {
                total -= last.OverlapWith(entry.Zones, s + entry.Distance);
            }

            s = 0f;
            last = (from < to ? _high[stretch] : _low[stretch]).Behind();
        }

        return total;
    }

    // Up the base order from position a (exclusive) to the first event before position `stop`.
    private Walk Up(int a, int stop, out bool through, out float span)
    {
        through = false;
        span = 0f;
        var d = 0f;
        var q = a;
        while (true)
        {
            d += _lengths[q];
            q++;
            if (q == stop)
            {
                through = true;
                span = d;
                return new Walk(d, 0, true);
            }

            if (d >= Reach)
            {
                return new Walk(d, 0, true);
            }

            if (q == _count - 1)
            {
                return new Walk(d, 0, false);
            }

            if (_fined[_base[q]])
            {
                return new Walk(d, 1, false);
            }
        }
    }

    // Down the base order from position b (exclusive) to the first event after position `stop`.
    private Walk Down(int b, int stop, out bool through, out float span)
    {
        through = false;
        span = 0f;
        var d = 0f;
        var q = b;
        while (true)
        {
            q--;
            d += _lengths[q];
            if (q == stop)
            {
                through = true;
                span = d;
                return new Walk(d, 0, true);
            }

            if (d >= Reach)
            {
                return new Walk(d, 0, true);
            }

            if (q == 0)
            {
                return new Walk(d, 0, false);
            }

            if (_fined[_base[q]])
            {
                return new Walk(d, 1, false);
            }
        }
    }

    // An event at Distance from where a walk started (a position along the route when used as the
    // last event), with 1 zone for a fined node and 0 for a route end; Far when none lies within Reach.
    private readonly record struct Walk(float Distance, int Zones, bool Far)
    {
        // The same event seen from the node the walk started at, which lies ahead of it on the route.
        public Walk Behind() => this with { Distance = -Distance };

        public float OverlapWith(int zones, float at) => Far ? 0f : TurnFine.Overlap(Zones, zones, at - Distance);
    }
}
