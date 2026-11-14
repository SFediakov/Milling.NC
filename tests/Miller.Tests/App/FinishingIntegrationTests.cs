using System.Text;
using Miller.Application.Services;
using Miller.Core.Geometry;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Miller.Tests.Core.GCode;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-223 (user request): with one or more finishing passes the program first runs exactly as with none, and the
// passes follow at its end over the final layer only. Project to exported file: the file with passes is the
// 0-pass file byte for byte up to that file's final retract, then one line per segment of the passes, their
// final retract and the same end lines. The generation passes and what their checks counted for the roughing
// are those of the 0-pass run; the passes cut no deeper than the final layer and reach its lowest level.
public sealed class FinishingIntegrationTests
{
    private const string TestVersion = "Build_0.0.0";

    // M5 and M30.
    private const int EndLines = 2;

    // Removal below this is float noise (CollisionDetector.Tolerance).
    private const float Noise = 1e-4f;

    // Milling and travel rates that differ per axis, so the roughing's G1 lines carry F words that the
    // comparison covers.
    private const float FeedRate = 300f;
    private const float FeedRateZ = 150f;
    private const float TravelRate = 1500f;
    private const float TravelRateZ = 600f;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string SampleProjectPath => Path.Combine(RepoRoot, "samples", "heart.miller.json");

    private static MillingProject Box(int passes, FinishingPattern pattern)
    {
        var project = MillingProject.Default();
        project.Parameters.FeedRate = FeedRate;
        project.Parameters.FeedRateZ = FeedRateZ;
        project.Parameters.TravelRate = TravelRate;
        project.Parameters.TravelRateZ = TravelRateZ;
        project.Parameters.FinishingPasses = passes;
        project.Parameters.FinishingPattern = pattern;
        project.Stock.SizeX = 40;
        project.Stock.SizeY = 40;
        project.Stock.SizeZ = 5;
        project.Parameters.CellSize = 0.5f;
        project.Parameters.Stepover = 2f;
        project.Models.Add(new ModelPlacement { StlPath = "box.stl" });
        return project;
    }

    // The 4 mm box on the floor of a 9 mm stock: the far stepdown has room above it.
    private static MillingProject TallStock(int passes, CutScope scope, float farStepdown, CollisionMode mode)
    {
        var project = Box(passes, FinishingPattern.CrossRows);
        project.CutScope = scope;
        project.CollisionMode = mode;
        project.Stock.SizeZ = 9;
        project.Parameters.FarStepdown = farStepdown;
        project.Parameters.Stepover = 1f;
        return project;
    }

    private static (PipelineResult Result, byte[] File) Generate(MillingProject project, IReadOnlyList<Mesh> meshes)
    {
        var result = new PipelineService().Run(project, meshes, null, CancellationToken.None);
        var directory = Path.Combine(Path.GetTempPath(), $"miller-finishing-{Guid.NewGuid():N}");
        try
        {
            var written = new ExportService().Export(result.Toolpath, project, TestVersion, Path.Combine(directory, "part.nc"));
            return (result, File.ReadAllBytes(written));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static MillingProject Heart(int passes, FinishingPattern pattern)
    {
        var project = ProjectSerializer.Deserialize(File.ReadAllText(SampleProjectPath));
        project.Parameters.FinishingPasses = passes;
        project.Parameters.FinishingPattern = pattern;
        return project;
    }

    private static IReadOnlyList<Mesh> HeartMeshes(MillingProject project)
    {
        var import = new MeshImportService();
        foreach (var model in project.Models)
        {
            import.Import(Path.Combine(Path.GetDirectoryName(SampleProjectPath)!, model.StlPath));
        }

        return import.Meshes;
    }

    private static List<int> LineStarts(byte[] file)
    {
        var starts = new List<int>();
        for (var k = 0; k < file.Length; k++)
        {
            if (k == 0 || file[k - 1] == (byte)'\n')
            {
                starts.Add(k);
            }
        }

        return starts;
    }

    private static string LineAt(byte[] file, int offset)
    {
        var start = offset;
        while (start > 0 && file[start - 1] != (byte)'\n')
        {
            start--;
        }

        var end = Array.IndexOf(file, (byte)'\n', start);
        return Encoding.UTF8.GetString(file, start, (end < 0 ? file.Length : end) - start);
    }

    private static int SameBytes(byte[] a, byte[] b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n])
        {
            n++;
        }

        return n;
    }

