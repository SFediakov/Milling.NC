using Avalonia;
using Avalonia.Headless;
using Miller.App;
using Xunit;

namespace Miller.Tests
{
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<Miller.App.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions());
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
