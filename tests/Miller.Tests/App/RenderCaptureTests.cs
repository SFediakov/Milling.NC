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