    private static float MostRemoved(HeightMap before, HeightMap after)
    {
        var most = 0f;
        for (var j = 0; j < before.Height; j++)
        {
            for (var i = 0; i < before.Width; i++)
            {
                most = MathF.Max(most, before[i, j] - after[i, j]);
            }
        }

        return most;
    }

    // `reference` is a 0-pass program (`plain` the run that wrote it, for its segment count and safe Z): every
    // byte before its final retract starts `finished`, then come the passes, their final retract and the end.
    private static void AssertTheSameUntilThePasses(byte[] reference, PipelineResult plain, byte[] finished, PipelineResult result)
    {
        var referenceLines = LineStarts(reference);
        var finishedLines = LineStarts(finished);
        var head = referenceLines.Count - plain.Toolpath.Count - EndLines;
        var retract = plain.Toolpath.Segments[^1];
        Assert.Equal((MoveKind.Rapid, plain.SafeZ), (retract.Kind, retract.End.Z));

        var roughingEnd = referenceLines[head + plain.Toolpath.Count - 1];
        var same = SameBytes(reference, finished);
        Assert.True(same >= roughingEnd,
            $"the program parts from the 0-pass one at line {referenceLines.Count(s => s <= same)} of {referenceLines.Count}: '{LineAt(reference, same)}' against '{LineAt(finished, same)}'");

        // Every segment before the passes is the 0-pass segment, rate included; after them the passes only.
        Assert.Equal(plain.Toolpath.Count - 1, result.FinishingFirstSegment);
        Assert.Equal(plain.Toolpath.Segments.Take(result.FinishingFirstSegment), result.Toolpath.Segments.Take(result.FinishingFirstSegment));
        Assert.True(result.Toolpath.Count - result.FinishingFirstSegment > 1, "the passes must move before their final retract");
        Assert.Equal(head + result.Toolpath.Count + EndLines, finishedLines.Count);
        Assert.Equal((MoveKind.Rapid, result.SafeZ), (result.Toolpath.Segments[^1].Kind, result.Toolpath.Segments[^1].End.Z));
        Assert.Equal(reference[referenceLines[^EndLines]..], finished[finishedLines[^EndLines]..]);
    }

    // The generation passes, their far stepdown and what their checks counted come from the roughing: the
    // passes added to the kept pass steer none of them.
    private static void AssertTheSameGeneration(PipelineResult plain, PipelineResult result)
    {
        Assert.Equal(plain.Passes, result.Passes);
        Assert.Equal(plain.PassCollisions.Select(p => (p.EnteredCells, p.FarCells, p.WithdrawnCells)), result.PassCollisions.Select(p => (p.EnteredCells, p.FarCells, p.WithdrawnCells)));
        Assert.Equal(plain.Collisions.Report!.Events.Select(e => (e.Kind, e.SegmentIndex, e.Position)),
            result.Collisions.Report!.Events.Where(e => e.SegmentIndex < result.FinishingFirstSegment).Select(e => (e.Kind, e.SegmentIndex, e.Position)));
    }

