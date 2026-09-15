using System.Numerics;
using Avalonia.Media;

namespace Miller.App.Rendering;

// Reads colors from the application resources, so Styles/Colors.axaml stays the only place where
// colors are declared. Must be called on the UI thread.
public static class ThemeColors
{
    public static Vector4 Get(string key)
    {
        var application = Avalonia.Application.Current ?? throw new InvalidOperationException("No Avalonia application is running.");
        if (!application.TryGetResource(key, application.ActualThemeVariant, out var value) || value is not Color color)
        {
            throw new InvalidOperationException($"Color resource {key} is missing from Styles/Colors.axaml.");
        }

        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }
}
