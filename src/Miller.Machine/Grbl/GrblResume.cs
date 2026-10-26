using System.Globalization;

namespace Miller.Machine.Grbl;

// The program that finishes an interrupted run (user request T-200). The executed lines are the lines
// the controller acknowledged minus ReplayedLines, which run again. An executed line keeps every word
// that sets a state (units, distance mode, plane, feed, spindle, coolant, work system, dwell) and loses
// what moves the axes: X Y Z I J K R, the motion words G0 G1 G2 G3 G38.x G80, G28 G30 G53, $H and $J=.
// Before the first remaining move: a rapid to the highest Z of the whole program, a rapid to the XY of
// that move, the units and distance mode it ran with, then the move itself, with its motion mode
// written in front when it continued a modal one and its Z written in when it had none (else it would
// run at the retract height). Comment and blank lines stay where they are, the remaining lines stay as
// written. Anything the new program could not reproduce is refused: an arc or a relative (G91) move at
// the resume point would run again from its own end point.
public static class GrblResume
{
    public const int ReplayedLines = 2;
    public const string NothingLeftMessage = "Every move of the program was executed; nothing is left to resume.";
    public const string NoZMessage = "The program has no Z position to retract to.";
    private const string NumberFormat = "0.####";
    private const string HomeLine = "$H";
    private const string JogPrefix = "$J=";
    private const string CrLf = "\r\n";

