using Miller.Machine.Grbl;
using Xunit;

namespace Miller.Tests.Machine;

// T-142 C1/C3: every machine command is the documented Grbl line: jogs only through $J= in relative
// millimetres with a feed, zero through G10 L20 P0 on the active work system, the touch-plate probe
// as G38.2 then zero at the plate thickness then lift, the outline around the program's XY moves.
public sealed class GrblCommandsTests
{
    [Fact]
    public void Jog_IsARelativeMillimetreJogWithFeed()
    {
        Assert.Equal("$J=G91 G21 X1 F500", GrblCommands.Jog(MachineAxis.X, 1, 500));
        Assert.Equal("$J=G91 G21 Z-0.05 F100", GrblCommands.Jog(MachineAxis.Z, -0.05, 100));
        Assert.Equal("$J=G91 G21 Y12.346 F1500.5", GrblCommands.Jog(MachineAxis.Y, 12.3456, 1500.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.Jog(MachineAxis.X, 0, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.Jog(MachineAxis.X, GrblCommands.MaxJogDistance + 1, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.Jog(MachineAxis.X, double.NaN, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.Jog(MachineAxis.X, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.Jog(MachineAxis.X, 1, GrblCommands.MaxFeed + 1));
    }

    [Fact]
    public void Zero_SetsTheActiveWorkSystem()
    {
        Assert.Equal("G10 L20 P0 X0 Y0 Z0", GrblCommands.Zero(true, true, true));
        Assert.Equal("G10 L20 P0 Z0", GrblCommands.Zero(false, false, true));
        Assert.Equal("G10 L20 P0 X0 Y0", GrblCommands.Zero(true, true, false));
        Assert.Throws<ArgumentException>(() => GrblCommands.Zero(false, false, false));
        Assert.Equal("G90 G0 X0 Y0", GrblCommands.GoToXyZero);
    }

    [Fact]
    public void ProbeZ_ProbesZeroesAtThePlateAndLifts()
    {
        Assert.Equal(
            new[] { "G21 G91 G38.2 Z-20 F50", "G10 L20 P0 Z15.2", "G0 Z2", "G90" },
            GrblCommands.ProbeZ(15.2, 20, 50, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.ProbeZ(-1, 20, 50, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.ProbeZ(0, 0, 50, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.ProbeZ(0, 20, 50, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.ProbeZ(0, 20, -5, 2));
    }

    [Fact]
    public void Walk_FollowsRelativeMovesInchesAndSkipsMachineCoordinates()
    {
        var steps = GrblMotion.Walk(GrblProgram.Parse("p", "G20 G90 G0 X1 Y1\nG91 G1 X1 F10\nG53 G0 X-300 Y-300\nG21 G90 G0 X0 Y0\n"));
        var ends = steps.Where(s => s.HasXY).Select(s => s.End).ToList();
        Assert.Equal(3, ends.Count);
        Assert.Equal(25.4, ends[0].X, 9);
        Assert.Equal(25.4, ends[0].Y, 9);
        Assert.Equal(50.8, ends[1].X, 9);
        Assert.Equal(25.4, ends[1].Y, 9);
        Assert.Equal(steps[1].End, steps[2].End);
        Assert.Equal(new GrblPoint(0, 0, 0), ends[2]);
        Assert.DoesNotContain(GrblMotion.Walk(GrblProgram.Parse("p", "G0 Z5\nM3 S1000\n")), s => s.HasXY);
    }
}
