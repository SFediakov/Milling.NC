using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Core.Progress;
using Miller.Core.Setup;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The status bar text names the stage and, where a stage iterates, the iteration and the percent
// of the stage: reach map rounds, head clearance iterations, routing passes. Fractions stay
// cumulative and repeated messages are forwarded only once the bar has visibly moved.
public sealed class StageProgressTests
{
    private static MillingProject BoxProject(string strategyId)
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 20;
        project.Stock.SizeY = 20;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.RoutingStrategyId = strategyId;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static List<ProgressReport> Run(string strategyId)
    {
        var reports = new List<ProgressReport>();
        new PipelineService().Run(BoxProject(strategyId), new[] { TestMeshes.Box(10, 10, 5) }, new SynchronousProgress(reports.Add), CancellationToken.None);
        return reports;
    }

    [Fact]
    public void Messages_NameTheStage_TheIteration_AndThePercent()
    {
        var reports = Run(ThreeAxisFreedomStrategy.StrategyId);
        Assert.Contains(reports, r => r.Stage == "reach map" && r.Message == "reach map: round 1 of 3, 33%");
        Assert.Contains(reports, r => r.Stage == "reach map" && r.Message == "reach map: round 2 of 3, 67%");
        Assert.Contains(reports, r => r.Stage == "reach map" && r.Message == "reach map: round 3 of 3, 100%");
        Assert.Contains(reports, r => r.Stage == "head clearance" && r.Message == "head clearance: iteration 1 of 8, 13%");
        Assert.Contains(reports, r => r.Stage == "route" && r.Message.StartsWith("route: pass 1 of ", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Stage == "route" && r.Message.EndsWith(", 100%", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Stage == "slice" && r.Message == "slice");
        Assert.Equal("Toolpath ready", reports[^1].Message);
        Assert.All(reports.Where(r => r.Stage != "done"), r => Assert.StartsWith(r.Stage, r.Message, StringComparison.Ordinal));
        Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).OrderBy(f => f)), "progress went backwards");
    }

    [Fact]
    public void ReachMapRounds_FillTheirThirdOfTheStage_InOrder()
    {
        var reports = Run(ThreeAxisFreedomStrategy.StrategyId).Where(r => r.Stage == "reach map").ToList();
        var rounds = reports.Select(r => r.Message).Where(m => m.Contains("round", StringComparison.Ordinal))
            .Select(m => int.Parse(m.AsSpan("reach map: round ".Length, 1)))
            .ToList();
        Assert.Equal(new[] { 1, 2, 3 }, rounds.Distinct());
        Assert.True(rounds.SequenceEqual(rounds.OrderBy(r => r)), "rounds out of order");
        var stageStart = reports[0].Fraction;
        var stageEnd = reports[^1].Fraction;
        Assert.Equal(0.25f, stageStart, 4);
        Assert.Equal(0.40f, stageEnd, 4);
    }

    [Fact]
    public void RepeatedMessages_AreForwardedOnlyWhenTheBarMoves()
    {
        var reports = Run(ZLayerByLayerStrategy.StrategyId);
        for (var k = 1; k < reports.Count; k++)
        {
            var same = reports[k].Message == reports[k - 1].Message;
            var moved = reports[k].Fraction - reports[k - 1].Fraction >= PipelineService.MinVisibleDelta;
            Assert.True(!same || moved, $"report {k} repeats '{reports[k].Message}' with the bar at {reports[k].Fraction}");
        }

        Assert.Contains(reports, r => r.Stage == "route" && r.Message.StartsWith("route: pass 1 of ", StringComparison.Ordinal));
    }

    [Fact]
    public void Strategies_CountTheirPasses()
    {
        foreach (var context in new[] { TestContexts.BoxInStock(), TestContexts.BumpPlate() })
        {
            var free = new List<StepProgress>();
            new ThreeAxisFreedomStrategy().Generate(context, new Recorder(free.Add), TestContext.Current.CancellationToken);
            var layered = new List<StepProgress>();
            new ZLayerByLayerStrategy().Generate(context, new Recorder(layered.Add), TestContext.Current.CancellationToken);
            foreach (var reports in new[] { free, layered })
            {
                Assert.NotEmpty(reports);
                Assert.Equal(Enumerable.Range(1, reports.Count), reports.Select(r => r.Step));
                Assert.All(reports, r => Assert.Equal(reports.Count, r.Steps));
                Assert.Equal(1f, reports[^1].Fraction, 4);
                Assert.True(reports.Select(r => r.Fraction).SequenceEqual(reports.Select(r => r.Fraction).OrderBy(f => f)), "progress went backwards");
            }
        }
    }

    private sealed class Recorder : IProgress<StepProgress>
    {
        private readonly Action<StepProgress> _report;

        public Recorder(Action<StepProgress> report) => _report = report;

        public void Report(StepProgress value) => _report(value);
    }

    private sealed class SynchronousProgress : IProgress<ProgressReport>
    {
        private readonly Action<ProgressReport> _handler;

        public SynchronousProgress(Action<ProgressReport> handler) => _handler = handler;

        public void Report(ProgressReport value) => _handler(value);
    }
}
