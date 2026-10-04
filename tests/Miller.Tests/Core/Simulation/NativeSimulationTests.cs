using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Miller.Application.Services;
using Miller.Core.Analysis;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Simulation;

// T-159: the sweep and the check run in the native library. Their results must equal the C# rules
// they replaced bit for bit; the C# code is kept here as the reference.
public sealed class NativeSimulationTests
{
    private const float Cell = 0.5f;

    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static HeightMap Stock(float top = 5f) => new(0, 0, Cell, 24, 24, top);

    private static ToolProfile Profile(TipType tip, float diameter, float headDiameter = 8f, float cutterLength = 6f)
        => ToolProfile.Create(new ToolDefinition { CutterDiameter = diameter, CutterLength = cutterLength, HeadDiameter = headDiameter, TipType = tip }, Cell);

    // The C# sweep before T-159.
    private static DirtyRect ReferenceSweep(HeightMap stock, ToolProfile profile, Vector3 from, Vector3 to)
    {
        var spacing = stock.CellSize / 2;
        var length = Vector3.Distance(from, to);
        var steps = length > 0 ? (int)MathF.Ceiling(length / spacing) : 0;
        var dirty = DirtyRect.Empty;
        for (var s = 0; s <= steps; s++)
        {
            var t = steps == 0 ? 0f : (float)s / steps;
            dirty = dirty.Union(ReferenceStamp(stock, profile, Vector3.Lerp(from, to, t)));
        }

        return dirty;
    }

    private static DirtyRect ReferenceStamp(HeightMap stock, ToolProfile profile, Vector3 tip)
    {
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        var dirty = DirtyRect.Empty;
        foreach (var offset in profile.Offsets)
        {
            var i = ci + offset.Dx;
            var j = cj + offset.Dy;
            if (!stock.InBounds(i, j))
            {
                continue;
            }

            var current = stock[i, j];
            var cut = tip.Z + offset.Dz;
            if (float.IsNaN(current) || !(cut < current))
            {
                continue;
            }

            stock[i, j] = cut;
            dirty = dirty.Include(i, j);
        }

        return dirty;
    }

    // The C# check before T-159: the first entered cell and every entered cell in offset order.
    private static (SimulationEventKind Kind, string Message)? ReferenceCheck(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segment, List<(int I, int J, float Surface)> entered)
    {
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        (SimulationEventKind, string)? found = null;
        float Cell(int i, int j) => stock.InBounds(i, j) ? stock[i, j] : float.NaN;
        if (kind == MoveKind.Rapid)
        {
            foreach (var o in profile.Offsets)
            {
                var z = Cell(ci + o.Dx, cj + o.Dy);
                var surface = tip.Z + o.Dz;
                if (z > surface + CollisionDetector.Tolerance)
                {
                    found ??= (SimulationEventKind.RapidIntoMaterial, CollisionDetector.RapidMessage(segment, tip, z));
                    entered.Add((ci + o.Dx, cj + o.Dy, surface));
                }
            }
        }

        var headBottom = tip.Z + cutterLength;
        foreach (var o in profile.AnnulusOffsets)
        {
            var z = Cell(ci + o.Dx, cj + o.Dy);
            var underside = headBottom + o.Dz;
            if (z > underside + CollisionDetector.Tolerance)
            {
                found ??= (SimulationEventKind.HeadCollision, CollisionDetector.HeadMessage(segment, tip, z, underside));
                entered.Add((ci + o.Dx, cj + o.Dy, underside));
            }
        }

        return found;
    }

    private static readonly (Vector3 From, Vector3 To)[] Parts =
    {
        (new Vector3(2.25f, 5.25f, 3f), new Vector3(9.25f, 5.25f, 3f)),
        (new Vector3(1.1f, 1.3f, 4.2f), new Vector3(10.7f, 8.9f, 2.6f)),
        (new Vector3(6.25f, 6.25f, 5f), new Vector3(6.25f, 6.25f, 1f)),
        (new Vector3(11.9f, 2.2f, 4.8f), new Vector3(0.1f, 11.6f, 4.3f)),
        (new Vector3(3.3f, 3.3f, 2f), new Vector3(3.3f, 3.3f, 2f)),
        (new Vector3(-1f, 6f, 1f), new Vector3(13f, 6f, 1f)),
    };

