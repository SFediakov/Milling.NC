using System.Numerics;
using System.Text.RegularExpressions;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

// T-135: a frustum head from a 4 mm bottom to an 8 mm top over 6 mm, above a 2 mm cutter 5 mm long,
// on 0.5 mm cells. Along +X the offset (k, 0) lies at k / 2 mm, so the underside rises
// 6 * (d - 2) / (4 - 2) = 3 * (d - 2) above the head bottom between d = 2 and d = 4.
public sealed class HeadShapeTests
{
    private const float Cell = 0.5f;
    private const float CutterLength = 5f;
    private const int Size = 41;
    private const int Center = 20;

    private static ToolDefinition Frustum(float bottom = 4f, float top = 8f, float length = 6f) => new()
    {
        CutterDiameter = 2f,
        CutterLength = CutterLength,
        HeadDiameter = bottom,
        HeadShape = HeadShape.Frustum,
        HeadTopDiameter = top,
        HeadLength = length,
    };

    private static ToolDefinition Cylinder(float diameter) => new() { CutterDiameter = 2f, CutterLength = CutterLength, HeadDiameter = diameter };

    private static Dictionary<(int, int), float> Annulus(ToolDefinition tool)
        => ToolProfile.Create(tool, Cell).AnnulusOffsets.ToDictionary(o => (o.Dx, o.Dy), o => o.Dz);

    // Flat floor at 0 with one column of the given height `k` cells to the +X side of the center.
    private static HeightMap Column(int k, float height)
    {
        var map = new HeightMap(0, 0, Cell, Size, Size, 0f);
        map[Center + k, Center] = height;
        return map;
    }

    private static Vector3 CenterTip(float z) => new((Center + 0.5f) * Cell, (Center + 0.5f) * Cell, z);

    [Fact]
    public void Frustum_AnnulusDz_RisesLinearlyFromTheBottomToTheTopRadius()
    {
        var annulus = Annulus(Frustum());
        Assert.Equal(0f, annulus[(3, 0)]);   // d = 1.5, under the flat bottom
        Assert.Equal(0f, annulus[(4, 0)]);   // d = 2, the bottom edge
        Assert.Equal(1.5f, annulus[(5, 0)]); // d = 2.5
        Assert.Equal(3f, annulus[(6, 0)]);   // d = 3
        Assert.Equal(6f, annulus[(8, 0)]);   // d = 4, the top edge
        Assert.False(annulus.ContainsKey((9, 0)));
        Assert.False(annulus.ContainsKey((2, 0))); // d = 1, under the cutter
        Assert.Equal(8, ToolProfile.Create(Frustum(), Cell).HeadRadiusCells);
        Assert.Equal(4f, Frustum().HeadRadius);
        Assert.All(annulus.Values, dz => Assert.InRange(dz, 0f, 6f));
    }

    [Fact]
    public void Frustum_NarrowingUpward_IsTheCylinderOfItsBottom()
    {
        Assert.Equal(Annulus(Cylinder(8f)), Annulus(Frustum(bottom: 8f, top: 4f)));
        Assert.Equal(4f, Frustum(bottom: 8f, top: 4f).HeadRadius);
    }

    [Fact]
    public void Frustum_WithEqualDiameters_IsTheCylinder()
    {
        Assert.Equal(Annulus(Cylinder(6f)), Annulus(Frustum(bottom: 6f, top: 6f)));
    }

    [Fact]
    public void Cylinder_IgnoresTheFrustumFields()
    {
        var plain = Cylinder(6f);
        var withFields = Cylinder(6f);
        withFields.HeadTopDiameter = 40f;
        withFields.HeadLength = 0f;
        Assert.Equal(3f, withFields.HeadRadius);
        Assert.Equal(Annulus(plain), Annulus(withFields));
        Assert.All(Annulus(plain).Values, dz => Assert.Equal(0f, dz));
    }

    [Theory]
    [InlineData(0f, 6f)]
    [InlineData(8f, 0f)]
    [InlineData(float.NaN, 6f)]
    [InlineData(8f, float.PositiveInfinity)]
    public void Frustum_WithoutAPositiveTopOrLength_IsRejected(float top, float length)
    {
        var error = Assert.Throws<ArgumentException>(() => ToolProfile.Create(Frustum(top: top, length: length), Cell));
        Assert.Contains("frustum", error.Message);
    }

    [Fact]
    public void UnknownHeadShape_IsRejected()
    {
        var tool = Cylinder(6f);
        tool.HeadShape = (HeadShape)7;
        var error = Assert.Throws<ArgumentException>(() => ToolProfile.Create(tool, Cell));
        Assert.Contains("head shape 7", error.Message);
    }

