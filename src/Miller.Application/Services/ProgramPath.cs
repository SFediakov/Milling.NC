using System.Numerics;
using Miller.Core.Toolpaths;
using Miller.Machine.Grbl;

namespace Miller.Application.Services;

// The moves of a program as toolpath segments for the machine view, one per line with a target in work
// coordinates, and the segments behind the first lines. G0 is a rapid, a feed straight down a plunge,
// every other move a feed; an arc is drawn as its chord. Rates are the program's modal feed (0 before an
// F word); the view never times them.
public sealed class ProgramPath
{
    private readonly int[] _segmentsAfterLine;
    private readonly int[] _lineOfSegment;

    private ProgramPath(IReadOnlyList<ToolpathSegment> segments, int[] segmentsAfterLine, int[] lineOfSegment)
    {
        Segments = segments;
        _segmentsAfterLine = segmentsAfterLine;
        _lineOfSegment = lineOfSegment;
    }

    public IReadOnlyList<ToolpathSegment> Segments { get; }

    public static ProgramPath Of(GrblProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var steps = GrblMotion.Walk(program);
        var segments = new List<ToolpathSegment>();
        var lines = new List<int>();
        var after = new int[steps.Count];
        for (var k = 0; k < steps.Count; k++)
        {
            var step = steps[k];
            if (step.Target)
            {
                var start = Point(step.Start);
                var end = Point(step.End);
                var kind = step.Motion == GrblMotionMode.Rapid ? MoveKind.Rapid
                    : start.X == end.X && start.Y == end.Y && end.Z < start.Z ? MoveKind.Plunge
                    : MoveKind.Feed;
                segments.Add(new ToolpathSegment(start, end, kind, (float)step.Feed));
                lines.Add(k);
            }

            after[k] = segments.Count;
        }

        return new ProgramPath(segments, after, lines.ToArray());
    }

    // Segments behind the first `executedLines` program lines.
    public int SegmentsDone(int executedLines)
        => executedLines <= 0 || _segmentsAfterLine.Length == 0 ? 0 : _segmentsAfterLine[Math.Min(executedLines, _segmentsAfterLine.Length) - 1];

    // The program line (index into GrblProgram.Lines) a segment comes from.
    public int LineOf(int segment) => _lineOfSegment[segment];

    private static Vector3 Point(GrblPoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
}
