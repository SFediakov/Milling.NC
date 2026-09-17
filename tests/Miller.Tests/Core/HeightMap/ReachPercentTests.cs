using Miller.Application.Validation;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Core.HeightMaps;

// The reach rule as a percentage of the footprint: 50 is the majority rule the map always used,
// 100 is the drop cutter, lower values reach deeper, and the project carries and validates it.
public sealed class ReachPercentTests
{
    [Fact]
    public void Rank_At50_EqualsTheMedianRule_ForEveryFootprintSize()
    {
        for (var n = 1; n <= 120; n++)
        {
            Assert.Equal(n - (n / 2 + 1), ReachMap.Rank(n, ReachMap.DefaultPercent));
            Assert.Equal(n - 1, ReachMap.Rank(n, ReachMap.MaxPercent));
            Assert.Equal(0, ReachMap.Rank(n, 1e-3f));
        }

        Assert.Equal(2, ReachMap.Rank(5, 60f));
        Assert.Equal(3, ReachMap.Rank(5, 61f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachMap.Rank(0, 50f));
    }

    [Fact]
    public void Default_ReproducesTheMajorityMap_And100EqualsTheDropCutter()
    {
        var context = TestContexts.BumpPlate();
        var majority = ReachMap.Compute(context.Model, context.Stock, context.Profile, context.Stock.Min());
        var same = ReachMap.Compute(context.Model, context.Stock, context.Profile, context.Stock.Min(), ReachMap.DefaultPercent);
        for (var k = 0; k < same.CellCount; k++)
        {
            Assert.True(float.IsNaN(majority.Z[k]) ? float.IsNaN(same.Z[k]) : majority.Z[k] == same.Z[k], $"cell {k} differs");
        }

        var full = ReachMap.Compute(context.Model, context.Stock, context.Profile, context.Stock.Min(), ReachMap.MaxPercent);
        var drop = HeightMapDilation.ComputeTipMap(context.Model, context.Profile);
        var floor = context.Stock.Min();
        for (var k = 0; k < full.CellCount; k++)
        {
            if (!float.IsNaN(full.Z[k]))
            {
                Assert.Equal(MathF.Max(drop.Z[k], floor), full.Z[k], 4);
            }
        }
    }

    [Fact]
    public void LowerPercent_NeverRaisesTheFloor()
    {
        var context = TestContexts.BumpPlate();
        var floor = context.Stock.Min();
        var previous = ReachMap.Compute(context.Model, context.Stock, context.Profile, floor, 100f);
        foreach (var percent in new[] { 80f, 50f, 30f, 10f })
        {
            var map = ReachMap.Compute(context.Model, context.Stock, context.Profile, floor, percent);
            for (var k = 0; k < map.CellCount; k++)
            {
                Assert.True(float.IsNaN(map.Z[k]) == float.IsNaN(previous.Z[k]));
                if (!float.IsNaN(map.Z[k]))
                {
                    Assert.True(map.Z[k] <= previous.Z[k] + 1e-6f, $"cell {k}: {map.Z[k]} above {previous.Z[k]} at {percent} percent");
                }
            }

            previous = map;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ReachMap.Compute(context.Model, context.Stock, context.Profile, floor, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReachMap.Compute(context.Model, context.Stock, context.Profile, floor, 101f));
    }

    [Fact]
    public void Project_CarriesThePercent_AndTheValidatorBoundsIt()
    {
        var project = MillingProject.Default();
        Assert.Equal(ReachMap.DefaultPercent, project.ReachPercent);
        Assert.True(ProjectValidator.Validate(project, null).IsValid);

        project.ReachPercent = 0f;
        Assert.Contains(ProjectValidator.Validate(project, null).Errors, e => e.Field == "Strategy.ReachPercent");
        project.ReachPercent = 100.5f;
        Assert.Contains(ProjectValidator.Validate(project, null).Errors, e => e.Field == "Strategy.ReachPercent");
        project.ReachPercent = 100f;
        Assert.True(ProjectValidator.Validate(project, null).IsValid);

        project.ReachPercent = 75f;
        var json = ProjectSerializer.Serialize(project);
        Assert.Contains("\"ReachPercent\": 75", json);
        Assert.Equal(75f, ProjectSerializer.Deserialize(json).ReachPercent);
        var legacy = System.Text.RegularExpressions.Regex.Replace(json, ",\\s*\"ReachPercent\": 75", string.Empty);
        Assert.DoesNotContain("ReachPercent", legacy);
        Assert.Equal(ReachMap.DefaultPercent, ProjectSerializer.Deserialize(legacy).ReachPercent);
    }
}
