using System.Numerics;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;

namespace Miller.Core.Simulation;

// The stock as a heightmap plus the numbers every later stage needs from it.
public sealed record StockGeometry(HeightMap Map, float StockTop, float StockBottom, BoundingBox Bounds)
{
    public Vector3 Corner => Bounds.Min;

    public Vector3 Size => Bounds.Size;
}

public static class StockModel
{
    // The stock's minimum corner comes from the same rule AxisSetup.ToMatrix used to place machine
    // zero, so model and stock agree by construction (native mn_stock_map). Box: every cell at the
    // top. Cylinder: cells whose center lies outside the circle inscribed in the bounding square are
    // NaN (no material).
    public static unsafe StockGeometry Create(StockDefinition stock, BoundingBox modelBoundsMachine, float cellSize)
    {
        ArgumentNullException.ThrowIfNull(stock);
        if (modelBoundsMachine.IsEmpty)
        {
            throw new ArgumentException("Model bounds are empty.", nameof(modelBoundsMachine));
        }

        var size = AxisSetup.StockBoundingSize(stock);
        if (!(size.X > 0 && size.Y > 0 && size.Z > 0))
        {
            throw new ArgumentException($"Stock size must be positive, got {size}.", nameof(stock));
        }

        var corner = AxisSetup.StockCorner(modelBoundsMachine, stock);
        var bounds = new BoundingBox(corner, corner + size);
        var top = bounds.Max.Z;
        Native.CoreNative.Grid grid;
        float* z = null;
        Native.CoreNative.Check(Native.CoreNative.mn_stock_map(corner.X, corner.Y, size.X, size.Y, top, stock.Shape == StockShape.Cylinder ? 1 : 0, stock.Diameter, cellSize, &grid, &z));
        try
        {
            return new StockGeometry(Native.CoreNative.MapOf(grid, z), top, bounds.Min.Z, bounds);
        }
        finally
        {
            Native.CoreNative.mn_free(z);
        }
    }
}
