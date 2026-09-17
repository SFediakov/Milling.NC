using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Xunit;

namespace Miller.Tests.Core.Simulation;

public sealed class StockModelTests
{
    private static readonly BoundingBox Model = new(new Vector3(-10, -5, -20), new Vector3(10, 5, 0));

    [Fact]
    public void Box_FillsEveryCellAtTheTop()
    {
        var stock = new StockDefinition { Shape = StockShape.Box, SizeX = 100, SizeY = 60, SizeZ = 30 };
        var geometry = StockModel.Create(stock, Model, 1f);
        Assert.Equal((100, 60), (geometry.Map.Width, geometry.Map.Height));
        Assert.Equal(6000, geometry.Map.MaterialCellCount());
        Assert.All(geometry.Map.Z, z => Assert.Equal(geometry.StockTop, z));
        Assert.Equal(30f, geometry.StockTop - geometry.StockBottom, 4);
        Assert.Equal(new Vector3(100, 60, 30), geometry.Size);
    }

    [Fact]
    public void AutoFit_CentersTheStockOnTheModelWithTheTopAtTheModelTop()
    {
        var stock = new StockDefinition { Shape = StockShape.Box, SizeX = 40, SizeY = 30, SizeZ = 25, Placement = StockPlacement.AutoFitWithMargin };
        var geometry = StockModel.Create(stock, Model, 0.5f);
        Assert.True(geometry.Bounds.Contains(Model));
        Assert.Equal(Model.Max.Z, geometry.StockTop, 4);
        Assert.Equal(Model.Center.X, geometry.Bounds.Center.X, 4);
        Assert.Equal(Model.Center.Y, geometry.Bounds.Center.Y, 4);
        Assert.Equal(new Vector3(-20, -15, -25), geometry.Corner);
    }

    [Fact]
    public void Explicit_PlacesTheCornerRelativeToTheModelMinimum()
    {
        var stock = new StockDefinition
        {
            Shape = StockShape.Box, SizeX = 40, SizeY = 30, SizeZ = 25,
            Placement = StockPlacement.Explicit, ExplicitOrigin = new Vector3(-3, -4, -5),
        };
        var geometry = StockModel.Create(stock, Model, 0.5f);
        Assert.Equal(Model.Min + new Vector3(-3, -4, -5), geometry.Corner);
        Assert.Equal(geometry.Corner.Z + 25, geometry.StockTop, 4);
    }

    [Fact]
    public void Cylinder_MasksCellsOutsideTheCircle()
    {
        var stock = new StockDefinition { Shape = StockShape.Cylinder, Diameter = 100, Height = 30 };
        var geometry = StockModel.Create(stock, Model, 0.25f);
        Assert.Equal((400, 400), (geometry.Map.Width, geometry.Map.Height));
        var nanFraction = 1.0 - (double)geometry.Map.MaterialCellCount() / geometry.Map.CellCount;
        Assert.InRange(nanFraction, 1 - Math.PI / 4 - 0.005, 1 - Math.PI / 4 + 0.005);

        var centerCell = geometry.Map.CellOf(geometry.Bounds.Center.X, geometry.Bounds.Center.Y);
        Assert.Equal(geometry.StockTop, geometry.Map[centerCell.I, centerCell.J]);
        Assert.True(float.IsNaN(geometry.Map[0, 0]));
        Assert.True(float.IsNaN(geometry.Map[399, 399]));
        Assert.Equal(geometry.StockTop, geometry.Map.Max());
    }

    [Fact]
    public void BadInput_IsRejected()
    {
        var stock = new StockDefinition { SizeZ = 0 };
        Assert.Throws<ArgumentException>(() => StockModel.Create(stock, Model, 1f));
        Assert.Throws<ArgumentException>(() => StockModel.Create(StockDefinition.Default(), BoundingBox.Empty, 1f));
    }
}