    [Theory]
    [InlineData(TipType.Flat, 2.2f)]
    [InlineData(TipType.Ball, 3f)]
    [InlineData(TipType.Flat, 6f)]
    public void Sweep_MatchesTheReference_BitForBit(TipType tip, float diameter)
    {
        var profile = Profile(tip, diameter);
        var native = Stock();
        var reference = Stock();
        native[3, 20] = float.NaN;
        reference[3, 20] = float.NaN;
        foreach (var (from, to) in Parts)
        {
            var dirtyNative = MaterialRemover.Sweep(native, profile, from, to);
            var dirtyReference = ReferenceSweep(reference, profile, from, to);
            Assert.Equal(dirtyReference, dirtyNative);
            Assert.Equal(reference.Z, native.Z);
        }

        Assert.Contains(native.Z, z => z < 5f);
        Assert.True(float.IsNaN(native[3, 20]));
        Assert.Equal(ReferenceStamp(reference, profile, new Vector3(5.6f, 7.1f, 0.5f)), MaterialRemover.Stamp(native, profile, new Vector3(5.6f, 7.1f, 0.5f)));
        Assert.Equal(reference.Z, native.Z);
    }

    [Fact]
    public void Check_MatchesTheReference_EventsAndEnteredCells()
    {
        var profile = Profile(TipType.Flat, 2f, headDiameter: 6f, cutterLength: 2f);
        var stock = Stock(5f);
        for (var j = 8; j < 16; j++)
        {
            for (var i = 8; i < 16; i++)
            {
                stock[i, j] = 1f;
            }
        }

        var tips = new[] { new Vector3(6f, 6f, 1f), new Vector3(2.25f, 10.25f, 4f), new Vector3(12.25f, 12.25f, 1.1f), new Vector3(12.25f, 12.25f, 6f), new Vector3(0.1f, 0.1f, 0.5f), new Vector3(30f, 30f, 0f) };
        var checks = 0;
        foreach (var tip in tips)
        {
            foreach (var kind in new[] { MoveKind.Rapid, MoveKind.Feed })
            {
                var entered = new List<(int I, int J, float Surface)>();
                var reference = ReferenceCheck(stock, profile, 2f, tip, kind, 7, entered);
                var seen = new List<(int I, int J, float Surface)>();
                var found = CollisionDetector.Check(stock, profile, 2f, tip, kind, 7, (i, j, s) => seen.Add((i, j, s)));
                Assert.Equal(reference?.Kind, found?.Kind);
                Assert.Equal(reference?.Message, found?.Message);
                Assert.Equal(entered, seen);
                if (found is not null)
                {
                    Assert.Equal(7, found.SegmentIndex);
                    Assert.Equal(tip, found.Position);
                    checks++;
                }
            }
        }

        Assert.True(checks >= 3, $"{checks} events found");
    }

