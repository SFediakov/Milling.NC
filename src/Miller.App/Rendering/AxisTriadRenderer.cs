using System.Numerics;
using Avalonia.OpenGL;
using Miller.Core.Geometry;
using Miller.Core.Setup;

namespace Miller.App.Rendering;

// Machine axes at the origin and the stock outline (box edges, or two rings with verticals for a
// cylinder), drawn with the line shader.
public sealed class AxisTriadRenderer : IDisposable
{
    public const float AxisLength = 20f;
    public const int CylinderSegments = 64;
    private const int FloatsPerVertex = 7;

    private readonly VertexBuffer _axes;
    private readonly VertexBuffer _stock;
    private readonly Vector4 _stockColor;

    public AxisTriadRenderer(GlInterface gl, Vector4 xColor, Vector4 yColor, Vector4 zColor, Vector4 stockColor)
    {
        _axes = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.ColorLocation, 4));
        _stock = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.ColorLocation, 4));
        _stockColor = stockColor;
        var data = new List<float>(6 * FloatsPerVertex);
        AddLine(data, Vector3.Zero, Vector3.UnitX * AxisLength, xColor);
        AddLine(data, Vector3.Zero, Vector3.UnitY * AxisLength, yColor);
        AddLine(data, Vector3.Zero, Vector3.UnitZ * AxisLength, zColor);
        _axes.Upload(data.ToArray(), data.Count, GlConsts.GL_STATIC_DRAW);
    }

    public void Build(BoundingBox? bounds, StockDefinition? definition)
    {
        if (bounds is not { IsEmpty: false } b || definition is null)
        {
            _stock.Upload(Array.Empty<float>(), 0, GlConsts.GL_STATIC_DRAW);
            return;
        }

        var data = new List<float>();
        if (definition.Shape == StockShape.Cylinder)
        {
            AddCylinder(data, b, definition.Diameter / 2);
        }
        else
        {
            AddBox(data, b);
        }

        _stock.Upload(data.ToArray(), data.Count, GlConsts.GL_STATIC_DRAW);
    }

    public void Draw(bool includeStock)
    {
        _axes.Draw(GlConstants.GL_LINES);
        if (includeStock)
        {
            _stock.Draw(GlConstants.GL_LINES);
        }
    }

    public void Dispose()
    {
        _axes.Dispose();
        _stock.Dispose();
    }

    private void AddBox(List<float> data, BoundingBox b)
    {
        Vector3 P(int i) => new((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        foreach (var (from, to) in new[] { (0, 1), (1, 3), (3, 2), (2, 0), (4, 5), (5, 7), (7, 6), (6, 4), (0, 4), (1, 5), (2, 6), (3, 7) })
        {
            AddLine(data, P(from), P(to), _stockColor);
        }
    }

    private void AddCylinder(List<float> data, BoundingBox b, float radius)
    {
        var center = new Vector2(b.Center.X, b.Center.Y);
        foreach (var z in new[] { b.Min.Z, b.Max.Z })
        {
            for (var k = 0; k < CylinderSegments; k++)
            {
                var a0 = 2 * MathF.PI * k / CylinderSegments;
                var a1 = 2 * MathF.PI * (k + 1) / CylinderSegments;
                AddLine(data, Ring(center, radius, a0, z), Ring(center, radius, a1, z), _stockColor);
            }
        }

        for (var k = 0; k < 4; k++)
        {
            var a = MathF.PI / 2 * k;
            AddLine(data, Ring(center, radius, a, b.Min.Z), Ring(center, radius, a, b.Max.Z), _stockColor);
        }
    }

    private static Vector3 Ring(Vector2 center, float radius, float angle, float z)
        => new(center.X + radius * MathF.Cos(angle), center.Y + radius * MathF.Sin(angle), z);

    private static void AddLine(List<float> data, Vector3 from, Vector3 to, Vector4 color)
    {
        AddVertex(data, from, color);
        AddVertex(data, to, color);
    }

    private static void AddVertex(List<float> data, Vector3 p, Vector4 c)
    {
        data.Add(p.X);
        data.Add(p.Y);
        data.Add(p.Z);
        data.Add(c.X);
        data.Add(c.Y);
        data.Add(c.Z);
        data.Add(c.W);
    }
}
