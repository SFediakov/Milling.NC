using System.Diagnostics;
using Miller.Application.Macros;
using Miller.Application.Services;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Machine.Links;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.Application;

// T-204: macros are kept in macros.json beside the settings with unique names, validated line by line
// before they are stored, read back after a restart, refused as a whole when the file is corrupt, and
// run through the machine gate as one command job with the macro's delay.
public sealed class MacroServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-macros-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void SaveRenameDelete_AreKeptInTheFile_AndReadBack()
    {
        var service = new MacroService(_root);
        service.Load();
        Assert.Empty(service.Macros);

        var park = service.Save(" Park ", 250, "G53 G0 Z-1\n\n  G53 G0 X-5 Y-5  \n", null);
        Assert.Equal(new MachineMacro("Park", 250, new[] { "G53 G0 Z-1", "G53 G0 X-5 Y-5" }).Name, park.Name);
        Assert.Equal(new[] { "G53 G0 Z-1", "G53 G0 X-5 Y-5" }, park.Lines);
        service.Save("Air", 0, "M8", null);
        Assert.Equal(new[] { "Air", "Park" }, service.Macros.Select(m => m.Name));
        Assert.Throws<ArgumentException>(() => service.Save("park", 0, "M9", null));

        service.Save("Park top", 100, "G53 G0 Z-1", "Park");
        Assert.Equal(new[] { "Air", "Park top" }, service.Macros.Select(m => m.Name));

        var again = new MacroService(_root);
        again.Load();
        Assert.Equal(new[] { "Air", "Park top" }, again.Macros.Select(m => m.Name));
        Assert.Equal(100, again.Find("park TOP")!.DelayMs);

        again.Delete("Air");
        var third = new MacroService(_root);
        third.Load();
        Assert.Equal(new[] { "Park top" }, third.Macros.Select(m => m.Name));
        Assert.Throws<ArgumentException>(() => third.Delete("Air"));
    }

    [Theory]
    [InlineData("", 0, "M8", "1 to 40 characters")]
    [InlineData("A name that is far longer than forty chars", 0, "M8", "1 to 40 characters")]
    [InlineData("x", -1, "M8", "0 to 60000 ms")]
    [InlineData("x", 60001, "M8", "0 to 60000 ms")]
    [InlineData("x", 0, "M8\nG0 X1 !", "Line 2 (G0 X1 !)")]
    [InlineData("x", 0, "  \n(only a comment)\n", "at least one command")]
    public void Create_RefusesWhatTheControllerCannotTake(string name, int delay, string text, string expected)
    {
        var refused = Assert.Throws<ArgumentException>(() => MacroService.Create(name, delay, text));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Contains("200 lines", Assert.Throws<ArgumentException>(() => MacroService.Create("x", 0, string.Join('\n', Enumerable.Repeat("M8", MacroService.MaxLines + 1)))).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"SchemaVersion\": 9, \"Macros\": [] }")]
    [InlineData("{ \"SchemaVersion\": 1, \"Macros\": [ { \"Name\": \"a\", \"DelayMs\": 0, \"Lines\": [\"M8\"] }, { \"Name\": \"A\", \"DelayMs\": 0, \"Lines\": [\"M9\"] } ] }")]
    [InlineData("{ \"SchemaVersion\": 1, \"Macros\": [ { \"Name\": \"a\", \"DelayMs\": 0, \"Lines\": [\"M8 ?\"] } ] }")]
    public void Load_RefusesACorruptFile(string text)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, MacroService.FileName), text);
        var service = new MacroService(_root);
        Assert.Throws<InvalidDataException>(service.Load);
        Assert.Empty(service.Macros);
    }

    [Fact]
    public async Task RunMacro_SendsTheLinesAsOneJob_WithTheDelay()
    {
        var link = new FakeGrblLink();
        using var machine = new MachineService(_ => link, new MachineTiming(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50, IdentifyTimeoutMs: 1000, WatchdogMs: 500, HoldTimeoutMs: 1000));
        var macro = MacroService.Create("Spin", 80, "M3 S5000\nM3 S9000 (faster)\nM5");
        Assert.Throws<InvalidOperationException>(() => machine.RunMacro(macro));
        await machine.ConnectAsync(new MachineConnectionSettings(MachineConnectionKind.Serial, "fake", SerialLink.DefaultBaudRate, string.Empty, TcpLink.DefaultPort));
        var watch = Stopwatch.StartNew();
        while (machine.Snapshot!.Status.State != GrblStatus.Idle)
        {
            Assert.True(watch.ElapsedMilliseconds < 10_000);
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        machine.RunMacro(macro);
        while (machine.Snapshot.Job is not { IsActive: false })
        {
            Assert.True(watch.ElapsedMilliseconds < 10_000);
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }

        Assert.Equal(new[] { "M3S5000", "M3S9000", "M5" }, link.Lines);
        Assert.Equal("Macro Spin", machine.Snapshot.Job.Name);
        Assert.True(machine.Snapshot.Job.Elapsed.TotalMilliseconds >= 2 * 80);
    }
}
