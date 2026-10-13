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

    // T-189, T-190: Global zero is the global origin whatever CustomOffset holds; the explicit stock corner
    // is measured from it, so the model and the stock move on their own against the one 0.
    [Fact]
    public void Global_ZeroIsTheGlobalOrigin_WhileTheModelAndTheStockMoveOnTheirOwn()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        var project = Project(OriginMode.Global, new ModelPlacement { StlPath = "a.stl" });
        project.Axes.CustomOffset = new Vector3(5, -4, 2);
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = new Vector3(-10, -10, -20);
        var model = ModelLayout.MachineBounds(project, bounds);

        Assert.Equal(Vector3.Zero, ModelLayout.MachineZero(project, bounds));
        Assert.Equal(Vector3.Zero, Vector3.Transform(Vector3.Zero, ModelLayout.MachineMatrices(project, bounds)[0]));
        Assert.Equal(new Vector3(-10, -10, -20), ModelLayout.StockBoundsMachine(project, bounds).Min);

        project.Models[0].Offset = new Vector3(3, 1, -1);
        AssertClose(model.Min + new Vector3(3, 1, -1), ModelLayout.MachineBounds(project, bounds).Min);
        Assert.Equal(new Vector3(-10, -10, -20), ModelLayout.StockBoundsMachine(project, bounds).Min);

        project.Stock.ExplicitOrigin += new Vector3(2, -6, 4);
        AssertClose(model.Min + new Vector3(3, 1, -1), ModelLayout.MachineBounds(project, bounds).Min);
        Assert.Equal(new Vector3(-8, -16, -16), ModelLayout.StockBoundsMachine(project, bounds).Min);
    }

    // The user's turned, scaled and offset setup: the explicit stock stands at its corner from zero while
    // the axes turn the model about zero.
    [Fact]
    public void Global_ExplicitStockStandsAtItsCorner_WhateverTheAxesAndThePlacementDo()
    {
        var bounds = new[] { TestMeshes.Box(20, 4, 12).Bounds };
        var project = Turned(OriginMode.Global);
        Assert.Equal(project.Stock.ExplicitOrigin, ModelLayout.StockBoundsMachine(project, bounds).Min);
        var model = ModelLayout.MachineBounds(project, bounds);

        project.Axes.RotationZ = 0;
        project.Models[0].Scale = Vector3.One;
        Assert.Equal(project.Stock.ExplicitOrigin, ModelLayout.StockBoundsMachine(project, bounds).Min);
        Assert.NotEqual(model.Min, ModelLayout.MachineBounds(project, bounds).Min);
    }

    // T-190: an auto-fit stock stays around the models in Global (zero at the global origin).
    [Fact]
    public void Global_AutoFitStock_StaysAroundTheModels()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        var project = Project(OriginMode.Global, new ModelPlacement { StlPath = "a.stl", Offset = new Vector3(4, -2, 1) });
        project.Stock.ExplicitOrigin = new Vector3(7, 7, 7);
        var corner = AxisSetup.StockCorner(ModelLayout.AnchorBounds(project, bounds), project.Stock);
        Assert.Equal(corner, ModelLayout.StockBoundsMachine(project, bounds).Min);
        AssertClose(new Vector3(-45, -45, -25), corner);
    }

    // T-190: the align commands put the model at the explicit stock that stands at its corner from zero.
    [Fact]
    public void Global_AlignmentPutsTheModelAtTheExplicitStock()
    {
        var bounds = new[] { TestMeshes.Box(10, 10, 5).Bounds };
        var project = Project(OriginMode.Global, new ModelPlacement { StlPath = "a.stl" });
        project.Stock.Placement = StockPlacement.Explicit;
        project.Stock.ExplicitOrigin = new Vector3(-30, 20, -12);
        for (var axis = 0; axis < 3; axis++)
        {
            project.Models[0].Offset = ModelLayout.CenteredOffset(project, bounds, 0, axis);
        }

        AssertClose(ModelLayout.StockBoundsMachine(project, bounds).Center, ModelLayout.MachineBounds(project, bounds).Center);
        project.Models[0].Offset = ModelLayout.AlignedOffset(project, bounds, 0, 2, StockAlignment.Max);
        AssertClose(new Vector3(0, 0, -12 + 30), new Vector3(0, 0, ModelLayout.MachineBounds(project, bounds).Max.Z));
    }

    // T-191: changing machine zero changes the reference, not the setup: an explicit stock keeps its place
    // against the models into and out of Global; other changes and an auto-fit stock keep their values.
    [Fact]
    public void ChangingTheOriginMode_KeepsTheStockAgainstTheModels()
    {
        var bounds = new[] { TestMeshes.Box(20, 4, 12).Bounds };
        foreach (var from in StockModes.Concat(ModelModes))
        {
            var project = Turned(from);
            var inStock = ModelLayout.ModelBoundsInStock(project, bounds);
            var origin = project.Stock.ExplicitOrigin;

            ModelLayout.SetOriginMode(project, bounds, OriginMode.Global);
            Assert.Equal(OriginMode.Global, project.Axes.OriginMode);
            AssertClose(inStock.Min, ModelLayout.ModelBoundsInStock(project, bounds).Min);
            Assert.Equal(project.Stock.ExplicitOrigin, ModelLayout.StockBoundsMachine(project, bounds).Min);

            ModelLayout.SetOriginMode(project, bounds, from);
            AssertClose(inStock.Min, ModelLayout.ModelBoundsInStock(project, bounds).Min);
            AssertClose(origin, project.Stock.ExplicitOrigin);
        }

        var other = Turned(OriginMode.StockCenterTopZ);
        ModelLayout.SetOriginMode(other, bounds, OriginMode.ModelCustom);
        Assert.Equal(new Vector3(0, 0, -5.25f), other.Stock.ExplicitOrigin);

        var fitted = Turned(OriginMode.StockCornerMinXYMinZ);
        fitted.Stock.Placement = StockPlacement.AutoFitWithMargin;
        ModelLayout.SetOriginMode(fitted, bounds, OriginMode.Global);
        Assert.Equal(new Vector3(0, 0, -5.25f), fitted.Stock.ExplicitOrigin);

        var empty = Turned(OriginMode.StockCornerMinXYMinZ);
        ModelLayout.SetOriginMode(empty, null, OriginMode.Global);
        Assert.Equal(OriginMode.Global, empty.Axes.OriginMode);
        Assert.Equal(new Vector3(0, 0, -5.25f), empty.Stock.ExplicitOrigin);
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

    // T-189: with the stock corner at the global origin and the model where the stock corner mode has it,
    // Global gives that toolpath; moving the model and the stock together shifts it by the same vector.
    [Fact]
    public void Global_WithTheStockAtZero_GivesTheToolpathOfTheStockCorner_AndMovingBothShiftsIt()
    {
        var meshes = new[] { TestMeshes.Box(8, 8, 4) };
        var bounds = meshes.Select(m => m.Bounds).ToList();
        var corner = Small(OriginMode.StockCornerMinXYMinZ);
        var expected = new PipelineService().Run(corner, meshes, null, CancellationToken.None).Toolpath.Segments;
        Assert.Contains(expected, s => s.Kind == Miller.Core.Toolpaths.MoveKind.Feed);

        var global = Small(OriginMode.Global);
        global.Axes.CustomOffset = new Vector3(4, -2, 6);
        global.Stock.Placement = StockPlacement.Explicit;
        global.Stock.ExplicitOrigin = Vector3.Zero;
        global.Models[0].Offset = -ModelLayout.MachineZero(corner, bounds);
        Assert.Equal(expected, new PipelineService().Run(global, meshes, null, CancellationToken.None).Toolpath.Segments);

        var shift = new Vector3(3, -2, 1);
        global.Models[0].Offset += shift;
        global.Stock.ExplicitOrigin += shift;
        var shifted = new PipelineService().Run(global, meshes, null, CancellationToken.None).Toolpath.Segments;
        Assert.Equal(expected.Count, shifted.Count);
        for (var k = 0; k < expected.Count; k++)
        {
            Assert.Equal(expected[k].Kind, shifted[k].Kind);
            AssertClose(expected[k].Start + shift, shifted[k].Start);
            AssertClose(expected[k].End + shift, shifted[k].End);
        }
    }

    // T-192: the user's heart setup of the Build_1.0.123 screenshot (turned axes, scaled and offset model,
    // 60 x 30 x 5.25 explicit stock). There the corner 0 put the stock at the models' minimum
    // (-2.746, 0.698, 0.352), away from the axes; now it puts the stock at the axes, the one zero, and the
    // generation cuts the stock box of that same frame.
    [Fact]
    public void Global_OnTheUsersHeart_PutsTheStockCornerAtTheAxes_AndTheGenerationUsesTheSameZero()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var import = new MeshImportService();
        import.Import(Path.Combine(root, "Milling_Heart_V2.STL"));
        var bounds = import.Bounds;
        var project = Turned(OriginMode.StockCornerMinXYMinZ);
        project.Models[0].StlPath = "Milling_Heart_V2.STL";
        project.Axes.CustomOffset = Vector3.Zero;

        ModelLayout.SetOriginMode(project, bounds, OriginMode.Global);
        var model = ModelLayout.MachineBounds(project, bounds);
        AssertClose(new Vector3(-1.746f, 2.898f, -4.898f), model.Min);
        AssertClose(new Vector3(27.463f, 25.603f, 5.25f), model.Size);
        AssertClose(new Vector3(-2.746f, 0.698f, -4.898f), project.Stock.ExplicitOrigin);
        AssertClose(new Vector3(-2.746f, 0.698f, 0.352f), ModelLayout.AnchorBounds(project, bounds).Min);

        var stored = project.Stock.ExplicitOrigin;
        project.Stock.ExplicitOrigin = Vector3.Zero;
        Assert.Equal(Vector3.Zero, ModelLayout.MachineZero(project, bounds));
        Assert.Equal(Vector3.Zero, ModelLayout.StockBoundsMachine(project, bounds).Min);

        project.Stock.ExplicitOrigin = stored;
        project.Parameters.CellSize = 0.5f;
        var result = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
        Assert.Equal(stored, result.Stock.Bounds.Min);
        AssertClose(model.Min, result.MachineMesh.Bounds.Min);
        var reach = project.Tool.CutterDiameter / 2 + project.Parameters.CellSize;
        Assert.All(result.Toolpath.Segments, s =>
        {
            Assert.InRange(s.End.X, stored.X - reach, stored.X + 60 + reach);
            Assert.InRange(s.End.Y, stored.Y - reach, stored.Y + 30 + reach);
        });
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
