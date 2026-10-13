using System.Globalization;
using System.Text;

namespace Miller.Application.Services;

// The end-of-generation report of what the toolpath leaves uncut (T-173): the cells standing more than
// the tolerance above what the plan intended, and in Backwards the positions its check left unreached.
// It follows the collision summary in the summary window and the status bar.
public static class UncutReport
{
    public const string NothingLeftText = "Not cut: none, every cell the plan cuts ends within the tolerance of its planned surface.";

    public static string Summarize(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();
        var uncut = result.Uncut;
        text.Append(uncut.Cells == 0
            ? NothingLeftText
            : string.Create(CultureInfo.InvariantCulture,
                $"Not cut: {uncut.Cells} cells ({uncut.AreaMm2:0.00} mm2) stand more than the tolerance above the planned surface, {uncut.VolumeMm3:0.000} mm3 of material, at most {uncut.MaxHeight:0.000} mm."));
        if (result.Unreached is { } unreached)
        {
            text.Append(string.Create(CultureInfo.InvariantCulture,
                $"\nUnreached positions (Backwards): {unreached.Unreached} of {unreached.ModelCells} model cells, {unreached.Percent:0.00} %; the head cannot reach them without cutting the model."));
        }

        return text.ToString();
    }

    // Short form for the status bar.
    public static string StatusSuffix(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Create(CultureInfo.InvariantCulture, $"not cut: {result.Uncut.Cells} cells");
    }
}
