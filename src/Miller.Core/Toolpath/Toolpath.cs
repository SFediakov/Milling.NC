using Miller.Core.Geometry;

namespace Miller.Core.Toolpaths;

// Ordered segments produced by strategies and the linker.
public sealed class Toolpath
{
    private readonly List<ToolpathSegment> _segments = new();

    public IReadOnlyList<ToolpathSegment> Segments => _segments;

    public int Count => _segments.Count;

    public void Add(ToolpathSegment segment)
    {
        if (!(segment.FeedRate > 0))
        {
            throw new ArgumentException($"Segment rate must be positive, got {segment.FeedRate}.", nameof(segment));
        }

        _segments.Add(segment);
    }

    public void AddRange(IEnumerable<ToolpathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
        {
            Add(segment);
        }
    }

    public BoundingBox Bounds
    {
        get
        {
            var bounds = BoundingBox.Empty;
            foreach (var s in _segments)
            {
                bounds = bounds.Include(s.Start).Include(s.End);
            }

            return bounds;
        }
    }

    public float TotalLength(MoveKind kind)
    {
        var total = 0f;
        foreach (var s in _segments)
        {
            if (s.Kind == kind)
            {
                total += s.Length;
            }
        }

        return total;
    }

    public float TotalLength()
    {
        var total = 0f;
        foreach (var s in _segments)
        {
            total += s.Length;
        }

        return total;
    }
}