    // From the stock the roughing leaves, the passes remove at most the simplifier's slack and go down to the
    // lowest level the roughing cut.
    private static void AssertOnlyTheFinalLayer(PipelineResult plain, PipelineResult result)
    {
        var stock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, stock, result.Profile);
        engine.SeekToSegment(result.FinishingFirstSegment);
        var rough = stock.Clone();
        engine.RunToEnd(TestContext.Current.CancellationToken);
        Assert.True(MostRemoved(rough, stock) <= result.Tolerance + Noise, $"the passes removed {MostRemoved(rough, stock)} mm below the final layer");
        var lowest = plain.Toolpath.Segments.Where(s => s.Kind != MoveKind.Rapid).Min(s => s.End.Z);
        Assert.Equal(lowest, result.Toolpath.Segments.Skip(result.FinishingFirstSegment).Where(s => s.Kind != MoveKind.Rapid).Min(s => s.End.Z), 4);
    }

    // Returns the 0-pass program.
    private static byte[] AssertFinishingFollowsThePlainProgram(MillingProject plainProject, MillingProject finishedProject, IReadOnlyList<Mesh> meshes)
    {
        var (plain, plainFile) = Generate(plainProject, meshes);
        var (result, file) = Generate(finishedProject, meshes);
        AssertTheSameUntilThePasses(plainFile, plain, file, result);
        AssertTheSameGeneration(plain, result);
        AssertOnlyTheFinalLayer(plain, result);
        return plainFile;
    }

    [Theory]
    [InlineData(1, FinishingPattern.ShiftedRows)]
    [InlineData(1, FinishingPattern.CrossRows)]
    [InlineData(1, FinishingPattern.DiagonalRows)]
    [InlineData(2, FinishingPattern.ShiftedRows)]
    [InlineData(2, FinishingPattern.CrossRows)]
    [InlineData(2, FinishingPattern.DiagonalRows)]
    [InlineData(5, FinishingPattern.ShiftedRows)]
    [InlineData(5, FinishingPattern.CrossRows)]
    [InlineData(5, FinishingPattern.DiagonalRows)]
    public void EveryPassCountAndPattern_FollowsTheZeroPassProgram(int passes, FinishingPattern pattern)
    {
        var plainFile = AssertFinishingFollowsThePlainProgram(Box(0, pattern), Box(passes, pattern), new[] { TestMeshes.Box(10, 10, 5) });

        // The compared roughing carries F words: it has feeds without material contact and moves with a Z part.
        Assert.Contains(Encoding.UTF8.GetString(plainFile).Split('\n'), l => l.StartsWith("G1", StringComparison.Ordinal) && l.Contains(" F", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CutScope.Everything, 0f, CollisionMode.Backwards)]
    [InlineData(CutScope.Separation, 0f, CollisionMode.Backwards)]
    [InlineData(CutScope.Everything, 4f, CollisionMode.Backwards)]
    [InlineData(CutScope.Separation, 4f, CollisionMode.Backwards)]
    [InlineData(CutScope.Everything, 4f, CollisionMode.Backwards_GPU)]
    public void EveryCutScope_FarStepdownAndCollisionMode_FollowsTheZeroPassProgram(CutScope scope, float farStepdown, CollisionMode mode)
    {
        Assert.SkipWhen(mode == CollisionMode.Backwards_GPU && !Miller.Core.Native.GpuDevice.Available, "No OpenCL GPU.");
        AssertFinishingFollowsThePlainProgram(TallStock(0, scope, farStepdown, mode), TallStock(2, scope, farStepdown, mode), new[] { TestMeshes.Box(10, 10, 4) });
    }

    // The heart sample against its golden file, which is the 0-pass program of the sample.
    [Theory]
    [InlineData(1, FinishingPattern.ShiftedRows)]
    [InlineData(2, FinishingPattern.CrossRows)]
    public void TheHeartSample_FollowsItsGoldenFile(int passes, FinishingPattern pattern)
    {
        var plainProject = Heart(0, pattern);
        var meshes = HeartMeshes(plainProject);
        var (plain, _) = Generate(plainProject, meshes);
        var (result, file) = Generate(Heart(passes, pattern), meshes);
        var golden = File.ReadAllBytes(GrblPostProcessorTests.GoldenPath("heart_grbl.nc"));

        // The version comment line differs from the golden file's on purpose (EndToEndTests); it is left out on both sides.
        var goldenBody = golden[(Array.IndexOf(golden, (byte)'\n') + 1)..];
        var body = file[(Array.IndexOf(file, (byte)'\n') + 1)..];
        AssertTheSameUntilThePasses(goldenBody, plain, body, result);
        AssertTheSameGeneration(plain, result);
        AssertOnlyTheFinalLayer(plain, result);
    }
}
