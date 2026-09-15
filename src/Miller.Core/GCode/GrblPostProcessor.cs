using Miller.Core.Setup;
using Miller.Core.Toolpaths;

namespace Miller.Core.GCode;

// Grbl v1.1 subset, template in docs/DEVELOPMENT_GUIDE.md 6.5: header comments, G21 G90 G94 G17,
// spindle on, G0 to safe Z and the start point, then one line per segment end (G0 for rapids, G1
// with F only when the rate changes), spindle off, M30. Line endings are LF regardless of the OS.
public sealed class GrblPostProcessor : IPostProcessor
{
    public const string ProcessorId = "grbl";

    private const string LineEnd = "\n";

    public string Id => ProcessorId;

    public string DisplayName => "Grbl (G0/G1)";

    public string FileExtension => ".nc";

    public void Write(Toolpath toolpath, MillingProject project, string appVersion, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(toolpath);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);

        var tool = project.Tool;
        var stock = project.Stock;
        Line(writer, $"( Miller {Comment(appVersion)} )");
        Line(writer, $"( tool: {Comment(tool.Name)} d={GCodeFormatter.Format(tool.CutterDiameter)} {tool.TipType.ToString().ToLowerInvariant()} )");
        Line(writer, stock.Shape == StockShape.Cylinder
            ? $"( stock: cylinder d={GCodeFormatter.Format(stock.Diameter)} h={GCodeFormatter.Format(stock.Height)} )"
            : $"( stock: box {GCodeFormatter.Format(stock.SizeX)}x{GCodeFormatter.Format(stock.SizeY)}x{GCodeFormatter.Format(stock.SizeZ)} )");
        Line(writer, "G21 G90 G94 G17");
        Line(writer, $"{GCodeFormatter.Word('S', project.Parameters.SpindleRpm)} M3");

        if (toolpath.Count > 0)
        {
            var start = toolpath.Segments[0].Start;
            Line(writer, $"G0 {GCodeFormatter.Word('Z', start.Z)}");
            Line(writer, $"G0 {GCodeFormatter.Word('X', start.X)} {GCodeFormatter.Word('Y', start.Y)}");
            float? lastFeed = null;
            foreach (var s in toolpath.Segments)
            {
                var xyz = $"{GCodeFormatter.Word('X', s.End.X)} {GCodeFormatter.Word('Y', s.End.Y)} {GCodeFormatter.Word('Z', s.End.Z)}";
                if (s.Kind == MoveKind.Rapid)
                {
                    Line(writer, $"G0 {xyz}");
                    continue;
                }

                if (lastFeed != s.FeedRate)
                {
                    lastFeed = s.FeedRate;
                    Line(writer, $"G1 {xyz} {GCodeFormatter.Word('F', s.FeedRate)}");
                }
                else
                {
                    Line(writer, $"G1 {xyz}");
                }
            }
        }

        Line(writer, "M5");
        Line(writer, "M30");
    }

    private static void Line(TextWriter writer, string text)
    {
        writer.Write(text);
        writer.Write(LineEnd);
    }

    // Parentheses would end the comment early.
    private static string Comment(string text) => text.Replace('(', '[').Replace(')', ']');
}
