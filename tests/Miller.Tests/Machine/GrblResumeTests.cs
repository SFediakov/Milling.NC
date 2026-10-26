using Miller.Machine.Grbl;
using Xunit;

namespace Miller.Tests.Machine;

// T-200: the resume program drops the moves of the executed lines (acknowledged minus two) and keeps
// their settings, retracts to the highest Z of the whole program, goes to the XY of the first remaining
// move, restores units and modes, and continues exactly where the original continued; whatever it
// could not reproduce is refused with the line.
public sealed class GrblResumeTests
{
    private const string Square =
        "( Miller Build_1 )\n" +
        "G21 G90 G94 G17\n" +
        "S10000 M3\n" +
        "G0 Z5\n" +
        "G0 X0 Y0\n" +
        "G1 X0 Y0 Z-1 F200\n" +
        "G1 X10 Y0 Z-1 F800\n" +
        "G1 X10 Y10 Z-1\n" +
        "G1 X0 Y10 Z-1\n" +
        "G0 X0 Y10 Z5\n" +
        "G0 X20 Y20 Z5\n" +
        "G1 X20 Y20 Z-2 F200\n" +
        "G1 X30 Y20 Z-2 F800\n" +
        "M5\n" +
        "M30\n";

    private static GrblProgram Parse(string text) => GrblProgram.Parse("p.nc", text);

    [Fact]
    public void Build_DropsExecutedMoves_KeepsSettings_AndRetractsBeforeTheFirstRemainingMove()
    {
        // 8 acknowledged lines: the last two of them (G1 X10 Y10, G1 X0 Y10) run again.
        var text = GrblResume.Build(Parse(Square), 8);
        Assert.Equal(
            "( Miller Build_1 )\n" +
            "G21 G90 G94 G17\n" +
            "S10000 M3\n" +
            "F200\n" +
            "F800\n" +
            "G21 G90 G0 Z5\n" +
            "G0 X10 Y10\n" +
            "G1 X10 Y10 Z-1\n" +
            "G1 X0 Y10 Z-1\n" +
            "G0 X0 Y10 Z5\n" +
            "G0 X20 Y20 Z5\n" +
            "G1 X20 Y20 Z-2 F200\n" +
            "G1 X30 Y20 Z-2 F800\n" +
            "M5\n" +
            "M30\n",
            text);
    }

