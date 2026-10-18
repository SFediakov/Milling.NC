using Miller.Machine.Grbl;
using Xunit;

namespace Miller.Tests.Machine;

// T-199: the one modal walk over a program follows G90/G91, G20/G21, the motion mode and the feed,
// keeps the position on lines that are no work-coordinate target, and knows which axes are absolute.
public sealed class GrblMotionTests
{
    private static IReadOnlyList<GrblStep> Walk(string text) => GrblMotion.Walk(GrblProgram.Parse("p", text));

    [Fact]
    public void Walk_FollowsModesUnitsAndFeed()
    {
        var steps = Walk("G21 G90\nG0 Z5\nG0 X10 Y5\nG1 Z-1 F300\nX30\nG91 Y2\nG20 G90 X1\nG2 X2 Y2 I1 J0 F10\nG80\n");
        Assert.Equal(9, steps.Count);
        Assert.False(steps[0].Target);
        Assert.Equal(new GrblPoint(0, 0, 5), steps[1].End);
        Assert.True(steps[1].HasZ);
        Assert.False(steps[1].HasXY);
        Assert.Equal(GrblMotionMode.Rapid, steps[2].Motion);
        Assert.Equal(GrblMotionMode.Linear, steps[3].Motion);
        Assert.Equal(300, steps[3].Feed);
        Assert.Equal(new GrblPoint(30, 5, -1), steps[4].End);
        Assert.False(steps[4].MotionWord);
        Assert.Equal(GrblMotionMode.Linear, steps[4].Motion);
        Assert.True(steps[5].Incremental);
        Assert.Equal(new GrblPoint(30, 7, -1), steps[5].End);
        Assert.True(steps[6].Inches);
        Assert.Equal(25.4, steps[6].End.X, 9);
        Assert.Equal(GrblMotionMode.ArcClockwise, steps[7].Motion);
        Assert.Equal(10 * 25.4, steps[7].Feed, 9);
        Assert.Equal(new GrblPoint(50.8, 50.8, -1), steps[7].End with { X = Math.Round(steps[7].End.X, 9), Y = Math.Round(steps[7].End.Y, 9) });
        Assert.Equal(GrblMotionMode.Cancel, steps[8].Motion);
        Assert.Equal(steps[7].End, steps[8].Start);
    }

    [Fact]
    public void Walk_KeepsThePosition_OnOffsetAndMachineLines_AndMarksUnknownAxes()
    {
        var steps = Walk("G90 G0 X1 Y2 Z3\nG53 G0 Z-1\nG92 X0\nG28\nG0 X5 Y6 Z7\nG38.2 Z-10 F50\n$H\n");
        Assert.True(steps[0].KnownX && steps[0].KnownY && steps[0].KnownZ);
        Assert.True(steps[1].MachineMove);
        Assert.False(steps[1].Target);
        Assert.Equal(steps[0].End, steps[1].End);
        Assert.False(steps[1].KnownZ);
        Assert.True(steps[1].KnownX);
        Assert.False(steps[2].Target);
        Assert.False(steps[2].KnownX);
        Assert.True(steps[3].MachineMove);
        Assert.False(steps[3].KnownX || steps[3].KnownY || steps[3].KnownZ);
        Assert.True(steps[4].KnownX && steps[4].KnownY && steps[4].KnownZ);
        Assert.Equal(GrblMotionMode.Probe, steps[5].Motion);
        Assert.True(steps[5].Target);
        Assert.False(steps[5].KnownZ);
        Assert.False(steps[6].Target);
        Assert.Equal(steps[5].End, steps[6].End);
    }

    [Fact]
    public void Walk_RelativeMovesBeforeAnAbsoluteOne_AreUnknown()
    {
        var steps = Walk("G91 G0 X5 Z1\nG90 X2\nG91 X1\n");
        Assert.False(steps[0].KnownX);
        Assert.False(steps[0].KnownZ);
        Assert.True(steps[1].KnownX);
        Assert.True(steps[2].KnownX);
        Assert.Equal(3, steps[2].End.X);
        Assert.False(steps[2].KnownZ);
    }

    [Fact]
    public void Words_LocateEveryPairInTheLine()
    {
        var line = "G38.2Z-10.500F50";
        var words = GrblMotion.Words(line);
        Assert.Equal(new[] { "G38.2", "Z-10.500", "F50" }, words.Select(w => line.Substring(w.Start, w.Length)));
        Assert.Equal(38.2, words[0].Value);
        Assert.True(GrblMotion.IsProbe(words[0].Value));
        Assert.Equal(line.Length, words.Sum(w => w.Length));
    }
}
