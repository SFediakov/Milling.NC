using Xunit;

namespace Miller.Tests.App
{
    // Guards the TrimPackageNativeAssets target in Directory.Build.props: the test output folder is
    // the same tree the app runs from, so it must hold only the supported runtimes and no native symbols.
    public sealed class BuildOutputTests
    {
        private static readonly string[] SupportedRuntimes = ["win-x64", "linux-x64"];

        private static string RuntimesDirectory => Path.Combine(AppContext.BaseDirectory, "runtimes");

        [Fact]
        public void RuntimesFolder_HoldsOnlySupportedRuntimes()
        {
            Assert.True(Directory.Exists(RuntimesDirectory), $"missing {RuntimesDirectory}");
            var present = Directory.GetDirectories(RuntimesDirectory).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(SupportedRuntimes.Order().ToArray(), present);
        }

        [Fact]
        public void RuntimesFolder_HoldsNoNativeSymbols()
        {
            var symbols = Directory.GetFiles(RuntimesDirectory, "*.pdb", SearchOption.AllDirectories);
            Assert.Empty(symbols);
        }

        [Fact]
        public void RuntimesFolder_HoldsSkiaForEverySupportedRuntime()
        {
            foreach (var runtime in SupportedRuntimes)
            {
                var native = Path.Combine(RuntimesDirectory, runtime, "native");
                Assert.True(Directory.Exists(native), $"missing {native}");
                Assert.NotEmpty(Directory.GetFiles(native, "libSkiaSharp.*"));
            }
        }
    }
}
