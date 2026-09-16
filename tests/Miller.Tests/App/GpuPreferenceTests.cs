using Microsoft.Win32;
using Miller.App.Services;
using Xunit;

namespace Miller.Tests.App;

public sealed class GpuPreferenceTests
{
    // A root of its own, deleted as a tree after the test, so no key the app may use is touched.
    private const string TestRoot = @"Software\MillerTests";
    private const string TestKey = TestRoot + @"\UserGpuPreferences";

    [Fact]
    public void RegistersTheExecutableOnce_UnderATemporaryKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(GpuPreferenceOutcome.NotWindows, GpuPreference.EnsureHighPerformance(@"C:\miller\Miller.exe", TestKey));
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"miller-gpu-{Guid.NewGuid():N}");
        var exe = Path.Combine(folder, "Miller.exe");
        var chosen = Path.Combine(folder, "Chosen.exe");
        const string powerSaving = "GpuPreference=1;";
        try
        {
            Assert.Equal(GpuPreferenceOutcome.Set, GpuPreference.EnsureHighPerformance(exe, TestKey));
            Assert.Equal(GpuPreferenceOutcome.Kept, GpuPreference.EnsureHighPerformance(exe, TestKey));
            using (var key = Registry.CurrentUser.OpenSubKey(TestKey, writable: true))
            {
                Assert.Equal(GpuPreference.HighPerformance, key!.GetValue(exe));
                key.SetValue(chosen, powerSaving);
            }

            // A choice the user made in the graphics settings is not overridden.
            Assert.Equal(GpuPreferenceOutcome.Kept, GpuPreference.EnsureHighPerformance(chosen, TestKey));
            using var check = Registry.CurrentUser.OpenSubKey(TestKey);
            Assert.Equal(powerSaving, check!.GetValue(chosen));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void RejectsEmptyArguments()
    {
        Assert.Throws<ArgumentException>(() => GpuPreference.EnsureHighPerformance(string.Empty, TestKey));
        Assert.Throws<ArgumentException>(() => GpuPreference.EnsureHighPerformance(@"C:\miller\Miller.exe", " "));
        Assert.Equal(@"Software\Microsoft\DirectX\UserGpuPreferences", GpuPreference.UserPreferencesKey);
    }
}