    [Fact]
    public void HeadLimit_UnderTheFrustum_SubtractsTheUndersideHeight()
    {
        // A 20 mm column 3 mm from the axis: the underside there is 3 mm above the head bottom.
        var model = Column(6, 20f);
        var frustum = HeadClearance.ComputeHeadLimit(model, ToolProfile.Create(Frustum(), Cell), CutterLength);
        var cylinder = HeadClearance.ComputeHeadLimit(model, ToolProfile.Create(Cylinder(8f), Cell), CutterLength);
        Assert.Equal(20f - 3f - CutterLength, frustum[Center, Center]);
        Assert.Equal(20f - CutterLength, cylinder[Center, Center]);

        // Under the flat bottom the frustum limits like a cylinder.
        var near = Column(3, 20f);
        Assert.Equal(20f - CutterLength, HeadClearance.ComputeHeadLimit(near, ToolProfile.Create(Frustum(), Cell), CutterLength)[Center, Center]);
    }

    [Fact]
    public void HeadLimit_OfTheFrustum_LiesBetweenTheCylindersOfItsDiameters()
    {
        var model = new HeightMap(0, 0, Cell, Size, Size, 0f);
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                model[i, j] = (i * 7 + j * 13) % 11;
            }
        }

        var frustum = HeadClearance.ComputeHeadLimit(model, ToolProfile.Create(Frustum(), Cell), CutterLength);
        var bottom = HeadClearance.ComputeHeadLimit(model, ToolProfile.Create(Cylinder(4f), Cell), CutterLength);
        var top = HeadClearance.ComputeHeadLimit(model, ToolProfile.Create(Cylinder(8f), Cell), CutterLength);
        for (var j = 0; j < Size; j++)
        {
            for (var i = 0; i < Size; i++)
            {
                Assert.InRange(frustum[i, j], bottom[i, j], top[i, j]);
            }
        }
    }

    [Fact]
    public void Collision_UnderTheFrustum_ComparesWithTheUnderside()
    {
        var frustum = ToolProfile.Create(Frustum(), Cell);
        var cylinder = ToolProfile.Create(Cylinder(8f), Cell);

        // Head bottom at 5, underside 3 mm higher at the column: 7 clears the frustum, not the cylinder.
        var lower = Column(6, 7f);
        Assert.Null(CollisionDetector.Check(lower, frustum, CutterLength, CenterTip(0f), MoveKind.Feed, 0));
        var hit = CollisionDetector.Check(lower, cylinder, CutterLength, CenterTip(0f), MoveKind.Feed, 0);
        Assert.Equal(SimulationEventKind.HeadCollision, hit?.Kind);

        var higher = Column(6, 8.5f);
        var frustumHit = CollisionDetector.Check(higher, frustum, CutterLength, CenterTip(0f), MoveKind.Feed, 3);
        Assert.Equal(SimulationEventKind.HeadCollision, frustumHit?.Kind);
        Assert.Contains("underside 8", frustumHit!.Message);
    }

    [Fact]
    public void Serializer_RoundTripsTheHeadShape_AndOldFilesLoadAsCylinder()
    {
        var project = MillingProject.Default();
        project.Tool = Frustum(top: 12f, length: 7.5f);
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"HeadShape\": \"Frustum\"", json);
        var copy = ProjectSerializer.Deserialize(json);
        Assert.Equal(HeadShape.Frustum, copy.Tool.HeadShape);
        Assert.Equal(12f, copy.Tool.HeadTopDiameter);
        Assert.Equal(7.5f, copy.Tool.HeadLength);

        var legacy = Regex.Replace(ProjectSerializer.Serialize(MillingProject.Default()),
            @"^\s*""(HeadShape|HeadTopDiameter|HeadLength)"": [^\r\n]*\r?\n", string.Empty, RegexOptions.Multiline);
        Assert.DoesNotContain("HeadShape", legacy);
        Assert.DoesNotContain("HeadTopDiameter", legacy);
        Assert.DoesNotContain("HeadLength", legacy);
        var old = ProjectSerializer.Deserialize(legacy);
        Assert.Equal(HeadShape.Cylinder, old.Tool.HeadShape);
        Assert.Equal(ToolDefinition.DefaultHeadTopDiameter, old.Tool.HeadTopDiameter);
        Assert.Equal(ToolDefinition.DefaultHeadLength, old.Tool.HeadLength);
    }
}
