using System.Globalization;
using Avalonia.Data.Converters;

namespace Miller.App.Converters;

// Slider position t in [0, 1] to speed factor 10^(-1 + 4 t) in [0.1, 1000] and back, so one slider
// travel covers four decades evenly. Values outside the range clamp at the ends.
public sealed class LogSliderConverter : IValueConverter
{
    public const float Min = 0.1f;
    public const float Max = 1000f;
    private const double Decades = 4;
    private const double LogMin = -1;

    public static double ToSlider(float speed)
    {
        var clamped = Math.Clamp(speed, Min, Max);
        return (Math.Log10(clamped) - LogMin) / Decades;
    }

    public static float ToSpeed(double slider)
    {
        var clamped = Math.Clamp(slider, 0, 1);
        return (float)Math.Pow(10, LogMin + Decades * clamped);
    }

    // Speed factor (float or double) to slider position.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            float f => ToSlider(f),
            double d => ToSlider((float)d),
            _ => ToSlider(Min),
        };

    // Slider position to speed factor.
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            double d => ToSpeed(d),
            float f => ToSpeed(f),
            _ => Min,
        };
}
