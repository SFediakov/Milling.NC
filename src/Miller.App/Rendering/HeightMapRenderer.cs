using System.Numerics;
using Avalonia.OpenGL;
using Miller.Core.HeightMaps;

namespace Miller.App.Rendering;

// Stock or final model as a grid mesh: one vertex per cell center, two triangles per quad whose four
// cells hold material, normals from the neighbouring cells. Walls hang from every boundary cell
// down to the floor so the map reads as a solid block; a wall's top is the cell vertex itself, so
// cutting an edge cell lowers the wall with it. Rows are contiguous in the buffer, so a dirty
// rectangle is refreshed with one sub-data call per row. Geometry building is static so tests
// cover it without a GL context.
public sealed class HeightMapRenderer : IDisposable
{
    // Measured in T-085; above this the validator warns about slow interaction.
    public const int MaxCellsForInteractiveFrame = 1_000_000;
    public const int FloatsPerVertex = 10;
    private static readonly (int Di, int Dj)[] Sides = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    private readonly VertexBuffer _buffer;
    private float[] _vertices = Array.Empty<float>();
    private HeightMap? _map;
    private Vector4 _color;

    public HeightMapRenderer(GlInterface gl)
    {
        _buffer = new VertexBuffer(gl, (GlShaders.PositionLocation, 3), (GlShaders.NormalLocation, 3), (GlShaders.ColorLocation, 4));
    }

    public int CellCount => _map?.CellCount ?? 0;

    public int TriangleCount => _buffer.IndexCount / 3;

    public void Upload(HeightMap? map, float floorZ, Vector4 color)
    {
        _map = map;
        _color = color;
        var (vertices, indices) = Build(map, floorZ, color);
        _vertices = vertices;
        _buffer.Upload(_vertices, _vertices.Length, GlConstants.GL_DYNAMIC_DRAW);
        _buffer.UploadIndices(indices, indices.Length, GlConsts.GL_STATIC_DRAW);
    }

    // Refreshes positions and normals of the cells in [i0, i1] x [j0, j1] plus a one-cell border.
    public void Update(GlFunctions functions, HeightMap map, int i0, int j0, int i1, int j1)
    {
        if (_map is null || !ReferenceEquals(map, _map))
        {
            throw new InvalidOperationException("Update needs the map that was uploaded.");
        }

        var fromI = Math.Max(0, i0 - 1);
        var toI = Math.Min(map.Width - 1, i1 + 1);
        for (var j = Math.Max(0, j0 - 1); j <= Math.Min(map.Height - 1, j1 + 1); j++)
        {
            WriteRow(_vertices, map, j, fromI, toI, _color, keepColors: true);
            var first = map.Index(fromI, j) * FloatsPerVertex;
            _buffer.Update(functions, _vertices, first, (toI - fromI + 1) * FloatsPerVertex);
        }
    }

    // Per-cell colors from a category array; the category type arrives with the analysis (T-097).
    public void SetCategories<TCategory>(TCategory[] categories, IReadOnlyDictionary<TCategory, Vector4> colors)
        where TCategory : notnull
    {
        if (_map is null || categories.Length != _map.CellCount)
        {
            throw new ArgumentException("Category array does not match the uploaded map.", nameof(categories));
        }

        for (var k = 0; k < categories.Length; k++)
        {
            WriteColor(_vertices, k, colors[categories[k]]);
        }

        _buffer.Upload(_vertices, _vertices.Length, GlConstants.GL_DYNAMIC_DRAW);
    }

    public void Draw() => _buffer.DrawIndexed(GlConsts.GL_TRIANGLES);

    public void Dispose() => _buffer.Dispose();

    // Grid vertices first (one per cell, NaN cells included but never indexed), then one floor
    // vertex per boundary cell; indices hold the top triangles followed by the wall triangles.
    public static (float[] Vertices, uint[] Indices) Build(HeightMap? map, float floorZ, Vector4 color)
    {
        if (map is null)
        {
            return (Array.Empty<float>(), Array.Empty<uint>());
        }

        var floorIndex = BoundaryFloorIndices(map, out var floorCount);
        var vertices = new float[(map.CellCount + floorCount) * FloatsPerVertex];
        for (var j = 0; j < map.Height; j++)
        {
            WriteRow(vertices, map, j, 0, map.Width - 1, color, keepColors: false);
        }

        var indices = new List<uint>(map.CellCount * 6);
        for (var j = 0; j + 1 < map.Height; j++)
        {
            for (var i = 0; i + 1 < map.Width; i++)
            {
                if (!Has(map, i, j) || !Has(map, i + 1, j) || !Has(map, i, j + 1) || !Has(map, i + 1, j + 1))
                {
                    continue;
                }

                var a = (uint)map.Index(i, j);
                var b = a + 1;
                var c = a + (uint)map.Width;
                var d = c + 1;
                indices.Add(a); indices.Add(b); indices.Add(d);
                indices.Add(a); indices.Add(d); indices.Add(c);
            }
        }

        WriteFloorVertices(vertices, map, floorZ, floorIndex, color);
        AddWalls(map, floorIndex, indices);
        return (vertices, indices.ToArray());
    }

