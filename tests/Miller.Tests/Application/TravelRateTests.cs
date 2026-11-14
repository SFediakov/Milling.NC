using System.Numerics;
using System.Text.RegularExpressions;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.GCode;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-218 (user request): a feed or plunge that meets no material over its whole length runs at the travel
// rate of the Cutting tab, every other one at the feed rate. The check of the generation decides it from
// the stock as the toolpath leaves it; the .nc file writes F only where the rate changes; files written
// before have no travel rate and keep travelling at their feed rate.
public sealed class TravelRateTests
{
    private const float FeedRate = 150f;
    private const float TravelRate = 600f;

    // Removal below this is float noise, the slack of the check (CollisionDetector.Tolerance).
    private const float Removed = 1e-4f;

    // The Z rates equal the XY rates here (T-221), so every move of a kind runs at one rate.
    private static MillingProject Project(float travelRate)
    {
        var project = MillingProject.Default();
        project.Parameters.FeedRate = FeedRate;
        project.Parameters.FeedRateZ = FeedRate;
        project.Parameters.TravelRate = travelRate;
        project.Parameters.TravelRateZ = travelRate;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Parameters.Stepover = 2f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(float travelRate) => new PipelineService().Run(Project(travelRate), new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

    private static string Nc(PipelineResult result, float travelRate)
    {
        var writer = new StringWriter();
        new GrblPostProcessor().Write(result.Toolpath, Project(travelRate), "Build_0.0.0", writer);
        return writer.ToString();
    }

    [Fact]
    public void TheTravelRate_IsSavedAndLoaded_AndOldFilesTravelAtTheirFeedRate()
    {
        Assert.Equal(CuttingParameters.DefaultFeedRate, MillingProject.Default().Parameters.TravelRate);

        var project = Project(TravelRate);
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"TravelRate\": 600", json, StringComparison.Ordinal);
        Assert.Equal(TravelRate, ProjectSerializer.Deserialize(json).Parameters.TravelRate);

        // A file of T-215 has neither travel rate nor Z rates (T-221).
        const string Newer = @"\s*""(TravelRate|TravelRateZ|FeedRateZ)"": [0-9.]+,";
        var old = Regex.Replace(json, Newer, string.Empty);
        Assert.DoesNotContain("TravelRate", old, StringComparison.Ordinal);
        Assert.Equal(FeedRate, ProjectSerializer.Deserialize(old).Parameters.TravelRate);
        Assert.Equal(FeedRate, ProjectSerializer.Deserialize(old).Parameters.TravelRateZ);

        var preset = MillingPreset.FromProject(project, "brass");
        Assert.Equal(TravelRate, MillingPreset.Clone(preset).Parameters.TravelRate);
        var presetJson = System.Text.Json.JsonSerializer.Serialize(preset, ProjectSerializer.Options);
        var oldPreset = System.Text.Json.JsonSerializer.Deserialize<MillingPreset>(Regex.Replace(presetJson, Newer, string.Empty), ProjectSerializer.Options)!;
        Assert.Equal(FeedRate, oldPreset.Parameters.TravelRate);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    public void TheValidator_RefusesATravelRateThatIsNotPositive(float rate)
    {
        var project = Project(rate);
        Assert.Contains(ProjectValidator.Validate(project, null).Errors, e => e.Field == "Parameters.TravelRate");
        project.Parameters.TravelRate = TravelRate;
        Assert.DoesNotContain(ProjectValidator.Validate(project, null).Errors, e => e.Field == "Parameters.TravelRate");
    }

    [Fact]
    public void MovesWithoutMaterialContact_RunAtTheTravelRate_TheOthersAtTheFeedRate()
    {
        var result = Run(TravelRate);
        Assert.Empty(result.Collisions.Report!.Events);
        var segments = result.Toolpath.Segments;
        Assert.Contains(segments, s => s.Kind != MoveKind.Rapid && s.FeedRate == TravelRate);
        Assert.Contains(segments, s => s.Kind != MoveKind.Rapid && s.FeedRate == FeedRate);
        Assert.All(segments.Where(s => s.Kind != MoveKind.Rapid), s => Assert.True(s.FeedRate == FeedRate || s.FeedRate == TravelRate, $"{s}"));

        // Walked one segment at a time over the stock as the path leaves it: travels remove nothing, every
        // other feed and plunge removes material.
        var stock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, stock, result.Profile);
        for (var k = 0; k < segments.Count; k++)
        {
            var before = stock.Clone();
            engine.SeekToSegment(k + 1);
            if (segments[k].Kind == MoveKind.Rapid)
            {
                continue;
            }

            var removed = MostRemoved(before, stock);
            if (segments[k].FeedRate == TravelRate)
            {
                Assert.True(removed <= Removed, $"travel {k} {segments[k]} removed {removed}");
            }
            else
            {
                Assert.True(removed > Removed, $"feed {k} {segments[k]} removed only {removed}");
            }
        }
    }

    [Fact]
    public void TheEstimatedTime_IsTheLengthOverBothRates_AndTheSimulatedTime()
    {
        var result = Run(TravelRate);
        double minutes = 0;
        foreach (var s in result.Toolpath.Segments)
        {
            minutes += Vector3.Distance(s.Start, s.End) / s.FeedRate;
        }

        Assert.Equal(minutes, result.Statistics.EstimatedMinutes, 3);
        Assert.True(result.Statistics.EstimatedMinutes < Run(FeedRate).Statistics.EstimatedMinutes, "faster travels must shorten the program");
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        Assert.Equal(result.Statistics.EstimatedMinutes * 60, simulation.ElapsedSimulated, 1);
    }

    [Fact]
    public void TheNcFile_WritesFOnlyWhereTheRateChanges_AndEveryMoveRunsAtItsSegmentsRate()
    {
        var result = Run(TravelRate);
        var text = Nc(result, TravelRate);
        var lines = text.Split('\n');
        var firstMove = Array.FindIndex(lines, l => l.StartsWith("G0", StringComparison.Ordinal) || l.StartsWith("G1", StringComparison.Ordinal));
        Assert.Equal("F150", Assert.Single(lines.Take(firstMove), l => l.Contains('F')));

        var program = GrblProgram.Parse("box.nc", text);
        var steps = GrblMotion.Walk(program);
        var modal = (double)FeedRate;
        var moves = new List<GrblStep>();
        for (var k = 0; k < program.Count; k++)
        {
            var line = program.Lines[k];
            if (line.StartsWith("G1", StringComparison.Ordinal) && line.Contains('F'))
            {
                Assert.NotEqual(modal, steps[k].Feed);
            }

            modal = steps[k].Feed;
            if (steps[k].Target)
            {
                moves.Add(steps[k]);
            }
        }

        // Two lead-in moves (safe Z, start XY), then one line per segment.
        var segments = result.Toolpath.Segments;
        Assert.Equal(segments.Count + 2, moves.Count);
        for (var k = 0; k < segments.Count; k++)
        {
            if (segments[k].Kind != MoveKind.Rapid)
            {
                Assert.Equal(segments[k].FeedRate, (float)moves[k + 2].Feed);
            }
        }
    }

    [Fact]
    public void EqualRates_GiveOneFeedWord()
    {
        var result = Run(FeedRate);
        Assert.All(result.Toolpath.Segments.Where(s => s.Kind != MoveKind.Rapid), s => Assert.Equal(FeedRate, s.FeedRate));
        Assert.Single(Nc(result, FeedRate).Split('\n'), l => l.Contains('F'));
    }

    [Fact]
    public void AThirdRate_IsRefused()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(0, 0, -1), MoveKind.Plunge, FeedRate));
        path.Add(new ToolpathSegment(new Vector3(0, 0, -1), new Vector3(10, 0, -1), MoveKind.Feed, TravelRate));
        var written = new StringWriter();
        new GrblPostProcessor().Write(path, Project(TravelRate), "Build_0.0.0", written);
        Assert.Contains("G1 X10 Y0 Z-1 F600", written.ToString(), StringComparison.Ordinal);

        path.Add(new ToolpathSegment(new Vector3(10, 0, -1), new Vector3(10, 10, -1), MoveKind.Feed, 333f));
        var refused = Assert.Throws<ArgumentException>(() => new GrblPostProcessor().Write(path, Project(TravelRate), "Build_0.0.0", new StringWriter()));
        Assert.Contains("333", refused.Message, StringComparison.Ordinal);
    }

    // Save progress (T-200) after rate changes: the remaining moves run at the rates they had.
    [Fact]
    public void AResumedProgram_KeepsTheRateOfEveryRemainingMove()
    {
        var program = GrblProgram.Parse("box.nc", Nc(Run(TravelRate), TravelRate));
        var original = GrblMotion.Walk(program);
        var built = 0;
        for (var executed = program.Count / 4; executed < program.Count; executed += program.Count / 8)
        {
            var resumed = GrblProgram.Parse("resume.nc", GrblResume.Build(program, executed));
            var again = GrblMotion.Walk(resumed);
            var cut = Math.Max(0, executed - GrblResume.ReplayedLines);
            var resume = Enumerable.Range(cut, program.Count - cut).First(k => original[k].Target);
            var tail = program.Count - resume;
            for (var k = 0; k < tail; k++)
            {
                Assert.Equal(original[resume + k].Feed, again[again.Count - tail + k].Feed);
            }

            built++;
        }

        Assert.True(built >= 4, $"{built} resume programs");
    }

    private static float MostRemoved(HeightMap before, HeightMap after)
    {
        var most = 0f;
        for (var j = 0; j < before.Height; j++)
        {
            for (var i = 0; i < before.Width; i++)
            {
                var removed = before[i, j] - after[i, j];
                if (removed > most)
                {
                    most = removed;
                }
            }
        }

        return most;
    }
}
