using System.Numerics;
using Miller.App.Rendering;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.App;

// The renderers build their geometry in static methods so the vertex and index layout is checked
// here without an OpenGL context.
public sealed class RendererGeometryTests
{
    private static readonly Vector4 Red = new(1, 0, 0, 1);
    private static readonly Vector4 Green = new(0, 1, 0, 1);
    private static readonly Vector4 Blue = new(0, 0, 1, 1);
    private static readonly Vector4 Background = new(0.1f, 0.1f, 0.1f, 1);

    private static readonly Dictionary<MoveKind, Vector4> Colors = new()
    {
        [MoveKind.Rapid] = Red,
        [MoveKind.Feed] = Green,
        [MoveKind.Plunge] = Blue,
    };

    [Fact]
    public void Toolpath_TwoVerticesPerSegment_BrightAndDimmedByKind()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(1, 0, 5), MoveKind.Rapid, 1000));
        path.Add(new ToolpathSegment(new Vector3(1, 0, 5), new Vector3(1, 0, 0), MoveKind.Plunge, 100));
        path.Add(new ToolpathSegment(new Vector3(1, 0, 0), new Vector3(2, 0, 0), MoveKind.Feed, 500));

        var (bright, dim) = ToolpathRenderer.Build(path, Colors, Background);
        Assert.Equal(3 * 2 * ToolpathRenderer.FloatsPerVertex, bright.Length);
        Assert.Equal(bright.Length, dim.Length);
        // Second segment, end vertex: position then color.
        var o = (2 + 1) * ToolpathRenderer.FloatsPerVertex;
        Assert.Equal(new Vector3(1, 0, 0), new Vector3(bright[o], bright[o + 1], bright[o + 2]));
        Assert.Equal(Blue, new Vector4(bright[o + 3], bright[o + 4], bright[o + 5], bright[o + 6]));
        var expectedDim = ToolpathRenderer.Dim(Blue, Background);
        Assert.Equal(expectedDim, new Vector4(dim[o + 3], dim[o + 4], dim[o + 5], dim[o + 6]));
        Assert.Equal(1f, expectedDim.W);
        Assert.True(expectedDim.Z < Blue.Z && expectedDim.Z > Background.Z);
        Assert.Equal(new Vector3(bright[o], bright[o + 1], bright[o + 2]), new Vector3(dim[o], dim[o + 1], dim[o + 2]));
    }

    [Theory]
    [InlineData(0, 3, 0, 6)]
    [InlineData(2, 3, 4, 2)]
    [InlineData(3, 3, 6, 0)]
    [InlineData(9, 3, 6, 0)]
    [InlineData(-1, 3, 0, 6)]
    public void Toolpath_SplitClampsTheProgressIndex(int progress, int segments, int done, int remaining)
    {
        Assert.Equal((done, remaining), ToolpathRenderer.Split(progress, segments));
    }

    [Fact]
    public void Toolpath_EmptyOrNull_ProducesNoGeometry()
    {
        Assert.Empty(ToolpathRenderer.Build(null, Colors, Background).Bright);
        Assert.Empty(ToolpathRenderer.Build(new Toolpath(), Colors, Background).Dim);
    }

    [Theory]
    [InlineData(TipType.Flat)]
    [InlineData(TipType.Ball)]
    public void Tool_GeometrySpansTipToHeadTop(TipType tip)
    {
        var tool = new ToolDefinition { CutterDiameter = 6, CutterLength = 20, HeadDiameter = 10, TipType = tip };
        var data = ToolRenderer.Build(tool, Red, Green);
        Assert.Equal(0, data.Length % ToolRenderer.FloatsPerVertex);
        var cylinder = ToolRenderer.Segments * 6;
        var disc = ToolRenderer.Segments * 3;
        var tipVertices = tip == TipType.Ball ? ToolRenderer.Segments / 4 * ToolRenderer.Segments * 6 : disc;
        Assert.Equal((cylinder + tipVertices + cylinder + 2 * disc) * ToolRenderer.FloatsPerVertex, data.Length);

        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        var maxCutterRadius = 0f;
        var maxHeadRadius = 0f;
        for (var o = 0; o < data.Length; o += ToolRenderer.FloatsPerVertex)
        {
            var p = new Vector3(data[o], data[o + 1], data[o + 2]);
            var color = new Vector4(data[o + 6], data[o + 7], data[o + 8], data[o + 9]);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
            var radius = MathF.Sqrt(p.X * p.X + p.Y * p.Y);
            if (color == Red)
            {
                maxCutterRadius = MathF.Max(maxCutterRadius, radius);
                Assert.InRange(p.Z, 0f, tool.CutterLength);
            }
            else
            {
                Assert.Equal(Green, color);
                maxHeadRadius = MathF.Max(maxHeadRadius, radius);
                Assert.InRange(p.Z, tool.CutterLength, tool.CutterLength + ToolRenderer.HeadDisplayLength);
            }

            var n = new Vector3(data[o + 3], data[o + 4], data[o + 5]);
            Assert.Equal(1f, n.Length(), 3);
        }

        Assert.Equal(0f, minZ, 4);
        Assert.Equal(tool.CutterLength + ToolRenderer.HeadDisplayLength, maxZ, 4);
        Assert.Equal(tool.CutterRadius, maxCutterRadius, 3);
        Assert.Equal(tool.HeadRadius, maxHeadRadius, 3);
    }

    [Fact]
    public void Tool_NullDefinition_ProducesNoGeometry()
    {
        Assert.Empty(ToolRenderer.Build(null, Red, Green));
    }

    [Fact]
    public void HeightMap_FullBox_TopQuadsAndFourWalls()
    {
        var map = new HeightMap(0, 0, 1, 2, 2, 5f);
        var (vertices, indices) = HeightMapRenderer.Build(map, -3f, Red);
        // 4 grid vertices and 4 floor vertices (every cell touches the outside).
        Assert.Equal(8 * HeightMapRenderer.FloatsPerVertex, vertices.Length);
        // One top quad (2 triangles) and four wall quads (8 triangles).
        Assert.Equal((2 + 8) * 3, indices.Length);
        Assert.All(indices, k => Assert.InRange(k, 0u, 7u));
        for (var v = 4; v < 8; v++)
        {
            Assert.Equal(-3f, vertices[v * HeightMapRenderer.FloatsPerVertex + 2]);
        }

        for (var v = 0; v < 4; v++)
        {
            var o = v * HeightMapRenderer.FloatsPerVertex;
            Assert.Equal(5f, vertices[o + 2]);
            Assert.Equal(new Vector3(0, 0, 1), new Vector3(vertices[o + 3], vertices[o + 4], vertices[o + 5]));
            Assert.Equal(Red, new Vector4(vertices[o + 6], vertices[o + 7], vertices[o + 8], vertices[o + 9]));
        }
    }

    [Fact]
    public void HeightMap_NaNCells_AreNeverIndexed()
    {
        var map = new HeightMap(0, 0, 1, 4, 4, 2f);
        map[1, 1] = float.NaN;
        map[2, 1] = float.NaN;
        map[0, 3] = float.NaN;
        var (_, indices) = HeightMapRenderer.Build(map, 0f, Red);
        Assert.NotEmpty(indices);
        foreach (var k in indices.Where(k => k < (uint)map.CellCount))
        {
            var i = (int)k % map.Width;
            var j = (int)k / map.Width;
            Assert.False(float.IsNaN(map[i, j]), $"index {k} references the empty cell ({i}, {j})");
        }

        // Every top triangle touches only material; with the hole, the four quads around (1,1)/(2,1) are gone.
        var topTriangles = 0;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            if (indices[t] < map.CellCount && indices[t + 1] < map.CellCount && indices[t + 2] < map.CellCount)
            {
                topTriangles++;
            }
        }

        // 3x3 quads in a 4x4 grid, minus the 6 quads touching the two empty cells, minus 1 quad at (0,3).
        Assert.Equal((9 - 6 - 1) * 2, topTriangles);
    }

    [Fact]
    public void HeightMap_Normals_FollowTheSlope()
    {
        var map = new HeightMap(0, 0, 1, 3, 3, 0f);
        for (var j = 0; j < 3; j++)
        {
            for (var i = 0; i < 3; i++)
            {
                map[i, j] = i;
            }
        }

        var n = HeightMapRenderer.Normal(map, 1, 1);
        Assert.Equal(Vector3.Normalize(new Vector3(-1, 0, 1)), n);
        Assert.Equal(new Vector3(0, 0, 1), HeightMapRenderer.Normal(new HeightMap(0, 0, 1, 1, 1, 4f), 0, 0));
    }

    [Fact]
    public void HeightMap_Null_ProducesNoGeometry()
    {
        var (vertices, indices) = HeightMapRenderer.Build(null, 0f, Red);
        Assert.Empty(vertices);
        Assert.Empty(indices);
    }
}
