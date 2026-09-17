using System.Numerics;
using Avalonia.OpenGL;
using Miller.Core.Setup;

namespace Miller.App.Rendering;

// Cutter cylinder (flat disc or ball tip) and the wider head cylinder, built around the tool tip
// at the origin and drawn translated to the current tool position. Geometry building is static so
// tests cover it without a GL context.
public sealed class ToolRenderer : IDisposable
{
    public const int Segments = 32;
    public const float HeadDisplayLength = 15f;
    public const int FloatsPerVertex = 10;

    private readonly VertexBuffer _buffer;
    private readonly Vector4 _cutterColor;
    private readonly Vector4 _headColor;

    public ToolRenderer(GlInterface gl, Vector4 cutterColor, Vector4 headColor)
    {
        _buffer = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.NormalLocation, 3), (GlShaders.ColorLocation, 4));
        _cutterColor = cutterColor;
        _headColor = headColor;
    }

    public bool HasGeometry => _buffer.VertexCount > 0;

    public void Build(ToolDefinition? tool)
    {
        var data = Build(tool, _cutterColor, _headColor);
        _buffer.Upload(data, data.Length, GlConsts.GL_STATIC_DRAW);
    }

    public void Draw(ShaderProgram lit, int mvpLocation, Matrix4x4 viewProjection, Vector3 tip)
    {
        lit.SetMatrix(mvpLocation, Matrix4x4.CreateTranslation(tip) * viewProjection);
        _buffer.Draw(GlConsts.GL_TRIANGLES);
    }

    public void Dispose() => _buffer.Dispose();

    // Position, normal and color per vertex; the tip touches z = 0, the head starts at CutterLength.
    public static float[] Build(ToolDefinition? tool, Vector4 cutterColor, Vector4 headColor)
    {
        if (tool is null)
        {
            return Array.Empty<float>();
        }

        var data = new List<float>();
        var r = tool.CutterRadius;
        var cutterBottom = tool.TipType == TipType.Ball ? r : 0f;
        Cylinder(data, r, cutterBottom, tool.CutterLength, cutterColor);
        if (tool.TipType == TipType.Ball)
        {
            Hemisphere(data, r, cutterColor);
        }
        else
        {
            Disc(data, r, 0f, -1f, cutterColor);
        }

        var headTop = tool.CutterLength + HeadDisplayLength;
        Cylinder(data, tool.HeadRadius, tool.CutterLength, headTop, headColor);
        Disc(data, tool.HeadRadius, tool.CutterLength, -1f, headColor);
        Disc(data, tool.HeadRadius, headTop, 1f, headColor);
        return data.ToArray();
    }

    private static void Cylinder(List<float> data, float radius, float z0, float z1, Vector4 color)
    {
        for (var k = 0; k < Segments; k++)
        {
            var a0 = 2 * MathF.PI * k / Segments;
            var a1 = 2 * MathF.PI * (k + 1) / Segments;
            var n0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0);
            var n1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0);
            var p00 = new Vector3(n0.X * radius, n0.Y * radius, z0);
            var p01 = new Vector3(n0.X * radius, n0.Y * radius, z1);
            var p10 = new Vector3(n1.X * radius, n1.Y * radius, z0);
            var p11 = new Vector3(n1.X * radius, n1.Y * radius, z1);
            Vertex(data, p00, n0, color); Vertex(data, p10, n1, color); Vertex(data, p11, n1, color);
            Vertex(data, p00, n0, color); Vertex(data, p11, n1, color); Vertex(data, p01, n0, color);
        }
    }

    private static void Disc(List<float> data, float radius, float z, float normalZ, Vector4 color)
    {
        var center = new Vector3(0, 0, z);
        var n = new Vector3(0, 0, normalZ);
        for (var k = 0; k < Segments; k++)
        {
            var a0 = 2 * MathF.PI * k / Segments;
            var a1 = 2 * MathF.PI * (k + 1) / Segments;
            var p0 = new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, z);
            var p1 = new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, z);
            Vertex(data, center, n, color);
            Vertex(data, normalZ > 0 ? p0 : p1, n, color);
            Vertex(data, normalZ > 0 ? p1 : p0, n, color);
        }
    }

    // Lower half of a sphere of the cutter radius with its center at z = radius: the tip touches z = 0.
    private static void Hemisphere(List<float> data, float radius, Vector4 color)
    {
        var rings = Segments / 4;
        var c = new Vector3(0, 0, radius);
        for (var ring = 0; ring < rings; ring++)
        {
            var t0 = -MathF.PI / 2 + MathF.PI / 2 * ring / rings;
            var t1 = -MathF.PI / 2 + MathF.PI / 2 * (ring + 1) / rings;
            for (var k = 0; k < Segments; k++)
            {
                var a0 = 2 * MathF.PI * k / Segments;
                var a1 = 2 * MathF.PI * (k + 1) / Segments;
                var n00 = Sphere(t0, a0); var n01 = Sphere(t1, a0);
                var n10 = Sphere(t0, a1); var n11 = Sphere(t1, a1);
                Vertex(data, c + n00 * radius, n00, color); Vertex(data, c + n10 * radius, n10, color); Vertex(data, c + n11 * radius, n11, color);
                Vertex(data, c + n00 * radius, n00, color); Vertex(data, c + n11 * radius, n11, color); Vertex(data, c + n01 * radius, n01, color);
            }
        }
    }

    private static Vector3 Sphere(float elevation, float azimuth)
        => new(MathF.Cos(elevation) * MathF.Cos(azimuth), MathF.Cos(elevation) * MathF.Sin(azimuth), MathF.Sin(elevation));

    private static void Vertex(List<float> data, Vector3 p, Vector3 n, Vector4 c)
    {
        data.Add(p.X); data.Add(p.Y); data.Add(p.Z);
        data.Add(n.X); data.Add(n.Y); data.Add(n.Z);
        data.Add(c.X); data.Add(c.Y); data.Add(c.Z); data.Add(c.W);
    }
}
