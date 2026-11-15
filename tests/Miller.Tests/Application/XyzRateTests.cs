using System.Numerics;
using System.Text.RegularExpressions;
using Miller.Application.Services;
using Miller.Application.Validation;
using Miller.Core.GCode;
using Miller.Core.Setup;
using Miller.Core.Toolpaths;
using Miller.Machine.Grbl;
using Miller.Solver;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-221 (user request): the milling and the travel rate each have an XY and a Z speed. A pure XY move runs
// at the XY rate, a pure Z move at the Z rate, any other move at the vector feed at which neither axis
// passes its rate and the move is not faster than the larger rate. Equal XY and Z rates give the former
// toolpath and file; old files take their XY rates for Z.
public sealed class XyzRateTests
{
    private const float MillXy = 600f;
    private const float MillZ = 150f;
    private const float TravelXy = 1200f;
    private const float TravelZ = 400f;

    // An axis may run this much faster than its rate by float rounding of the vector feed.
    private const float Slack = 1e-3f;

    private static MillingProject Project(float millXy, float millZ, float travelXy, float travelZ)
    {
        var project = MillingProject.Default();
        project.Parameters.FeedRate = millXy;
        project.Parameters.FeedRateZ = millZ;
        project.Parameters.TravelRate = travelXy;
        project.Parameters.TravelRateZ = travelZ;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Parameters.Stepover = 2f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    private static PipelineResult Run(MillingProject project) => new PipelineService().Run(project, new[] { TestMeshes.Box(10, 10, 5) }, null, CancellationToken.None);

    private static ToolpathSegment Move(Vector3 from, Vector3 to) => new(from, to, MoveKind.Feed, 1f);

    [Fact]
    public void ThePureMoves_TakeTheirAxisRate_AndNoAxisPassesItsRate()
    {
        Assert.Equal(800f, Move(Vector3.Zero, new Vector3(10, 3, 0)).RateAt(800, 200));
        Assert.Equal(200f, Move(Vector3.Zero, new Vector3(0, 0, -4)).RateAt(800, 200));

        // A 45 degree ramp is held by Z: 200 / sin 45 = 282.8 mm/min, 200 on each axis.
        var ramp = Move(Vector3.Zero, new Vector3(3, 0, -3));
        Assert.Equal(200f * MathF.Sqrt(2f), ramp.RateAt(800, 200), 2);

        // A gentle ramp runs at the XY rate, its Z part far below the Z rate.
        var gentle = Move(Vector3.Zero, new Vector3(10, 0, -1));
        Assert.Equal(800f, gentle.RateAt(800, 200));

        // With equal rates every direction runs exactly at that rate.
        foreach (var to in new[] { new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 1, 1), new Vector3(3, 0.2f, -7), new Vector3(0.001f, 0, 5) })
        {
            Assert.Equal(500f, Move(Vector3.Zero, to).RateAt(500, 500));
        }
    }

    [Fact]
    public void EveryMove_RunsAtTheVectorFeedOfItsRates_AndNoAxisPassesItsRate()
    {
        var project = Project(MillXy, MillZ, TravelXy, TravelZ);
        var result = Run(project);
        Assert.Empty(result.Collisions.Report!.Events);
        var moves = result.Toolpath.Segments.Where(s => s.Kind != MoveKind.Rapid).ToList();
        Assert.Contains(moves, s => s.FeedRate == MillXy);
        Assert.Contains(moves, s => s.FeedRate == MillZ);
        Assert.Contains(moves, s => s.FeedRate == TravelXy);
        foreach (var s in moves)
        {
            var milling = s.RateAt(MillXy, MillZ);
            var travel = s.RateAt(TravelXy, TravelZ);
            Assert.True(s.FeedRate == milling || s.FeedRate == travel, $"{s}");
            var (xy, z) = s.FeedRate == milling ? (MillXy, MillZ) : (TravelXy, TravelZ);
            var length = s.Length;
            var planar = Vector2.Distance(new Vector2(s.Start.X, s.Start.Y), new Vector2(s.End.X, s.End.Y));
            var vertical = MathF.Abs(s.End.Z - s.Start.Z);
            Assert.True(s.FeedRate * planar / length <= xy * (1 + Slack), $"XY too fast: {s}");
            Assert.True(s.FeedRate * vertical / length <= z * (1 + Slack), $"Z too fast: {s}");
        }

        // The file sets the milling XY rate first and every G1 runs at its segment's rate.
        var writer = new StringWriter();
        new GrblPostProcessor().Write(result.Toolpath, project, "Build_0.0.0", writer);
        var program = GrblProgram.Parse("box.nc", writer.ToString());
        var targets = GrblMotion.Walk(program).Where(step => step.Target).ToList();
        Assert.Equal(result.Toolpath.Count + 2, targets.Count);
        for (var k = 0; k < result.Toolpath.Count; k++)
        {
            if (result.Toolpath.Segments[k].Kind != MoveKind.Rapid)
            {
                Assert.Equal(result.Toolpath.Segments[k].FeedRate, (float)targets[k + 2].Feed);
            }
        }

        Assert.Equal(GCodeFormatter.Word('F', MillXy), writer.ToString().Split('\n').First(l => l.Contains('F')));
    }

