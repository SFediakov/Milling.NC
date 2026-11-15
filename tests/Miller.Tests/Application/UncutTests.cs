using Miller.Application.Services;
using Miller.Core.Generation;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-173 (user request): at the end of a generation the cells the toolpath leaves above what the plan
// intended are summarized: those standing more than the tolerance above the closing of the deepest level
// of every position in the first pass's plan, with their area, the volume above that surface and the
// highest of them.
public sealed class UncutTests
{
    private static MillingProject Wall(CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.CollisionMode = mode;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "wall.stl" });
        return project;
    }

    // The frustum head beside a 4 mm box leaves a ring of material it cannot reach (see BackwardsTests).
    private static MillingProject Frustum(CollisionMode mode, float farStepdown)
    {
        var project = Wall(mode);
        project.Tool.CutterLength = 2f;
        project.Tool.HeadShape = HeadShape.Frustum;
        project.Tool.HeadDiameter = 7f;
        project.Tool.HeadTopDiameter = 10f;
        project.Tool.HeadLength = 10f;
        project.Parameters.FarStepdown = farStepdown;
        project.Parameters.Stepover = 1f;
        project.Models[0] = new ModelPlacement { StlPath = "box.stl" };
        return project;
    }

    private static PipelineResult RunWall(CollisionMode mode) => new PipelineService().Run(Wall(mode), new[] { TestMeshes.Box(40, 4, 6) }, null, CancellationToken.None);

    private static PipelineResult RunFrustum(CollisionMode mode, float farStepdown) => new PipelineService().Run(Frustum(mode, farStepdown), new[] { TestMeshes.Box(10, 10, 4) }, null, CancellationToken.None);

    // The summary computed again from the simulation run to the end and the plan of a one-pass result.
    private static UncutCells Reference(PipelineResult result)
    {
        Assert.Equal(1, result.Passes);
        var simulation = new SimulationService();
        simulation.Load(result);
        simulation.RunToEnd();
        var final = simulation.Stock!;
        var plan = result.Plan;
        int width = final.Width, height = final.Height;
        var cell = final.CellSize;
        var deepest = new float[width, height];
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                deepest[i, j] = float.NaN;
                for (var k = plan.Levels - 1; k >= 0 && float.IsNaN(deepest[i, j]); k--)
                {
                    deepest[i, j] = plan.Steps[k].Mask[i, j] ? plan.Steps[k].Level : float.NaN;
                }
            }
        }

        var cells = 0;
        double volume = 0;
        var highest = 0f;
        for (var j = 0; j < height; j++)
        {
            for (var i = 0; i < width; i++)
            {
                var intended = float.NaN;
                foreach (var o in result.Profile.Offsets)
                {
                    int qi = i + o.Dx, qj = j + o.Dy;
                    if (qi < 0 || qi >= width || qj < 0 || qj >= height || float.IsNaN(deepest[qi, qj]))
                    {
                        continue;
                    }

                    var candidate = deepest[qi, qj] + o.Dz;
                    intended = float.IsNaN(intended) || candidate < intended ? candidate : intended;
                }

                var f = final[i, j];
                if (float.IsNaN(f) || float.IsNaN(intended) || !(f > intended + result.Tolerance + 1e-4f))
                {
                    continue;
                }

                cells++;
                volume += (double)(f - intended) * cell * cell;
                highest = MathF.Max(highest, f - intended);
            }
        }

        return new UncutCells(cells, cells * (double)cell * cell, volume, highest);
    }

    [Fact]
    public void NotCutCells_EqualTheSimulationAgainstThePlan()
    {
        foreach (var result in new[] { RunWall(CollisionMode.Backwards), RunFrustum(CollisionMode.Backwards, 0f), RunFrustum(CollisionMode.Backwards, 4f) })
        {
            var expected = Reference(result);
            Assert.Equal(expected.Cells, result.Uncut.Cells);
            Assert.Equal(expected.AreaMm2, result.Uncut.AreaMm2, 9);
            Assert.Equal(expected.VolumeMm3, result.Uncut.VolumeMm3, 6);
            Assert.Equal(expected.MaxHeight, result.Uncut.MaxHeight);
        }
    }

    // The wall is cut to the plan; under the frustum the ring the head cannot reach stays.
    [Fact]
    public void TheWallIsCutWhole_TheFrustumBoxIsNot()
    {
        Assert.Equal(0, RunWall(CollisionMode.Backwards).Uncut.Cells);
        var backwards = RunFrustum(CollisionMode.Backwards, 0f);
        Assert.True(backwards.Uncut.Cells > 0, "the unreached ring must leave material");
        Assert.True(backwards.Uncut.VolumeMm3 > 0 && backwards.Uncut.MaxHeight > backwards.Tolerance);
    }

    [Fact]
    public void Report_NamesTheCells_AndTheUnreachedPositions()
    {
        var wall = RunWall(CollisionMode.Backwards);
        Assert.StartsWith(UncutReport.NothingLeftText + "\nUnreached positions (Backwards): 0 of ", UncutReport.Summarize(wall));
        Assert.Equal("not cut: 0 cells", UncutReport.StatusSuffix(wall));

        var frustum = RunFrustum(CollisionMode.Backwards, 0f);
        var uncut = frustum.Uncut;
        var unreached = frustum.Unreached!.Value;
        var text = UncutReport.Summarize(frustum);
        Assert.StartsWith(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Not cut: {uncut.Cells} cells ({uncut.AreaMm2:0.00} mm2) stand more than the tolerance above the planned surface, {uncut.VolumeMm3:0.000} mm3 of material, at most {uncut.MaxHeight:0.000} mm."), text);
        Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Unreached positions (Backwards): {unreached.Unreached} of {unreached.ModelCells} model cells, {unreached.Percent:0.00} %"), text);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"not cut: {uncut.Cells} cells"), UncutReport.StatusSuffix(frustum));
    }
}
