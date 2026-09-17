using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Miller.Core.Setup;
using Xunit;

namespace Miller.Tests.Core.Setup;

public sealed class ProjectSerializerTests
{
    private static MillingProject FullyCustomized() => new()
    {
        Models = { new ModelPlacement { StlPath = "../Milling_Heart_V2.STL", Name = "heart", Offset = new Vector3(1, 2, 3), RotationZ = 15 } },
        Tool = new ToolDefinition
        {
            Name = "3 mm ball",
            CutterDiameter = 3,
            CutterLength = 12.5f,
            HeadDiameter = 8,
            TipType = TipType.Ball,
        },
        Stock = new StockDefinition
        {
            Shape = StockShape.Cylinder,
            SizeX = 50,
            SizeY = 40,
            SizeZ = 25,
            Diameter = 60,
            Height = 22,
            Placement = StockPlacement.Explicit,
            Margin = 2.5f,
            ExplicitOrigin = new Vector3(-1, -2, -3),
        },
        Axes = new AxisSetup
        {
            MapX = ModelAxis.Y,
            MapY = ModelAxis.Z,
            MapZ = ModelAxis.X,
            FlipX = true,
            FlipY = false,
            FlipZ = true,
            RotationX = 10,
            RotationY = -20,
            RotationZ = 90,
            OriginMode = OriginMode.Custom,
            CustomOffset = new Vector3(1.5f, 2.5f, 3.5f),
        },
        Parameters = new CuttingParameters
        {
            FeedRate = 1200,
            PlungeRate = 300,
            RapidRate = 4000,
            SpindleRpm = 18000,
            Stepover = 1.5f,
            FinishingStepover = 0.25f,
            Stepdown = 1,
            SafeHeight = 8,
            CellSize = 0.1f,
            Tolerance = 0.02f,
        },
        RoutingStrategyId = "three-axis-freedom",
        PostProcessorId = "grbl",
        CutScope = CutScope.Separation,
        MinIslandVolume = 120.5f,
    };

