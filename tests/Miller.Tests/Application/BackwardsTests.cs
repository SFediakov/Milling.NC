using System.Numerics;
using Miller.Application.Progress;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-169 to T-172 (user request): the collision mode Backwards. A check from the lowest level up leaves out
// every position whose head would meet material only cutting the model frees; the share of unreached
// positions against the model cells is reported. Since T-193 the Z layer routes the positions the check
// reaches by its own rules (far stepdown only away from every obstacle, everything else level by level,
// route units ending where the next begins), so the slabs run from the top and each ends where the one
// below it starts.
public sealed class BackwardsTests
{
    private const float WallTop = 6f;

    // A 40 x 4 x 6 wall across a 40 x 40 x 9 stock under the default tool: the head clears the wall at
    // every level, so nothing is unreached.
    private static MillingProject Wall(float farStepdown)
    {
        var project = MillingProject.Default();
        project.CollisionMode = CollisionMode.Backwards;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Parameters.FarStepdown = farStepdown;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "wall.stl" });
        return project;
    }

    // A 10 x 10 x 4 box in a 40 x 40 x 9 stock under a 2 mm cutter and a 7 to 10 mm frustum head: below
    // level 3 the head meets the box top beside it, so a ring of positions is unreached there, and the
    // material that ring keeps unreaches a wider ring one level deeper.
    private static MillingProject Frustum(float farStepdown)
    {
        var project = MillingProject.Default();
        project.CollisionMode = CollisionMode.Backwards;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Tool.CutterLength = 2f;
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadDiameter = 7f;
        project.Tool.HeadTopDiameter = 10f;
        project.Tool.HeadLength = 10f;
        project.Parameters.FarStepdown = farStepdown;
        project.Parameters.Stepover = 1f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult RunWall(float farStepdown) => new PipelineService().Run(Wall(farStepdown), new[] { TestMeshes.Box(40, 4, WallTop) }, null, CancellationToken.None);

    private static PipelineResult RunFrustum(float farStepdown) => new PipelineService().Run(Frustum(farStepdown), new[] { TestMeshes.Box(10, 10, 4) }, null, CancellationToken.None);

    private static void AssertContinuous(Toolpath path)
    {
        for (var k = 1; k < path.Count; k++)
        {
            Assert.True(path.Segments[k].Start == path.Segments[k - 1].End, $"segment {k} starts at {path.Segments[k].Start}, the one before ends at {path.Segments[k - 1].End}");
        }
    }

    // The rule of the check on the grid, cell by cell: the deepest level each position reaches, swept
    // from the lowest level up against the closing of the deepest levels of the sweep before, until a
    // sweep changes nothing; a material cell standing h above the head bottom forbids the positions
    // within the meet radius at h plus the head margin. Flat tip.
    private static (long Unreached, long ModelCells) Reference(PipelineResult result, ToolDefinition tool)
    {
        var plan = result.Plan;
        var model = result.Model;
        var deepest = ReferenceDeepest(result, tool);
        long unreached = 0, modelCells = 0;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                for (var k = 0; k < plan.Levels; k++)
                {
                    unreached += plan.Steps[k].Mask[i, j] && deepest[i, j] < k ? 1 : 0;
                    modelCells += model[i, j] > result.Floor + 1e-4f && model[i, j] > plan.Steps[k].Level + 1e-4f ? 1 : 0;
                }
            }
        }

        return (unreached, modelCells);
    }

    // The deepest level index every position reaches by the rule above, -1 for none.
    private static int[,] ReferenceDeepest(PipelineResult result, ToolDefinition tool)
    {
        var plan = result.Plan;
        var stock = result.Stock.Map;
        var model = result.Model;
        int width = stock.Width, height = stock.Height, levels = plan.Levels;
        var cell = stock.CellSize;
        var r = tool.CutterDiameter / 2f;
        var footprint = new List<(int Dx, int Dy)>();
        var reach = (int)MathF.Ceiling(r / cell);
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (cell * MathF.Sqrt(dx * dx + dy * dy) <= r + 1e-5f)
                {
                    footprint.Add((dx, dy));
                }
            }
        }

        var deepest = new int[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                deepest[i, j] = -1;
                for (var k = levels - 1; k >= 0 && deepest[i, j] < 0; k--)
                {
                    deepest[i, j] = plan.Steps[k].Mask[i, j] ? k : -1;
                }
            }
        }

        var margin = MathF.Sqrt(2f) * cell + result.Tolerance;
        for (var round = 0; round <= levels; round++)
        {
            var closing = new float[width, height];
            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    var best = float.NaN;
                    foreach (var (dx, dy) in footprint)
                    {
                        int qi = i + dx, qj = j + dy;
                        if (qi >= 0 && qi < width && qj >= 0 && qj < height && deepest[qi, qj] >= 0)
                        {
                            var z = plan.Steps[deepest[qi, qj]].Level;
                            best = float.IsNaN(best) || z < best ? z : best;
                        }
                    }

                    closing[i, j] = best;
                }
            }

            var next = new int[width, height];
            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    next[i, j] = -1;
                }
            }

            for (var k = levels - 1; k >= 0; k--)
            {
                var clamp = k > 0 ? plan.Steps[k - 1].Level : result.Stock.StockTop;
                var headBottom = plan.Steps[k].Level + tool.CutterLength;
                var sources = new List<(int I, int J, float R2)>();
                for (var j = 0; j < height; j++)
                {
                    for (var i = 0; i < width; i++)
                    {
                        var s = stock[i, j];
                        if (float.IsNaN(s))
                        {
                            continue;
                        }

                        var a = closing[i, j];
                        var top = float.IsNaN(a) ? s : MathF.Min(s, MathF.Max(a, clamp));
                        var h = top - headBottom;
                        if (h > 1e-4f)
                        {
                            var radius = (Meet(tool, h) + margin) / cell;
                            sources.Add((i, j, radius * radius));
                        }
                    }
                }

                for (var j = 0; j < height; j++)
                {
                    for (var i = 0; i < width; i++)
                    {
                        if (!plan.Steps[k].Mask[i, j] || next[i, j] >= 0)
                        {
                            continue;
                        }

                        var clear = sources.All(c => (c.I - i) * (c.I - i) + (c.J - j) * (c.J - j) > c.R2);
                        next[i, j] = clear ? k : -1;
                    }
                }
            }

            var same = true;
            for (var j = 0; j < height && same; j++)
            {
                for (var i = 0; i < width && same; i++)
                {
                    same = next[i, j] == deepest[i, j];
                }
            }

            deepest = next;
            if (same)
            {
                break;
            }
        }

        return deepest;
    }

    // The head radius h above the head bottom, never less than the cutter radius.
    private static float Meet(ToolDefinition tool, float h)
    {
        var bottom = tool.HeadDiameter / 2f;
        var top = tool.HeadTopDiameter / 2f;
        var radius = bottom;
        if (tool.HeadShape == HeadShape.Frustum)
        {
            var widening = top > bottom ? tool.HeadLength : 0f;
            radius = !(widening > 0) || h >= widening ? MathF.Max(bottom, top) : bottom + (top - bottom) * MathF.Max(0f, h) / widening;
        }

        return MathF.Max(radius, tool.CutterDiameter / 2f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void TheHeadClearsEveryCut_WithoutGouge_OnAContinuousPath(float farStepdown)
    {
        foreach (var result in new[] { RunWall(farStepdown), RunFrustum(farStepdown) })
        {
            Assert.Equal(1, result.Passes);
            Assert.Empty(result.Collisions.Report!.Events);
            Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
            AssertContinuous(result.Toolpath);
            Assert.Equal(MoveKind.Plunge, result.Toolpath.Segments[0].Kind);
            Assert.Equal(result.SafeZ, result.Toolpath.Segments[0].Start.Z);
        }
    }

    // The counts equal the rule computed cell by cell; the wall needs no unreached position, the box under
    // the frustum a ring on the two lowest levels.
    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void UnreachedPositions_AndModelCells_FollowTheRule(float farStepdown)
    {
        var wall = RunWall(farStepdown);
        var frustum = RunFrustum(farStepdown);
        foreach (var (result, tool) in new[] { (wall, Wall(farStepdown).Tool), (frustum, Frustum(farStepdown).Tool) })
        {
            var expected = Reference(result, tool);
            var unreached = Assert.NotNull(result.Unreached);
            Assert.Equal(expected, (unreached.Unreached, unreached.ModelCells));
            Assert.Equal(100.0 * unreached.Unreached / unreached.ModelCells, unreached.Percent, 10);
        }

        Assert.Equal(0, wall.Unreached!.Value.Unreached);
        Assert.Equal(1, wall.Unreached.Value.Sweeps);
        Assert.True(frustum.Unreached!.Value.Unreached > 0, "the frustum must leave positions beside the box unreached");
        Assert.True(frustum.Unreached.Value.Sweeps >= 2, "the material of the first unreached ring must unreach more positions");
    }

    // A slab (levels 7-5, 3-1 and 0) runs whole before the next deeper one, in machining order from the top,
    // its far block first at its bottom level (T-193); the band beside the wall is cut at every level, so
    // every level of the plan is a layer (the far go of T-171 cut the band too and skipped level 3).
    [Fact]
    public void Slabs_RunFromTheTop_FarBlockFirst_AndEveryLevelIsALayer()
    {
        var result = RunWall(4f);
        var levels = result.Plan.Steps.Select(s => s.Level).ToList();
        Assert.All(result.Layers, l => Assert.Contains(l.Level, levels));
        var slabs = result.Layers.Select(l => levels.FindIndex(z => z == l.Level) / 2).ToList();
        Assert.True(slabs.SequenceEqual(slabs.OrderBy(s => s)), $"slabs in path order: {string.Join(" ", slabs)}");
        Assert.Equal(new[] { 0, 1, 2 }, slabs.Distinct());
        Assert.Equal(levels.OrderBy(z => z), result.Layers.Select(l => l.Level).Distinct().OrderBy(z => z));

        var farCount = 0;
        foreach (var cellIsFar in result.FarStepdown.Milled)
        {
            farCount += cellIsFar ? 1 : 0;
        }

        Assert.True(farCount > 0, "the pockets must be cut by the far block");
        Assert.Equal(levels[1], result.Layers[0].Level);
    }

    // T-193: the far stepdown takes only positions reached at the bottom level of their slab and at least the
    // stepover from every obstacle there, the model, the stock it stands above and every position the check
    // leaves unreached at that level; the ring the frustum head cannot reach beside the box stays out.
    [Fact]
    public void TheFarBlock_KeepsTheStepoverFromTheModel_AndFromEveryUnreachedPosition()
    {
        var project = Frustum(4f);
        var result = RunFrustum(4f);
        var plan = result.Plan;
        var tip = result.EffectiveTip;
        var deepest = ReferenceDeepest(result, project.Tool);
        var cell = tip.CellSize;
        int width = tip.Width, height = tip.Height;
        var group = (int)MathF.Round(project.Parameters.FarStepdown / project.Parameters.Stepdown);
        var allowed = new bool[width, height];
        for (var bottom = group - 1; bottom < plan.Levels; bottom += group)
        {
            var level = plan.Steps[bottom].Level;
            var obstacles = new List<(int I, int J)>();
            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    if ((!float.IsNaN(tip[i, j]) && tip[i, j] > level + 1e-4f) || (plan.Steps[bottom].Mask[i, j] && deepest[i, j] < bottom))
                    {
                        obstacles.Add((i, j));
                    }
                }
            }

            for (var j = 0; j < height; j++)
            {
                for (var i = 0; i < width; i++)
                {
                    if (plan.Steps[bottom].Mask[i, j] && deepest[i, j] >= bottom
                        && obstacles.All(o => cell * MathF.Sqrt((o.I - i) * (o.I - i) + (o.J - j) * (o.J - j)) + 1e-4f >= project.Parameters.Stepover))
                    {
                        allowed[i, j] = true;
                    }
                }
            }
        }

        var far = 0;
        var nearUnreached = 0;
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                if (!result.FarStepdown.Milled[i, j])
                {
                    continue;
                }

                far++;
                Assert.True(allowed[i, j], $"far position ({i}, {j}) lies within the stepover of an obstacle or is not reached at its slab's bottom");
            }
        }

        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                nearUnreached += plan.Steps[plan.Levels - 1].Mask[i, j] && deepest[i, j] < plan.Levels - 1 ? 1 : 0;
            }
        }

        Assert.True(far > 0, "the open stock must be cut by the far block");
        Assert.True(nearUnreached > 0, "the frustum must leave a ring unreached");
        Assert.Equal(1, result.Passes);
        Assert.Empty(result.Collisions.Report!.Events);
    }

    // Where nothing is unreached the far stepdown changes the order, not what is removed: the wall's final
    // stock with far goes equals the one without them within the simplifier's slack.
    [Theory]
    [InlineData(0f)]
    [InlineData(4f)]
    public void TheWall_EndsAtTheStockWithoutAFarStepdown(float farStepdown)
    {
        static SimulationService Finished(PipelineResult result)
        {
            var simulation = new SimulationService();
            simulation.Load(result);
            simulation.RunToEnd();
            return simulation;
        }

        var backwards = RunWall(farStepdown);
        Assert.Equal(0, backwards.Unreached!.Value.Unreached);
        var reference = RunWall(0f);
        var ours = Finished(backwards);
        var theirs = Finished(reference);
        Assert.Empty(ours.Events);
        Assert.Empty(theirs.Events);
        var slack = 2 * backwards.Tolerance + CollisionDetector.Tolerance;
        for (var k = 0; k < backwards.Stock.Map.Z.Length; k++)
        {
            Assert.True(MathF.Abs(ours.Stock!.Z[k] - theirs.Stock!.Z[k]) <= slack, $"cell {k}: {ours.Stock.Z[k]} against {theirs.Stock.Z[k]}");
        }
    }

    // Every unit begins where the one before it ends, slabs included: each entry is reached on the path in
    // order.
    [Fact]
    public void EverySlabBeginsWhereTheOneAboveEnds()
    {
        var result = RunWall(4f);
        AssertContinuous(result.Toolpath);
        var position = 0;
        foreach (var entry in result.RouteUnitEntries)
        {
            var at = -1;
            for (var k = position; k < result.Toolpath.Count && at < 0; k++)
            {
                at = result.Toolpath.Segments[k].End == entry ? k : -1;
            }

            Assert.True(at >= position, $"entry {entry} is not reached after segment {position}");
            position = at;
        }

        Assert.True(result.RouteUnitEntries.Count >= 2, "the wall fixture must have several slabs");
    }

    // The checker on half the workers and the solver on the other half give the same toolpath and counts
    // for any number of workers.
    [Fact]
    public void TheResult_IsTheSameForEveryWorkerCount()
    {
        PipelineResult Run(int workers)
        {
            NativeWorkers.Limit(workers);
            try
            {
                return RunFrustum(4f);
            }
            finally
            {
                NativeWorkers.Limit(0);
            }
        }

        var one = Run(1);
        foreach (var other in new[] { Run(2), Run(0) })
        {
            Assert.Equal(one.Toolpath.Segments, other.Toolpath.Segments);
            Assert.Equal(one.Unreached, other.Unreached);
            Assert.Equal(one.RouteUnitEntries, other.RouteUnitEntries);
        }
    }

    // Cancelled while the slabs are routed, the run stops with the checker thread joined.
    [Fact]
    public void Cancellation_DuringTheRoutes_StopsBothSides()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress(report =>
        {
            if (report.Stage == "route")
            {
                cts.Cancel();
            }
        });
        Assert.ThrowsAny<OperationCanceledException>(() => new PipelineService().Run(Frustum(4f), new[] { TestMeshes.Box(10, 10, 4) }, progress, cts.Token));
        Assert.Empty(RunFrustum(4f).Collisions.Report!.Events);
    }

    // T-214: the strategy on its own runs the backward check too (the one collision handling); a mode
    // outside the two is refused by the native library.
    [Fact]
    public void TheStrategyAlone_RunsTheBackwardCheck_AndRefusesAnUnknownMode()
    {
        Assert.DoesNotContain(ProjectValidator.Validate(Wall(0f), null).Errors, e => e.Field == "Strategy.CollisionMode");
        var context = TestContexts.BoxInStock();
        Assert.Equal(CollisionMode.Backwards, context.CollisionMode);
        Assert.NotEmpty(new ZLayerByLayerStrategy().Generate(context, null, TestContext.Current.CancellationToken).Segments);
        var unknown = context with { CollisionMode = (CollisionMode)5 };
        var error = Assert.Throws<ArgumentException>(() => new ZLayerByLayerStrategy().Generate(unknown, null, TestContext.Current.CancellationToken));
        Assert.Contains("Unknown collision mode 5", error.Message);
    }

    // A far stepdown deeper than the cutter length: the far block steps within the cutter length as in every
    // mode (T-193), the validator gives the Z layer's warning and the result keeps every head clear.
    [Fact]
    public void AFarStepdownDeeperThanTheCutter_IsWarned_AndStaysClear()
    {
        var project = Frustum(4f);
        Assert.Contains(ProjectValidator.Validate(project, null).Warnings, w => w.Field == "Parameters.FarStepdown" && w.Message.Contains("steps of at most the cutter length", StringComparison.Ordinal));
        Assert.Empty(RunFrustum(4f).Collisions.Report!.Events);
    }

    private sealed class SynchronousProgress(Action<ProgressReport> report) : IProgress<ProgressReport>
    {
        public void Report(ProgressReport value) => report(value);
    }
}
