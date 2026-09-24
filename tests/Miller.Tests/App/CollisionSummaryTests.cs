using System.Numerics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Miller.App.Services;
using Miller.App.ViewModels;
using Miller.Application.Services;
using Miller.Core.Analysis;
using Miller.Core.Simulation;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-145: the collision summary opens once after every finished generation and closes with OK.
// T-146: the collision cells of the generation's check are laid over the analysis in their own colors.
public sealed class CollisionSummaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"miller-collisions-{Guid.NewGuid():N}");
    private readonly FakeFileDialogService _dialogs = new();
    private readonly FakeErrorDialogService _errors = new();
    private readonly FakeConfirmDialogService _confirm = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private async Task<MainWindowViewModel> CreateWithBoxAsync()
    {
        Directory.CreateDirectory(_root);
        var stl = Path.Combine(_root, "box.stl");
        File.WriteAllText(stl, TestMeshes.AsciiCubeText());
        var vm = TestServices.MainWindowViewModel(_root, _dialogs, _errors, _confirm);
        _dialogs.OpenResults.Enqueue(stl);
        await vm.OpenStlCommand.ExecuteAsync(null);
        vm.Stock.SizeX = 10;
        vm.Stock.SizeY = 10;
        vm.Stock.SizeZ = 3;
        vm.Cutting.CellSize = 0.5f;
        return vm;
    }

    [Fact]
    public async Task Generate_ShowsTheSummaryOnce_WithTheCheckOfTheResult()
    {
        var vm = await CreateWithBoxAsync();
        await vm.GenerateCommand.ExecuteAsync(null);

        var (title, message) = Assert.Single(_confirm.Notices);
        Assert.Equal(MainWindowViewModel.CollisionSummaryTitle, title);
        var check = new CollisionService().Run(vm.LastResult!, null, CancellationToken.None);
        Assert.Equal(CollisionService.Summarize(check), message);
        Assert.EndsWith(CollisionService.StatusSuffix(check), vm.StatusText);
        Assert.StartsWith("Toolpath ready:", vm.StatusText);
        Assert.Equal(check.Report!.Contacts, vm.Analysis.Collisions!.Contacts);
        Assert.Empty(_confirm.Questions);

        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(2, _confirm.Notices.Count);
    }

    [Fact]
    public async Task InvalidProject_ShowsNoSummary()
    {
        var vm = await CreateWithBoxAsync();
        vm.Cutting.Stepdown = 0f;
        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.Single(_errors.Shown);
        Assert.Empty(_confirm.Notices);
    }

    // The check is the last stage of the generation: a cancel there discards the result.
    [Fact]
    public async Task CancelDuringTheCheck_CancelsTheGeneration_AndShowsNoSummary()
    {
        var vm = await CreateWithBoxAsync();
        var sawCheck = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.StatusText) && vm.StatusText.StartsWith(CollisionService.StageName, StringComparison.Ordinal) && !sawCheck)
            {
                sawCheck = true;
                vm.CancelGenerateCommand.Execute(null);
            }
        };

        await vm.GenerateCommand.ExecuteAsync(null);
        Assert.True(sawCheck);
        Assert.Equal(MainWindowViewModel.CancelledStatus, vm.StatusText);
        Assert.Null(vm.LastResult);
        Assert.Empty(_confirm.Notices);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Analysis_LaysTheCollisionsOverEveryOtherCategory()
    {
        var vm = await CreateWithBoxAsync();
        await vm.GenerateCommand.ExecuteAsync(null);
        var result = vm.LastResult!;
        var contacts = new CollisionContact[result.Model.CellCount];
        contacts[0] = CollisionContact.Model;
        contacts[1] = CollisionContact.Stock;
        contacts[^1] = CollisionContact.Stock;
        var events = new[] { new SimulationEvent(SimulationEventKind.HeadCollision, 3, Vector3.Zero, "Head touches the stock in segment 3.") };
        var analysis = vm.Analysis;
        analysis.SetPipeline(result, new CollisionReport(events, contacts, 1));
        await analysis.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(analysis.ShowCollisions);
        analysis.ShowUncuttable = true;
        var composed = analysis.Compose();
        Assert.Equal(CellCategory.CollisionModel, composed[0]);
        Assert.Equal(CellCategory.CollisionStock, composed[1]);
        Assert.Equal(CellCategory.CollisionStock, composed[^1]);
        Assert.Equal(composed, vm.Viewport.StockCategories);

        analysis.ShowCollisions = false;
        var plain = analysis.Compose();
        Assert.DoesNotContain(plain, c => c is CellCategory.CollisionModel or CellCategory.CollisionStock);
        Assert.Equal(plain, vm.Viewport.StockCategories);
        for (var k = 2; k < plain.Length - 1; k++)
        {
            Assert.Equal(plain[k], composed[k]);
        }

        Assert.Contains("Collisions: 1 (model: 1 cells, stock: 2 cells)", analysis.SummaryText);

        analysis.SetPipeline(result, null);
        await analysis.AnalyzeCommand.ExecuteAsync(null);
        Assert.Contains("Collisions: not checked", analysis.SummaryText);
        Assert.Null(analysis.Collisions);

        Assert.Throws<ArgumentException>(() => analysis.SetPipeline(result, new CollisionReport(events, new CollisionContact[3], 0)));
        Assert.Throws<ArgumentException>(() => analysis.SetPipeline(null, new CollisionReport(events, contacts, 1)));

        vm.NewProjectCommand.Execute(null);
        Assert.Null(analysis.Collisions);
    }

    [AvaloniaFact]
    public void Inform_ShowsTheMessageWithOneOkButton_OkClosesIt()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var service = new ConfirmDialogService(() => owner);
            var shown = service.InformAsync(MainWindowViewModel.CollisionSummaryTitle, CollisionService.NoCollisionsText);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            Assert.Equal(MainWindowViewModel.CollisionSummaryTitle, dialog.Title);
            Assert.Contains(dialog.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == CollisionService.NoCollisionsText);
            var ok = Assert.Single(dialog.GetLogicalDescendants().OfType<Button>());
            Assert.Equal(ConfirmDialogService.OkLabel, ok.Content);
            Assert.True(ok.IsDefault);
            Assert.True(ok.IsCancel);
            Assert.False(shown.IsCompleted);

            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(shown.IsCompletedSuccessfully);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            owner.Close();
        }
    }

    // Render check: the longest summary (listed events and the "more" line) is written to
    // out/ui-captures/dialog-collision-summary.png.
    [AvaloniaFact]
    public void Inform_LongSummary_RendersToPng()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var events = Enumerable.Range(0, CollisionService.MaxListedEvents + 4)
                .Select(k => new SimulationEvent(SimulationEventKind.HeadCollision, 100 + k, new Vector3(12.5f, 7.25f, 3f),
                    $"Head touches the stock in segment {100 + k} at 12.5, 7.25, 3 (stock 15 above the head underside 13.5)."))
                .ToArray();
            var contacts = new[] { CollisionContact.Model, CollisionContact.Stock, CollisionContact.Stock };
            var summary = CollisionService.Summarize(new CollisionCheck(new CollisionReport(events, contacts, 2), null));
            _ = new ConfirmDialogService(() => owner).InformAsync(MainWindowViewModel.CollisionSummaryTitle, summary);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.UpdateLayout();
            var frame = dialog.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Directory.CreateDirectory(RenderCaptureTests.CaptureDirectory);
            frame.Save(Path.Combine(RenderCaptureTests.CaptureDirectory, "dialog-collision-summary.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            dialog.Close();
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Inform_EscapeClosesIt()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var shown = new ConfirmDialogService(() => owner).InformAsync(MainWindowViewModel.CollisionSummaryTitle, CollisionService.NoCollisionsText);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(shown.IsCompletedSuccessfully);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            owner.Close();
        }
    }

    // Both collision colors are declared in Colors.axaml, and no two analysis categories share a color.
    [Fact]
    public void CollisionColors_AreDeclared_AndEveryCategoryColorIsDistinct()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Miller.App", "Styles", "Colors.axaml"));
        var text = File.ReadAllText(path);
        foreach (var name in new[] { "CategoryCollisionModel", "CategoryCollisionStock" })
        {
            Assert.Contains($"x:Key=\"{name}Color\"", text);
            Assert.Contains($"x:Key=\"{name}Brush\"", text);
        }

        var colors = Regex.Matches(text, @"<Color x:Key=""(Category\w+|Stock)Color"">(#[0-9A-Fa-f]{8})</Color>")
            .Select(m => m.Groups[2].Value.ToUpperInvariant())
            .ToList();
        Assert.Equal(Enum.GetValues<CellCategory>().Length, colors.Count);
        Assert.Equal(colors.Count, colors.Distinct().Count());
    }

    [Fact]
    public async Task Inform_RefusesAnEmptyMessage()
    {
        var service = new ConfirmDialogService(() => null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.InformAsync("title", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.InformAsync(" ", "message"));
    }
}
