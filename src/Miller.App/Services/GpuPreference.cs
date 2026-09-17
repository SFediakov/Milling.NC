using Microsoft.Win32;

namespace Miller.App.Services;

public enum GpuPreferenceOutcome
{
    NotWindows,
    Kept,
    Set,
}

// Windows chooses the adapter for a process from the user's graphics preferences (Settings >
// Display > Graphics), keyed by the executable path. The app registers itself for the high
// performance GPU there when no entry exists, so a machine with an integrated and a discrete GPU
// renders the viewport on the discrete one from the next start on. An existing entry is the
// user's own choice and stays. Linux uses DRI_PRIME=1 from the launcher instead.
public static class GpuPreference
{
    public const string UserPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string HighPerformance = "GpuPreference=2;";

    public static GpuPreferenceOutcome EnsureHighPerformance(string executablePath) => EnsureHighPerformance(executablePath, UserPreferencesKey);

    public static GpuPreferenceOutcome EnsureHighPerformance(string executablePath, string subKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        if (!OperatingSystem.IsWindows())
        {
            return GpuPreferenceOutcome.NotWindows;
        }

        return Register(executablePath, subKey);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static GpuPreferenceOutcome Register(string executablePath, string subKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($"Cannot open HKCU\\{subKey}.");
        if (key.GetValue(executablePath) is string existing && existing.Length > 0)
        {
            return GpuPreferenceOutcome.Kept;
        }

        key.SetValue(executablePath, HighPerformance, RegistryValueKind.String);
        return GpuPreferenceOutcome.Set;
    }
}
