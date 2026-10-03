using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Core.Toolpaths.Strategies;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// The far stepdown through the pipeline: the same final stock as without it, no gouge, no collision
// event in either collision mode, the layers of the far pass and the normal levels, and no effect on
// "3 axis freedom".
public sealed class FarStepdownPipelineTests
{
    private static PipelineResult Run(string strategy, float farStepdown, CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.RoutingStrategyId = strategy;
        project.CollisionMode = mode;
        project.Parameters.FarStepdown = farStepdown;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);
    }

    private static SimulationService Finished(PipelineResult result)
    {
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        return simulation;
    }

    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void ZLayer_EndsAtThePlainStock_WithoutGougesOrEvents(CollisionMode mode)
    {
        var far = Run(ZLayerByLayerStrategy.StrategyId, 4f, mode);
        var plain = Run(ZLayerByLayerStrategy.StrategyId, 0f, mode);
        Assert.Empty(GougeChecker.Verify(far.Toolpath, far.EffectiveTip, far.Tolerance));
        var farRun = Finished(far);
        var plainRun = Finished(plain);
        Assert.Empty(farRun.Events);
        Assert.Empty(plainRun.Events);
        var slack = 2 * far.Tolerance + CollisionDetector.Tolerance;
        for (var k = 0; k < far.Stock.Map.Z.Length; k++)
        {
            Assert.True(MathF.Abs(farRun.Stock!.Z[k] - plainRun.Stock!.Z[k]) <= slack, $"cell {k}: {farRun.Stock.Z[k]} against {plainRun.Stock.Z[k]}");
        }

        Assert.Equal(new[] { 3f, 1f, 0f }, far.Plan.Steps.Select(s => s.Level));
        Assert.Equal(new[] { 1f, 3f, 1f, 0f }, far.Layers.Select(l => l.Level));
        Assert.Equal(new[] { 3f, 1f, 0f }, plain.Layers.Select(l => l.Level));
    }

    // A 2 mm cutter under a cylinder or frustum head, a 10 x 10 x 2 box on the floor of a 40 x 40 x 9
    // stock, far stepdown 8: the far block must step by the cutter length over shrinking regions
    // while the near band and the stock over the box stand at the top. The box is lower than the
    // cutter length, so its walls never block the head.
    private static PipelineResult RunShortCutter(float farStepdown, CollisionMode mode, bool frustum)
    {
        var project = MillingProject.Default();
        project.CollisionMode = mode;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Tool.CutterLength = 2f;
        if (frustum)
        {
            project.Tool.HeadShape = HeadShape.Frustum;
            project.Tool.HeadDiameter = 7f;
            project.Tool.HeadTopDiameter = 20f;
            project.Tool.HeadLength = 10f;
        }

        project.Parameters.FarStepdown = farStepdown;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 2) }, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(CollisionMode.Recursion, false)]
    [InlineData(CollisionMode.OneRun, false)]
    [InlineData(CollisionMode.Recursion, true)]
    [InlineData(CollisionMode.OneRun, true)]
    public void ShortCutter_FarBlockKeepsTheHeadClear_InBothModes(CollisionMode mode, bool frustum)
    {
        var far = RunShortCutter(8f, mode, frustum);
        var plain = RunShortCutter(0f, mode, frustum);
        Assert.Empty(GougeChecker.Verify(far.Toolpath, far.EffectiveTip, far.Tolerance));

        // The far block's layers descend from the first level, then the walk starts there again.
        var levels = far.Layers.Select(l => l.Level).ToList();
        Assert.Equal(7f, levels[0]);
        Assert.Equal(5f, levels[1]);
        var steps = 1;
        while (steps < levels.Count && levels[steps] < levels[steps - 1])
        {
            steps++;
        }

        Assert.InRange(steps, 2, 4);
        Assert.Equal(7f, levels[steps]);
        var farBlockEnd = far.Layers[steps].FirstSegment;

        // No event inside the far block; beside the box the collision handling of either mode may leave
        // the same events as without a far stepdown, never more.
        var farRun = Finished(far);
        var plainRun = Finished(plain);
        Assert.DoesNotContain(farRun.Events, e => e.SegmentIndex < farBlockEnd);
        Assert.True(farRun.Events.Count <= plainRun.Events.Count, $"{farRun.Events.Count} events against {plainRun.Events.Count} without a far stepdown");

        // The final stock equals the plain one except where the collision handling of the mode decides
        // differently next to the box: a few cells within the head radius of it, in either direction,
        // never below the reach floor (the gouge check above).
        var slack = 2 * far.Tolerance + CollisionDetector.Tolerance;
        var map = far.Stock.Map;
        var differing = 0;
        for (var k = 0; k < map.Z.Length; k++)
        {
            if (MathF.Abs(farRun.Stock!.Z[k] - plainRun.Stock!.Z[k]) <= slack)
            {
                continue;
            }

            var x = map.OriginX + (k % map.Width + 0.5f) * map.CellSize;
            var y = map.OriginY + (k / map.Width + 0.5f) * map.CellSize;
            var dx = MathF.Max(0f, MathF.Max(15f - x, x - 25f));
            var dy = MathF.Max(0f, MathF.Max(15f - y, y - 25f));
            var toBox = MathF.Sqrt(dx * dx + dy * dy);
            Assert.True(toBox <= far.Profile.Tool.HeadRadius, $"cell {k} at ({x}, {y}) stands at {farRun.Stock.Z[k]} against {plainRun.Stock.Z[k]}, {toBox} from the box");
            differing++;
        }

        Assert.True(differing <= 60, $"{differing} cells differ from the run without a far stepdown");

        // Before every step route, no annulus cell of any tip position of the route stands above the
        // head underside: the material of the same step stands one cutter length up at most, the rest
        // lies beyond the head radius at its slab height.
        var profile = far.Profile;
        for (var k = 0; k < steps; k++)
        {
            var from = far.Layers[k].FirstSegment;
            var to = far.Layers[k + 1].FirstSegment;
            var before = map.Clone();
            new SimulationEngine(far.Toolpath, before, profile).SeekToSegment(from);
            for (var s = from; s < to; s++)
            {
                var segment = far.Toolpath.Segments[s];
                if (segment.Kind == MoveKind.Rapid)
                {
                    continue;
                }

                foreach (var tip in new[] { segment.Start, segment.End, (segment.Start + segment.End) / 2 })
                {
                    var i = (int)MathF.Floor((tip.X - map.OriginX) / map.CellSize);
                    var j = (int)MathF.Floor((tip.Y - map.OriginY) / map.CellSize);
                    foreach (var o in profile.AnnulusOffsets)
                    {
                        var ii = i + o.Dx;
                        var jj = j + o.Dy;
                        if (ii < 0 || jj < 0 || ii >= map.Width || jj >= map.Height)
                        {
                            continue;
                        }

                        var z = before[ii, jj];
                        var underside = tip.Z + 2f + o.Dz;
                        Assert.True(float.IsNaN(z) || z <= underside + CollisionDetector.Tolerance + 1e-3f,
                            $"step {k} segment {s}: material at ({ii}, {jj}) stands at {z} above the head underside {underside} of the tip {tip}");
                    }
                }
            }
        }
    }

    [Fact]
    public void ThreeAxisFreedom_IgnoresTheFarStepdown()
    {
        var far = Run(ThreeAxisFreedomStrategy.StrategyId, 4f, CollisionMode.Recursion);
        var plain = Run(ThreeAxisFreedomStrategy.StrategyId, 0f, CollisionMode.Recursion);
        Assert.Equal(plain.Toolpath.Segments, far.Toolpath.Segments);
    }
}