    [Fact]
    public void Default_HasTheT017DefaultsAndIds()
    {
        var project = MillingProject.Default();

        Assert.Equal(3, MillingProject.CurrentSchemaVersion);
        Assert.Equal(MillingProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Empty(project.Models);
        Assert.Null(project.StlPath);
        Assert.Equal(MillingProject.DefaultRoutingStrategyId, project.RoutingStrategyId);
        Assert.Equal("z-layer-by-layer", project.RoutingStrategyId);
        Assert.Null(project.RoughingStrategyId);
        Assert.Null(project.FinishingStrategyId);
        Assert.Equal("grbl", project.PostProcessorId);

        Assert.Equal(6f, project.Tool.CutterDiameter);
        Assert.Equal(20f, project.Tool.CutterLength);
        Assert.Equal(10f, project.Tool.HeadDiameter);
        Assert.Equal(TipType.Flat, project.Tool.TipType);

        Assert.Equal(StockShape.Box, project.Stock.Shape);
        Assert.Equal(100f, project.Stock.SizeX);
        Assert.Equal(100f, project.Stock.SizeY);
        Assert.Equal(30f, project.Stock.SizeZ);
        Assert.Equal(StockPlacement.AutoFitWithMargin, project.Stock.Placement);
        Assert.Equal(5f, project.Stock.Margin);

        Assert.Equal(800f, project.Parameters.FeedRate);
        Assert.Equal(200f, project.Parameters.PlungeRate);
        Assert.Equal(3000f, project.Parameters.RapidRate);
        Assert.Equal(12000f, project.Parameters.SpindleRpm);
        Assert.Equal(3f, project.Parameters.Stepover);
        Assert.Equal(0.5f, project.Parameters.FinishingStepover);
        Assert.Equal(2f, project.Parameters.Stepdown);
        Assert.Equal(5f, project.Parameters.SafeHeight);
        Assert.Equal(0.2f, project.Parameters.CellSize);
        Assert.Equal(0.05f, project.Parameters.Tolerance);
        Assert.Null(project.Parameters.Direction);
    }

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = FullyCustomized();
        var json = ProjectSerializer.Serialize(original);
        var copy = ProjectSerializer.Deserialize(json);

        Assert.Equal(original.SchemaVersion, copy.SchemaVersion);
        var model = Assert.Single(copy.Models);
        Assert.Equal("../Milling_Heart_V2.STL", model.StlPath);
        Assert.Equal("heart", model.Name);
        Assert.Equal(new Vector3(1, 2, 3), model.Offset);
        Assert.Equal(15f, model.RotationZ);
        Assert.DoesNotContain("\"StlPath\": null", json);
        Assert.DoesNotContain("RoughingStrategyId", json);
        Assert.DoesNotContain("FinishingStrategyId", json);
        Assert.DoesNotContain("Direction", json);
        Assert.Equal(original.RoutingStrategyId, copy.RoutingStrategyId);
        Assert.Equal(original.PostProcessorId, copy.PostProcessorId);
        Assert.Equal(CutScope.Separation, copy.CutScope);
        Assert.Equal(120.5f, copy.MinIslandVolume);

        Assert.Equal(original.Tool.Name, copy.Tool.Name);
        Assert.Equal(original.Tool.CutterDiameter, copy.Tool.CutterDiameter);
        Assert.Equal(original.Tool.CutterLength, copy.Tool.CutterLength);
        Assert.Equal(original.Tool.HeadDiameter, copy.Tool.HeadDiameter);
        Assert.Equal(original.Tool.TipType, copy.Tool.TipType);

        Assert.Equal(original.Stock.Shape, copy.Stock.Shape);
        Assert.Equal(original.Stock.SizeX, copy.Stock.SizeX);
        Assert.Equal(original.Stock.SizeY, copy.Stock.SizeY);
        Assert.Equal(original.Stock.SizeZ, copy.Stock.SizeZ);
        Assert.Equal(original.Stock.Diameter, copy.Stock.Diameter);
        Assert.Equal(original.Stock.Height, copy.Stock.Height);
        Assert.Equal(original.Stock.Placement, copy.Stock.Placement);
        Assert.Equal(original.Stock.Margin, copy.Stock.Margin);
        Assert.Equal(original.Stock.ExplicitOrigin, copy.Stock.ExplicitOrigin);

        Assert.Equal(original.Axes.MapX, copy.Axes.MapX);
        Assert.Equal(original.Axes.MapY, copy.Axes.MapY);
        Assert.Equal(original.Axes.MapZ, copy.Axes.MapZ);
        Assert.Equal(original.Axes.FlipX, copy.Axes.FlipX);
        Assert.Equal(original.Axes.FlipY, copy.Axes.FlipY);
        Assert.Equal(original.Axes.FlipZ, copy.Axes.FlipZ);
        Assert.Equal(original.Axes.RotationX, copy.Axes.RotationX);
        Assert.Equal(original.Axes.RotationY, copy.Axes.RotationY);
        Assert.Equal(original.Axes.RotationZ, copy.Axes.RotationZ);
        Assert.Equal(original.Axes.OriginMode, copy.Axes.OriginMode);
        Assert.Equal(original.Axes.CustomOffset, copy.Axes.CustomOffset);

        Assert.Equal(original.Parameters.FeedRate, copy.Parameters.FeedRate);
        Assert.Equal(original.Parameters.PlungeRate, copy.Parameters.PlungeRate);
        Assert.Equal(original.Parameters.RapidRate, copy.Parameters.RapidRate);
        Assert.Equal(original.Parameters.SpindleRpm, copy.Parameters.SpindleRpm);
        Assert.Equal(original.Parameters.Stepover, copy.Parameters.Stepover);
        Assert.Equal(original.Parameters.FinishingStepover, copy.Parameters.FinishingStepover);
        Assert.Equal(original.Parameters.Stepdown, copy.Parameters.Stepdown);
        Assert.Equal(original.Parameters.SafeHeight, copy.Parameters.SafeHeight);
        Assert.Equal(original.Parameters.CellSize, copy.Parameters.CellSize);
        Assert.Equal(original.Parameters.Tolerance, copy.Parameters.Tolerance);

        Assert.Equal(json, ProjectSerializer.Serialize(copy));
    }

