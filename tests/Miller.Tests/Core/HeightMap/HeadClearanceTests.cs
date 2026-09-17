using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

public sealed class HeadClearanceTests
{
    private const float CellSize = 0.5f;
    private const float Tolerance = 0.05f;
    private const float ShortCutter = 6f;
    private const float LongCutter = 12f;
    private const float PlateTop = TestMeshes.SlottedPlateHeight;
    private const float SlotFloor = TestMeshes.SlottedPlateHeight - TestMeshes.SlotDepth;

    private static ToolDefinition Tool(float cutterLength)
        => new() { TipType = TipType.Flat, CutterDiameter = 3f, HeadDiameter = 8f, CutterLength = cutterLength };

    private static HeightMap SlottedPlateModel()
    {
        var mesh = TestMeshes.SlottedPlate();
        var model = MeshRasterizer.CreateGridFor(mesh.Bounds, CellSize, 0f);
        MeshRasterizer.Rasterize(mesh, model, 0f);
        return model;
    }

    // Cells whose whole cutter footprint fits inside the slot: the tip map reaches the slot floor there.
    private static List<(int I, int J)> SlotFloorCells(HeightMap tip)
    {
        var cells = new List<(int, int)>();
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                if (MathF.Abs(tip[i, j] - SlotFloor) < 1e-4f) cells.Add((i, j));
            }
        }

        return cells;
    }

    [Fact]
    public void ShortCutter_CannotReachTheSlotFloor()
    {
        var model = SlottedPlateModel();
        var profile = ToolProfile.Create(Tool(ShortCutter), CellSize);
        var tip = HeightMapDilation.ComputeTipMap(model, profile);
        var limit = HeadClearance.ComputeHeadLimit(model, profile, ShortCutter);
        var effective = HeadClearance.ApplyHeadLimit(tip, limit);
        var mask = HeadClearance.HeadLimitedMask(tip, limit, Tolerance);

        var floorCells = SlotFloorCells(tip);
        Assert.Equal(80, floorCells.Count);
        foreach (var (i, j) in floorCells)
        {
            Assert.Equal(PlateTop - ShortCutter, effective[i, j], 4);
            Assert.True(mask[i, j], $"({i},{j}) should be head-limited");
        }

        Assert.Equal(floorCells.Count, CountTrue(mask));
    }

    [Fact]
    public void LongCutter_ReachesTheSlotFloor()
    {
        var model = SlottedPlateModel();
        var profile = ToolProfile.Create(Tool(LongCutter), CellSize);
        var tip = HeightMapDilation.ComputeTipMap(model, profile);
        var limit = HeadClearance.ComputeHeadLimit(model, profile, LongCutter);
        var effective = HeadClearance.ApplyHeadLimit(tip, limit);

        foreach (var (i, j) in SlotFloorCells(tip))
        {
            Assert.Equal(SlotFloor, effective[i, j], 4);
        }

        Assert.Equal(0, CountTrue(HeadClearance.HeadLimitedMask(tip, limit, Tolerance)));
        Assert.Equal(tip.Z, effective.Z);
    }

    [Fact]
    public void OpenAreas_AreNeverHeadLimited()
    {
        var model = SlottedPlateModel();
        var profile = ToolProfile.Create(Tool(ShortCutter), CellSize);
        var tip = HeightMapDilation.ComputeTipMap(model, profile);
        var limit = HeadClearance.ComputeHeadLimit(model, profile, ShortCutter);
        var mask = HeadClearance.HeadLimitedMask(tip, limit, Tolerance);
        for (var j = 0; j < tip.Height; j++)
        {
            for (var i = 0; i < tip.Width; i++)
            {
                if (MathF.Abs(tip[i, j] - PlateTop) < 1e-4f)
                {
                    Assert.False(mask[i, j], $"plate top at ({i},{j}) must not be head-limited");
                    Assert.True(limit[i, j] <= tip[i, j]);
                }
            }
        }
    }

    [Fact]
    public void Limit_IsNaN_WithoutAnnulusMaterialAndDoesNotConstrain()
    {
        var model = new HeightMap(0, 0, CellSize, 30, 30, float.NaN);
        model[15, 15] = 4f;
        var profile = ToolProfile.Create(Tool(ShortCutter), CellSize);
        var limit = HeadClearance.ComputeHeadLimit(model, profile, ShortCutter);
        Assert.True(float.IsNaN(limit[15, 15]));
        Assert.Equal(4f - ShortCutter, limit[15 + 5, 15], 4);
        Assert.True(float.IsNaN(limit[0, 0]));

        var tip = new HeightMap(0, 0, CellSize, 30, 30, 1f);
        tip[0, 0] = float.NaN;
        var effective = HeadClearance.ApplyHeadLimit(tip, limit);
        Assert.Equal(1f, effective[15, 15]);
        Assert.True(float.IsNaN(effective[0, 0]));
        Assert.False(HeadClearance.HeadLimitedMask(tip, limit, Tolerance)[0, 0]);
    }

    [Fact]
    public void GridMismatchAndBadLength_AreRejected()
    {
        var model = new HeightMap(0, 0, CellSize, 4, 4, 0f);
        var profile = ToolProfile.Create(Tool(ShortCutter), CellSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => HeadClearance.ComputeHeadLimit(model, profile, 0f));
        var other = new HeightMap(0, 0, CellSize, 5, 4, 0f);
        Assert.Throws<ArgumentException>(() => HeadClearance.ApplyHeadLimit(model, other));
        Assert.Throws<ArgumentException>(() => HeadClearance.HeadLimitedMask(model, other, Tolerance));
    }

    private static int CountTrue(bool[,] mask)
    {
        var n = 0;
        foreach (var b in mask) if (b) n++;
        return n;
    }
}