    // executedLines: program lines the controller acknowledged (0 to Count).
    public static string Build(GrblProgram program, int executedLines)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (executedLines < 0 || executedLines > program.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(executedLines), $"Executed lines must be 0 to {program.Count}, got {executedLines}.");
        }

        var steps = GrblMotion.Walk(program);
        var cut = Math.Max(0, executedLines - ReplayedLines);
        var resume = Enumerable.Range(cut, program.Count - cut).FirstOrDefault(k => steps[k].Target || steps[k].MachineMove, -1);
        if (resume < 0)
        {
            throw new InvalidOperationException(NothingLeftMessage);
        }

        var move = steps[resume];
        var where = $"Line {program.SourceLines[resume]} ({program.Lines[resume]})";
        if (move.MachineMove || move.Motion is GrblMotionMode.Probe or GrblMotionMode.ArcClockwise or GrblMotionMode.ArcCounterClockwise || move.Incremental)
        {
            throw new InvalidOperationException(
                $"{where} is an arc, a relative (G91) move, a probe or a move outside work coordinates (G28, G30, G53); the program cannot resume there.");
        }

        for (var k = 0; k < resume; k++)
        {
            if (DependsOnPosition(program.Lines[k]))
            {
                throw new InvalidOperationException(
                    $"Line {program.SourceLines[k]} ({program.Lines[k]}) sets an offset or a stored position from where the machine stood (G92, G10 L20, G28.1, G30.1); the resumed program would set it elsewhere.");
            }
        }

        var top = HighestZ(program, steps);
        if (!move.KnownX || !move.KnownY || !move.KnownZ)
        {
            throw new InvalidOperationException($"The position of {where} depends on where the program started (relative moves before an absolute one).");
        }

        var before = resume > 0 ? steps[resume - 1] : GrblMotion.Initial;
        var inserted = new List<string>
        {
            $"G21 G90 G0 Z{Number(top)}",
            $"G0 X{Number(move.End.X)} Y{Number(move.End.Y)}",
        };
        var restore = (before.Inches ? " G20" : string.Empty) + (before.Incremental ? " G91" : string.Empty);
        if (restore.Length > 0)
        {
            inserted.Add(restore.TrimStart());
        }

        var prefix = move.MotionWord || before.Motion == GrblMotionMode.Rapid ? string.Empty : MotionCode(before.Motion) + " ";
        var line = program.Lines[resume];
        var resumed = move.HasZ ? null
            : prefix + string.Join(" ", GrblMotion.Words(line).Select(w => line.Substring(w.Start, w.Length)))
                + $" Z{Number(move.End.Z / (move.Inches ? GrblMotion.MillimetresPerInch : 1))}";
        var indexOfSource = new int[program.Text.Split('\n').Length + 1];
        Array.Fill(indexOfSource, -1);
        for (var k = 0; k < program.Count; k++)
        {
            indexOfSource[program.SourceLines[k]] = k;
        }

        var lineEnd = program.Text.Contains(CrLf, StringComparison.Ordinal) ? CrLf : "\n";
        var output = new List<string>();
        var source = program.Text.Split('\n');
        for (var s = 0; s < source.Length; s++)
        {
            var raw = source[s].TrimEnd('\r');
            var index = indexOfSource[s + 1];
            if (index == resume)
            {
                output.AddRange(inserted);
                output.Add(resumed ?? prefix + raw);
            }
            else if (index >= 0 && index < cut)
            {
                if (Executed(program, index, raw) is string kept)
                {
                    output.Add(kept);
                }
            }
            else
            {
                output.Add(raw);
            }
        }

        return string.Join(lineEnd, output);
    }

    // The executed line without what moves the axes; null when nothing else is left.
    private static string? Executed(GrblProgram program, int index, string raw)
    {
        var line = program.Lines[index];
        if (line.StartsWith('$'))
        {
            return line == HomeLine || line.StartsWith(JogPrefix, StringComparison.Ordinal) ? null : raw;
        }

        var words = GrblMotion.Words(line);
        if (words.Sum(w => w.Length) != line.Length)
        {
            throw new InvalidOperationException($"Line {program.SourceLines[index]} ({line}) is not a sequence of letter-number words.");
        }

        if (words.Any(w => w.Letter == 'G' && w.Value == 10) || !words.Any(Moves))
        {
            return raw;
        }

        var kept = words.Where(w => !Moves(w)).ToList();
        return kept.All(w => w.Letter == 'N') ? null : string.Join(" ", kept.Select(w => line.Substring(w.Start, w.Length)));
    }

    private static bool Moves(GrblWord word)
        => word.Letter is 'X' or 'Y' or 'Z' or 'I' or 'J' or 'K' or 'R'
            || (word.Letter == 'G' && (word.Value is 0 or 1 or 2 or 3 or 80 or 28 or 30 or 53 || GrblMotion.IsProbe(word.Value)));

    private static bool DependsOnPosition(string line)
    {
        if (line.StartsWith('$'))
        {
            return false;
        }

        var words = GrblMotion.Words(line);
        var g10 = words.Any(w => w.Letter == 'G' && w.Value == 10);
        return words.Any(w => w.Letter == 'G' && w.Value is 92 or 28.1 or 30.1) || (g10 && words.Any(w => w.Letter == 'L' && w.Value == 20));
    }

    // Probe targets are left out: the probe stops where it touches.
    private static double HighestZ(GrblProgram program, IReadOnlyList<GrblStep> steps)
    {
        double? highest = null;
        for (var k = 0; k < steps.Count; k++)
        {
            var step = steps[k];
            if (!step.HasZ || step.Motion == GrblMotionMode.Probe)
            {
                continue;
            }

            if (!step.KnownZ)
            {
                throw new InvalidOperationException(
                    $"The Z position of line {program.SourceLines[k]} ({program.Lines[k]}) depends on where the program started (relative moves before an absolute one).");
            }

            highest = Math.Max(highest ?? step.End.Z, step.End.Z);
        }

        return highest ?? throw new InvalidOperationException(NoZMessage);
    }

    private static string MotionCode(GrblMotionMode motion) => motion switch
    {
        GrblMotionMode.Linear => "G1",
        GrblMotionMode.ArcClockwise => "G2",
        GrblMotionMode.ArcCounterClockwise => "G3",
        GrblMotionMode.Cancel => "G80",
        GrblMotionMode.Rapid => "G0",
        _ => throw new ArgumentOutOfRangeException(nameof(motion), motion, "A probe move is never resumed."),
    };

    private static string Number(double value) => value.ToString(NumberFormat, CultureInfo.InvariantCulture);
}
