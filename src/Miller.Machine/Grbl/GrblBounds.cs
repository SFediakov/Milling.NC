using System.Globalization;

namespace Miller.Machine.Grbl;

// The XY rectangle a program's moves end in, in millimetres of the work coordinate system. The
// modal state is followed as far as it moves the tool: G90 and G91, G20 and G21. Lines whose axis
// words are not a target in work coordinates are left out: G53 (machine coordinates), G10 and G92
// (offsets), G28 and G30 (intermediate points). Arcs count by their end points, so a program with
// arcs can reach slightly beyond the rectangle.
public sealed record GrblBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    private const double MillimetresPerInch = 25.4;

    public static GrblBounds? Of(GrblProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        var absolute = true;
        var scale = 1.0;
        double x = 0, y = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var line in program.Lines)
        {
            if (line.StartsWith('$'))
            {
                continue;
            }

            double? wordX = null, wordY = null;
            var skip = false;
            foreach (var (letter, value) in Words(line))
            {
                switch (letter)
                {
                    case 'G' when value == 90:
                        absolute = true;
                        break;
                    case 'G' when value == 91:
                        absolute = false;
                        break;
                    case 'G' when value == 20:
                        scale = MillimetresPerInch;
                        break;
                    case 'G' when value == 21:
                        scale = 1;
                        break;
                    case 'G' when value is 53 or 10 or 92 or 28 or 30:
                        skip = true;
                        break;
                    case 'X':
                        wordX = value;
                        break;
                    case 'Y':
                        wordY = value;
                        break;
                }
            }

            if (skip || (wordX is null && wordY is null))
            {
                continue;
            }

            // A unit or distance word applies to the whole line, wherever it stands in it.
            x = wordX is double nx ? (absolute ? nx * scale : x + nx * scale) : x;
            y = wordY is double ny ? (absolute ? ny * scale : y + ny * scale) : y;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return minX <= maxX ? new GrblBounds(minX, minY, maxX, maxY) : null;
    }

    // Letter-number pairs of a prepared line (upper case, no spaces or comments).
    private static IEnumerable<(char Letter, double Value)> Words(string line)
    {
        var k = 0;
        while (k < line.Length)
        {
            var letter = line[k++];
            var start = k;
            while (k < line.Length && (char.IsAsciiDigit(line[k]) || line[k] is '.' or '-' or '+'))
            {
                k++;
            }

            if (char.IsAsciiLetterUpper(letter) && k > start
                && double.TryParse(line.AsSpan(start, k - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return (letter, value);
            }
        }
    }
}
