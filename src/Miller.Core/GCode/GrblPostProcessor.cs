using Miller.Core.Setup;
using Miller.Core.Toolpaths;

namespace Miller.Core.GCode;

// Grbl v1.1 subset, template in docs/DEVELOPMENT_GUIDE.md 6.5: header comments, G21 G90 G94 G17,
// spindle on, the feed rate (T-215: the slow zones of the turn fine only price the routes), G0 to safe Z
// and the start point, then one line per segment end (G0 for rapids, G1 for feeds and plunges), spindle
// off, M30. A G1 line carries F only where its rate differs from the modal one: the travel rates of a move
// without material contact (T-218), the vector feed of the XY and Z rates of a move with a Z part (T-221);
// with all rates equal no G1 line carries F. Line endings are LF regardless of the OS. A feed or plunge at
// a rate neither the milling nor the travel rates give its direction is refused.
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

        var p = project.Parameters;
        var feed = p.FeedRate;
        for (var k = 0; k < toolpath.Count; k++)
        {
            var s = toolpath.Segments[k];
            if (s.Kind == MoveKind.Rapid)
            {
                continue;
            }

            var milling = s.RateAt(p.FeedRate, p.FeedRateZ);
            var travel = s.RateAt(p.TravelRate, p.TravelRateZ);
            if (s.FeedRate != milling && s.FeedRate != travel)
            {
                throw new ArgumentException(
                    $"Segment {k} ({s.Kind}) runs at {GCodeFormatter.Format(s.FeedRate)} mm/min, but its direction runs at {GCodeFormatter.Format(milling)} (milling) or {GCodeFormatter.Format(travel)} mm/min (travel).",
                    nameof(toolpath));
            }
        }

        var tool = project.Tool;
        var stock = project.Stock;
        Line(writer, $"( Miller {Comment(appVersion)} )");
        Line(writer, $"( tool: {Comment(tool.Name)} d={GCodeFormatter.Format(tool.CutterDiameter)} {tool.TipType.ToString().ToLowerInvariant()} )");
        Line(writer, stock.Shape == StockShape.Cylinder
            ? $"( stock: cylinder d={GCodeFormatter.Format(stock.Diameter)} h={GCodeFormatter.Format(stock.Height)} )"
            : $"( stock: box {GCodeFormatter.Format(stock.SizeX)}x{GCodeFormatter.Format(stock.SizeY)}x{GCodeFormatter.Format(stock.SizeZ)} )");
        Line(writer, "G21 G90 G94 G17");
        Line(writer, $"{GCodeFormatter.Word('S', project.Parameters.SpindleRpm)} M3");
        Line(writer, GCodeFormatter.Word('F', feed));

        if (toolpath.Count > 0)
        {
            var start = toolpath.Segments[0].Start;
            Line(writer, $"G0 {GCodeFormatter.Word('Z', start.Z)}");
            Line(writer, $"G0 {GCodeFormatter.Word('X', start.X)} {GCodeFormatter.Word('Y', start.Y)}");
            var modal = feed;
            foreach (var s in toolpath.Segments)
            {
                var xyz = $"{GCodeFormatter.Word('X', s.End.X)} {GCodeFormatter.Word('Y', s.End.Y)} {GCodeFormatter.Word('Z', s.End.Z)}";
                if (s.Kind == MoveKind.Rapid)
                {
                    Line(writer, $"G0 {xyz}");
                }
                else if (s.FeedRate == modal)
                {
                    Line(writer, $"G1 {xyz}");
                }
                else
                {
                    modal = s.FeedRate;
                    Line(writer, $"G1 {xyz} {GCodeFormatter.Word('F', modal)}");
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
