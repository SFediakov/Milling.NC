using Miller.Machine.Grbl;
using Xunit;

namespace Miller.Tests.Machine;

// T-198: the work coordinate panel and the zero buttons build the same G10 L20 P0 line; only the named
// axes are written, and a value that is no number or lies beyond the limit is refused before sending.
public sealed class GrblSetWorkTests
{
    [Fact]
    public void SetWork_WritesTheNamedAxesOnly()
    {
        Assert.Equal("G10 L20 P0 X12.5 Y-3 Z0.25", GrblCommands.SetWork(12.5, -3, 0.25));
        Assert.Equal("G10 L20 P0 Y7", GrblCommands.SetWork(null, 7, null));
        Assert.Equal("G10 L20 P0 Z0.001", GrblCommands.SetWork(null, null, 0.0012));
        Assert.Equal(GrblCommands.SetWork(0, 0, 0), GrblCommands.Zero(true, true, true));
        Assert.Equal(GrblCommands.SetWork(null, null, 0), GrblCommands.Zero(false, false, true));
    }

    [Fact]
    public void SetWork_RefusesNoAxisAndValuesThatAreNoCoordinate()
    {
        Assert.Throws<ArgumentException>(() => GrblCommands.SetWork(null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.SetWork(double.NaN, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.SetWork(null, double.PositiveInfinity, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => GrblCommands.SetWork(null, null, GrblCommands.MaxWorkCoordinate + 1));
        Assert.Equal("G10 L20 P0 X-100000", GrblCommands.SetWork(-GrblCommands.MaxWorkCoordinate, null, null));
    }
}
