using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Setup;

// Alignment of the model union to an auto-fit stock (stock rule) and of one model inside the stock
// (offset fixed point), with the case the offset cannot solve reported instead of drifting.
public sealed class ModelAlignmentTests
{
    private static MillingProject Project(params ModelPlacement[] placements)
    {
        var project = MillingProject.Default();
        project.Models.AddRange(placements);
        return project;
    }

    [Theory]
    [InlineData(StockAlignment.Min, StockAlignment.Min, StockAlignment.Min, 0f, 0f, 0f)]
    [InlineData(StockAlignment.Center, StockAlignment.Center, StockAlignment.Max, 45f, 45f, 25f)]
    [InlineData(StockAlignment.Max, StockAlignment.Min, StockAlignment.Center, 90f, 0f, 12.5f)]
    public void StockCorner_FollowsThePerAxisAlignment(StockAlignment x, StockAlignment y, StockAlignment z, float minX, float minY, float minZ)
    {
        // 10 x 10 x 5 box with its minimum at the origin in a 100 x 100 x 30 stock: the stock minimum
        // corner lands where the alignment says, expressed in the box's own coordinates.
        var stock = new StockDefinition { AlignX = x, AlignY = y, AlignZ = z };
        var bounds = TestMeshes.Box(10, 10, 5).Bounds;
        var corner = AxisSetup.StockCorner(bounds, stock);
        Assert.Equal(bounds.Min.X - minX, corner.X, 3);
        Assert.Equal(bounds.Min.Y - minY, corner.Y, 3);
        Assert.Equal(bounds.Min.Z - minZ, corner.Z, 3);
    }

    [Fact]
    public void DefaultAlignment_IsCentredWithTheModelTopAtTheStockTop()
    {
        var stock = StockDefinition.Default();
        Assert.Equal(StockAlignment.Center, stock.AlignX);
        Assert.Equal(StockAlignment.Center, stock.AlignY);
        Assert.Equal(StockAlignment.Max, stock.AlignZ);
        var bounds = TestMeshes.Box(10, 10, 5).Bounds;
        var corner = AxisSetup.StockCorner(bounds, stock);
        Assert.Equal(bounds.Center.X - 50f, corner.X, 3);
        Assert.Equal(bounds.Max.Z - 30f, corner.Z, 3);
    }

    [Fact]
    public void LoneModel_BottomAlignedByTheStockRule_LandsOnTheStockBottom()
    {
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" });
        project.Stock.AlignZ = StockAlignment.Min;
        var machine = ModelLayout.MachineBounds(project, new[] { mesh.Bounds });
        // Machine zero is the stock's minimum corner, so the model bottom sits at Z 0 and the stock
        // extends 30 above it.
        Assert.Equal(0f, machine.Min.Z, 3);
        Assert.Equal(5f, machine.Max.Z, 3);
        Assert.Equal(45f, machine.Min.X, 3);

        project.Stock.AlignZ = StockAlignment.Max;
        machine = ModelLayout.MachineBounds(project, new[] { mesh.Bounds });
        Assert.Equal(25f, machine.Min.Z, 3);
        Assert.Equal(30f, machine.Max.Z, 3);
    }

    [Fact]
    public void AlignedOffset_MinAndMax_MoveOneModelToTheStockSides()
    {
        // Two boxes; the stock corner sits at the union minimum (explicit placement), model 1 is
        // moved while model 0 anchors the union: its minimum goes to the stock minimum, its maximum
        // to the stock maximum (100 wide, 30 high).
        var a = TestMeshes.Box(10, 10, 5);
        var b = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(20, 30, 0) });
        project.Stock.Placement = StockPlacement.Explicit;
        var bounds = new[] { a.Bounds, b.Bounds };

        var minX = ModelLayout.AlignedOffset(project, bounds, 1, 0, StockAlignment.Min);
        Assert.Equal(0f, minX.X, 3);
        Assert.Equal(30f, minX.Y, 3);
        var maxX = ModelLayout.AlignedOffset(project, bounds, 1, 0, StockAlignment.Max);
        Assert.Equal(90f, maxX.X, 3);
        var maxZ = ModelLayout.AlignedOffset(project, bounds, 1, 2, StockAlignment.Max);
        Assert.Equal(25f, maxZ.Z, 3);
        Assert.Equal(20f, maxZ.X, 3);
        Assert.Equal(new Vector3(20, 30, 0), project.Models[1].Offset);

        // Model 0 defines the union minimum on X: moving it moves the stock, so the fixed point of
        // "minimum at the stock minimum" is any offset; the shift is zero at once.
        Assert.Equal(0f, ModelLayout.AlignedOffset(project, bounds, 0, 0, StockAlignment.Min).X, 3);
    }

    [Fact]
    public void AlignedOffset_ReportsWhenTheStockFollowsTheModel()
    {
        // A lone model in an auto-fit stock: the stock is centred on it and hangs from its top, so no
        // offset can move it to the stock bottom or centre it on Z.
        var mesh = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" });
        var bounds = new[] { mesh.Bounds };
        var ex = Assert.Throws<InvalidOperationException>(() => ModelLayout.AlignedOffset(project, bounds, 0, 2, StockAlignment.Min));
        Assert.Contains("Z", ex.Message);
        Assert.Throws<InvalidOperationException>(() => ModelLayout.CenteredOffset(project, bounds, 0, 2));
        Assert.Equal(Vector3.Zero, project.Models[0].Offset);
        // Centred in X already: the fixed point is the current offset.
        Assert.Equal(Vector3.Zero, ModelLayout.CenteredOffset(project, bounds, 0, 0));
        // Top aligned already: Max on Z holds.
        Assert.Equal(Vector3.Zero, ModelLayout.AlignedOffset(project, bounds, 0, 2, StockAlignment.Max));
    }

    [Fact]
    public void AlignedOffset_TwoCubesSideBySide_CentresTheSecondOnYFromAnyStart()
    {
        // Auto-fit stock: the union centre halves the shift each round (see ModelLayout), which needs
        // more than 16 rounds from a 7 mm start; the offset ends at 0 within the tolerance.
        var a = TestMeshes.Box(10, 10, 5);
        var b = TestMeshes.Box(10, 10, 5);
        var project = Project(new ModelPlacement { StlPath = "a.stl" }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(20, 7, 0) });
        var centred = ModelLayout.CenteredOffset(project, new[] { a.Bounds, b.Bounds }, 1, 1);
        Assert.Equal(0f, centred.Y, 3);
        Assert.Equal(20f, centred.X, 3);
    }

    [Fact]
    public void StockAlignment_RoundTripsThroughJson_AndLegacyFilesGetTheDefaults()
    {
        var project = MillingProject.Default();
        project.Stock.AlignZ = StockAlignment.Min;
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"AlignZ\": \"Min\"", json);
        Assert.Equal(StockAlignment.Min, ProjectSerializer.Deserialize(json).Stock.AlignZ);
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, "\\s*\"Align[XYZ]\": \"[A-Za-z]+\",", string.Empty);
        Assert.DoesNotContain("Align", legacy);
        var loaded = ProjectSerializer.Deserialize(legacy).Stock;
        Assert.Equal(StockAlignment.Center, loaded.AlignX);
        Assert.Equal(StockAlignment.Max, loaded.AlignZ);
    }
}