    [Fact]
    public void Serialize_WritesEnumsAsStringsAndIndents()
    {
        var json = ProjectSerializer.Serialize(FullyCustomized());

        Assert.Contains("\"TipType\": \"Ball\"", json);
        Assert.Contains("\"Shape\": \"Cylinder\"", json);
        Assert.Contains("\"Placement\": \"Explicit\"", json);
        Assert.Contains("\"MapX\": \"Y\"", json);
        Assert.Contains("\"OriginMode\": \"Custom\"", json);
        Assert.Contains("\"RoutingStrategyId\": \"three-axis-freedom\"", json);
        Assert.Contains("\"SchemaVersion\": 3", json);
        Assert.Contains("\n", json);
        Assert.DoesNotContain("\"TipType\": 1", json);
    }

    [Fact]
    public void Serialize_UsesDotDecimalSeparatorUnderCommaCulture()
    {
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = comma;
        try
        {
            var json = ProjectSerializer.Serialize(FullyCustomized());
            Assert.Contains("\"CutterLength\": 12.5", json);
            Assert.Equal(12.5f, ProjectSerializer.Deserialize(json).Tool.CutterLength);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Deserialize_UnknownSchemaVersion_Throws()
    {
        var json = ProjectSerializer.Serialize(MillingProject.Default()).Replace("\"SchemaVersion\": 3", "\"SchemaVersion\": 7");
        var ex = Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(json));
        Assert.Contains("7", ex.Message);
    }

    [Fact]
    public void Deserialize_NullOrUnknownMember_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("null"));
        var json = ProjectSerializer.Serialize(MillingProject.Default()).Replace("\"Tool\"", "\"Tools\"");
        Assert.Throws<JsonException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_MissingSubObjects_UseDefaults()
    {
        var project = ProjectSerializer.Deserialize("{ \"SchemaVersion\": 1 }");
        Assert.Equal(6f, project.Tool.CutterDiameter);
        Assert.Equal(MillingProject.DefaultRoutingStrategyId, project.RoutingStrategyId);
    }

    [Fact]
    public void Deserialize_SchemaOne_MigratesTheStlPathIntoTheModelList()
    {
        var project = ProjectSerializer.Deserialize("{ \"SchemaVersion\": 1, \"StlPath\": \"parts/heart.stl\" }");
        Assert.Equal(MillingProject.CurrentSchemaVersion, project.SchemaVersion);
        var model = Assert.Single(project.Models);
        Assert.Equal("parts/heart.stl", model.StlPath);
        Assert.Equal(Vector3.Zero, model.Offset);
        Assert.Null(project.StlPath);
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("{ \"SchemaVersion\": 4 }"));
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize("{ \"SchemaVersion\": 0 }"));
    }

    // A schema 2 file names a roughing and a finishing strategy and a milling direction; all three
    // are dropped, the routing strategy is the default and nothing legacy is written back.
    [Fact]
    public void Deserialize_SchemaTwo_DropsTheStrategyPairAndTheDirection()
    {
        const string json = "{ \"SchemaVersion\": 2, \"RoughingStrategyId\": \"layer-complete\", \"FinishingStrategyId\": \"raster-finishing\", " +
            "\"Parameters\": { \"Stepover\": 0.9, \"Direction\": \"OneWay\" }, \"CutScope\": \"Separation\" }";
        var project = ProjectSerializer.Deserialize(json);
        Assert.Equal(MillingProject.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal(MillingProject.DefaultRoutingStrategyId, project.RoutingStrategyId);
        Assert.Null(project.RoughingStrategyId);
        Assert.Null(project.FinishingStrategyId);
        Assert.Null(project.Parameters.Direction);
        Assert.Equal(0.9f, project.Parameters.Stepover);
        Assert.Equal(CutScope.Separation, project.CutScope);
        Assert.Equal(0f, project.MinIslandVolume);
        var written = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("RoughingStrategyId", written);
        Assert.DoesNotContain("FinishingStrategyId", written);
        Assert.DoesNotContain("Direction", written);
        Assert.Contains("\"SchemaVersion\": 3", written);
    }
}
