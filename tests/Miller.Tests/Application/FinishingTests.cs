using System.Numerics;
using System.Text.RegularExpressions;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-222 (user request): finishing passes after the roughing. Each visits the positions the roughing cut, at
// the final level it took them to, on rows of its own pattern (moved by k / (passes + 1) of the stepover,
// half of it for one pass: parallel to the roughing rows, across them or along the diagonal), and runs at
// the milling rates. On a stock milled as planned it cuts nothing beyond the simplifier's slack; what the
// roughing left above the final level under its rows it removes. 0 passes is the former toolpath.
public sealed class FinishingTests
{
    private const float CellSize = 0.5f;
    private const float Stepover = 2f;
    private const int Step = 4;
    private const float FeedRate = 300f;
    private const float TravelRate = 1500f;

    // Removal below this is float noise (CollisionDetector.Tolerance).
    private const float Noise = 1e-4f;

    private static MillingProject Project(int passes, FinishingPattern pattern)
    {
        var project = MillingProject.Default();
        project.Parameters.FeedRate = FeedRate;
        project.Parameters.FeedRateZ = FeedRate;
        project.Parameters.TravelRate = TravelRate;
        project.Parameters.TravelRateZ = TravelRate;
        project.Parameters.FinishingPasses = passes;
        project.Parameters.FinishingPattern = pattern;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = CellSize;
        project.Parameters.Stepover = Stepover;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(int passes, FinishingPattern pattern = FinishingPattern.ShiftedRows)
        => new PipelineService().Run(Project(passes, pattern), new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

    private static IEnumerable<ToolpathSegment> Finishing(PipelineResult result) => result.Toolpath.Segments.Skip(result.FinishingFirstSegment);

    // The stock as the roughing leaves it, and the engine ready to cover the finishing passes.
    private static (HeightMap Stock, SimulationEngine Engine) AfterRoughing(PipelineResult result)
    {
        var stock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, stock, result.Profile);
        engine.SeekToSegment(result.FinishingFirstSegment);
        return (stock, engine);
    }

    private static float MostRemoved(HeightMap before, HeightMap after)
    {
        var most = 0f;
        for (var j = 0; j < before.Height; j++)
        {
            for (var i = 0; i < before.Width; i++)
            {
                most = MathF.Max(most, before[i, j] - after[i, j]);
            }
        }

        return most;
    }

    // Feeds of the finishing passes along a straight line on one level, longer than a cell.
    private static List<ToolpathSegment> LevelRuns(PipelineResult result)
        => Finishing(result).Where(s => s.Kind == MoveKind.Feed && MathF.Abs(s.End.Z - s.Start.Z) < Noise && s.End.Z < result.SafeZ - Noise && s.Length > 1.5f * CellSize).ToList();

    [Fact]
    public void ZeroPasses_KeepTheToolpathWithoutFinishing()
    {
        var result = Run(0);
        Assert.Equal(result.Toolpath.Count, result.FinishingFirstSegment);
    }

    [Fact]
    public void OnePass_CutsNothingOnAStockMilledAsPlanned_AndRunsAtTheMillingRates()
    {
        var result = Run(1);
        Assert.Empty(result.Collisions.Report!.Events);
        Assert.InRange(result.FinishingFirstSegment, 1, result.Toolpath.Count - 2);
        Assert.Equal(MoveKind.Rapid, result.Toolpath.Segments[^1].Kind);
        Assert.Equal(result.SafeZ, result.Toolpath.Segments[^1].End.Z, 4);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));

        // Every feed and plunge of the passes runs at the milling rate, though most of them meet no material.
        Assert.All(Finishing(result).Where(s => s.Kind != MoveKind.Rapid), s => Assert.Equal(FeedRate, s.FeedRate));

        var (stock, engine) = AfterRoughing(result);
        var rough = stock.Clone();
        engine.RunToEnd(TestContext.Current.CancellationToken);
        Assert.True(MostRemoved(rough, stock) <= result.Tolerance + Noise, $"the finishing removed {MostRemoved(rough, stock)} mm");

