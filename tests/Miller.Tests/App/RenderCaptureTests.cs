using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// The graphical rendering check required for every UI task: each settings tab is rendered by the
// headless Skia platform and written to out/ui-captures/ for inspection. The assertions only make
// sure a frame of the expected size came out.
public sealed class RenderCaptureTests
{
    public const int Width = 1280;
    public const int Height = 760;

    public static string CaptureDirectory
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "out", "ui-captures"));

    [AvaloniaFact]
    public void AboutWindow_RendersToPng()
    {
        var about = new AboutWindow { DataContext = new Miller.App.ViewModels.AboutViewModel(TestServices.Version) };
        try
        {
            about.Show();
            about.UpdateLayout();
            var frame = about.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Directory.CreateDirectory(CaptureDirectory);
            frame.Save(Path.Combine(CaptureDirectory, "about.png"), PngBitmapEncoderOptions.Default);
        }
        finally
        {
            about.Close();
        }
    }

    // T-135: the frustum fields appear only for the frustum head, and the schematic draws a trapezoid.
    [AvaloniaFact]
    public void ToolTab_FrustumHead_RendersToPng()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-render-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel, Width = Width, Height = Height };
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Name == "ToolTab");
            window.UpdateLayout();
            var fields = tabs.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "FrustumFields");
            Assert.False(fields.IsEffectivelyVisible);

            viewModel.Tool.HeadShape = Miller.Core.Setup.HeadShape.Frustum;
            viewModel.Tool.HeadTopDiameter = 16f;
            viewModel.Tool.HeadLength = 4f;
            window.UpdateLayout();
            Assert.True(fields.IsEffectivelyVisible);
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Directory.CreateDirectory(CaptureDirectory);
            frame.Save(Path.Combine(CaptureDirectory, "tab-ToolTab-frustum.png"), PngBitmapEncoderOptions.Default);
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

    // T-143: the machine tab connected to a fake Grbl with every section open, top and end of the panel.
    [AvaloniaFact]
    public async Task MachineTab_Connected_RendersToPng()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-render-{Guid.NewGuid():N}");
        var timing = new Miller.Machine.MachineTiming(ReadTimeoutMs: 5, PollMs: 10, BannerWaitMs: 50);
        var machine = new Miller.Application.Services.MachineService(_ => new FakeGrblLink(), timing);
        var viewModel = TestServices.MainWindowViewModel(root, machine: machine);
        viewModel.Machine.SerialPort = "COM3";
        await viewModel.Machine.ConnectCommand.ExecuteAsync(null);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (viewModel.Machine.StateText != "Idle" && watch.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            viewModel.Machine.Refresh();
        }

        Assert.Equal("Idle", viewModel.Machine.StateText);
        machine.Send("$I");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        viewModel.Machine.Refresh();
        var window = new MainWindow { DataContext = viewModel, Width = Width, Height = Height };
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Name == "MachineTab");
            window.UpdateLayout();
            foreach (var expander in tabs.GetVisualDescendants().OfType<Expander>())
            {
                expander.IsExpanded = true;
            }

            window.UpdateLayout();
            var panel = tabs.GetVisualDescendants().OfType<Miller.App.Views.MachineView>().Single();
            Assert.True(panel.Bounds.Width <= tabs.Bounds.Width);
            Directory.CreateDirectory(CaptureDirectory);
            window.CaptureRenderedFrame()!.Save(Path.Combine(CaptureDirectory, "tab-MachineTab-connected.png"), PngBitmapEncoderOptions.Default);
            var scroll = panel.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.Equal(0, scroll.Extent.Width - scroll.Viewport.Width, 1);
            var jogPad = panel.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "JogPad");
            jogPad.BringIntoView();
            window.UpdateLayout();
            window.CaptureRenderedFrame()!.Save(Path.Combine(CaptureDirectory, "tab-MachineTab-connected-jog.png"), PngBitmapEncoderOptions.Default);
            scroll.ScrollToEnd();
            window.UpdateLayout();
            window.CaptureRenderedFrame()!.Save(Path.Combine(CaptureDirectory, "tab-MachineTab-connected-end.png"), PngBitmapEncoderOptions.Default);
        }
        finally
        {
            window.Close();
            viewModel.Machine.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [AvaloniaFact]
    public async Task SettingsTabs_RenderToPng()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-render-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        Assert.True(await viewModel.OpenStlFileAsync(TestMeshes.FixturePath()));
        viewModel.Tool.HeadDiameter = 5f;
        // 100 x 100 stock at 0.05 mm: allowed, but the cutting tab shows the interactive-limit warning.
        viewModel.Cutting.CellSize = 0.05f;
        var window = new MainWindow { DataContext = viewModel, Width = Width, Height = Height };
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            Directory.CreateDirectory(CaptureDirectory);
            foreach (var tab in tabs.Items.OfType<TabItem>().ToList())
            {
                tabs.SelectedItem = tab;
                window.UpdateLayout();
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Assert.Equal(Width, frame.PixelSize.Width);
                Assert.Equal(Height, frame.PixelSize.Height);
                frame.Save(Path.Combine(CaptureDirectory, $"tab-{tab.Name}.png"), PngBitmapEncoderOptions.Default);

                // Long panels scroll: a second frame shows their end (validation text sits below the fields).
                foreach (var scroll in tabs.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible))
                {
                    scroll.ScrollToEnd();
                    window.UpdateLayout();
                    if (scroll.Offset.Y > 0)
                    {
                        window.CaptureRenderedFrame()!.Save(Path.Combine(CaptureDirectory, $"tab-{tab.Name}-end.png"), PngBitmapEncoderOptions.Default);
                    }
                }
            }
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
