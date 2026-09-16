using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// A press on the progress bar of the simulation panel seeks to the pressed fraction.
public sealed class SimulationProgressSeekTests
{
    [Theory]
    [InlineData(0.0, 200.0, 0f)]
    [InlineData(50.0, 200.0, 0.25f)]
    [InlineData(250.0, 200.0, 1f)]
    [InlineData(-3.0, 200.0, 0f)]
    [InlineData(10.0, 0.0, 0f)]
    public void FractionAt_MapsThePressToTheBar(double x, double width, float expected)
    {
        Assert.Equal(expected, SimulationControlsView.FractionAt(x, width), 5);
    }

    [AvaloniaFact]
    public async Task PressOnTheBar_SeeksToThatFraction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-seek-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var viewModel = TestServices.MainWindowViewModel(root);
        var view = new SimulationControlsView { DataContext = viewModel.SimulationPanel, Width = 400, Height = 500 };
        var window = new Window { Width = 400, Height = 500, Content = view };
        try
        {
            var stl = Path.Combine(root, "box.stl");
            File.WriteAllText(stl, TestMeshes.AsciiCubeText());
            Assert.True(await viewModel.OpenStlFileAsync(stl));
            viewModel.Stock.SizeX = 10;
            viewModel.Stock.SizeY = 10;
            viewModel.Stock.SizeZ = 3;
            viewModel.Cutting.CellSize = 0.5f;
            await viewModel.GenerateCommand.ExecuteAsync(null);
            Assert.True(viewModel.SimulationPanel.IsLoaded);

            window.Show();
            window.UpdateLayout();
            var bar = view.FindControl<ProgressBar>("SimulationProgress")!;
            var origin = bar.TranslatePoint(new Point(0, 0), window)!.Value;
            var press = new Point(origin.X + bar.Bounds.Width / 2, origin.Y + bar.Bounds.Height / 2);
            window.MouseDown(press, MouseButton.Left);
            window.MouseUp(press, MouseButton.Left);
            var seek = viewModel.SimulationPanel.SeekCommand.ExecutionTask;
            Assert.NotNull(seek);
            await seek;
            Assert.Equal(0.5f, viewModel.SimulationPanel.Progress, 2);
            Assert.False(viewModel.SimulationPanel.IsPlaying);

            // The right button does nothing.
            var quarter = new Point(origin.X + bar.Bounds.Width / 4, press.Y);
            window.MouseDown(quarter, MouseButton.Right);
            window.MouseUp(quarter, MouseButton.Right);
            Assert.Equal(0.5f, viewModel.SimulationPanel.Progress, 2);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
