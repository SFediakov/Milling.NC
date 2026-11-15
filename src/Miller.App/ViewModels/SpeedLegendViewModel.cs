using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Miller.Core.Toolpaths;

namespace Miller.App.ViewModels;

// The speed legend at the left of the viewport (T-224): the speeds of the scale the shown path is colored
// on, in mm/min, from the slowest (first) to the fastest (last) at equal steps, or the one speed of a path
// whose moves all run alike. Shown only while the path is.
public sealed partial class SpeedLegendViewModel : ViewModelBase
{
    public const int LabelCount = 5;

    // Five significant digits keep every label short, also for an absurd F word of a file.
    public const string LabelFormat = "G5";

    [ObservableProperty]
    private bool _isShown;

    [ObservableProperty]
    private bool _isSingleSpeed;

    [ObservableProperty]
    private IReadOnlyList<string> _labels = Array.Empty<string>();

    public void Show(ToolpathSpeedScale? scale, bool pathShown)
    {
        IsShown = pathShown && scale is not null;
        IsSingleSpeed = scale is { IsSingleSpeed: true };
        var labels = scale is null ? Array.Empty<string>() : scale.Ticks(LabelCount).Select(Format).ToArray();
        // A new list resets the items control; publish one only when the text changed.
        if (!labels.SequenceEqual(Labels))
        {
            Labels = labels;
        }
    }

    public static string Format(float speed) => speed.ToString(LabelFormat, CultureInfo.InvariantCulture);
}
