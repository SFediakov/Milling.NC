using System.Numerics;
using System.Text;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.GCode;

public sealed class GrblPostProcessorTests
{
    private const string TestVersion = "Build_0.0.0";

    public static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "Golden", name);

    // Safe height 5 above (0, 0), plunge to Z -1, a 10 mm square, retract; feeds and the plunge at the feed rate.
    private static Toolpath Square()
    {
        var p = MillingProject.Default().Parameters;
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(0, 0, -1), MoveKind.Plunge, p.FeedRate));
        path.Add(new ToolpathSegment(new Vector3(0, 0, -1), new Vector3(10, 0, -1), MoveKind.Feed, p.FeedRate));
        path.Add(new ToolpathSegment(new Vector3(10, 0, -1), new Vector3(10, 10, -1), MoveKind.Feed, p.FeedRate));
        path.Add(new ToolpathSegment(new Vector3(10, 10, -1), new Vector3(0, 10, -1), MoveKind.Feed, p.FeedRate));
        path.Add(new ToolpathSegment(new Vector3(0, 10, -1), new Vector3(0, 0, -1), MoveKind.Feed, p.FeedRate));
        path.Add(new ToolpathSegment(new Vector3(0, 0, -1), new Vector3(0, 0, 5), MoveKind.Rapid, p.RapidRate));
        return path;
    }

    private static string Write(Toolpath toolpath, MillingProject project)
    {
        var writer = new StringWriter();
        new GrblPostProcessor().Write(toolpath, project, TestVersion, writer);
        return writer.ToString();
    }

    [Fact]
    public void Square_MatchesTheGoldenFileByteForByte()
    {
        var expected = File.ReadAllText(GoldenPath("square_grbl.nc"));
        Assert.Equal(expected, Write(Square(), MillingProject.Default()));
    }

    // T-215: the feed is set once, in the header after the spindle, and no move line carries F.
    [Fact]
    public void FeedWord_IsWrittenOnce_InTheHeader()
    {
        var lines = Write(Square(), MillingProject.Default()).Split('\n');
        Assert.Equal("F800", lines[5]);
        Assert.Single(lines, line => line.Contains('F'));
        Assert.All(lines.Where(l => l.StartsWith("G1", StringComparison.Ordinal)), l => Assert.DoesNotContain('F', l));
        Assert.Equal(5, lines.Count(l => l.StartsWith("G1", StringComparison.Ordinal)));
        var text = string.Join('\n', lines);
        Assert.Contains($"( Miller {TestVersion} )", text);
        Assert.EndsWith("M30\n", text);
        Assert.DoesNotContain("\r", text);
    }

    // A feed or plunge at another rate than the project's feed cannot be written as one feed: refused with
    // the segment; rapids may carry any rate (G0 ignores F).
    [Fact]
    public void ASegmentAtAnotherRate_IsRefused()
    {
        var slow = Square();
        var segments = slow.Segments.ToList();
        var path = new Toolpath();
        for (var k = 0; k < segments.Count; k++)
        {
            var s = segments[k];
            path.Add(k == 2 ? s with { FeedRate = s.FeedRate / 3 } : s);
        }

        var error = Assert.Throws<ArgumentException>(() => Write(path, MillingProject.Default()));
        Assert.Contains("Segment 2", error.Message);
        var rapid = new Toolpath();
        rapid.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(10, 0, 5), MoveKind.Rapid, 1234f));
        Assert.Contains("G0 X10 Y0 Z5", Write(rapid, MillingProject.Default()));
    }

    [Fact]
    public void EmptyToolpath_WritesHeaderAndFooterOnly()
    {
        var project = MillingProject.Default();
        project.Stock.Shape = StockShape.Cylinder;
        project.Tool.Name = "ball (test)";
        var text = Write(new Toolpath(), project);
        Assert.Equal(new[]
        {
            $"( Miller {TestVersion} )", "( tool: ball [test] d=6 flat )", "( stock: cylinder d=100 h=30 )", "G21 G90 G94 G17", "S12000 M3", "F800", "M5", "M30", "",
        }, text.Split('\n'));
    }

    [Fact]
    public void Registry_ListsGrbl()
    {
        var grbl = PostProcessorRegistry.GetById(GrblPostProcessor.ProcessorId);
        Assert.IsType<GrblPostProcessor>(grbl);
        Assert.Equal(".nc", grbl.FileExtension);
        Assert.Single(PostProcessorRegistry.All);
        Assert.Throws<KeyNotFoundException>(() => PostProcessorRegistry.GetById("mach3"));
    }

    [Fact]
    public void Write_RejectsMissingVersion()
    {
        Assert.Throws<ArgumentException>(() => new GrblPostProcessor().Write(new Toolpath(), MillingProject.Default(), " ", new StringWriter()));
    }
}
