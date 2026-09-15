using System.Numerics;
using Miller.Core.HeightMaps;
using Miller.Core.Setup;
using Miller.Core.Simulation;
using Miller.Core.Toolpaths;
using Xunit;

namespace Miller.Tests.Core.Simulation;

public sealed class CollisionDetectorTests
{
    private const float Cell = 0.5f;
    private const float Top = 10f;
    private const float SlotFloor = 2f;

    // 20 x 20 mm stock with a 2 mm wide, 8 mm deep slot along X at y in [9, 11].
    private static HeightMap Slot()
    {
        var stock = new HeightMap(0, 0, Cell, 40, 40, Top);
        for (var j = 0; j < stock.Height; j++)
        {
            var y = stock.CellCenter(0, j).Y;
            if (y > 9f && y < 11f)
            {
                for (var i = 0; i < stock.Width; i++)
                {
                    stock[i, j] = SlotFloor;
                }
            }
        }

        return stock;
    }

    private static ToolProfile Profile(float cutterLength) => ToolProfile.Create(new ToolDefinition { CutterDiameter = 1.5f, CutterLength = cutterLength, HeadDiameter = 5f }, Cell);

    [Fact]
    public void ShortCutterInTheSlot_HitsWithTheHead()
    {
        var profile = Profile(5f);
        var found = CollisionDetector.Check(Slot(), profile, 5f, new Vector3(10.25f, 10.25f, SlotFloor), MoveKind.Feed, 7);
        Assert.NotNull(found);
        Assert.Equal(SimulationEventKind.HeadCollision, found.Kind);
        Assert.Equal(7, found.SegmentIndex);
        Assert.Equal(new Vector3(10.25f, 10.25f, SlotFloor), found.Position);
        Assert.Contains("segment 7", found.Message);
    }

    [Fact]
    public void LongCutterInTheSlot_IsClear()
    {
        var profile = Profile(20f);
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, new Vector3(10.25f, 10.25f, SlotFloor), MoveKind.Feed, 0));
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, new Vector3(10.25f, 10.25f, SlotFloor), MoveKind.Plunge, 0));
    }

    [Fact]
    public void RapidBelowTheStock_IsReported_FeedIsNot()
    {
        var profile = Profile(20f);
        var tip = new Vector3(5.25f, 5.25f, 5f);
        var found = CollisionDetector.Check(Slot(), profile, 20f, tip, MoveKind.Rapid, 3);
        Assert.NotNull(found);
        Assert.Equal(SimulationEventKind.RapidIntoMaterial, found.Kind);
        Assert.Equal(3, found.SegmentIndex);
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, tip, MoveKind.Feed, 3));
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, tip, MoveKind.Plunge, 3));
    }

    [Fact]
    public void RapidOnOrAboveTheSurface_IsClear()
    {
        var profile = Profile(20f);
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, new Vector3(5.25f, 5.25f, Top), MoveKind.Rapid, 0));
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, new Vector3(5.25f, 5.25f, Top + 5), MoveKind.Rapid, 0));
        // Inside the slot the footprint sits on the slot floor; the head is far above the top.
        Assert.Null(CollisionDetector.Check(Slot(), profile, 20f, new Vector3(10.25f, 10.25f, SlotFloor), MoveKind.Rapid, 0));
    }

    [Fact]
    public void OutsideTheGridAndEmptyCells_NeverCollide()
    {
        var stock = Slot();
        stock[0, 0] = float.NaN;
        var profile = Profile(5f);
        Assert.Null(CollisionDetector.Check(stock, profile, 5f, new Vector3(-30f, -30f, 0f), MoveKind.Rapid, 0));
        Assert.Null(CollisionDetector.Check(new HeightMap(0, 0, Cell, 2, 2, float.NaN), profile, 5f, new Vector3(0.25f, 0.25f, -5f), MoveKind.Rapid, 0));
    }
}
