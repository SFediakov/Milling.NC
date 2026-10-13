using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Native;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-166, T-167: the Z layer routes by units, one cave of one slab of far stepdown height each, solved in
// parallel and merged in order. A unit begins where the one before it ends; the boundary lies 1 mm from
// the model, or as far from it as the first route of the unit allows.
public sealed class RouteUnitsTests
{
    private const float WallTop = 6f;

    // A 40 x 4 x 6 wall across a 40 x 40 x 9 stock: levels 7, 5, 3, 1, 0 with far stepdown 4 over
    // stepdown 2 make the slabs 7-5, 3-1 and 0. Level 7 passes over the wall, so the first slab is one
    // cave; below the wall top the two pockets are separate caves: five units.
    private static PipelineResult Wall(CollisionMode mode)
    {
        var project = MillingProject.Default();
        project.CollisionMode = mode;
        project.Stock.AlignZ = StockAlignment.Min;
        project.Parameters.FarStepdown = 4f;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 9;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "wall.stl" });
        return new PipelineService().Run(project, new[] { TestMeshes.Box(40, 4, WallTop) }, null, CancellationToken.None);
    }

    // Two 20 x 9.25 x 12 blocks with a 1.5 mm slot between them filling a 20 x 20 x 12 stock: the slot is
    // the only cave and every position in it lies within 0.75 mm of the model.
    private static PipelineResult Slot()
    {
        var a = TestMeshes.Box(20, 9.25f, 12);
        var b = TestMeshes.Box(20, 9.25f, 12);
        var shift = new Vector3(0, 10.75f, 0);
        var mesh = new Mesh(a.Triangles.Concat(b.Triangles.Select(t => new Triangle(t.A + shift, t.B + shift, t.C + shift))));
        var project = MillingProject.Default();
        project.Stock.AlignZ = StockAlignment.Min;
        project.Tool.CutterDiameter = 0.8f;
        project.Tool.HeadDiameter = 2f;
        project.Parameters.Stepover = 0.4f;
        project.Parameters.FinishingStepover = 0.4f;
        project.Parameters.FarStepdown = 4f;
        project.Stock.SizeX = 20;
        project.Stock.SizeY = 20;
        project.Stock.SizeZ = 12;
        project.Parameters.CellSize = 0.25f;
        project.Models.Add(new ModelPlacement { StlPath = "slot.stl" });
        return new PipelineService().Run(project, new[] { mesh }, null, CancellationToken.None);
    }

    // XY distance from a point to the nearest centre of a model cell (model above the floor).
    private static float ModelDistance(HeightMap model, float floor, float x, float y)
    {
        var nearest = float.PositiveInfinity;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                if (model[i, j] > floor + 1e-4f)
                {
                    var c = model.CellCenter(i, j);
                    nearest = MathF.Min(nearest, (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y));
                }
            }
        }

        return MathF.Sqrt(nearest);
    }

    private static void AssertContinuous(Toolpath path)
    {
        for (var k = 1; k < path.Count; k++)
        {
            Assert.True(path.Segments[k].Start == path.Segments[k - 1].End, $"segment {k} starts at {path.Segments[k].Start}, the one before ends at {path.Segments[k - 1].End}");
        }
    }

    private static int EndingAt(Toolpath path, Vector3 point)
    {
        for (var k = 0; k < path.Count; k++)
        {
            if (path.Segments[k].End == point)
            {
                return k;
            }
        }

        return -1;
    }

    [Theory]
    [InlineData(CollisionMode.Recursion)]
    [InlineData(CollisionMode.OneRun)]
    public void Wall_EachPocketIsCutWholeWithinItsSlab_AndTheUnitsChainAtTheirEntries(CollisionMode mode)
    {
        var result = Wall(mode);
        AssertContinuous(result.Toolpath);
        Assert.Empty(GougeChecker.Verify(result.Toolpath, result.EffectiveTip, result.Tolerance));
        var entries = result.RouteUnitEntries;
        Assert.Equal(4, entries.Count);

        var model = result.Model;
        var boundaries = new List<int> { 0 };
        foreach (var entry in entries)
        {
            var distance = ModelDistance(model, result.Floor, entry.X, entry.Y);
            Assert.True(distance >= 1f, $"entry {entry} lies {distance} mm from the model");
            var at = EndingAt(result.Toolpath, entry);
            Assert.True(at > boundaries[^1], $"entry {entry} is not reached after the unit before it");
            boundaries.Add(at);
        }

        boundaries.Add(result.Toolpath.Count);

        // Below the wall top a unit stays in the pocket of its entry: every cutting move from its entry up to
        // its approach to the next unit's entry (the trailing moves on the next entry's side) lies there.
        var wallMin = float.PositiveInfinity;
        var wallMax = float.NegativeInfinity;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                if (model[i, j] > result.Floor + 1e-4f)
                {
                    wallMin = MathF.Min(wallMin, model.CellCenter(i, j).Y);
                    wallMax = MathF.Max(wallMax, model.CellCenter(i, j).Y);
                }
            }
        }

        var wallMid = (wallMin + wallMax) / 2;
        for (var u = 1; u < boundaries.Count - 1; u++)
        {
            var side = MathF.Sign(entries[u - 1].Y - wallMid);
            var stop = boundaries[u + 1];
            if (u < entries.Count)
            {
                var next = MathF.Sign(entries[u].Y - wallMid);
                while (stop > boundaries[u] + 1)
                {
                    var t = result.Toolpath.Segments[stop - 1];
                    if (t.Kind != MoveKind.Rapid && t.End.Z < WallTop - 1e-3f && MathF.Sign(t.End.Y - wallMid) != next)
                    {
                        break;
                    }

                    stop--;
                }
            }

            for (var k = boundaries[u] + 1; k < stop; k++)
            {
                var s = result.Toolpath.Segments[k];
                if (s.Kind == MoveKind.Rapid || s.End.Z >= WallTop - 1e-3f)
                {
                    continue;
                }

                Assert.True(s.End.Y < wallMin || s.End.Y > wallMax ? MathF.Sign(s.End.Y - wallMid) == side : true, $"unit {u} cuts at {s.End} outside the pocket of its entry {entries[u - 1]}");
            }
        }
    }

    [Fact]
    public void Wall_AnyWorkerCount_GivesTheSameToolpath()
    {
        NativeWorkers.Limit(1);
        PipelineResult one;
        try
        {
            one = Wall(CollisionMode.Recursion);
        }
        finally
        {
            NativeWorkers.Limit(0);
        }

        NativeWorkers.Limit(2);
        PipelineResult two;
        try
        {
            two = Wall(CollisionMode.Recursion);
        }
        finally
        {
            NativeWorkers.Limit(0);
        }

        var all = Wall(CollisionMode.Recursion);
        foreach (var other in new[] { two, all })
        {
            Assert.Equal(one.Toolpath.Count, other.Toolpath.Count);
            for (var k = 0; k < one.Toolpath.Count; k++)
            {
                Assert.Equal(one.Toolpath.Segments[k], other.Toolpath.Segments[k]);
            }

            Assert.Equal(one.RouteUnitEntries, other.RouteUnitEntries);
            Assert.Equal(one.Collisions.Report!.Events.Count, other.Collisions.Report!.Events.Count);
        }
    }

    // No position of the slot lies 1 mm from the model: every boundary takes the farthest one of the
    // positions the unit's first route reaches at its height.
    [Fact]
    public void Slot_WithoutRoomFor1mm_TakesTheFarthestPosition()
    {
        var result = Slot();
        AssertContinuous(result.Toolpath);
        Assert.NotEmpty(result.RouteUnitEntries);
        var model = result.Model;
        var tip = result.EffectiveTip;
        foreach (var entry in result.RouteUnitEntries)
        {
            var farthest = 0f;
            for (var j = 0; j < tip.Height; j++)
            {
                for (var i = 0; i < tip.Width; i++)
                {
                    if (!float.IsNaN(tip[i, j]) && tip[i, j] <= entry.Z + 1e-3f)
                    {
                        var c = model.CellCenter(i, j);
                        farthest = MathF.Max(farthest, ModelDistance(model, result.Floor, c.X, c.Y));
                    }
                }
            }

            var d = ModelDistance(model, result.Floor, entry.X, entry.Y);
            Assert.True(farthest < 1f, $"the slot offers {farthest} mm");
            Assert.Equal(farthest, d, 4);
            Assert.True(EndingAt(result.Toolpath, entry) >= 0, $"entry {entry} is not on the toolpath");
        }
    }

    // Without a far stepdown the plan is one slab and, with one cave at its top, one unit.
    [Fact]
    public void NoFarStepdown_OneCave_IsOneUnit()
    {
        var project = MillingProject.Default();
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        var result = new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);
        Assert.Empty(result.RouteUnitEntries);
        AssertContinuous(result.Toolpath);
    }
}
