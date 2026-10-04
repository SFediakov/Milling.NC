using System.Globalization;
using System.Text;
using Miller.Core.Simulation;

namespace Miller.Application.Services;

// Report is null exactly when the check failed; Error then carries the reason.
public sealed record CollisionCheck(CollisionReport? Report, string? Error)
{
    public bool Succeeded => Report is not null;
}

// The gate of the collision check cluster (T-147): the dynamic check runs inside the native
// generation over the stock as every pass leaves it; this class turns its report into the summary
// window text and the status bar suffix. A generation that fails inside the check fails as a whole,
// so a check of a finished generation always succeeded.
public static class CollisionService
{
    public const string StageName = "collision check";
    public const int MaxListedEvents = 5;
    public const string NoCollisionsText = "No collisions: the head and the rapid moves stay clear of the stock and the model.";

    public static CollisionCheck Of(CollisionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new CollisionCheck(report, null);
    }

    // Text of the summary window: the counts per kind, the model split and the first events.
    public static string Summarize(CollisionCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.Report is not { } report)
        {
            return $"The collision check failed: {check.Error}\nThe toolpath is kept; the analysis shows no collision marks.";
        }

        if (report.Events.Count == 0)
        {
            return NoCollisionsText;
        }

        var text = new StringBuilder();
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Collisions in {report.Segments} segments of the toolpath.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Head touches material: {report.Count(SimulationEventKind.HeadCollision)} segments.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Rapid move into material: {report.Count(SimulationEventKind.RapidIntoMaterial)} segments.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Touching the model: {report.ModelSegments} segments, {report.Cells(CollisionContact.Model)} cells.\n"));
        text.Append(string.Create(CultureInfo.InvariantCulture, $"Stock only: {report.Cells(CollisionContact.Stock)} cells.\n"));
        text.Append("The Analysis tab marks the cells.\n\nFirst collisions:\n");
        foreach (var e in report.Events.Take(MaxListedEvents))
        {
            text.Append(e.Message).Append('\n');
        }

        if (report.Events.Count > MaxListedEvents)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture, $"... and {report.Events.Count - MaxListedEvents} more.\n"));
        }

        return text.ToString().TrimEnd('\n');
    }

    // Short form for the status bar.
    public static string StatusSuffix(CollisionCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.Report is { } report
            ? string.Create(CultureInfo.InvariantCulture, $"collisions: {report.Events.Count}")
            : "collision check failed";
    }
}