    [Fact]
    public void Build_TheRemainingLines_ReachTheOriginalPositionsAndModes()
    {
        var programs = new[]
        {
            Square,
            "G20 G90\nG0 Z0.5\nG0 X0 Y0\nG1 Z-0.1 F10\nX1\nX2\nY1\nX0\nM30\n",
            "G90 G0 Z5\nG0 X0 Y0\nG91 G1 X1 F100\nX1\nG90 X5 Z-1\nX6\nY3\nM30\n",
            "G0 Z3\nG0 X1 Y1\nG1 Z-1 F100\nX5\nY5\nX1\nG0 Z3\nM30\n",
        };
        var built = 0;
        foreach (var text in programs)
        {
            var original = Parse(text);
            var steps = GrblMotion.Walk(original);
            for (var executed = 0; executed <= original.Count; executed++)
            {
                string resumed;
                try
                {
                    resumed = GrblResume.Build(original, executed);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                built++;
                var again = GrblMotion.Walk(Parse(resumed));
                var cut = Math.Max(0, executed - GrblResume.ReplayedLines);
                var resume = Enumerable.Range(cut, original.Count - cut).First(k => steps[k].Target);
                var tail = original.Count - resume;
                for (var k = 0; k < tail; k++)
                {
                    var expected = steps[resume + k];
                    var actual = again[again.Count - tail + k];
                    var where = $"{text} executed {executed}, line {resume + k}";
                    Assert.True(Near(expected.End, actual.End), where);
                    Assert.True(expected.Motion == actual.Motion && expected.Inches == actual.Inches && expected.Incremental == actual.Incremental, where);
                    Assert.True(Math.Abs(expected.Feed - actual.Feed) < 1e-9, where);
                }
            }
        }

        Assert.True(built >= 20, $"only {built} resume programs were built");
    }

    [Fact]
    public void Build_RestoresUnitsAndDistance_WritesTheModalMotion_AndAddsAMissingZ()
    {
        // Resume at "X1": inches, G1 modal, no Z word of its own (Z stays -0.1 in).
        var inches = GrblResume.Build(Parse("G20 G90\nG0 Z0.5\nG0 X0 Y0\nG1 Z-0.1 F10\nX1\nX2\n"), 6);
        Assert.Equal("G20 G90\nF10\nG21 G90 G0 Z12.7\nG0 X25.4 Y0\nG20\nG1 X1 Z-0.1\nX2\n", inches);

        // Resume at "G90 X5 Z-1" after G91 lines: G91 is restored before it, the line sets G90 itself.
        var relative = GrblResume.Build(Parse("G90 G0 Z5\nG0 X0 Y0\nG91 G1 X1 F100\nX1\nG90 X5 Z-1\nX6\n"), 6);
        Assert.Equal("G90\nG91 F100\nG21 G90 G0 Z5\nG0 X5 Y0\nG91\nG1 G90 X5 Z-1\nX6\n", relative);
    }

    [Fact]
    public void Build_KeepsCommentsCrLfAndOffsetsByTable_AndStartsAtTheTopBelowTwoLines()
    {
        var text = "%\r\n(setup)\r\nG10 L2 P1 X-100 Y-50\r\nG54 G21 G90\r\nG0 Z8 ; clear\r\nG0 X1 Y2\r\nG1 Z-1 F300\r\nX3 (cut)\r\nX5\r\n%\r\n";
        var resumed = GrblResume.Build(Parse(text), 7);
        Assert.Equal("%\r\n(setup)\r\nG10 L2 P1 X-100 Y-50\r\nG54 G21 G90\r\nF300\r\nG21 G90 G0 Z8\r\nG0 X3 Y2\r\nG1 X3 Z-1\r\nX5\r\n%\r\n", resumed);

        // Fewer than three lines acknowledged: nothing counts as executed.
        var top = Parse("G21 G90\nG0 X0 Y0 Z5\nG1 Z-1 F100\nG1 X5\n");
        Assert.Equal("G21 G90\nG21 G90 G0 Z5\nG0 X0 Y0\nG0 X0 Y0 Z5\nG1 Z-1 F100\nG1 X5\n", GrblResume.Build(top, 0));
        Assert.Equal(GrblResume.Build(top, 0), GrblResume.Build(top, 2));
    }

    [Fact]
    public void Build_DropsHomingAndJogLines_AndKeepsOtherSystemLines()
    {
        var resumed = GrblResume.Build(Parse("$H\n$J=G91 X1 F100\n$X\nG90 G0 Z2\nG0 X1 Y1\nG1 Z0 F50\nG1 X2 Z0\nG1 X3 Z0\n"), 8);
        Assert.Equal("$X\nG90\nF50\nG21 G90 G0 Z2\nG0 X2 Y1\nG1 X2 Z0\nG1 X3 Z0\n", resumed);
    }

    [Theory]
    [InlineData("G0 Z5\nG0 X1 Y1\nG1 Z-1 F100\nM5\nM30\n", 5, GrblResume.NothingLeftMessage)]
    [InlineData("G0 X1 Y1\nG1 X2 F100\nG1 X3\nG1 X4\n", 4, GrblResume.NoZMessage)]
    [InlineData("G90 G0 Z5 X0 Y0\nG92 X0 Y0\nG1 X1 F100 Z-1\nG1 X2\nG1 X3\nG1 X4\n", 6, "Line 2 (G92X0Y0)")]
    [InlineData("G90 G0 Z5 X0 Y0\nG10 L20 P0 X0\nG1 X1 F100 Z-1\nG1 X2\nG1 X3\nG1 X4\n", 6, "Line 2 (G10L20P0X0)")]
    [InlineData("G91 G0 X5 Y5 Z1\nG1 X1 F100\nG1 X1\nG90 G1 Z-1\nG1 Z-2\n", 5, "line 1 (G91G0X5Y5Z1) depends on where the program started")]
    [InlineData("G90 G0 Z5 X0\nG91 G1 Y1 F100\nG90 G1 X1 Z-1\nG1 X2\nG1 X3\n", 5, "position of Line 4 (G1X2) depends on where the program started")]
    [InlineData("G90 G0 Z5\nG0 X0 Y0\nG1 Z-1 F100\nG2 X10 Y0 I5 J0\nG2 X0 Y0 I-5 J0\n", 5, "Line 4 (G2X10Y0I5J0) is an arc")]
    [InlineData("G90 G0 Z5\nG0 X0 Y0\nG91 G1 X1 F100\nX1\nX1\n", 5, "Line 4 (X1) is an arc, a relative (G91) move")]
    [InlineData("G90 G0 Z5\nG0 X0 Y0\nG1 Z-1 F100\nG53 G0 Z-2\nG0 X1 Y1 Z5\n", 5, "Line 4 (G53G0Z-2)")]
    public void Build_RefusesWhatItCannotReproduce(string text, int executed, string expected)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => GrblResume.Build(Parse(text), executed));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RefusesAnExecutedCountOutsideTheProgram()
    {
        var program = Parse(Square);
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblResume.Build(program, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblResume.Build(program, program.Count + 1));
    }

    private static bool Near(GrblPoint a, GrblPoint b)
        => Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Y - b.Y) < 1e-3 && Math.Abs(a.Z - b.Z) < 1e-3;
}
