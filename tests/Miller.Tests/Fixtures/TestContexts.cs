using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Slicing;
using Miller.Core.Toolpaths;

namespace Miller.Tests.Fixtures;

// Builds a ToolpathContext the way the pipeline does, for strategy tests.
public static class TestContexts
{
    public static ToolDefinition FlatTool6() => new() { TipType = TipType.Flat, CutterDiameter = 6f, HeadDiameter = 10f, CutterLength = 20f };

    public static ToolDefinition BallTool6() => new() { TipType = TipType.Ball, CutterDiameter = 6f, HeadDiameter = 10f, CutterLength = 20f };

    public static CuttingParameters Parameters(float cellSize = 0.5f) => new()
    {
        CellSize = cellSize,
        Stepover = 3f,
        FinishingStepover = 0.5f,
        Stepdown = 2f,
        SafeHeight = 5f,
        Tolerance = 0.05f,
        FeedRate = 800f,
        PlungeRate = 200f,
        RapidRate = 3000f,
    };

    public static ToolpathContext Build(Mesh mesh, StockDefinition stock, ToolDefinition tool, CuttingParameters parameters)
    {
        var axes = AxisSetup.Default();
        var machineMesh = mesh.Transform(axes.ToMatrix(mesh.Bounds, stock));
        var geometry = StockModel.Create(stock, machineMesh.Bounds, parameters.CellSize);
        var model = MeshRasterizer.CreateGridFor(geometry.Bounds, parameters.CellSize, geometry.StockBottom);
        MeshRasterizer.Rasterize(machineMesh, model, geometry.StockBottom);
        var profile = ToolProfile.Create(tool, parameters.CellSize);
        var tip = ReachMap.Compute(model, geometry.Map, profile, geometry.StockBottom, ReachMap.DefaultPercent, parameters.Tolerance);
        var limit = HeadClearance.ComputeHeadLimit(model, profile, tool.CutterLength);
        var effective = HeadClearance.ApplyHeadLimit(tip, limit);
        var plan = Slicer.Build(effective, geometry.Map, parameters);
        return new ToolpathContext(model, tip, effective, limit, geometry.Map, plan, tool, profile, parameters, geometry.StockTop);
    }

    // 10 x 10 x 5 box centered in a 20 x 20 x 5 stock: the box occupies x, y in [5, 15], the ring
    // around it is cut down to the stock bottom at z = 0, the stock top is z = 5.
    public static ToolpathContext BoxInStock(ToolDefinition? tool = null, CuttingParameters? parameters = null)
        => Build(
            TestMeshes.Box(10, 10, 5),
            new StockDefinition { Shape = StockShape.Box, SizeX = 20, SizeY = 20, SizeZ = 5 },
            tool ?? FlatTool6(),
            parameters ?? Parameters());

    // 20 x 20 x 5 plate with a hemispherical bump, in a 24 x 24 x 10 stock.
    public static ToolpathContext BumpPlate(ToolDefinition? tool = null, CuttingParameters? parameters = null)
        => Build(
            TestMeshes.BumpPlate(),
            new StockDefinition { Shape = StockShape.Box, SizeX = 24, SizeY = 24, SizeZ = 10 },
            tool ?? BallTool6(),
            parameters ?? Parameters());
}