    public static bool Has(HeightMap map, int i, int j) => map.InBounds(i, j) && !float.IsNaN(map[i, j]);

    // Floor vertex index per cell (after the grid vertices), -1 for cells without an open side.
    private static int[] BoundaryFloorIndices(HeightMap map, out int count)
    {
        var result = new int[map.CellCount];
        count = 0;
        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                var k = map.Index(i, j);
                result[k] = Has(map, i, j) && Sides.Any(s => !Has(map, i + s.Di, j + s.Dj)) ? map.CellCount + count++ : -1;
            }
        }

        return result;
    }

    private static void WriteFloorVertices(float[] vertices, HeightMap map, float floorZ, int[] floorIndex, Vector4 color)
    {
        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                var v = floorIndex[map.Index(i, j)];
                if (v < 0)
                {
                    continue;
                }

                var outward = Vector3.Zero;
                foreach (var (di, dj) in Sides)
                {
                    if (!Has(map, i + di, j + dj))
                    {
                        outward += new Vector3(di, dj, 0);
                    }
                }

                var c = map.CellCenter(i, j);
                WriteVertex(vertices, v, new Vector3(c.X, c.Y, floorZ), Vector3.Normalize(outward), color);
            }
        }
    }

    // A wall quad joins two neighbouring boundary cells that share an open side: their two grid
    // vertices on top and their two floor vertices below, wound to face outward.
    private static void AddWalls(HeightMap map, int[] floorIndex, List<uint> indices)
    {
        for (var j = 0; j < map.Height; j++)
        {
            for (var i = 0; i < map.Width; i++)
            {
                if (floorIndex[map.Index(i, j)] < 0)
                {
                    continue;
                }

                Wall(map, floorIndex, indices, i, j, 1, 0, 0, 1);
                Wall(map, floorIndex, indices, i, j, -1, 0, 0, 1);
                Wall(map, floorIndex, indices, i, j, 0, 1, 1, 0);
                Wall(map, floorIndex, indices, i, j, 0, -1, 1, 0);
            }
        }
    }

    private static void Wall(HeightMap map, int[] floorIndex, List<uint> indices, int i, int j, int openDi, int openDj, int alongDi, int alongDj)
    {
        var ni = i + alongDi;
        var nj = j + alongDj;
        if (Has(map, i + openDi, j + openDj) || !Has(map, ni, nj) || Has(map, ni + openDi, nj + openDj))
        {
            return;
        }

        var topA = (uint)map.Index(i, j);
        var topB = (uint)map.Index(ni, nj);
        var floorA = (uint)floorIndex[topA];
        var floorB = (uint)floorIndex[topB];
        var outwardFirst = openDi * alongDj - openDj * alongDi < 0;
        if (outwardFirst)
        {
            indices.Add(topA); indices.Add(topB); indices.Add(floorB);
            indices.Add(topA); indices.Add(floorB); indices.Add(floorA);
        }
        else
        {
            indices.Add(topA); indices.Add(floorB); indices.Add(topB);
            indices.Add(topA); indices.Add(floorA); indices.Add(floorB);
        }
    }

    private static void WriteRow(float[] vertices, HeightMap map, int j, int fromI, int toI, Vector4 color, bool keepColors)
    {
        for (var i = fromI; i <= toI; i++)
        {
            var z = map[i, j];
            var c = map.CellCenter(i, j);
            var v = map.Index(i, j);
            var o = v * FloatsPerVertex;
            var vertexColor = keepColors ? new Vector4(vertices[o + 6], vertices[o + 7], vertices[o + 8], vertices[o + 9]) : color;
            WriteVertex(vertices, v, new Vector3(c.X, c.Y, float.IsNaN(z) ? 0f : z), Normal(map, i, j), vertexColor);
        }
    }

    private static void WriteVertex(float[] vertices, int v, Vector3 p, Vector3 n, Vector4 c)
    {
        var o = v * FloatsPerVertex;
        vertices[o] = p.X;
        vertices[o + 1] = p.Y;
        vertices[o + 2] = p.Z;
        vertices[o + 3] = n.X;
        vertices[o + 4] = n.Y;
        vertices[o + 5] = n.Z;
        WriteColor(vertices, v, c);
    }

    private static void WriteColor(float[] vertices, int v, Vector4 c)
    {
        var o = v * FloatsPerVertex + 6;
        vertices[o] = c.X;
        vertices[o + 1] = c.Y;
        vertices[o + 2] = c.Z;
        vertices[o + 3] = c.W;
    }

    // Central differences, falling back to the cell itself at NaN or grid edges.
    public static Vector3 Normal(HeightMap map, int i, int j)
    {
        float Z(int ii, int jj)
        {
            if (!Has(map, ii, jj))
            {
                var own = map[i, j];
                return float.IsNaN(own) ? 0f : own;
            }

            return map[ii, jj];
        }

        var dx = (Z(i + 1, j) - Z(i - 1, j)) / (2 * map.CellSize);
        var dy = (Z(i, j + 1) - Z(i, j - 1)) / (2 * map.CellSize);
        return Vector3.Normalize(new Vector3(-dx, -dy, 1f));
    }
}
