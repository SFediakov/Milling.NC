using System.Numerics;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Setup;

// Machine zero on the stock (as before), on the placed models, or at a fixed point of the global frame
// (T-179): the stock modes keep their bits, the model modes follow the models, Global stays while the
// models and the stock move against it, and every consumer reads one stock box.
public sealed class MachineZeroTests
{
    private static readonly OriginMode[] StockModes = { OriginMode.StockCornerMinXYMinZ, OriginMode.StockCornerMinXYTopZ, OriginMode.StockCenterTopZ, OriginMode.Custom };

    private static readonly OriginMode[] ModelModes = { OriginMode.ModelCornerMinXYMinZ, OriginMode.ModelCornerMinXYTopZ, OriginMode.ModelCenterTopZ, OriginMode.ModelCustom };

    private static MillingProject Project(OriginMode mode, params ModelPlacement[] placements)
    {
        var project = MillingProject.Default();
        project.Axes.OriginMode = mode;
        project.Axes.CustomOffset = new Vector3(1, 2, 3);
        project.Models.AddRange(placements);
        return project;
    }

    // The user's live setup in miniature: turned axes, a scaled and offset model, an explicit stock.
    private static MillingProject Turned(OriginMode mode)
    {
        var project = Project(mode, new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(1, 2.2f, -5.25f), Scale = new Vector3(1.25f, 1.25f, 1.05f) });
        project.Axes.RotationX = 90;
        project.Axes.RotationZ = 90;
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = new Vector3(0, 0, -5.25f);
        project.Stock.SizeX = 60;
        project.Stock.SizeY = 30;
        project.Stock.SizeZ = 5.25f;
        return project;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    [Fact]
    public void StockModes_KeepTheMatricesAndTheStockBoxOfTheFormulaBefore()
    {
        var bounds = new[] { TestMeshes.Box(20, 4, 12).Bounds };
        foreach (var mode in StockModes)
        {
            foreach (var project in new[] { Turned(mode), Project(mode, new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(3, -1, 0) }) })
            {
                var anchor = ModelLayout.AnchorBounds(project, bounds);
                var zero = AxisSetup.StockCorner(anchor, project.Stock) + project.Axes.OriginOffset(project.Stock);
                var expected = ModelLayout.PlacementMatrix(project.Axes, project.Models[0], bounds[0]) * Matrix4x4.CreateTranslation(-zero);
                Assert.Equal(expected, ModelLayout.MachineMatrices(project, bounds)[0]);

                var corner = AxisSetup.StockCorner(new BoundingBox(anchor.Min - zero, anchor.Max - zero), project.Stock);
                Assert.Equal(corner, ModelLayout.StockBoundsMachine(project, bounds).Min);
                AssertClose(ModelLayout.StockBoundsMachine(project).Min, corner);
            }
        }
    }

    [Fact]
    public void ModelModes_PutTheNamedPointOfThePlacedModelsAtZero()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds, TestMeshes.Box(6, 8, 3).Bounds };
        foreach (var mode in ModelModes)
        {
            var project = Project(mode, new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(-12, 4, 0) }, new ModelPlacement { StlPath = "b.stl", Offset = new Vector3(15, -3, 1) });
            var machine = ModelLayout.MachineBounds(project, bounds);
            var point = mode switch
            {
                OriginMode.ModelCornerMinXYMinZ => machine.Min,
                OriginMode.ModelCornerMinXYTopZ => new Vector3(machine.Min.X, machine.Min.Y, machine.Max.Z),
                OriginMode.ModelCenterTopZ => new Vector3(machine.Center.X, machine.Center.Y, machine.Max.Z),
                _ => machine.Min + project.Axes.CustomOffset,
            };
            AssertClose(Vector3.Zero, point);
        }
    }

    [Fact]
    public void ModelModes_ZeroFollowsTheModel_AndTheStockMovesAgainstIt()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        var project = Project(OriginMode.ModelCenterTopZ, new ModelPlacement { StlPath = "a.stl" });
        var model = ModelLayout.MachineBounds(project, bounds);
        var stock = ModelLayout.StockBoundsMachine(project, bounds);

        project.Models[0].Offset = new Vector3(7, -3, 2);
        AssertClose(model.Min, ModelLayout.MachineBounds(project, bounds).Min);
        AssertClose(stock.Min - new Vector3(7, -3, 2), ModelLayout.StockBoundsMachine(project, bounds).Min);
        Assert.Throws<InvalidOperationException>(() => ModelLayout.StockBoundsMachine(project));
    }

    [Fact]
    public void Global_ZeroStays_WhileTheModelAndTheStockMoveOnTheirOwn()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        var project = Project(OriginMode.Global, new ModelPlacement { StlPath = "a.stl" });
        project.Axes.CustomOffset = new Vector3(5, -4, 2);
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = new Vector3(-10, -10, -20);
        var model = ModelLayout.MachineBounds(project, bounds);
        var stock = ModelLayout.StockBoundsMachine(project, bounds);

        // The global origin, the STL's own origin, lands at minus the point.
        AssertClose(new Vector3(-5, 4, -2), Vector3.Transform(Vector3.Zero, ModelLayout.MachineMatrices(project, bounds)[0]));

        project.Models[0].Offset = new Vector3(3, 1, -1);
        AssertClose(model.Min + new Vector3(3, 1, -1), ModelLayout.MachineBounds(project, bounds).Min);
        AssertClose(stock.Min, ModelLayout.StockBoundsMachine(project, bounds).Min);

        project.Stock.ExplicitOrigin += new Vector3(2, -6, 4);
        AssertClose(model.Min + new Vector3(3, 1, -1), ModelLayout.MachineBounds(project, bounds).Min);
        AssertClose(stock.Min + new Vector3(2, -6, 4), ModelLayout.StockBoundsMachine(project, bounds).Min);
    }

    [Fact]
    public void Global_IsAPointOfTheModelCoordinatesAfterTheAxisMapping()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        var project = Project(OriginMode.Global, new ModelPlacement { StlPath = "a.stl" });
        project.Axes.RotationX = 90;
        project.Axes.CustomOffset = new Vector3(0, 0, 0);
        var matrix = ModelLayout.MachineMatrices(project, bounds)[0];
        AssertClose(Vector3.Zero, Vector3.Transform(Vector3.Zero, matrix));
        AssertClose(Vector3.Transform(new Vector3(0, 10, 0), project.Axes.ToOrientationMatrix()), Vector3.Transform(new Vector3(0, 10, 0), matrix));
    }

    [Fact]
    public void Containment_DoesNotDependOnWhereZeroIs()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        foreach (var mode in Enum.GetValues<OriginMode>())
        {
            var project = Project(mode, new ModelPlacement { StlPath = "a.stl" });
            var inside = ModelLayout.ModelBoundsInStock(project, bounds);
            AssertClose(new Vector3(45, 45, 25), inside.Min);
            Assert.DoesNotContain(ProjectValidator.Validate(project, inside).Warnings, w => w.Field == "Stock.Placement");

            project.Models[0].Offset = new Vector3(50, 0, 0);
            var outside = ModelLayout.ModelBoundsInStock(project, bounds);
            Assert.Contains(ProjectValidator.Validate(project, outside).Warnings, w => w.Field == "Stock.Placement");
        }
    }

    private static MillingProject Small(OriginMode mode)
    {
        var project = Project(mode, new ModelPlacement { StlPath = "box.stl" });
        project.Stock.SizeX = 20;
        project.Stock.SizeY = 20;
        project.Stock.SizeZ = 8;
        project.Parameters.CellSize = 0.5f;
        return project;
    }

    [Fact]
    public void Pipeline_CutsTheStockBoxOfTheLayout_InEveryMode()
    {
        var meshes = new[] { TestMeshes.Box(8, 8, 4) };
        var bounds = meshes.Select(m => m.Bounds).ToList();
        foreach (var mode in new[] { OriginMode.ModelCenterTopZ, OriginMode.Global })
        {
            var project = Small(mode);
            project.Axes.CustomOffset = new Vector3(4, -2, 6);
            var result = new PipelineService().Run(project, meshes, null, CancellationToken.None);
            Assert.Equal(ModelLayout.StockBoundsMachine(project, bounds), result.Stock.Bounds);
            AssertClose(ModelLayout.MachineBounds(project, bounds).Min, result.MachineMesh.Bounds.Min);
        }
    }

    [Fact]
    public void Global_AtTheStockCorner_GivesTheToolpathOfTheStockCorner_AndAShiftedPointShiftsIt()
    {
        var meshes = new[] { TestMeshes.Box(8, 8, 4) };
        var bounds = meshes.Select(m => m.Bounds).ToList();
        var corner = Small(OriginMode.StockCornerMinXYMinZ);
        var expected = new PipelineService().Run(corner, meshes, null, CancellationToken.None).Toolpath.Segments;
        Assert.Contains(expected, s => s.Kind == Miller.Core.Toolpaths.MoveKind.Feed);

        var global = Small(OriginMode.Global);
        global.Axes.CustomOffset = ModelLayout.MachineZero(corner, bounds);
        Assert.Equal(expected, new PipelineService().Run(global, meshes, null, CancellationToken.None).Toolpath.Segments);

        var shift = new Vector3(3, -2, 1);
        global.Axes.CustomOffset += shift;
        var shifted = new PipelineService().Run(global, meshes, null, CancellationToken.None).Toolpath.Segments;
        Assert.Equal(expected.Count, shifted.Count);
        for (var k = 0; k < expected.Count; k++)
        {
            Assert.Equal(expected[k].Kind, shifted[k].Kind);
            AssertClose(expected[k].Start - shift, shifted[k].Start);
            AssertClose(expected[k].End - shift, shifted[k].End);
        }
    }

    [Theory]
    [InlineData(OriginMode.ModelCornerMinXYMinZ)]
    [InlineData(OriginMode.ModelCornerMinXYTopZ)]
    [InlineData(OriginMode.ModelCenterTopZ)]
    [InlineData(OriginMode.ModelCustom)]
    [InlineData(OriginMode.Global)]
    public void NewModes_RoundTripByName(OriginMode mode)
    {
        var project = Project(mode);
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains($"\"OriginMode\": \"{mode}\"", json, StringComparison.Ordinal);
        var copy = ProjectSerializer.Deserialize(json);
        Assert.Equal(mode, copy.Axes.OriginMode);
        Assert.Equal(project.Axes.CustomOffset, copy.Axes.CustomOffset);
    }
}
