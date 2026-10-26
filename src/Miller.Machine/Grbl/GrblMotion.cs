using System.Globalization;

namespace Miller.Machine.Grbl;

public enum GrblMotionMode
{
    Rapid,
    Linear,
    ArcClockwise,
    ArcCounterClockwise,
    Probe,
    Cancel,
}

// A tool tip position in millimetres of the work coordinate system.
public readonly record struct GrblPoint(double X, double Y, double Z);

// One letter-number pair of a prepared line; Start and Length locate its text ("X10.500").
public readonly record struct GrblWord(char Letter, double Value, int Start, int Length);

// The state after one prepared line. Target: the line moves the tool to a point in work coordinates.
// MachineMove: G28, G30 or G53, a move whose end is not a work coordinate. MotionWord: the line names
// its motion mode itself. Feed is in mm/min, 0 until an F word. Known: the axis position is absolute,
// not relative to wherever the machine stood when the program started.
public sealed record GrblStep(
    GrblPoint Start,
    GrblPoint End,
    bool Target,
    bool HasXY,
    bool HasZ,
    bool MachineMove,
    bool MotionWord,
    GrblMotionMode Motion,
    bool Inches,
    bool Incremental,
    double Feed,
    bool KnownX,
    bool KnownY,
    bool KnownZ);

// The one modal walk over a prepared program, shared by the path view and the resume file.
// It follows what moves the tool: G90 and G91, G20 and G21, the motion mode and the feed. Lines whose
// axis words are no target in work coordinates keep the position: G53 (machine coordinates), G10 and
// G92 (offsets), G28 and G30 (intermediate points); the axes they move or redefine become unknown, as do
// the axes a probe moves (it stops at the contact). Arcs go straight to their end points. The walk
// starts at 0, 0, 0 with Grbl's power-up modes (G0, G21, G90).
public static class GrblMotion
{
    public const double MillimetresPerInch = 25.4;

    public static readonly GrblStep Initial = new(
        default, default, false, false, false, false, false, GrblMotionMode.Rapid, false, false, 0, false, false, false);

    public static IReadOnlyList<GrblStep> Walk(GrblProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var steps = new GrblStep[program.Count];
        var state = Initial;
        for (var k = 0; k < steps.Length; k++)
        {
            state = Next(state, program.Lines[k]);
            steps[k] = state;
        }

        return steps;
    }

    // Letter-number pairs of a prepared line (upper case, no spaces or comments); text that is no
    // such pair is left out, so a caller that must account for every character compares the lengths.
    public static IReadOnlyList<GrblWord> Words(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var words = new List<GrblWord>();
        var k = 0;
        while (k < line.Length)
        {
            var begin = k;
            var letter = line[k++];
            var start = k;
            while (k < line.Length && (char.IsAsciiDigit(line[k]) || line[k] is '.' or '-' or '+'))
            {
                k++;
            }

            if (char.IsAsciiLetterUpper(letter) && k > start
                && double.TryParse(line.AsSpan(start, k - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                words.Add(new GrblWord(letter, value, begin, k - begin));
            }
        }

        return words;
    }

    public static bool IsProbe(double code) => code is 38.2 or 38.3 or 38.4 or 38.5;

    private static GrblStep Next(GrblStep before, string line)
    {
        var start = before.End;
        var at = before with { Start = start, Target = false, HasXY = false, HasZ = false, MachineMove = false, MotionWord = false };
        if (line.StartsWith('$'))
        {
            return at;
        }

        var motion = before.Motion;
        var inches = before.Inches;
        var incremental = before.Incremental;
        double? x = null, y = null, z = null, f = null;
        bool offset = false, home = false, machine = false, motionWord = false;
        foreach (var word in Words(line))
        {
            switch (word.Letter)
            {
                case 'G':
                    switch (word.Value)
                    {
                        case 0:
                            motion = GrblMotionMode.Rapid;
                            motionWord = true;
                            break;
                        case 1:
                            motion = GrblMotionMode.Linear;
                            motionWord = true;
                            break;
                        case 2:
                            motion = GrblMotionMode.ArcClockwise;
                            motionWord = true;
                            break;
                        case 3:
                            motion = GrblMotionMode.ArcCounterClockwise;
                            motionWord = true;
                            break;
                        case 80:
                            motion = GrblMotionMode.Cancel;
                            motionWord = true;
                            break;
                        case 90:
                            incremental = false;
                            break;
                        case 91:
                            incremental = true;
                            break;
                        case 20:
                            inches = true;
                            break;
                        case 21:
                            inches = false;
                            break;
                        case 28 or 30:
                            home = true;
                            break;
                        case 53:
                            machine = true;
                            break;
                        case 10 or 92:
                            offset = true;
                            break;
                        case var code when IsProbe(code):
                            motion = GrblMotionMode.Probe;
                            motionWord = true;
                            break;
                    }

                    break;
                case 'X':
                    x = word.Value;
                    break;
                case 'Y':
                    y = word.Value;
                    break;
                case 'Z':
                    z = word.Value;
                    break;
                case 'F':
                    f = word.Value;
                    break;
            }
        }

        // A unit or distance word applies to the whole line, wherever it stands in it.
        var scale = inches ? MillimetresPerInch : 1;
        var axes = x is not null || y is not null || z is not null;
        var skip = offset || home || machine;
        var target = axes && !skip;
        var end = target ? new GrblPoint(Axis(start.X, x, scale, incremental), Axis(start.Y, y, scale, incremental), Axis(start.Z, z, scale, incremental)) : start;
        bool knownX = before.KnownX, knownY = before.KnownY, knownZ = before.KnownZ;
        if (target && motion != GrblMotionMode.Probe)
        {
            knownX = x is null ? knownX : !incremental || knownX;
            knownY = y is null ? knownY : !incremental || knownY;
            knownZ = z is null ? knownZ : !incremental || knownZ;
        }
        else if (axes || home)
        {
            // Probed, moved outside work coordinates or redefined: G28 or G30 alone moves every axis.
            var all = home && !axes;
            knownX = knownX && x is null && !all;
            knownY = knownY && y is null && !all;
            knownZ = knownZ && z is null && !all;
        }

        return at with
        {
            End = end,
            Target = target,
            HasXY = target && (x is not null || y is not null),
            HasZ = target && z is not null,
            MachineMove = home || machine,
            MotionWord = motionWord,
            Motion = motion,
            Inches = inches,
            Incremental = incremental,
            Feed = f is double feed ? feed * scale : before.Feed,
            KnownX = knownX,
            KnownY = knownY,
            KnownZ = knownZ,
        };
    }

    private static double Axis(double current, double? word, double scale, bool incremental)
        => word is double value ? (incremental ? current + value * scale : value * scale) : current;
}
