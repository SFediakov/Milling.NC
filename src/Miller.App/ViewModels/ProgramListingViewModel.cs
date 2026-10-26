using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Machine;
using Miller.Machine.Grbl;

namespace Miller.App.ViewModels;

public enum ProgramRowState
{
    Pending,
    Executed,
    Executing,
}

// One row of the program listing: the line number in the file and the line as written.
public sealed partial class ProgramRowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExecuted), nameof(IsExecuting))]
    private ProgramRowState _state;

    public ProgramRowViewModel(int number, string text)
    {
        Number = number;
        Text = text;
    }

    public int Number { get; }

    public string Text { get; }

    public bool IsExecuted => State == ProgramRowState.Executed;

    public bool IsExecuting => State == ProgramRowState.Executing;
}

// The machine program row by row for the listing window. The executing row is the line of the move the
// tool is on (ExecutionTracker), or the last acknowledged line before the first move and once the run
// is done. A refresh changes only the rows between the old and the new executing row, so a long program
// costs nothing while the run moves on.
public sealed partial class ProgramListingViewModel : ViewModelBase
{
    public const string NoProgramText = "No program";
    public const string NotStartedText = "Not started";

    [ObservableProperty]
    private string _title = NoProgramText;

    [ObservableProperty]
    private string _description = NoProgramText;

    [ObservableProperty]
    private IReadOnlyList<ProgramRowViewModel> _rows = Array.Empty<ProgramRowViewModel>();

    // Index into Rows, -1 while nothing ran.
    [ObservableProperty]
    private int _currentRow = -1;

    // The window keeps the executing row in view while this is on.
    [ObservableProperty]
    private bool _follow = true;

    private GrblProgram? _program;

    // job: the progress of this program's latest run, null when it has not run; the executing row is the
    // last acknowledged line.
    public void Show(GrblProgram? program, JobProgress? job)
        => Show(program, job, program is null || job is not { Acknowledged: > 0 } ? -1 : Math.Min(job.Acknowledged, program.Count) - 1);

    // executingLine: index into the program's lines, -1 while nothing ran.
    public void Show(GrblProgram? program, JobProgress? job, int executingLine)
    {
        if (!ReferenceEquals(program, _program))
        {
            _program = program;
            CurrentRow = -1;
            Rows = program is null ? Array.Empty<ProgramRowViewModel>() : RowsOf(program.Text);
            Title = program is null ? NoProgramText : $"{program.Name}, {Rows.Count} rows";
        }

        var current = program is null || job is null || executingLine < 0 ? -1 : program.SourceLines[Math.Min(executingLine, program.Count - 1)] - 1;
        Apply(current);
        Description = program is null ? NoProgramText
            : current < 0 ? NotStartedText
            : $"{Prefix(job!.State)} line {current + 1} of {Rows.Count}: {Rows[current].Text.Trim()}";
    }

    private void Apply(int current)
    {
        var previous = CurrentRow;
        if (current == previous)
        {
            return;
        }

        for (var k = Math.Max(0, Math.Min(previous, current)); k <= Math.Max(previous, current); k++)
        {
            Rows[k].State = k < current ? ProgramRowState.Executed : k == current ? ProgramRowState.Executing : ProgramRowState.Pending;
        }

        CurrentRow = current;
    }

    private static string Prefix(JobState state) => state switch
    {
        JobState.Running => "Executing",
        JobState.Stopping => "Stopping at",
        JobState.Stopped => "Stopped at",
        JobState.Failed => "Failed at",
        _ => "Finished at",
    };

    private static ProgramRowViewModel[] RowsOf(string text)
    {
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var rows = new ProgramRowViewModel[count];
        for (var k = 0; k < count; k++)
        {
            rows[k] = new ProgramRowViewModel(k + 1, lines[k].TrimEnd('\r'));
        }

        return rows;
    }
}
