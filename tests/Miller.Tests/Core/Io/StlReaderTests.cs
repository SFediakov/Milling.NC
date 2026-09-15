using System.Numerics;
using System.Text;
using Miller.Core.Io;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.Io;

public sealed class StlReaderTests
{
    public const int FixtureTriangles = 4050;
    public const long FixtureBytes = 202584;
    private const float BoundsTolerance = 0.001f;
    private static readonly Vector3 FixtureMin = new(3.259f, 0.477f, 0f);
    private static readonly Vector3 FixtureMax = new(23.741f, 5.477f, 21.971f);

    [Fact]
    public void FixtureFile_Exists()
    {
        var path = TestMeshes.FixturePath();
        Assert.True(File.Exists(path), path);
        Assert.Equal(FixtureBytes, new FileInfo(path).Length);
    }

    [Fact]
    public void Fixture_IsBinaryWithRecordedFacts()
    {
        var (mesh, report) = StlReader.Read(TestMeshes.FixturePath());

        Assert.Equal(StlFormat.Binary, report.Format);
        Assert.Equal(FixtureTriangles, report.TriangleCount);
        Assert.Equal(FixtureTriangles, mesh.TriangleCount);
        Assert.Equal(0, report.DegenerateCount);
        Assert.Equal(TestMeshes.FixturePath(), report.Path);
        Assert.True(Vector3.Distance(FixtureMin, report.Bounds.Min) < BoundsTolerance, report.Bounds.Min.ToString());
        Assert.True(Vector3.Distance(FixtureMax, report.Bounds.Max) < BoundsTolerance, report.Bounds.Max.ToString());
        Assert.Equal(mesh.Bounds, report.Bounds);
    }

    [Fact]
    public void Fixture_HeaderStartsWithSolid_ButIsBinary()
    {
        var data = File.ReadAllBytes(TestMeshes.FixturePath());
        Assert.StartsWith("solid", Encoding.ASCII.GetString(data, 0, 5));
        Assert.True(StlBinaryParser.MatchesSizeRule(data));
        Assert.Equal(StlFormat.Binary, StlReader.Read(data, "fixture").Report.Format);
    }

    [Fact]
    public void Fixture_IsClosedManifold()
    {
        var (mesh, _) = StlReader.Read(TestMeshes.FixturePath());
        Assert.True(TestMeshes.IsClosed(mesh));
        Assert.Equal(6075, TestMeshes.EdgeCount(mesh));
    }

    [Fact]
    public void AsciiCube_ParsesAsAsciiWithTwelveTriangles()
    {
        var data = Encoding.UTF8.GetBytes(TestMeshes.AsciiCubeText());
        var (mesh, report) = StlReader.Read(data, "cube.stl");

        Assert.Equal(StlFormat.Ascii, report.Format);
        Assert.Equal(12, report.TriangleCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(Vector3.Zero, report.Bounds.Min);
        Assert.Equal(Vector3.One, report.Bounds.Max);
        Assert.Equal("cube.stl", report.Path);
    }

    [Fact]
    public void AsciiFile_ReadFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"miller-{Guid.NewGuid():N}.stl");
        try
        {
            File.WriteAllText(path, TestMeshes.AsciiCubeText());
            var (mesh, report) = StlReader.Read(path);
            Assert.Equal(StlFormat.Ascii, report.Format);
            Assert.Equal(12, mesh.TriangleCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TruncatedBinary_Throws()
    {
        var data = File.ReadAllBytes(TestMeshes.FixturePath());
        var truncated = data[..(data.Length - 50)];

        // The size rule fails, so the bytes go to the ASCII parser; the header says "solid", so the
        // rejection happens on the first binary line after it.
        var ex = Assert.Throws<InvalidDataException>(() => StlReader.Read(truncated, "truncated"));
        Assert.Matches(@"^line \d+:", ex.Message);

        var binaryEx = Assert.Throws<InvalidDataException>(() => StlBinaryParser.Parse(data[..(data.Length - 7)]));
        Assert.Contains("202584", binaryEx.Message);
    }

    [Fact]
    public void GarbageText_ThrowsWithLineNumber()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not an stl file\nat all\n");
        var ex = Assert.Throws<InvalidDataException>(() => StlReader.Read(garbage, "garbage"));
        Assert.StartsWith("line 1:", ex.Message);

        var brokenVertex = TestMeshes.AsciiCubeText().Split('\n');
        brokenVertex[4] = "      vertex 1 x 0";
        var ex2 = Assert.Throws<InvalidDataException>(
            () => StlReader.Read(Encoding.UTF8.GetBytes(string.Join('\n', brokenVertex)), "broken"));
        Assert.StartsWith("line 5:", ex2.Message);
    }

    [Fact]
    public void DegenerateFacets_AreRemovedAndCounted()
    {
        var text = TestMeshes.AsciiCubeText().Replace(
            "endsolid cube",
            "  facet normal 0 0 0\n    outer loop\n      vertex 0 0 0\n      vertex 1 1 1\n      vertex 2 2 2\n    endloop\n  endfacet\nendsolid cube");
        var (mesh, report) = StlReader.Read(Encoding.UTF8.GetBytes(text), "degenerate");

        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(12, report.TriangleCount);
        Assert.Equal(1, report.DegenerateCount);
        Assert.DoesNotContain(mesh.Triangles, t => t.IsDegenerate);
    }
}
