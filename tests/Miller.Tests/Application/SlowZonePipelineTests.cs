using Miller.Application.Services;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The slow zones of a generated toolpath reach everything downstream: two feed rates in the path,
// statistics and simulated time that include them, F words at the slow rate in the G-code, and still
// no gouge and no simulated collision.
public sealed class SlowZonePipelineTests
{
    [Fact]
    public void GeneratedToolpath_CarriesTheSlowRates_Everywhere()
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 20;
        project.Stock.SizeY = 20;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        var result = new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

        var feed = project.Parameters.FeedRate;
        var slow = SlowZones.SlowRate(feed);
        var feeds = result.Toolpath.Segments.Where(s => s.Kind == MoveKind.Feed).ToList();
        Assert.Equal(new[] { slow, feed }, feeds.Select(s => s.FeedRate).Distinct().OrderBy(r => r));
        Assert.True(feeds.Count(s => s.FeedRate == slow) > 2, "slow pieces");
        Assert.All(result.Toolpath.Segments.Where(s => s.Kind == MoveKind.Plunge), s => Assert.Equal(project.Parameters.PlungeRate, s.FeedRate));
        Assert.All(result.Layers, l => Assert.True(l.FirstSegment == 0 || result.Toolpath.Segments[l.FirstSegment - 1].End == result.Toolpath.Segments[l.FirstSegment].Start));

        var minutes = 0.0;
        foreach (var s in result.Toolpath.Segments)
        {
            minutes += s.Length / (s.Kind == MoveKind.Rapid ? project.Parameters.RapidRate : s.FeedRate);
        }

        Assert.Equal(minutes, result.Statistics.EstimatedMinutes, 3);
        Assert.Equal(result.Toolpath.Count, result.Statistics.SegmentCount);

        var service = new SimulationService();
        service.Load(result);
        service.SeekTo(1f);
        Assert.Equal(result.Statistics.EstimatedMinutes * 60, service.ElapsedSimulated, 1);
        Assert.Empty(service.Events);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Parameters.Tolerance));

        var writer = new StringWriter();
        new GrblPostProcessor().Write(result.Toolpath, project, "Build_0.0.0", writer);
        var lines = writer.ToString().Split('\n');
        Assert.Contains(lines, l => l.StartsWith("G1 ") && l.EndsWith(" " + GCodeFormatter.Word('F', slow)));
        Assert.Contains(lines, l => l.StartsWith("G1 ") && l.EndsWith(" " + GCodeFormatter.Word('F', feed)));
    }
}
