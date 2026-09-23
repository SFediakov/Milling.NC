namespace Miller.Solver;

// 2-opt and Or-opt on an open path whose first node stays fixed, over the candidate lists, with
// don't-look bits (a node leaves the queue when no move around it improves and returns when an
// edge at it or its turn fine changes). The costs of the current edges are kept, so evaluating a
// move computes only the new edges: the candidate edge comes from the cache, the other new edge is
// bounded from below first and traced exactly only when the bound still leaves a gain. The turn
// fine of a move is exact: the windowed term of FineWindow over the nodes within two places of a
// removed or added edge, on the route the move would produce against the current one, with the
// fined status of every node kept current. One evaluation is one candidate move considered,
// whatever it costs to evaluate. A segment move is done as two or three reversals, which keeps the
// interior edge costs in place.
internal sealed class LocalSearch
{
    private const float MinGain = 1e-5f;
    private const int CancellationStride = 1 << 16;
    private const int MaxSegment = 3;
    private const int MinCacheBits = 12;
    private const int MaxCacheBits = 20;

    // Three removed and three added edges, four nodes around each.
    private const int MaxChanged = 24;

    private readonly RouteProblem _problem;
    private readonly CandidateLists _candidates;
    private readonly int[] _order;
    private readonly int[] _pos;
    private readonly float[] _edge;
    private readonly float[] _len;
    private readonly bool[] _queued;
    private readonly Queue<int> _queue;
    private readonly long _allowance;
    private readonly CancellationToken _cancellation;
    private readonly int _n;
    private readonly long[] _cacheKeys;
    private readonly float[] _cacheValues;
    private readonly int _cacheMask;
    private readonly bool[] _fined;
    private readonly FineWindow _window;
    private readonly PathView _current;
    private readonly PathView _moved;
    private readonly int[] _changed = new int[MaxChanged];
    private readonly int[] _changedNodes = new int[MaxChanged];
    private int _changedCount;