    [Fact]
    public void EqualXyAndZRates_GiveOneFeedWord()
    {
        var project = Project(MillXy, MillXy, MillXy, MillXy);
        var result = Run(project);
        Assert.All(result.Toolpath.Segments.Where(s => s.Kind != MoveKind.Rapid), s => Assert.Equal(MillXy, s.FeedRate));
        var writer = new StringWriter();
        new GrblPostProcessor().Write(result.Toolpath, project, "Build_0.0.0", writer);
        Assert.Single(writer.ToString().Split('\n'), l => l.Contains('F'));
    }

    [Fact]
    public void OldFiles_TakeTheirXyRatesForZ_AndTheValidatorRefusesZRatesThatAreNotPositive()
    {
        var json = ProjectSerializer.Serialize(Project(MillXy, MillZ, TravelXy, TravelZ));
        var old = Regex.Replace(json, @"\s*""(FeedRateZ|TravelRateZ)"": [0-9.]+,", string.Empty);
        Assert.DoesNotContain("RateZ", old, StringComparison.Ordinal);
        var loaded = ProjectSerializer.Deserialize(old).Parameters;
        Assert.Equal(MillXy, loaded.FeedRateZ);
        Assert.Equal(TravelXy, loaded.TravelRateZ);
        var copy = ProjectSerializer.Deserialize(json).Parameters;
        Assert.Equal((MillZ, TravelZ), (copy.FeedRateZ, copy.TravelRateZ));

        foreach (var bad in new[] { 0f, -1f, float.NaN })
        {
            var errors = ProjectValidator.Validate(Project(MillXy, bad, TravelXy, bad), null).Errors;
            Assert.Contains(errors, e => e.Field == "Parameters.FeedRateZ");
            Assert.Contains(errors, e => e.Field == "Parameters.TravelRateZ");
        }
    }

    [Fact]
    public void TheWritersTravelChoice_TimesClimbsAndPlungesAtTheMillingZRate()
    {
        // A 9 mm ridge between two floor positions 4 mm apart under a 10 mm safe height. The surface route
        // climbs 9 and drops 9 mm at the Z rate; the retract rises 10 mm as a rapid (Z three times slower
        // than 3000) and plunges 10 mm at the Z rate. The surface wins only above about 976 mm/min of Z.
        var grid = new RouteGrid(new float[] { 0, 0, 9, 0, 0 }, 5, 1, 0, 0, 1f);
        Assert.Contains(Travel(grid, 800f), s => s.Kind == MoveKind.Rapid);
        Assert.DoesNotContain(Travel(grid, 2000f), s => s.Kind == MoveKind.Rapid);
    }

    private static List<ToolpathSegment> Travel(RouteGrid grid, float rateZ)
    {
        var parameters = TestContexts.Parameters();
        parameters.FeedRateZ = rateZ;
        var writer = new RouteWriter(parameters, 10f);
        writer.Travel(new RoutePoint(0.5f, 0.5f, 0f), grid);
        writer.Travel(new RoutePoint(4.5f, 0.5f, 0f), grid);
        return writer.Finish().Segments.Skip(1).SkipLast(1).ToList();
    }
}
