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
            RapidRate = 4000,
            SpindleRpm = 18000,
            Stepover = 1.5f,
            Stepdown = 1,
            SafeHeight = 8,
            CellSize = 0.1f,
            Tolerance = 0.02f,
        },
        RoutingStrategyId = "z-layer-by-layer",
        PostProcessorId = "grbl",
        CutScope = CutScope.Separation,
        MinIslandVolume = 120.5f,
        CollisionMode = CollisionMode.Backwards_GPU,
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
        Assert.Null(project.Parameters.PlungeRate);
        Assert.Equal(3000f, project.Parameters.RapidRate);
        Assert.Equal(12000f, project.Parameters.SpindleRpm);
        Assert.Equal(3f, project.Parameters.Stepover);
        Assert.Null(project.Parameters.FinishingStepover);
        Assert.Equal(2f, project.Parameters.Stepdown);
        Assert.Equal(5f, project.Parameters.SafeHeight);
        Assert.Equal(0.2f, project.Parameters.CellSize);
        Assert.Equal(0.05f, project.Parameters.Tolerance);
        Assert.Null(project.Parameters.Direction);
        Assert.Equal(CollisionMode.Backwards, project.CollisionMode);
        Assert.Null(project.RecursionRatio);
        Assert.Null(project.OneRunRatio);
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
        Assert.Equal(CollisionMode.Backwards_GPU, copy.CollisionMode);
        Assert.Contains("\"CollisionMode\": \"Backwards_GPU\"", json);
        Assert.DoesNotContain("RecursionRatio", json);
        Assert.DoesNotContain("OneRunRatio", json);
        Assert.DoesNotContain("FinishingStepover", json);

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
        Assert.DoesNotContain("PlungeRate", json);
        Assert.Equal(original.Parameters.RapidRate, copy.Parameters.RapidRate);
        Assert.Equal(original.Parameters.SpindleRpm, copy.Parameters.SpindleRpm);
        Assert.Equal(original.Parameters.Stepover, copy.Parameters.Stepover);
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
        Assert.Contains("\"RoutingStrategyId\": \"z-layer-by-layer\"", json);
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

    // A file written before the collision modes (T-151) loads Backwards, the default since T-214.
    [Fact]
    public void Deserialize_WithoutCollisionFields_LoadsTheDefaults()
    {
        var project = ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CutScope\": \"Separation\" }");
        Assert.Equal(CollisionMode.Backwards, project.CollisionMode);
        Assert.Null(project.RecursionRatio);
        Assert.Null(project.OneRunRatio);
    }

    // T-214: a file naming the removed strategy or collision modes loads with Z layer by layer and Backwards,
    // and their settings are read and dropped, so the file is written back without them.
    [Theory]
    [InlineData("three-axis-freedom", "Recursion")]
    [InlineData("three-axis-precise", "OneRun")]
    [InlineData("z-layer-by-layer", "recursion")]
    public void Deserialize_RemovedStrategyAndModes_LoadAsZLayerAndBackwards(string strategy, string mode)
    {
        var json = "{ \"SchemaVersion\": 3, \"RoutingStrategyId\": \"" + strategy + "\", \"CollisionMode\": \"" + mode + "\", \"RecursionRatio\": 2, " +
            "\"OneRunRatio\": 5, \"Parameters\": { \"Stepover\": 0.75, \"FinishingStepover\": 0.75 } }";
        var project = ProjectSerializer.Deserialize(json);
        Assert.Equal("z-layer-by-layer", project.RoutingStrategyId);
        Assert.Equal(CollisionMode.Backwards, project.CollisionMode);
        Assert.Null(project.RecursionRatio);
        Assert.Null(project.OneRunRatio);
        Assert.Null(project.Parameters.FinishingStepover);
        Assert.Equal(0.75f, project.Parameters.Stepover);
        var written = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("three-axis", written);
        Assert.DoesNotContain("Recursion", written);
        Assert.DoesNotContain("OneRun", written);
        Assert.DoesNotContain("FinishingStepover", written);
        Assert.Contains("\"CollisionMode\": \"Backwards\"", written);
    }

    // The two modes left read by name (any case); an unknown name or a number is an error, not a default.
    [Fact]
    public void Deserialize_CollisionMode_ReadsTheTwoNames_AndRefusesOthers()
    {
        Assert.Equal(CollisionMode.Backwards_GPU, ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CollisionMode\": \"backwards_gpu\" }").CollisionMode);
        Assert.Equal(CollisionMode.Backwards, ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CollisionMode\": \"Backwards\" }").CollisionMode);
        Assert.Throws<JsonException>(() => ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CollisionMode\": \"Sideways\" }"));
        Assert.Throws<JsonException>(() => ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CollisionMode\": \"1\" }"));
        Assert.Throws<JsonException>(() => ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CollisionMode\": 1 }"));
    }

    // The user's project as Build_1.0.131 wrote it (Mechanical/Lu/project.miller_V2_smaller.json, settings
    // only) loads unchanged apart from the dropped fields of the removed modes and strategy.
    [Fact]
    public void Deserialize_TheUsersBuild131Project_KeepsItsSettings()
    {
        const string json = """
            {
              "SchemaVersion": 3,
              "Models": [ { "StlPath": "Milling_Heart_V3.STL", "Name": "", "Offset": { "X": 1, "Y": 2.2, "Z": -5.25 }, "RotationZ": 0, "Scale": { "X": 1, "Y": 1, "Z": 1.05 }, "LinkedScale": false } ],
              "Tool": { "Name": "0.8 mm flat end mill", "CutterDiameter": 0.9, "CutterLength": 2, "HeadDiameter": 0.89, "HeadShape": "Frustum", "HeadTopDiameter": 4.02, "HeadLength": 8, "TipType": "Flat", "CutterRadius": 0.45, "HeadRadius": 2.01 },
              "Stock": { "Shape": "Box", "SizeX": 60, "SizeY": 30, "SizeZ": 5.25, "Diameter": 100, "Height": 30, "Placement": "Explicit", "AlignX": "Center", "AlignY": "Center", "AlignZ": "Max", "Margin": 1, "ExplicitOrigin": { "X": -1, "Y": 0, "Z": -5.25 } },
              "Axes": { "MapX": "X", "MapY": "Y", "MapZ": "Z", "FlipX": false, "FlipY": false, "FlipZ": false, "RotationX": 90, "RotationY": 0, "RotationZ": 90, "OriginMode": "Custom", "CustomOffset": { "X": 1, "Y": 0, "Z": 0 }, "IsPermutation": true, "IsOnStock": true, "IsOnModels": false, "IsGlobal": false },
              "Parameters": { "FeedRate": 150, "PlungeRate": 10, "RapidRate": 300, "SpindleRpm": 12000, "Stepover": 0.75, "FinishingStepover": 0.75, "Stepdown": 0.05, "FarStepdown": 0.05, "SafeHeight": 2, "CellSize": 0.025, "Tolerance": 0.05 },
              "RoutingStrategyId": "z-layer-by-layer",
              "PostProcessorId": "grbl",
              "CutScope": "Separation",
              "MinIslandVolume": 100,
              "BridgeCount": 4,
              "BridgeWidth": 0.3,
              "BridgeHeight": 0.6,
              "ReachPercent": 85,
              "CollisionMode": "Backwards_GPU",
              "RecursionRatio": 2,
              "OneRunRatio": 5
            }
            """;
        var project = ProjectSerializer.Deserialize(json);
        Assert.Equal(CollisionMode.Backwards_GPU, project.CollisionMode);
        Assert.Equal("z-layer-by-layer", project.RoutingStrategyId);
        Assert.Equal(150f, project.Parameters.FeedRate);
        Assert.Equal(0.75f, project.Parameters.Stepover);
        Assert.Equal(0.05f, project.Parameters.FarStepdown);
        Assert.Equal(0.025f, project.Parameters.CellSize);
        Assert.Equal(CutScope.Separation, project.CutScope);
        Assert.Equal(85f, project.ReachPercent);
        Assert.Equal(0.6f, project.BridgeHeight);
        Assert.Null(project.Parameters.PlungeRate);
        var written = ProjectSerializer.Serialize(project);
        Assert.DoesNotContain("RecursionRatio", written);
        Assert.DoesNotContain("OneRunRatio", written);
        Assert.DoesNotContain("FinishingStepover", written);
        Assert.DoesNotContain("PlungeRate", written);
    }

    // A file written before the holding bridges loads the defaults; set values round-trip.
    [Fact]
    public void Bridges_LoadTheDefaultsWhenMissing_AndRoundTrip()
    {
        var old = ProjectSerializer.Deserialize("{ \"SchemaVersion\": 3, \"CutScope\": \"Separation\" }");
        Assert.Equal(MillingProject.DefaultBridgeCount, old.BridgeCount);
        Assert.Equal(MillingProject.DefaultBridgeWidth, old.BridgeWidth);
        Assert.Equal(MillingProject.DefaultBridgeHeight, old.BridgeHeight);

        var original = FullyCustomized();
        original.BridgeCount = 6;
        original.BridgeWidth = 0.8f;
        original.BridgeHeight = 0.45f;
        var copy = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));
        Assert.Equal(6f, copy.BridgeCount);
        Assert.Equal(0.8f, copy.BridgeWidth);
        Assert.Equal(0.45f, copy.BridgeHeight);
    }

    // T-169: the third collision mode is written by name and read back.
    [Fact]
    public void Backwards_RoundTripsByName()
    {
        var original = MillingProject.Default();
        original.CollisionMode = CollisionMode.Backwards;
        var json = ProjectSerializer.Serialize(original);
        Assert.Contains("\"CollisionMode\": \"Backwards\"", json);
        Assert.Equal(CollisionMode.Backwards, ProjectSerializer.Deserialize(json).CollisionMode);
    }

    [Fact]
    public void RoundTrip_KeepsTheScaleAndTheLink()
    {
        var original = FullyCustomized();
        original.Models[0].Scale = new Vector3(1.25f, 0.5f, 3f);
        original.Models[0].LinkedScale = false;
        var model = Assert.Single(ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original)).Models);
        Assert.Equal(new Vector3(1.25f, 0.5f, 3f), model.Scale);
        Assert.False(model.LinkedScale);
    }

    // A file written before the scale existed loads every model at factor one, linked.
    [Fact]
    public void Deserialize_ModelWithoutScale_LoadsFactorOneLinked()
    {
        var project = ProjectSerializer.Deserialize(
            "{ \"SchemaVersion\": 3, \"Models\": [ { \"StlPath\": \"a.stl\", \"Offset\": { \"X\": 1, \"Y\": 2, \"Z\": 3 }, \"RotationZ\": 15 } ] }");
        var model = Assert.Single(project.Models);
        Assert.Equal(Vector3.One, model.Scale);
        Assert.True(model.LinkedScale);
        Assert.Equal(new Vector3(1, 2, 3), model.Offset);
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
