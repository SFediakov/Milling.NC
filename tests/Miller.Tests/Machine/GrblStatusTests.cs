using Miller.Machine.Grbl;
using Xunit;

namespace Miller.Tests.Machine;

// T-141/T-142: status reports give machine and work position through the work offset (WPos = MPos -
// WCO, whichever of the two the controller sends), carry the offset and the overrides that Grbl
// sends only now and then, and read the hold sub-state that tells a finished hold from a braking one.
public sealed class GrblStatusTests
{
    [Fact]
    public void MachinePosition_GivesWorkPositionThroughTheOffset()
    {
        var status = GrblStatus.Parse("<Idle|MPos:10.000,20.000,30.000|FS:500,12000|WCO:1.000,2.000,3.000>", GrblStatus.Unknown);
        Assert.Equal(GrblStatus.Idle, status.State);
        Assert.Equal(GrblStatus.NoValue, status.SubState);
        Assert.Equal(new Axes(10, 20, 30), status.MachinePosition);
        Assert.Equal(new Axes(9, 18, 27), status.WorkPosition);
        Assert.Equal(500, status.Feed);
        Assert.Equal(12000, status.Spindle);
        Assert.True(status.IsAtRest);
    }

    [Fact]
    public void WorkPosition_GivesMachinePosition()
    {
        var status = GrblStatus.Parse("<Jog|WPos:1.000,2.000,3.000|F:250|WCO:1.000,1.000,1.000>", GrblStatus.Unknown);
        Assert.Equal(new Axes(2, 3, 4), status.MachinePosition);
        Assert.Equal(new Axes(1, 2, 3), status.WorkPosition);
        Assert.Equal(250, status.Feed);
        Assert.False(status.IsAtRest);
    }

    [Fact]
    public void OffsetAndOverrides_AreCarriedFromThePreviousReport()
    {
        var first = GrblStatus.Parse("<Run|MPos:5.000,5.000,5.000|FS:800,10000|WCO:1.000,2.000,3.000|Ov:110,50,90>", GrblStatus.Unknown);
        var second = GrblStatus.Parse("<Run|MPos:6.000,5.000,5.000|FS:800,10000>", first);
        Assert.Equal(new Axes(1, 2, 3), second.WorkOffset);
        Assert.Equal(new Axes(5, 3, 2), second.WorkPosition);
        Assert.Equal((110, 50, 90), (second.FeedOverride, second.RapidOverride, second.SpindleOverride));
        Assert.Equal((100, 100, 100), (GrblStatus.Unknown.FeedOverride, GrblStatus.Unknown.RapidOverride, GrblStatus.Unknown.SpindleOverride));
    }

    [Fact]
    public void HoldSubState_BuffersLinePinsAndAccessories()
    {
        var done = GrblStatus.Parse("<Hold:0|MPos:0.000,0.000,0.000|Bf:15,128|Ln:42|F:100|Pn:XZ|A:SF>", GrblStatus.Unknown);
        Assert.Equal(GrblStatus.Hold, done.State);
        Assert.Equal(GrblStatus.HoldComplete, done.SubState);
        Assert.True(done.IsAtRest);
        Assert.Equal((15, 128, 42), (done.PlannerBlocksFree, done.ReceiveBytesFree, done.LineNumber));
        Assert.Equal("XZ", done.Pins);
        Assert.Equal("SF", done.Accessories);

        var braking = GrblStatus.Parse("<Hold:1|MPos:0.000,0.000,0.000>", done);
        Assert.False(braking.IsAtRest);
        Assert.Equal(string.Empty, braking.Pins);
    }

    [Fact]
    public void ExtraAxes_AreIgnored_AndNonReportsRejected()
    {
        var status = GrblStatus.Parse("<Idle|MPos:1.000,2.000,3.000,4.000|FS:0,0>", GrblStatus.Unknown);
        Assert.Equal(new Axes(1, 2, 3), status.MachinePosition);
        Assert.Throws<FormatException>(() => GrblStatus.Parse("ok", GrblStatus.Unknown));
        Assert.Throws<FormatException>(() => GrblStatus.Parse("<Idle|MPos:1.000,2.000>", GrblStatus.Unknown));
    }

    [Fact]
    public void Codes_AreDescribed()
    {
        Assert.Contains("Unsupported or invalid g-code", GrblCodes.DescribeError("error:20"), StringComparison.Ordinal);
        Assert.Equal("error 99", GrblCodes.DescribeError("error:99"));
        Assert.Equal("error:Bad number format", GrblCodes.DescribeError("error:Bad number format"));
        Assert.Contains("Hard limit", GrblCodes.DescribeAlarm("ALARM:1"), StringComparison.Ordinal);
        Assert.Contains("Probe did not contact", GrblCodes.DescribeAlarm("ALARM:5"), StringComparison.Ordinal);
    }
}
