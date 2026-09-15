using System.Numerics;
using Avalonia.OpenGL;
using Miller.Core.Geometry;

namespace Miller.App.Rendering;

// The machine-space model as flat-shaded triangles: three vertices per triangle, each carrying
// position, the triangle normal and the model color.
public sealed class MeshRenderer : IDisposable
{
    private const int FloatsPerVertex = 10;

    private readonly VertexBuffer _buffer;
    private readonly Vector4 _color;

    public MeshRenderer(GlInterface gl, Vector4 color)
    {
        _buffer = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.NormalLocation, 3), (GlShaders.ColorLocation, 4));
        _color = color;
    }

    public int TriangleCount => _buffer.VertexCount / 3;

    public void Upload(Mesh? mesh)
    {
        if (mesh is null || mesh.TriangleCount == 0)
        {
            _buffer.Upload(Array.Empty<float>(), 0, GlConsts.GL_STATIC_DRAW);
            return;
        }

        var data = new float[mesh.TriangleCount * 3 * FloatsPerVertex];
        var k = 0;
        foreach (var t in mesh.Triangles)
        {
            k = Put(data, k, t.A, t.Normal);
            k = Put(data, k, t.B, t.Normal);
            k = Put(data, k, t.C, t.Normal);
        }

        _buffer.Upload(data, data.Length, GlConsts.GL_STATIC_DRAW);
    }

    public void Draw() => _buffer.Draw(GlConsts.GL_TRIANGLES);

    public void Dispose() => _buffer.Dispose();

    private int Put(float[] data, int k, Vector3 p, Vector3 n)
    {
        data[k++] = p.X;
        data[k++] = p.Y;
        data[k++] = p.Z;
        data[k++] = n.X;
        data[k++] = n.Y;
        data[k++] = n.Z;
        data[k++] = _color.X;
        data[k++] = _color.Y;
        data[k++] = _color.Z;
        data[k++] = _color.W;
        return k;
    }
}
