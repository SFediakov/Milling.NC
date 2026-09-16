using System.Numerics;
using Miller.Core.Analysis;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Analysis;

public sealed class UncuttableRegionsTests
{
    private const float Cell = 0.5f;
    private const float Tolerance = 0.05f;
    private const float SlotFloor = TestMeshes.SlottedPlateHeight - TestMeshes.SlotDepth;

    private static StockDefinition PlateStock() => new() { SizeX = TestMeshes.SlottedPlateSize, SizeY = TestMeshes.SlottedPlateSize, SizeZ = TestMeshes.SlottedPlateHeight, Margin = 0 };

    private static (ToolpathContext Context, Mesh MachineMesh) Build(Mesh mesh, StockDefinition stock, ToolDefinition tool)
    {
        var parameters = TestContexts.Parameters(Cell);
        var context = TestContexts.Build(mesh, stock, tool, parameters);
        var machineMesh = mesh.Transform(AxisSetup.Default().ToMatrix(mesh.Bounds, stock));
        return (context, machineMesh);
    }

    // The plate fills its stock, so its floor is the stock bottom at z = 0, not the lowest model cell.
    private static UncuttableResult Compute(ToolpathContext context, Mesh machineMesh, float floor)
    {
        var headLimited = HeadClearance.HeadLimitedMask(context.Tip, context.HeadLimit, Tolerance);
        return UncuttableRegions.Compute(machineMesh, context.Model, context.EffectiveTip, headLimited, context.Profile, floor, Tolerance);
    }

    // The reach map decides by majority: a 4 mm slot is 42% of a 12 mm footprint, so the tool stays
    // out and the slot floor is corner limited; it is 78% of a 6 mm footprint, so the tool enters.
    [Fact]
    public void SlotWiderThanHalfTheFootprint_IsReached()
    {
        var tool = new ToolDefinition { CutterDiameter = 6f, HeadDiameter = 10f, CutterLength = 20f };
        var (context, machineMesh) = Build(TestMeshes.SlottedPlate(), PlateStock(), tool);
        var result = Compute(context, machineMesh, 0f);
        Assert.Equal(0, result.CornerLimitedCells);
        var model = context.Model;
        var (ci, cj) = model.CellOf(TestMeshes.SlottedPlateSize / 2, TestMeshes.SlottedPlateSize / 2);
        Assert.Equal(SlotFloorMachine(model), context.EffectiveTip[ci, cj], 3);
    }

    [Fact]
    public void SlotNarrowerThanHalfTheFootprint_IsCornerLimited()
    {
        var tool = new ToolDefinition { CutterDiameter = 12f, HeadDiameter = 16f, CutterLength = 20f };
        var (context, machineMesh) = Build(TestMeshes.SlottedPlate(), PlateStock(), tool);
        var result = Compute(context, machineMesh, 0f);
        Assert.Equal(0, result.OverhangCells);
        Assert.Equal(0, result.HeadLimitedCells);
        Assert.True(result.CornerLimitedCells > 0);
        var model = context.Model;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                if (MathF.Abs(model[i, j] - PlateTopMachine(model)) < 1e-3f)
                {
                    Assert.False(result.CornerLimited[i, j], $"plate top at ({i}, {j}) marked corner limited");
                }
                else if (MathF.Abs(model[i, j] - SlotFloorMachine(model)) < 1e-3f)
                {
                    Assert.True(result.CornerLimited[i, j], $"slot floor at ({i}, {j}) not corner limited");
                }
            }
        }
    }

    [Fact]
    public void DeepSlotWithAShortCutter_IsHeadLimitedOnTheSlotFloor_AndCornerLimitedAtItsWalls()
    {
        var tool = new ToolDefinition { CutterDiameter = 3f, HeadDiameter = 8f, CutterLength = 6f };
        var (context, machineMesh) = Build(TestMeshes.SlottedPlate(), PlateStock(), tool);
        var result = Compute(context, machineMesh, 0f);
        var tip = context.Tip;
        var model = context.Model;
        var reachable = 0;
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                var onSlotFloor = MathF.Abs(model[i, j] - SlotFloorMachine(model)) < 1e-3f;
                var cutterFits = MathF.Abs(tip[i, j] - SlotFloorMachine(model)) < 1e-3f;
                if (cutterFits)
                {
                    reachable++;
                    Assert.True(result.HeadLimited[i, j], $"cell ({i}, {j}) where the cutter fits is not head limited");
                }
                else if (onSlotFloor)
                {
                    Assert.True(result.CornerLimited[i, j], $"slot cell ({i}, {j}) beside the wall is not corner limited");
                }
                else
                {
                    Assert.False(result.HeadLimited[i, j] || result.CornerLimited[i, j], $"plate top at ({i}, {j}) flagged");
                }
            }
        }

        Assert.True(reachable > 0);
        Assert.Equal(reachable, result.HeadLimitedCells);
    }

    [Fact]
    public void FloatingBox_HasOverhangUnderItsWholeFootprint()
    {
        var mesh = TestMeshes.Box(6, 6, 5).Transform(Matrix4x4.CreateTranslation(0, 0, 3));
        var stock = new StockDefinition { SizeX = 12, SizeY = 12, SizeZ = 10, Margin = 0 };
        var tool = new ToolDefinition { CutterDiameter = 2f, HeadDiameter = 4f, CutterLength = 20f };
        var (context, machineMesh) = Build(mesh, stock, tool);
        var result = Compute(context, machineMesh, context.Model.Min());
        Assert.True(result.OverhangCells > 0);
        var model = context.Model;
        var top = model.Max();
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width; i++)
            {
                var underBox = MathF.Abs(model[i, j] - top) < 1e-3f;
                Assert.Equal(underBox, result.Overhang[i, j]);
            }
        }
    }

    [Fact]
    public void MismatchedGrids_AreRejected()
    {
        var tool = new ToolDefinition { CutterDiameter = 2f, HeadDiameter = 4f, CutterLength = 20f };
        var (context, machineMesh) = Build(TestMeshes.Box(6, 6, 5), new StockDefinition { SizeX = 12, SizeY = 12, SizeZ = 6, Margin = 0 }, tool);
        var other = new HeightMap(0, 0, Cell, 3, 3, 0f);
        Assert.Throws<ArgumentException>(() => UncuttableRegions.Compute(machineMesh, context.Model, other, new bool[3, 3], context.Profile, 0f, Tolerance));
    }

    private static float SlotFloorMachine(HeightMap model) => SlotFloor;

    private static float PlateTopMachine(HeightMap model) => TestMeshes.SlottedPlateHeight;
}
