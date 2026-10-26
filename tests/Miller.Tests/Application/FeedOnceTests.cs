using System.Numerics;
using Miller.Application.Services;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-215 (user request): the speed reduction of the turn fine prices the routes only. The toolpath runs
// every feed and plunge at the feed rate, the .nc file sets that rate once in its header and no G1 line
// changes it, and the estimated time is the file's length over its rates.
public sealed class FeedOnceTests
{
    private const float FeedRate = 150f;

    private static MillingProject Project()
    {
        var project = MillingProject.Default();
        project.Parameters.FeedRate = FeedRate;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Parameters.Stepover = 2f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run() => new PipelineService().Run(Project(), new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

    private static string Nc(PipelineResult result)
    {
        var writer = new StringWriter();
        new GrblPostProcessor().Write(result.Toolpath, Project(), "Build_0.0.0", writer);
        return writer.ToString();
    }

    [Fact]
    public void EveryFeedAndPlunge_RunsAtTheFeedRate_AndRapidsAtTheRapidRate()
    {
        var result = Run();
        Assert.Contains(result.Toolpath.Segments, s => s.Kind == MoveKind.Plunge);
        Assert.Contains(result.Toolpath.Segments, s => s.Kind == MoveKind.Feed);
        Assert.All(result.Toolpath.Segments.Where(s => s.Kind != MoveKind.Rapid), s => Assert.Equal(FeedRate, s.FeedRate));
        Assert.All(result.Toolpath.Segments.Where(s => s.Kind == MoveKind.Rapid), s => Assert.Equal(result.Parameters.RapidRate, s.FeedRate));
    }

    [Fact]
    public void TheEstimatedTime_IsTheLengthOverTheRates()
    {
        var result = Run();
        double minutes = 0;
        foreach (var s in result.Toolpath.Segments)
        {
            minutes += Vector3.Distance(s.Start, s.End) / s.FeedRate;
        }

        Assert.Equal(minutes, result.Statistics.EstimatedMinutes, 3);
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Equal(result.Statistics.EstimatedMinutes * 60, simulation.ElapsedSimulated, 1);
    }

    [Fact]
    public void TheNcFile_SetsTheFeedOnce_InItsHeader()
    {
        var lines = Nc(Run()).Split('\n');
        var feeds = lines.Select((line, k) => (Line: line, Index: k)).Where(t => t.Line.Contains('F')).ToList();
        var single = Assert.Single(feeds);
        Assert.Equal("F150", single.Line);
        var firstMove = Array.FindIndex(lines, l => l.StartsWith("G0", StringComparison.Ordinal) || l.StartsWith("G1", StringComparison.Ordinal));
        Assert.True(single.Index < firstMove, "the feed must be set before the first move");
        Assert.Contains(lines, l => l.StartsWith("G1", StringComparison.Ordinal));
    }

    // Save progress (T-200) on a file with the feed in its header: the header line is a state line of the
    // executed part and stays in the resume program before the remaining moves.
    [Fact]
    public void AResumedProgram_KeepsTheHeaderFeed()
    {
        var program = GrblProgram.Parse("box.nc", Nc(Run()));
        var resume = GrblResume.Build(program, program.Count / 2).Split('\n');
        var feed = Array.IndexOf(resume, "F150");
        Assert.True(feed >= 0, "the resume program lost the feed");
        Assert.True(feed < Array.FindIndex(resume, l => l.StartsWith("G1", StringComparison.Ordinal)), "the feed must come before the first feed move");
        Assert.DoesNotContain(resume, l => l.StartsWith("G1", StringComparison.Ordinal) && l.Contains('F'));
    }
}
