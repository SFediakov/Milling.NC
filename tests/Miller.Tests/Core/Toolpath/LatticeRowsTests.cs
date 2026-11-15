using Miller.Application.Services;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Toolpaths;

// T-210: rows at the stepover with nodes close enough along a row for the footprints alone to cover the
// cells between two rows (a square lattice up to r * sqrt(2) apart is kept), and lattice nodes only at
// least the cutter radius from the walls, so a stepover beyond r * sqrt(2) no longer fills every lattice
// square with a node off the rows that the route zigzags through.
public sealed class LatticeRowsTests
{
    // The footprint rule of mn_profile_build: cell centres within the radius and its tolerance.
    private static bool Covered(int rowStep, int columnStep, float radius, float cellSize)
    {
        for (var y = 0; y <= rowStep; y++)
        {
            for (var x = 0; x <= columnStep; x++)
            {
                var dx = Math.Min(x, columnStep - x);
                var dy = Math.Min(y, rowStep - y);
                if (cellSize * MathF.Sqrt(dx * dx + dy * dy) > radius + 1e-5f)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [Theory]
    [InlineData(6, 3f, 0.5f, 6)]
    [InlineData(8, 3f, 0.5f, 8)]
    [InlineData(10, 3f, 0.5f, 7)]
    [InlineData(44, 0.6f, 0.025f, 19)]
    [InlineData(50, 0.6f, 0.025f, 1)]
    public void ColumnStep_IsTheLargestStepWhoseFootprintsCoverBetweenTheRows(int rowStep, float radius, float cellSize, int expected)
    {
        var step = NodeLattice.ColumnStep(rowStep, radius, cellSize);
        Assert.Equal(expected, step);
        if (step > 1)
        {
            Assert.True(Covered(rowStep, step, radius, cellSize));
        }

        if (step < rowStep)
        {
            Assert.False(Covered(rowStep, step + 1, radius, cellSize));
        }
    }

    // A box on the floor of a wider stock, the 6 mm cutter, at a stepover within r * sqrt(2) (square
    // lattice) and beyond it (rows).
    private static PipelineResult Pocket(float stepover)
    {
        var project = MillingProject.Default();
        project.Parameters.Stepover = stepover;
        project.Parameters.CellSize = 0.5f;
        project.Stock.SizeX = 60;
        project.Stock.SizeY = 60;
        project.Stock.SizeZ = 6;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 4) }, null, CancellationToken.None);
    }

    // XY direction changes between joined feeds (the slow zones' rule: a sine beyond 1e-3 or a reversal).
    private static int Turns(Miller.Core.Toolpaths.Toolpath toolpath)
    {
        var turns = 0;
        var segments = toolpath.Segments;
        for (var k = 1; k < segments.Count; k++)
        {
            var a = segments[k - 1];
            var b = segments[k];
            if (a.Kind == MoveKind.Rapid || b.Kind == MoveKind.Rapid || a.End != b.Start)
            {
                continue;
            }

            float ux = a.End.X - a.Start.X, uy = a.End.Y - a.Start.Y, wx = b.End.X - b.Start.X, wy = b.End.Y - b.Start.Y;
            var lu = MathF.Sqrt(ux * ux + uy * uy);
            var lw = MathF.Sqrt(wx * wx + wy * wy);
            if (lu < 1e-5f || lw < 1e-5f)
            {
                continue;
            }

            var cross = ux * wy - uy * wx;
            var dot = ux * wx + uy * wy;
            turns += MathF.Abs(cross) > 1e-3f * lu * lw || dot < 0 ? 1 : 0;
        }

        return turns;
    }

    [Fact]
    public void Pocket_AWiderStepoverThanRSqrt2_TakesNoMoreTurnsThanANarrowerOne_AndCutsEverything()
    {
        var square = Pocket(4f);
        var rows = Pocket(5f);
        foreach (var result in new[] { square, rows })
        {
            Assert.Empty(result.Collisions.Report!.Events);
            Assert.Equal(0, result.Uncut.Cells);
        }

        Assert.True(Turns(rows.Toolpath) <= Turns(square.Toolpath), $"{Turns(rows.Toolpath)} turns at stepover 5 against {Turns(square.Toolpath)} at 4");
    }
}
