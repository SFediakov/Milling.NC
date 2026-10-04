using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Simulation;

// T-144: every entered cell is reported and classified as model or stock contact; events keep the
// one-per-segment-and-kind rule of the simulation panel.
public sealed class CollisionRecorderTests
{
    private const float Cell = 0.5f;
    private const float Top = 10f;
    private const float SlotFloor = 2f;
    private const float PartTop = 6f;
    private const float Floor = 0f;
    private static readonly Vector3 InSlot = new(10.25f, 10.25f, SlotFloor);

    private static bool IsSlot(HeightMap map, int j) => map.CellCenter(0, j).Y is > 9f and < 11f;

    // 20 x 20 mm stock with a 2 mm wide, 8 mm deep slot along X at y in [9, 11].
    private static HeightMap Slot()
    {
        var stock = new HeightMap(0, 0, Cell, 40, 40, Top);
        for (var j = 0; j < stock.Height; j++)
        {
            for (var i = 0; i < stock.Width && IsSlot(stock, j); i++)
            {
                stock[i, j] = SlotFloor;
            }
        }

        return stock;
    }

    // The part stands PartTop high beside the slot and at the slot floor inside it.
    private static HeightMap Part(float height = PartTop)
    {
        var model = new HeightMap(0, 0, Cell, 40, 40, height);
        for (var j = 0; j < model.Height; j++)
        {
            for (var i = 0; i < model.Width && IsSlot(model, j); i++)
            {
                model[i, j] = SlotFloor;
            }
        }

        return model;
    }

    private static ToolProfile Profile(float cutterLength) => ToolProfile.Create(new ToolDefinition { CutterDiameter = 1.5f, CutterLength = cutterLength, HeadDiameter = 5f }, Cell);

    // Cells of the head ring at the tip whose stock stands above the head underside, by brute force.
    private static HashSet<(int, int)> EnteredByHead(HeightMap stock, ToolProfile profile, float cutterLength, Vector3 tip)
    {
        var (ci, cj) = stock.CellOf(tip.X, tip.Y);
        var cells = new HashSet<(int, int)>();
        foreach (var o in profile.AnnulusOffsets)
        {
            var (i, j) = (ci + o.Dx, cj + o.Dy);
            if (stock.InBounds(i, j) && stock[i, j] > tip.Z + cutterLength + o.Dz + CollisionDetector.Tolerance)
            {
                cells.Add((i, j));
            }
        }

        return cells;
    }

