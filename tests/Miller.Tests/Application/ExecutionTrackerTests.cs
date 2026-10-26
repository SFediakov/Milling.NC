using System.Numerics;
using Miller.App.ViewModels;
using Miller.Application.Services;
using Miller.Machine;
using Miller.Machine.Grbl;
using Xunit;

namespace Miller.Tests.Application;

// T-205: the move the tool is on comes from the reported position among the segments between the one
// found last and the end of the acknowledged lines: acknowledged moves the tool has not reached stay
// ahead, a point passed twice counts at its first pass, nothing moves back during a run, a new run
// starts again, a finished run is complete; the listing names the line of that move.
public sealed class ExecutionTrackerTests
{
    // Segments: 0 up to Z5, 1 to X0 Y0 (zero length), 2 plunge, 3 along X, 4 along Y, 5 up the column at
    // X10 Y10, 6 down the same column, 7 back along X.
    private const string Text = "G21 G90\nG0 Z5\nG0 X0 Y0\nG1 Z-1 F100\nG1 X10\nG1 Y10\nG1 Z5\nG1 Z-1\nG1 X0\nM30\n";

    private static JobProgress Job(int acknowledged, long start = 1, JobState state = JobState.Running)
        => new(MachineJobKind.Program, "p", state, 11, acknowledged, acknowledged, acknowledged, start, 0, null);

    [Fact]
    public void Tracker_FollowsThePosition_InsideTheAcknowledgedWindow_AndNeverBack()
    {
        var program = GrblProgram.Parse("p", Text);
        var path = ProgramPath.Of(program);
        Assert.Equal(8, path.Segments.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Enumerable.Range(0, 8).Select(path.LineOf));
        var tracker = new ExecutionTracker();

        Assert.Equal(0, tracker.Executing(path, null, Vector3.Zero));
        Assert.Equal(0, tracker.Executing(path, Job(0), new Vector3(5, 0, -1)));

        // Every line acknowledged, the tool halfway along X: the moves after it are not done.
        Assert.Equal(3, tracker.Executing(path, Job(10), new Vector3(5, 0, -1)));

        // On the column the up and the down move pass the same point: the first pass counts.
        Assert.Equal(5, tracker.Executing(path, Job(10), new Vector3(10, 10, 2)));
        Assert.Equal(5, tracker.Executing(path, Job(10), new Vector3(10, 10, 2)));
        Assert.Equal(7, tracker.Executing(path, Job(10), new Vector3(5, 10, -1)));

        // A position from an earlier move does not move it back; no position keeps it.
        Assert.Equal(7, tracker.Executing(path, Job(10), new Vector3(0, 0, -0.5f)));
        Assert.Equal(7, tracker.Executing(path, Job(10), null));

        // A new run starts from the first segment; the window ends at the acknowledged lines.
        Assert.Equal(3, tracker.Executing(path, Job(5, start: 2), new Vector3(10, 10, 0)));
        Assert.Equal(path.Segments.Count, tracker.Executing(path, Job(11, start: 2, state: JobState.Done), new Vector3(10, 10, 0)));
    }

    [Fact]
    public void Listing_NamesTheLineOfTheExecutingMove()
    {
        var program = GrblProgram.Parse("p", "(head)\n" + Text);
        var listing = new ProgramListingViewModel();
        listing.Show(program, Job(10), 4);
        Assert.Equal(5, listing.CurrentRow);
        Assert.Equal("Executing line 6 of 11: G1 X10", listing.Description);
        Assert.True(listing.Rows[5].IsExecuting);
        Assert.True(listing.Rows[4].IsExecuted);
        Assert.Equal(ProgramRowState.Pending, listing.Rows[6].State);

        listing.Show(program, Job(10), -1);
        Assert.Equal(-1, listing.CurrentRow);
        Assert.Equal(ProgramListingViewModel.NotStartedText, listing.Description);
    }
}
