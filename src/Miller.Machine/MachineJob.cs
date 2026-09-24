using Miller.Machine.Grbl;

namespace Miller.Machine;

public enum MachineJobKind
{
    // A G-code program; it ends with G4 P0, whose ok arrives only after every move is finished.
    Program,

    // The program in Grbl's check mode ($C before and after): parsed and verified, nothing moves.
    Check,

    // Manual lines (console, jog, zero, probe, home); done with the ok of the last line.
    Command,
}

// Lines streamed one at a time; SourceLines gives the program line of each (0 for a line the job adds).
public sealed class MachineJob
{
    public const string SyncLine = "G4P0";
    public const string CheckModeLine = "$C";

    private MachineJob(MachineJobKind kind, string name, IReadOnlyList<string> lines, IReadOnlyList<int> sourceLines)
    {
        Kind = kind;
        Name = name;
        Lines = lines;
        SourceLines = sourceLines;
    }

    public MachineJobKind Kind { get; }

    public string Name { get; }

    public IReadOnlyList<string> Lines { get; }

    public IReadOnlyList<int> SourceLines { get; }

    public int Count => Lines.Count;

    public static MachineJob Program(GrblProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new MachineJob(MachineJobKind.Program, program.Name, program.Lines.Append(SyncLine).ToList(), program.SourceLines.Append(0).ToList());
    }

    public static MachineJob Check(GrblProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var lines = program.Lines.Prepend(CheckModeLine).Append(CheckModeLine).ToList();
        var sources = program.SourceLines.Prepend(0).Append(0).ToList();
        return new MachineJob(MachineJobKind.Check, program.Name, lines, sources);
    }

    // Every line is prepared like a program line; one that cannot be sent throws GrblProgramException.
    public static MachineJob Commands(string name, params string[] lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(lines);
        var prepared = new List<string>();
        for (var k = 0; k < lines.Length; k++)
        {
            var line = GrblProgram.Clean(lines[k], out var problem);
            if (problem is not null)
            {
                throw new GrblProgramException(0, $"'{lines[k]}': {problem}");
            }

            if (line.Length > 0)
            {
                prepared.Add(line);
            }
        }

        return prepared.Count > 0
            ? new MachineJob(MachineJobKind.Command, name, prepared, new int[prepared.Count])
            : throw new GrblProgramException(0, "The command holds no G-code.");
    }
}
