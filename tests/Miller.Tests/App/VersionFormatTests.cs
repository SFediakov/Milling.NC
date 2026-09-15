using Avalonia;
using Avalonia.Headless;
using Miller.App;
using Xunit;

namespace Miller.Tests
{
    public static class TestAppBuilder
    {
        // Real Skia rendering so tests can capture frames of the views (RenderCaptureTests).
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<Miller.App.App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

namespace Miller.Tests.App
{
    public sealed class VersionFormatTests
    {
        public const string VersionPattern = @"^Build_\d+\.\d+\.\d+$";

        [Fact]
        public void AppVersion_MatchesBuildFormat()
        {
            Assert.Matches(VersionPattern, Program.AppVersion);
        }
    }
}
