using System.Numerics;
using Avalonia.Media;

namespace Miller.App.Rendering;

// Reads colors from the application resources, so Styles/Colors.axaml stays the only place where
// colors are declared. Must be called on the UI thread.
public static class ThemeColors
{
    public static Vector4 Get(string key)
    {
        if (Resource(key) is not Color color)
        {
            throw new InvalidOperationException($"Color resource {key} is missing from Styles/Colors.axaml.");
        }

        return Vector(color);
    }

    // The stops of a gradient brush, offset and color, in the order the brush lists them.
    public static IReadOnlyList<(float Offset, Vector4 Color)> GetGradient(string key)
    {
        if (Resource(key) is not GradientBrush brush || brush.GradientStops.Count == 0)
        {
            throw new InvalidOperationException($"Gradient brush resource {key} is missing from Styles/Colors.axaml.");
        }

        return brush.GradientStops.Select(stop => ((float)stop.Offset, Vector(stop.Color))).ToArray();
    }

    private static object? Resource(string key)
    {
        var application = Avalonia.Application.Current ?? throw new InvalidOperationException("No Avalonia application is running.");
        return application.TryGetResource(key, application.ActualThemeVariant, out var value) ? value : null;
    }

    private static Vector4 Vector(Color color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
}
