using System.Numerics;

namespace Miller.App.Rendering;

// Colors along the stops of a gradient brush (ThemeColors.GetGradient), mixed the way the brush draws
// them: linearly between the two stops around a fraction, the first stop's color before it and the last
// stop's color after it. The toolpath lines take their speed colors from the brush the speed legend
// draws, so both show one color for one speed (T-224).
public sealed class ColorGradient
{
    private readonly (float Offset, Vector4 Color)[] _stops;

    public ColorGradient(IReadOnlyList<(float Offset, Vector4 Color)> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count == 0)
        {
            throw new ArgumentException("A color gradient needs at least one stop.", nameof(stops));
        }

        if (stops.Any(stop => !float.IsFinite(stop.Offset)))
        {
            throw new ArgumentException("Every stop of a color gradient needs a finite offset.", nameof(stops));
        }

        // OrderBy is stable: stops at one offset keep the order the brush lists them in.
        _stops = stops.OrderBy(stop => stop.Offset).ToArray();
    }

    public IReadOnlyList<(float Offset, Vector4 Color)> Stops => _stops;

    public Vector4 At(float fraction)
    {
        if (!(fraction > _stops[0].Offset))
        {
            return _stops[0].Color;
        }

        for (var k = 1; k < _stops.Length; k++)
        {
            var (offset, color) = _stops[k];
            if (fraction > offset)
            {
                continue;
            }

            if (fraction == offset)
            {
                return color;
            }

            var (previousOffset, previousColor) = _stops[k - 1];
            return Vector4.Lerp(previousColor, color, (fraction - previousOffset) / (offset - previousOffset));
        }

        return _stops[^1].Color;
    }
}
