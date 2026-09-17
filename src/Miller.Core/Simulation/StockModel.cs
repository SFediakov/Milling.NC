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
    // zero, so model and stock agree by construction. Box: every cell at the top. Cylinder: cells whose
    // center lies outside the circle inscribed in the bounding square are NaN (no material).
    public static StockGeometry Create(StockDefinition stock, BoundingBox modelBoundsMachine, float cellSize)
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
        var map = MeshRasterizer.CreateGridFor(bounds, cellSize, top);
        if (stock.Shape == StockShape.Cylinder)
        {
            MaskOutsideCircle(map, corner.X + size.X / 2, corner.Y + size.Y / 2, stock.Diameter / 2);
        }

        return new StockGeometry(map, top, bounds.Min.Z, bounds);
    }

    private static void MaskOutsideCircle(HeightMap map, float centerX, float centerY, float radius)
    {
        var r2 = radius * radius;
        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                var c = map.CellCenter(i, j);
                var dx = c.X - centerX;
                var dy = c.Y - centerY;
                if (dx * dx + dy * dy > r2)
                {
                    map[i, j] = float.NaN;
                }
            }
        }
    }
}
