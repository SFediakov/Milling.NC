namespace Miller.Machine.Grbl;

// A G-code program prepared for streaming: comments (parentheses and semicolon), spaces, blank lines
// and the % delimiters removed, letters upper case, every line within Grbl's line buffer. A line is
// refused, with its number, when it holds a character Grbl would take as a realtime command
// (? ~ ! and every byte from 0x80) or anything outside printable ASCII: streamed, such a byte would
// hold, resume or reset the machine instead of being part of the line.
public sealed class GrblProgram
{
    // Grbl's line buffer is 80 bytes including the terminator (error 11 above that).
    public const int MaxLineLength = 79;
    private const char ProgramDelimiter = '%';

    private GrblProgram(string name, IReadOnlyList<string> lines, IReadOnlyList<int> sourceLines)
    {
        Name = name;
        Lines = lines;
        SourceLines = sourceLines;
    }

    public string Name { get; }

    public IReadOnlyList<string> Lines { get; }

    // 1-based line number in the source text of every prepared line.
    public IReadOnlyList<int> SourceLines { get; }

    public int Count => Lines.Count;

    public static GrblProgram Parse(string name, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var sources = new List<int>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = Clean(raw, out var problem);
            if (problem is not null)
            {
                throw new GrblProgramException(number, problem);
            }

            if (line.Length > 0)
            {
                lines.Add(line);
                sources.Add(number);
            }
        }

        return lines.Count > 0 ? new GrblProgram(name, lines, sources) : throw new GrblProgramException(0, "The program holds no G-code line.");
    }

    // One line as Grbl receives it; empty for a comment or blank line. Problem is set when the line
    // cannot be sent.
    public static string Clean(string line, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(line);
        problem = null;
        var result = new System.Text.StringBuilder(line.Length);
        var inComment = false;
        foreach (var c in line)
        {
            if (inComment)
            {
                inComment = c != ')';
                continue;
            }

            switch (c)
            {
                case '(':
                    inComment = true;
                    continue;
                case ';':
                    return Finish(result, ref problem);
                case ')':
                    problem = "A ')' closes no comment.";
                    return string.Empty;
                case ' ' or '\t' or '\r':
                    continue;
            }

            if (GrblRealtime.IsRealtimeCharacter(c))
            {
                problem = $"'{(c < 0x80 ? c.ToString() : $"0x{(int)c:X2}")}' is a realtime command to the controller and cannot be part of a line.";
                return string.Empty;
            }

            if (c < 0x21 || c > 0x7E)
            {
                problem = $"The character 0x{(int)c:X2} is not printable ASCII.";
                return string.Empty;
            }

            result.Append(char.ToUpperInvariant(c));
        }

        if (inComment)
        {
            problem = "A '(' comment is not closed on its line.";
            return string.Empty;
        }

        return Finish(result, ref problem);
    }

    private static string Finish(System.Text.StringBuilder result, ref string? problem)
    {
        if (result.Length == 1 && result[0] == ProgramDelimiter)
        {
            return string.Empty;
        }

        if (result.Length > MaxLineLength)
        {
            problem = $"The line has {result.Length} characters without comments and spaces; the controller takes {MaxLineLength}.";
            return string.Empty;
        }

        return result.ToString();
    }
}

public sealed class GrblProgramException : FormatException
{
    public GrblProgramException(int line, string reason)
        : base(line > 0 ? $"Line {line}: {reason}" : reason)
    {
        Line = line;
    }

    public int Line { get; }
}