        // The roughing alone runs as without finishing passes.
        var plain = Run(0);
        Assert.Equal(plain.Toolpath.Segments.Take(plain.Toolpath.Count - 1).Select(s => (s.Start, s.End, s.Kind)),
            result.Toolpath.Segments.Take(result.FinishingFirstSegment).Select(s => (s.Start, s.End, s.Kind)));
    }

    [Fact]
    public void AnArtifactLeftByTheRoughing_IsCutDownToTheFinalLevel()
    {
        var result = Run(1);
        var (stock, engine) = AfterRoughing(result);

        // A bump of 0.6 mm on the floor under the middle of the longest finishing row at the lowest level.
        var floor = LevelRuns(result).Min(s => s.End.Z);
        var row = LevelRuns(result).Where(s => MathF.Abs(s.End.Z - floor) < Noise).MaxBy(s => s.Length);
        var middle = (row.Start + row.End) / 2f;
        var (ci, cj) = stock.CellOf(middle.X, middle.Y);
        var planned = stock[ci, cj];
        Assert.Equal(floor, planned, 3);
        for (var j = cj - 1; j <= cj + 1; j++)
        {
            for (var i = ci - 1; i <= ci + 1; i++)
            {
                stock[i, j] = planned + 0.6f;
            }
        }

        engine.RunToEnd(TestContext.Current.CancellationToken);
        for (var j = cj - 1; j <= cj + 1; j++)
        {
            for (var i = ci - 1; i <= ci + 1; i++)
            {
                Assert.True(stock[i, j] <= planned + result.Tolerance + Noise, $"cell {i}, {j} still at {stock[i, j]} over {planned}");
            }
        }
    }

    [Theory]
    [InlineData(1, FinishingPattern.ShiftedRows, new[] { 2 })]
    [InlineData(2, FinishingPattern.ShiftedRows, new[] { 1, 3 })]
    [InlineData(1, FinishingPattern.CrossRows, new[] { 2 })]
    public void TheRows_LieBetweenTheRoughingRows_ParallelOrAcross(int passes, FinishingPattern pattern, int[] offsets)
    {
        // The roughing rows lie on every Step-th grid line from 0; pass k of n moves them by k / (n + 1) Step.
        var result = Run(passes, pattern);
        var grid = result.EffectiveTip;
        var across = pattern == FinishingPattern.CrossRows;
        var runs = LevelRuns(result);
        var along = runs.Where(s => across ? MathF.Abs(s.End.X - s.Start.X) < Noise : MathF.Abs(s.End.Y - s.Start.Y) < Noise).ToList();
        Assert.True(along.Sum(s => s.Length) > 0.8f * runs.Sum(s => s.Length), "the passes must run along their rows");
        var lines = along.Select(s => across ? grid.CellOf(s.Start.X, s.Start.Y).I : grid.CellOf(s.Start.X, s.Start.Y).J).Distinct().ToList();
        Assert.All(lines, line => Assert.Contains(((line % Step) + Step) % Step, offsets));
        Assert.Equal(offsets.Order(), lines.Select(line => ((line % Step) + Step) % Step).Distinct().Order());
    }

    [Fact]
    public void DiagonalRows_RunAt45Degrees()
    {
        var result = Run(1, FinishingPattern.DiagonalRows);
        Assert.Empty(result.Collisions.Report!.Events);
        var runs = LevelRuns(result);
        var diagonal = runs.Where(s => MathF.Abs(MathF.Abs(s.End.X - s.Start.X) - MathF.Abs(s.End.Y - s.Start.Y)) < 1e-3f).ToList();
        Assert.True(diagonal.Sum(s => s.Length) > 0.5f * runs.Sum(s => s.Length), "the passes must run along the diagonal");
    }

    [Fact]
    public void TheSettings_AreSavedLoadedAndValidated()
    {
        var project = Project(3, FinishingPattern.CrossRows);
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"FinishingPattern\": \"CrossRows\"", json, StringComparison.Ordinal);
        var copy = ProjectSerializer.Deserialize(json).Parameters;
        Assert.Equal((3f, FinishingPattern.CrossRows), (copy.FinishingPasses, copy.FinishingPattern));
        var old = Regex.Replace(Regex.Replace(json, @"\s*""FinishingPasses"": [0-9.]+,", string.Empty), @",\s*""FinishingPattern"": ""[A-Za-z]+""", string.Empty);
        Assert.DoesNotContain("Finishing", old, StringComparison.Ordinal);
        Assert.Equal((0f, FinishingPattern.ShiftedRows), (ProjectSerializer.Deserialize(old).Parameters.FinishingPasses, ProjectSerializer.Deserialize(old).Parameters.FinishingPattern));
        Assert.Equal(3f, MillingPreset.Clone(MillingPreset.FromProject(project, "fine")).Parameters.FinishingPasses);

        Assert.DoesNotContain(ProjectValidator.Validate(project, null).Errors, e => e.Field.StartsWith("Parameters.Finishing", StringComparison.Ordinal));
        foreach (var bad in new[] { -1f, 6f, 1.5f, float.NaN })
        {
            var invalid = Project(0, FinishingPattern.ShiftedRows);
            invalid.Parameters.FinishingPasses = bad;
            Assert.Contains(ProjectValidator.Validate(invalid, null).Errors, e => e.Field == "Parameters.FinishingPasses");
        }

        var unknown = Project(1, (FinishingPattern)7);
        Assert.Contains(ProjectValidator.Validate(unknown, null).Errors, e => e.Field == "Parameters.FinishingPattern");
    }
}
