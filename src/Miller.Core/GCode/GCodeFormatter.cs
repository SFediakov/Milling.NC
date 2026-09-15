using System.Globalization;

namespace Miller.Core.GCode;

// Number formatting for G-code: invariant culture, three decimals, trailing zeros trimmed, negative
// zero normalized to 0.
public static class GCodeFormatter
{
    public const int Decimals = 3;

    private const string Pattern = "0.###";

    public static string Format(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException($"G-code numbers must be finite, got {value}.", nameof(value));
        }

        var rounded = MathF.Round(value, Decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0)
        {
            rounded = 0;
        }

        var text = rounded.ToString(Pattern, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public static string Word(char letter, float value) => letter + Format(value);
}