    [Fact]
    public void Detector_ReportsEveryEnteredHeadCell_AndTheSameEventAsWithoutAHandler()
    {
        var stock = Slot();
        var profile = Profile(5f);
        var seen = new HashSet<(int, int)>();
        var surfaces = new List<float>();
        var found = CollisionDetector.Check(stock, profile, 5f, InSlot, MoveKind.Feed, 7, (i, j, surface) =>
        {
            Assert.True(seen.Add((i, j)), $"cell {i}, {j} reported twice");
            surfaces.Add(surface);
        });

        var expected = EnteredByHead(stock, profile, 5f, InSlot);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, seen);
        Assert.All(surfaces, s => Assert.Equal(SlotFloor + 5f, s));
        Assert.Equal(CollisionDetector.Check(stock, profile, 5f, InSlot, MoveKind.Feed, 7), found);
    }

    [Fact]
    public void Detector_RapidEventWins_ButTheHeadCellsAreStillReported()
    {
        var stock = Slot();
        var profile = Profile(3f);
        var tip = new Vector3(5.25f, 5.25f, 5f);
        var count = 0;
        var found = CollisionDetector.Check(stock, profile, 3f, tip, MoveKind.Rapid, 2, (_, _, _) => count++);
        Assert.NotNull(found);
        Assert.Equal(SimulationEventKind.RapidIntoMaterial, found.Kind);
        Assert.Equal(CollisionDetector.Check(stock, profile, 3f, tip, MoveKind.Rapid, 2), found);
        Assert.Equal(profile.Offsets.Length + EnteredByHead(stock, profile, 3f, tip).Count, count);
    }

    [Fact]
    public void HeadAboveThePart_MarksStockOnly_HeadBelowThePartTop_MarksTheModel()
    {
        var stock = Slot();
        var above = new CollisionRecorder(Profile(5f), 5f, Part(), Floor);
        Assert.NotNull(above.Record(stock, new SimulationSample(3, MoveKind.Feed, InSlot)));
        Assert.Equal(EnteredByHead(stock, Profile(5f), 5f, InSlot).Count, above.Contacts.Count(c => c == CollisionContact.Stock));
        Assert.DoesNotContain(CollisionContact.Model, above.Contacts);
        Assert.Equal(0, above.ModelSegments);

        var below = new CollisionRecorder(Profile(3f), 3f, Part(), Floor);
        Assert.NotNull(below.Record(stock, new SimulationSample(3, MoveKind.Feed, InSlot)));
        Assert.Equal(EnteredByHead(stock, Profile(3f), 3f, InSlot).Count, below.Contacts.Count(c => c == CollisionContact.Model));
        Assert.Equal(1, below.ModelSegments);
    }

    [Fact]
    public void ModelAtTheFloor_IsNoModel()
    {
        var recorder = new CollisionRecorder(Profile(3f), 3f, Part(Floor), Floor);
        recorder.Record(Slot(), new SimulationSample(0, MoveKind.Feed, InSlot));
        Assert.Contains(CollisionContact.Stock, recorder.Contacts);
        Assert.DoesNotContain(CollisionContact.Model, recorder.Contacts);
    }

    [Fact]
    public void Events_OncePerSegmentAndKind_ModelOutranksStock_ClearResets()
    {
        var stock = Slot();
        var recorder = new CollisionRecorder(Profile(3f), 3f, Part(), Floor);
        var high = InSlot with { Z = SlotFloor + 2.5f };

        // The same cells first entered above the part top (stock), then below it (model).
        Assert.NotNull(recorder.Record(stock, new SimulationSample(4, MoveKind.Feed, high)));
        Assert.DoesNotContain(CollisionContact.Model, recorder.Contacts);
        Assert.Null(recorder.Record(stock, new SimulationSample(4, MoveKind.Feed, InSlot)));
        Assert.Contains(CollisionContact.Model, recorder.Contacts);
        var modelCells = recorder.Contacts.Count(c => c == CollisionContact.Model);
        Assert.Null(recorder.Record(stock, new SimulationSample(4, MoveKind.Feed, high)));
        Assert.Equal(modelCells, recorder.Contacts.Count(c => c == CollisionContact.Model));

        // A rapid below the slot floor enters the part under its footprint as well.
        Assert.NotNull(recorder.Record(stock, new SimulationSample(4, MoveKind.Rapid, InSlot with { Z = SlotFloor - 1f })));
        Assert.True(recorder.Contacts.Count(c => c == CollisionContact.Model) > modelCells);
        Assert.NotNull(recorder.Record(stock, new SimulationSample(5, MoveKind.Feed, high)));
        Assert.Null(recorder.Record(stock, new SimulationSample(6, MoveKind.Feed, InSlot with { Z = Top + 1f })));
        Assert.Equal(new[] { 4, 4, 5 }, recorder.Events.Select(e => e.SegmentIndex));
        Assert.Equal(1, recorder.ModelSegments);

        var report = recorder.Report();
        Assert.Equal(2, report.Segments);
        Assert.Equal(2, report.Count(SimulationEventKind.HeadCollision));
        Assert.Equal(1, report.Count(SimulationEventKind.RapidIntoMaterial));
        Assert.Equal(recorder.Contacts.Count(c => c == CollisionContact.Model), report.Cells(CollisionContact.Model));

        recorder.Clear();
        Assert.Empty(recorder.Events);
        Assert.Equal(0, recorder.ModelSegments);
        Assert.All(recorder.Contacts, c => Assert.Equal(CollisionContact.None, c));
        Assert.Equal(3, report.Events.Count);
        Assert.Contains(CollisionContact.Model, report.Contacts);
    }

    [Fact]
    public void StockOnAnotherGrid_IsRefused()
    {
        var recorder = new CollisionRecorder(Profile(3f), 3f, Part(), Floor);
        var other = new HeightMap(0, 0, Cell, 20, 20, Top);
        Assert.Throws<ArgumentException>(() => recorder.Record(other, new SimulationSample(0, MoveKind.Feed, InSlot)));
    }
}
