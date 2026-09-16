using System.Numerics;
using Miller.Core.Setup;
using Miller.Solver;

namespace Miller.Core.Toolpaths;

// Turns a sequence of tool positions into segments. A cut between two positions follows the
// surface polyline of SurfacePath over the clearance grid: level, rising and gently descending
// parts are feeds, a descent steeper than MaxRampSlope is a feed at the higher height to over the
// lower point and then a plunge, so the tool never drops down a wall at feed rate and never dips
// under a plateau. A travel between two positions takes the same polyline or a retract to safe Z,
// a rapid and a plunge, whichever the rates make faster; the program starts with a plunge from
// safe Z above the first position and ends with a retract.
public sealed class RouteWriter
{
    // Height differences below this are one level; float noise in a plateau is not a step.
    public const float LevelEpsilon = 1e-5f;

    // Steepest descent (drop per unit of XY travel) still cut as a ramp at feed rate; about 63
    // degrees. A drop down a wall crosses one cell and is far steeper, so it becomes a plunge.
    public const float MaxRampSlope = 2f;

    private readonly Toolpath _path = new();
    private readonly List<RoutePoint> _buffer = new();
    private readonly CuttingParameters _parameters;
    private readonly float _safeZ;
    private Vector3? _position;

    public RouteWriter(CuttingParameters parameters, float safeZ)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!(parameters.FeedRate > 0) || !(parameters.PlungeRate > 0) || !(parameters.RapidRate > 0))
        {
            throw new ArgumentException("Feed, plunge and rapid rates must be positive.", nameof(parameters));
        }

        _parameters = parameters;
        _safeZ = safeZ;
    }

    public Vector3? Position => _position;

    public int Count => _path.Count;

    public void Travel(RoutePoint to, RouteGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (to.Z > _safeZ)
        {
            throw new ArgumentException($"Position {to} lies above safe Z {_safeZ}.", nameof(to));
        }

        if (_position is not Vector3 from)
        {
            var above = new Vector3(to.X, to.Y, _safeZ);
            _path.Add(new ToolpathSegment(above, ToVector(to), MoveKind.Plunge, _parameters.PlungeRate));
            _position = ToVector(to);
            return;
        }

        var along = new List<ToolpathSegment>();
        Cut(from, to, grid, along);
        var alongMinutes = Minutes(along);
        var up = new Vector3(from.X, from.Y, _safeZ);
        var over = new Vector3(to.X, to.Y, _safeZ);
        var retract = new List<ToolpathSegment>
        {
            new(from, up, MoveKind.Rapid, _parameters.RapidRate),
            new(up, over, MoveKind.Rapid, _parameters.RapidRate),
            new(over, ToVector(to), MoveKind.Plunge, _parameters.PlungeRate),
        };
        foreach (var segment in alongMinutes <= Minutes(retract) ? along : retract)
        {
            Add(segment);
        }
    }

    public void Follow(RoutePoint to, RouteGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (_position is not Vector3 from)
        {
            throw new InvalidOperationException("Travel to the first position before following the surface.");
        }

        var segments = new List<ToolpathSegment>();
        Cut(from, to, grid, segments);
        foreach (var segment in segments)
        {
            Add(segment);
        }
    }

    public Toolpath Finish()
    {
        if (_position is Vector3 last)
        {
            Add(new ToolpathSegment(last, new Vector3(last.X, last.Y, _safeZ), MoveKind.Rapid, _parameters.RapidRate));
        }

        return _path;
    }

    private void Cut(Vector3 from, RoutePoint to, RouteGrid grid, List<ToolpathSegment> segments)
    {
        _buffer.Clear();
        SurfacePath.Trace(grid, new RoutePoint(from.X, from.Y, from.Z), to, _buffer);
        var at = from;
        foreach (var point in _buffer)
        {
            var next = ToVector(point);
            if (next == at)
            {
                continue;
            }

            var dx = next.X - at.X;
            var dy = next.Y - at.Y;
            var planar = MathF.Sqrt(dx * dx + dy * dy);
            var drop = at.Z - next.Z;
            if (drop > LevelEpsilon && drop > MaxRampSlope * planar)
            {
                if (planar > 0)
                {
                    var overNext = new Vector3(next.X, next.Y, at.Z);
                    segments.Add(new ToolpathSegment(at, overNext, MoveKind.Feed, _parameters.FeedRate));
                    at = overNext;
                }

                segments.Add(new ToolpathSegment(at, next, MoveKind.Plunge, _parameters.PlungeRate));
            }
            else
            {
                segments.Add(new ToolpathSegment(at, next, MoveKind.Feed, _parameters.FeedRate));
            }

            at = next;
        }
    }

    private void Add(ToolpathSegment segment)
    {
        if (segment.Start == segment.End)
        {
            return;
        }

        _path.Add(segment);
        _position = segment.End;
    }

    private float Minutes(List<ToolpathSegment> segments)
    {
        var minutes = 0f;
        foreach (var s in segments)
        {
            var rate = s.Kind == MoveKind.Rapid ? _parameters.RapidRate : s.FeedRate;
            minutes += s.Length / rate;
        }

        return minutes;
    }

    private static Vector3 ToVector(RoutePoint p) => new(p.X, p.Y, p.Z);
}