    // The heart sample at 0.4 mm cells: the engine's run equals the reference sweep of every cutting
    // segment, the recorded events equal the reference check at the same samples, and the native run
    // is several times faster than the C# rules were.
    [Fact]
    public void Heart_RunToEnd_MatchesTheReference_AndIsFaster()
    {
        var samplePath = Path.Combine(RepoRoot, "samples", "heart.miller.json");
        var project = ProjectSerializer.Deserialize(File.ReadAllText(samplePath));
        project.Parameters.CellSize = 0.4f;
        var import = new MeshImportService();
        foreach (var model in project.Models)
        {
            import.Import(Path.Combine(Path.GetDirectoryName(samplePath)!, model.StlPath));
        }

        var result = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
        var profile = result.Profile;
        var cutterLength = profile.Tool.CutterLength;

        var nativeStock = result.Stock.Map.Clone();
        var engine = new SimulationEngine(result.Toolpath, nativeStock, profile);
        var recorder = new CollisionRecorder(profile, cutterLength, result.Model, result.Floor);
        var events = new List<SimulationEvent>();
        engine.Sampled += sample =>
        {
            if (recorder.Record(nativeStock, sample) is { } found)
            {
                events.Add(found);
            }
        };
        var nativeWatch = Stopwatch.StartNew();
        engine.RunToEnd(TestContext.Current.CancellationToken);
        nativeWatch.Stop();

        var referenceStock = result.Stock.Map.Clone();
        var referenceEvents = new List<(SimulationEventKind Kind, int Segment, Vector3 Tip, string Message)>();
        var reported = new HashSet<(int, SimulationEventKind)>();
        var contacts = new CollisionContact[result.Model.CellCount];
        var spacing = MathF.Max(profile.CellSize, profile.Tool.CutterRadius);
        var referenceWatch = Stopwatch.StartNew();
        for (var k = 0; k < result.Toolpath.Count; k++)
        {
            var s = result.Toolpath.Segments[k];
            if (s.Kind == MoveKind.Rapid)
            {
                var length = s.Length;
                var samples = new List<Vector3> { s.Start };
                for (var d = spacing; d < length; d += spacing)
                {
                    samples.Add(Vector3.Lerp(s.Start, s.End, Math.Clamp(d / length, 0f, 1f)));
                }

                samples.Add(s.End);
                foreach (var tip in samples)
                {
                    RecordReference(referenceStock, profile, cutterLength, tip, MoveKind.Rapid, k, result.Model, result.Floor, referenceEvents, reported, contacts);
                }

                continue;
            }

            ReferenceSweep(referenceStock, profile, s.Start, s.End);
            RecordReference(referenceStock, profile, cutterLength, s.End, s.Kind, k, result.Model, result.Floor, referenceEvents, reported, contacts);
        }

        referenceWatch.Stop();

        Assert.Equal(referenceStock.Z, nativeStock.Z);
        Assert.Equal(referenceEvents.Select(e => (e.Kind, e.Segment, e.Tip, e.Message)), events.Select(e => (e.Kind, e.SegmentIndex, e.Position, e.Message)));
        Assert.Equal(contacts, recorder.Contacts);

        var line = string.Create(CultureInfo.InvariantCulture, $"heart at 0.4 mm cells, {result.Toolpath.Count} segments: native {nativeWatch.ElapsedMilliseconds} ms, C# reference {referenceWatch.ElapsedMilliseconds} ms");

        // The sample at its own 0.2 mm cells, native only (the C# rules took about 20 s here).
        project.Parameters.CellSize = 0.2f;
        var full = new PipelineService().Run(project, import.Meshes, null, CancellationToken.None);
        var fullStock = full.Stock.Map.Clone();
        var fullWatch = Stopwatch.StartNew();
        new SimulationEngine(full.Toolpath, fullStock, full.Profile).RunToEnd(TestContext.Current.CancellationToken);
        fullWatch.Stop();
        line += string.Create(CultureInfo.InvariantCulture, $"{Environment.NewLine}heart at 0.2 mm cells, {full.Toolpath.Count} segments: native {fullWatch.ElapsedMilliseconds} ms");
        Directory.CreateDirectory(Path.Combine(RepoRoot, "out"));
        File.WriteAllText(Path.Combine(RepoRoot, "out", "simulation-timing.txt"), line + Environment.NewLine);
        Assert.True(nativeWatch.ElapsedMilliseconds * 3 < referenceWatch.ElapsedMilliseconds, line);
        Assert.True(fullWatch.ElapsedMilliseconds < 4000, line);
    }

    private static void RecordReference(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip, MoveKind kind, int segment, HeightMap model, float floor,
        List<(SimulationEventKind Kind, int Segment, Vector3 Tip, string Message)> events, HashSet<(int, SimulationEventKind)> reported, CollisionContact[] contacts)
    {
        var entered = new List<(int I, int J, float Surface)>();
        var found = ReferenceCheck(stock, profile, cutterLength, tip, kind, segment, entered);
        foreach (var (i, j, surface) in entered)
        {
            var k = model.Index(i, j);
            var m = model.Z[k];
            if (m > floor + FinalModelAnalyzer.FloorTolerance && m > surface + CollisionDetector.Tolerance)
            {
                contacts[k] = CollisionContact.Model;
            }
            else if (contacts[k] == CollisionContact.None)
            {
                contacts[k] = CollisionContact.Stock;
            }
        }

        if (found is { } f && reported.Add((segment, f.Kind)))
        {
            events.Add((f.Kind, segment, tip, f.Message));
        }
    }
}
