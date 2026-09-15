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

    // Safe height 5 above (0, 0), plunge to Z -1, a 10 mm square, retract.
    private static Toolpath Square()
    {
        var p = MillingProject.Default().Parameters;
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(0, 0, -1), MoveKind.Plunge, p.PlungeRate));
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

    [Fact]
    public void FeedWord_AppearsOnlyWhenTheRateChanges()
    {
        var text = Write(Square(), MillingProject.Default());
        Assert.Equal(2, text.Split('\n').Count(line => line.Contains(" F")));
        Assert.Contains($"( Miller {TestVersion} )", text);
        Assert.EndsWith("M30\n", text);
        Assert.DoesNotContain("\r", text);
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
            $"( Miller {TestVersion} )", "( tool: ball [test] d=6 flat )", "( stock: cylinder d=100 h=30 )", "G21 G90 G94 G17", "S12000 M3", "M5", "M30", "",
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