    public LocalSearch(RouteProblem problem, CandidateLists candidates, int[] order, long allowance, CancellationToken cancellation)
    {
        _problem = problem;
        _candidates = candidates;
        _order = order;
        _n = order.Length;
        _allowance = allowance;
        _cancellation = cancellation;
        _pos = new int[_n];
        _edge = new float[Math.Max(_n - 1, 0)];
        _len = new float[Math.Max(_n - 1, 0)];
        _queued = new bool[_n];
        _queue = new Queue<int>(_n);
        var bits = Math.Clamp(64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)Math.Max(_n * 4 - 1, 1)), MinCacheBits, MaxCacheBits);
        _cacheKeys = new long[1 << bits];
        _cacheValues = new float[1 << bits];
        Array.Fill(_cacheKeys, -1L);
        _cacheMask = (1 << bits) - 1;
        _fined = new bool[problem.Count];
        _window = new FineWindow(_fined);
        _current = new PathView(problem);
        _current.Reset(order, _len);
        _current.Add(0, _n - 1, false);
        _moved = new PathView(problem);
        for (var k = 0; k < _n; k++)
        {
            _pos[order[k]] = k;
            if (k + 1 < _n)
            {
                _edge[k] = Cost(order[k], order[k + 1]);
                _len[k] = RouteCost.Planar(problem.Node(order[k]), problem.Node(order[k + 1]));
            }

            Push(order[k]);
        }

        for (var k = 0; k < _n; k++)
        {
            _fined[order[k]] = FinedAt(k);
        }
    }

    public long Evaluations { get; private set; }

    public void Run()
    {
        while (_queue.Count > 0 && Evaluations < _allowance)
        {
            var a = _queue.Dequeue();
            _queued[a] = false;
            while (Evaluations < _allowance && (TwoOpt(a) || OrOpt(a)))
            {
            }
        }
    }

    // Counts one candidate move; false, without counting, once the allowance is spent.
    private bool Spend()
    {
        if (Evaluations >= _allowance)
        {
            return false;
        }

        Evaluations++;
        if ((Evaluations & (CancellationStride - 1)) == 0)
        {
            _cancellation.ThrowIfCancellationRequested();
        }

        return true;
    }

    private bool TwoOpt(int a)
    {
        var i = _pos[a];
        var slots = _candidates.Of(a);
        var costs = _candidates.CostsOf(a);
        for (var m = 0; m < slots.Length; m++)
        {
            var c = slots[m];
            if (c < 0)
            {
                break;
            }

            if (!Spend())
            {
                return false;
            }

            var dac = costs[m];
            var j = _pos[c];
            if (j > i + 1)
            {
                // ..., a, s, ..., c, cn, ... becomes ..., a, c, ..., s, cn, ...
                var s = _order[i + 1];
                var hasNext = j + 1 < _n;
                var removed = _edge[i] + (hasNext ? _edge[j] : 0f);
                var fine = ReversalFine(i + 1, j);
                var added = 0f;
                if (hasNext)
                {
                    var cn = _order[j + 1];
                    if (removed - dac - fine - Lower(s, cn) <= MinGain)
                    {
                        continue;
                    }

                    added = Cost(s, cn);
                }

                if (removed - dac - added - fine > MinGain)
                {
                    Reverse(i + 1, j);
                    SetEdge(i);
                    SetEdge(j);
                    Push(a);
                    Push(s);
                    Push(c);
                    if (hasNext)
                    {
                        Push(_order[j + 1]);
                    }

                    RefreshFined();
                    return true;
                }
            }
            else if (j + 1 < i)
            {
                // ..., c, cs, ..., a, an, ... becomes ..., c, a, ..., cs, an, ...
                var cs = _order[j + 1];
                var hasNext = i + 1 < _n;
                var removed = _edge[j] + (hasNext ? _edge[i] : 0f);
                var fine = ReversalFine(j + 1, i);
                var added = 0f;
                if (hasNext)
                {
                    var an = _order[i + 1];
                    if (removed - dac - fine - Lower(cs, an) <= MinGain)
                    {
                        continue;
                    }

                    added = Cost(cs, an);
                }

                if (removed - dac - added - fine > MinGain)
                {
                    Reverse(j + 1, i);
                    SetEdge(j);
                    SetEdge(i);
                    Push(c);
                    Push(cs);
                    Push(a);
                    if (hasNext)
                    {
                        Push(_order[i + 1]);
                    }

                    RefreshFined();
                    return true;
                }
            }
        }

        return false;
    }

    // Moves the segment of one to MaxSegment nodes that starts at a next to a candidate of either
    // segment end, in either orientation.
    private bool OrOpt(int a)
    {
        for (var length = 1; length <= MaxSegment; length++)
        {
            var i = _pos[a];
            if (i < 1 || i + length - 1 > _n - 1)
            {
                continue;
            }

            var first = a;
            var last = _order[i + length - 1];
            var p = _order[i - 1];
            var hasNext = i + length < _n;
            var nx = hasNext ? _order[i + length] : -1;
            var removed = _edge[i - 1] + (hasNext ? _edge[i + length - 1] : 0f);
            var bridgeLower = hasNext ? Lower(p, nx) : 0f;
            for (var end = 0; end < 2; end++)
            {
                var e = end == 0 ? first : last;
                var other = end == 0 ? last : first;
                var slots = _candidates.Of(e);
                var costs = _candidates.CostsOf(e);
                for (var m = 0; m < slots.Length; m++)
                {
                    var c = slots[m];
                    if (c < 0)
                    {
                        break;
                    }

                    if (!Spend())
                    {
                        return false;
                    }

                    var jc = _pos[c];
                    if (jc >= i && jc <= i + length - 1)
                    {
                        continue;
                    }

                    var dec = costs[m];
                    if (c != p)
                    {
                        // ..., c, cs, ... becomes ..., c, e, ..., other, cs, ...
                        var hasCs = jc + 1 < _n;
                        var cs = hasCs ? _order[jc + 1] : -1;
                        var removedC = hasCs ? _edge[jc] : 0f;
                        var fine = SegmentFine(i, length, jc, reversed: end == 1);
                        if (removed + removedC - bridgeLower - dec - fine - (hasCs ? Lower(other, cs) : 0f) > MinGain)
                        {
                            var bridge = hasNext ? Cost(p, nx) : 0f;
                            var tail = hasCs ? Cost(other, cs) : 0f;
                            if (removed + removedC - bridge - dec - tail - fine > MinGain)
                            {
                                MoveSegment(i, length, jc, reversed: end == 1);
                                Push(p);
                                Push(c);
                                Push(first);
                                Push(last);
                                if (hasNext)
                                {
                                    Push(nx);
                                }

                                if (hasCs)
                                {
                                    Push(cs);
                                }

                                RefreshFined();
                                return true;
                            }
                        }
                    }

                    if (jc >= 1 && c != nx)
                    {
                        // ..., cp, c, ... becomes ..., cp, other, ..., e, c, ...
                        var cp = _order[jc - 1];
                        var removedC = _edge[jc - 1];
                        var fine = SegmentFine(i, length, jc - 1, reversed: end == 0);
                        if (removed + removedC - bridgeLower - dec - fine - Lower(cp, other) > MinGain)
                        {
                            var bridge = hasNext ? Cost(p, nx) : 0f;
                            var head = Cost(cp, other);
                            if (removed + removedC - bridge - dec - head - fine > MinGain)
                            {
                                MoveSegment(i, length, jc - 1, reversed: end == 0);
                                Push(p);
                                Push(c);
                                Push(cp);
                                Push(first);
                                Push(last);
                                if (hasNext)
                                {
                                    Push(nx);
                                }

                                RefreshFined();
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }

    // Change of the turn fine if order[l .. r] were reversed.
    private float ReversalFine(int l, int r)
    {
        _moved.Reset(_order, _len);
        _moved.Add(0, l - 1, false);
        _moved.Add(l, r, true);
        _moved.Add(r + 1, _n - 1, false);
        return FineChange(l - 1, r + 1 < _n ? r : -1, -1);
    }

    // Change of the turn fine if MoveSegment(i, length, t, reversed) were done.
    private float SegmentFine(int i, int length, int t, bool reversed)
    {
        var last = i + length - 1;
        _moved.Reset(_order, _len);
        if (t > last)
        {
            _moved.Add(0, i - 1, false);
            _moved.Add(last + 1, t, false);
            _moved.Add(i, last, reversed);
            _moved.Add(t + 1, _n - 1, false);
            return FineChange(i - 1, last, t + 1 < _n ? t : -1);
        }

        _moved.Add(0, t, false);
        _moved.Add(i, last, reversed);
        _moved.Add(t + 1, i - 1, false);
        _moved.Add(last + 1, _n - 1, false);
        return FineChange(t, i - 1, last + 1 < _n ? last : -1);
    }

    // Fine of the route in _moved less the fine of the current route, in RouteCost units. The removed
    // edges are given by their positions in the current route (-1 for none); the added edges are the
    // joins between the pieces of _moved. The nodes whose status may differ are kept for RefreshFined.
    private float FineChange(int removedA, int removedB, int removedC)
    {
        _changedCount = 0;
        AddAround(removedA);
        AddAround(removedB);
        AddAround(removedC);
        for (var k = 0; k + 1 < _moved.Pieces; k++)
        {
            var e = _moved.PieceEnd(k);
            for (var p = Math.Max(e - 1, 0); p <= Math.Min(e + 2, _n - 1); p++)
            {
                Add(_moved.BasePosition(p));
            }
        }

        _window.Prepare(_order, _len, _n, _changed, _changedCount);
        var after = _window.Term(_moved, true);
        var before = _window.Term(_current, false);
        return (after - before) * TurnFine.PerSlowMillimetre;
    }

    private void AddAround(int edge)
    {
        if (edge < 0)
        {
            return;
        }

        for (var p = Math.Max(edge - 1, 0); p <= Math.Min(edge + 2, _n - 1); p++)
        {
            Add(p);
        }
    }

    private void Add(int position)
    {
        _changed[_changedCount] = position;
        _changedNodes[_changedCount] = _order[position];
        _changedCount++;
    }

    // After a move: the status of every node the last FineChange named, which is the move just done.
    private void RefreshFined()
    {
        for (var k = 0; k < _changedCount; k++)
        {
            var node = _changedNodes[k];
            _fined[node] = FinedAt(_pos[node]);
            Push(node);
        }
    }

    private bool FinedAt(int k)
        => k > 0 && k < _n - 1
        && TurnFine.IsFined(_problem, k >= 2 ? _order[k - 2] : -1, _order[k - 1], _order[k], _order[k + 1], k + 2 < _n ? _order[k + 2] : -1);

    // Moves order[i .. i + length - 1] to directly after position t (t outside i - 1 .. i + length - 1),
    // reversed or not, as reversals of adjacent blocks; the three junction edges are recomputed.
    private void MoveSegment(int i, int length, int t, bool reversed)
    {
        var last = i + length - 1;
        if (t > last)
        {
            if (reversed)
            {
                Reverse(i, t);
                Reverse(i, t - length);
            }
            else
            {
                Reverse(i, last);
                Reverse(last + 1, t);
                Reverse(i, t);
            }

            SetEdge(i - 1);
            SetEdge(t - length);
            SetEdge(t);
        }
        else
        {
            if (reversed)
            {
                Reverse(t + 1, i - 1);
                Reverse(t + 1, last);
            }
            else
            {
                Reverse(t + 1, i - 1);
                Reverse(i, last);
                Reverse(t + 1, last);
            }

            SetEdge(t);
            SetEdge(t + length);
            SetEdge(last);
        }
    }

    // Reverses order[l .. r]; the interior edge costs are symmetric and only change position.
    private void Reverse(int l, int r)
    {
        while (l < r)
        {
            (_order[l], _order[r]) = (_order[r], _order[l]);
            _pos[_order[l]] = l;
            _pos[_order[r]] = r;
            if (r - 1 > l)
            {
                (_edge[l], _edge[r - 1]) = (_edge[r - 1], _edge[l]);
                (_len[l], _len[r - 1]) = (_len[r - 1], _len[l]);
            }

            l++;
            r--;
        }
    }

    private void SetEdge(int k)
    {
        if (k >= 0 && k + 1 < _n)
        {
            _edge[k] = Cost(_order[k], _order[k + 1]);
            _len[k] = RouteCost.Planar(_problem.Node(_order[k]), _problem.Node(_order[k + 1]));
        }
    }

    private void Push(int node)
    {
        if (!_queued[node])
        {
            _queued[node] = true;
            _queue.Enqueue(node);
        }
    }

    // Candidate costs come from the lists; every other pair goes through a direct-mapped cache,
    // since the same free edges are examined again and again while the search moves around them.
    private float Cost(int u, int v)
    {
        var cached = _candidates.Cached(u, v);
        if (!float.IsNaN(cached))
        {
            return cached;
        }

        var key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
        var slot = (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 40) & _cacheMask;
        if (_cacheKeys[slot] == key)
        {
            return _cacheValues[slot];
        }

        var cost = RouteCost.Exact(_problem.Grid, _problem.Node(u), _problem.Node(v));
        _cacheKeys[slot] = key;
        _cacheValues[slot] = cost;
        return cost;
    }

    private float Lower(int u, int v) => RouteCost.LowerBound(_problem.Node(u), _problem.Node(v));
}
