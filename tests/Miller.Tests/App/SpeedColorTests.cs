using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Miller.App.Rendering;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Application.Services;
using Miller.Core.Toolpaths;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-224 (user request): the shown path in a color representing the speed of the tool, red the slowest and
// green the fastest, and a small legend at the viewport's left from the slowest to the fastest speed.
public sealed class SpeedColorTests
{
    private const float RapidRate = 3000f;

    private static readonly Vector4 Red = new(1, 0, 0, 1);
    private static readonly Vector4 Yellow = new(1, 1, 0, 1);
    private static readonly Vector4 Green = new(0, 1, 0, 1);
    private static readonly Vector4 Background = new(0.1f, 0.1f, 0.1f, 1);
    private static readonly ColorGradient RedYellowGreen = new(new[] { (0f, Red), (0.5f, Yellow), (1f, Green) });

    // A rapid that carries a feed rate (a file's G0 carries the modal F), a feed at the slowest rate and a
    // plunge in the middle of 800 and the rapid rate.
    private static Toolpath ThreeSpeeds()
    {
        var path = new Toolpath();
        path.Add(new ToolpathSegment(new Vector3(0, 0, 5), new Vector3(10, 0, 5), MoveKind.Rapid, 100));
        path.Add(new ToolpathSegment(new Vector3(10, 0, 5), new Vector3(20, 0, 5), MoveKind.Feed, 800));
        path.Add(new ToolpathSegment(new Vector3(20, 0, 5), new Vector3(20, 0, 0), MoveKind.Plunge, 1900));
        return path;
    }

    private static Vector4 ColorAt(float[] data, int segment, int vertex)
    {
        var o = (segment * 2 + vertex) * ToolpathRenderer.FloatsPerVertex;
        return new Vector4(data[o + 3], data[o + 4], data[o + 5], data[o + 6]);
    }

    // Hue in degrees, red near 0 (slightly negative when blue exceeds green), yellow 60, green 120.
    private static (float Hue, float Saturation, float Value) Hsv(Vector4 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var min = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var delta = max - min;
        var hue = delta == 0 ? 0f
            : max == color.X ? 60f * ((color.Y - color.Z) / delta)
            : max == color.Y ? 60f * (2f + (color.Z - color.X) / delta)
            : 60f * (4f + (color.X - color.Y) / delta);
        return (hue, max == 0 ? 0f : delta / max, max);
    }

    [Fact]
    public void Gradient_GivesTheStopColorsAtTheirOffsets_AndMixesLinearlyBetween()
    {
        Assert.Equal(Red, RedYellowGreen.At(0));
        Assert.Equal(Yellow, RedYellowGreen.At(0.5f));
        Assert.Equal(Green, RedYellowGreen.At(1));
        Assert.Equal(new Vector4(1, 0.5f, 0, 1), RedYellowGreen.At(0.25f));
        Assert.Equal(new Vector4(0.5f, 1, 0, 1), RedYellowGreen.At(0.75f));
        Assert.Equal(Red, RedYellowGreen.At(-1));
        Assert.Equal(Red, RedYellowGreen.At(float.NaN));
        Assert.Equal(Green, RedYellowGreen.At(2));
    }

    [Fact]
    public void Gradient_SortsItsStops_AndRefusesNoneOrAnUnknownOffset()
    {
        var unsorted = new ColorGradient(new[] { (1f, Green), (0f, Red), (0.5f, Yellow) });
        Assert.Equal(new[] { 0f, 0.5f, 1f }, unsorted.Stops.Select(s => s.Offset));
        Assert.Equal(new Vector4(1, 0.5f, 0, 1), unsorted.At(0.25f));
        Assert.Equal(Yellow, new ColorGradient(new[] { (0.3f, Yellow) }).At(0.9f));
        Assert.Throws<ArgumentException>(() => new ColorGradient(Array.Empty<(float, Vector4)>()));
        Assert.Throws<ArgumentException>(() => new ColorGradient(new[] { (float.NaN, Red) }));
    }

    // The brush of Colors.axaml that the lines and the legend share: red at the slow end, green at the fast
    // end, the hue rising all the way and every mixed color bright and saturated. A red-to-green mix without
    // the yellow stop fails here: its middle has saturation 0.28 and value 0.57 (a dark olive).
    [AvaloniaFact]
    public void SpeedBrush_RunsFromRedOverYellowToGreen_WithoutADarkMiddle()
    {
        var stops = ThemeColors.GetGradient("ToolpathSpeedBrush");
        Assert.Equal(new[] { 0f, 0.5f, 1f }, stops.Select(s => s.Offset));
        Assert.Equal(ThemeColors.Get("ToolpathSlowColor"), stops[0].Color);
        Assert.Equal(ThemeColors.Get("ToolpathMediumColor"), stops[1].Color);
        Assert.Equal(ThemeColors.Get("ToolpathFastColor"), stops[2].Color);

        var swatch = Assert.IsType<SolidColorBrush>(Avalonia.Application.Current!.FindResource("ToolpathSlowBrush"));
        Assert.Equal(stops[0].Color, new Vector4(swatch.Color.R / 255f, swatch.Color.G / 255f, swatch.Color.B / 255f, swatch.Color.A / 255f));

        var gradient = new ColorGradient(stops);
        Assert.InRange(Hsv(gradient.At(0)).Hue, -15f, 15f);
        Assert.InRange(Hsv(gradient.At(0.5f)).Hue, 40f, 70f);
        Assert.InRange(Hsv(gradient.At(1)).Hue, 90f, 160f);
        var previous = float.NegativeInfinity;
        for (var k = 0; k <= 100; k++)
        {
            var (hue, saturation, value) = Hsv(gradient.At(k / 100f));
            Assert.True(hue > previous, $"hue {hue} at {k} percent does not rise past {previous}");
            Assert.True(saturation >= 0.45f, $"saturation {saturation} at {k} percent");
            Assert.True(value >= 0.75f, $"value {value} at {k} percent");
            previous = hue;
        }
    }

    [Fact]
    public void Lines_TakeTheColorOfTheirSpeed_SlowestRed_FastestGreen()
    {
        var path = ThreeSpeeds();
        var scale = ToolpathSpeedScale.Of(path.Segments, RapidRate);
        Assert.NotNull(scale);

        var (bright, dim) = ToolpathRenderer.Build(path, RedYellowGreen, scale, Background);

        Assert.Equal(3 * 2 * ToolpathRenderer.FloatsPerVertex, bright.Length);
        Assert.Equal(bright.Length, dim.Length);
        // The rapid runs at the rapid rate (the fastest) whatever rate it carries; the plunge lies halfway.
        var expected = new[] { Green, Red, Yellow };
        for (var segment = 0; segment < expected.Length; segment++)
        {
            for (var vertex = 0; vertex < 2; vertex++)
            {
                Assert.Equal(expected[segment], ColorAt(bright, segment, vertex));
                Assert.Equal(ToolpathRenderer.Dim(expected[segment], Background), ColorAt(dim, segment, vertex));
            }
        }

        var o = (2 * 2 + 1) * ToolpathRenderer.FloatsPerVertex;
        Assert.Equal(new Vector3(20, 0, 0), new Vector3(bright[o], bright[o + 1], bright[o + 2]));
        Assert.Equal(new Vector3(20, 0, 0), new Vector3(dim[o], dim[o + 1], dim[o + 2]));
    }

    [Fact]
    public void Lines_WithoutAKnownSpeed_TakeTheSlowEnd_AndCarryNoNaN()
    {
        var segments = new[]
        {
            new ToolpathSegment(Vector3.Zero, Vector3.UnitX, MoveKind.Feed, 0),
            new ToolpathSegment(Vector3.UnitX, Vector3.UnitY, MoveKind.Feed, float.NaN),
            new ToolpathSegment(Vector3.UnitY, Vector3.UnitZ, MoveKind.Rapid, 800),
        };

        var (bright, dim) = ToolpathRenderer.BuildSegments(segments, RedYellowGreen, ToolpathSpeedScale.Of(segments, float.NaN), Background);

        Assert.All(bright, value => Assert.True(float.IsFinite(value)));
        Assert.All(dim, value => Assert.True(float.IsFinite(value)));
        for (var segment = 0; segment < segments.Length; segment++)
        {
            Assert.Equal(Red, ColorAt(bright, segment, 0));
        }

        Assert.Empty(ToolpathRenderer.Build(null, RedYellowGreen, null, Background).Bright);
    }

    // The machine view shows a program's own moves: its rapids carry the modal F (0 before the first F word),
    // yet run at the rapid rate.
    [Fact]
    public void MachinePath_RapidsRunAtTheRapidRate_NotAtTheModalFeed()
    {
        var program = GrblProgram.Parse("speeds.nc", "G21 G90\nG0 Z5\nG0 X10 Y0\nG1 Z-1 F300\nG1 X20 F900\nG0 Z5\n");
        var path = ProgramPath.Of(program).Segments;
        Assert.Equal(new[] { 0f, 0f, 300f, 900f, 900f }, path.Select(s => s.FeedRate));

        var viewport = new ViewportViewModel();
        viewport.ShowMachineView(path, 0, Vector3.Zero);

        Assert.NotNull(viewport.SpeedScale);
        Assert.Equal(300f, viewport.SpeedScale.Slowest);
        Assert.Equal(RapidRate, viewport.SpeedScale.Fastest);
        Assert.Equal(new[] { 1f, 1f, 0f, 600f / 2700f, 1f }, path.Select(viewport.SpeedScale.Fraction));
        Assert.Equal(new[] { "300", "975", "1650", "2325", "3000" }, viewport.SpeedLegend.Labels);
    }

    [Fact]
    public void Viewport_ScaleFollowsTheShownPathAndTheRapidRate_AndNothingElse()
    {
        var viewport = new ViewportViewModel();
        Assert.Equal(Miller.Core.Setup.CuttingParameters.DefaultRapidRate, viewport.RapidRate);
        Assert.Null(viewport.SpeedScale);

        var toolpath = ThreeSpeeds();
        viewport.SetToolpath(toolpath);
        var scale = viewport.SpeedScale;
        Assert.NotNull(scale);
        Assert.Equal(800f, scale.Slowest);
        Assert.Equal(RapidRate, scale.Fastest);

        // Progress, the tool and the machine position change no scale and upload nothing again.
        var version = viewport.ToolpathVersion;
        viewport.SetToolProgress(2, Vector3.One);
        viewport.SetMachinePosition(1, Vector3.UnitX);
        viewport.SetRapidRate(RapidRate);
        Assert.Same(scale, viewport.SpeedScale);
        Assert.Equal(version, viewport.ToolpathVersion);

        viewport.SetRapidRate(6000);
        Assert.Equal(6000f, viewport.SpeedScale!.Fastest);
        Assert.True(viewport.ToolpathVersion > version);

        // The machine view has a scale of its own; leaving it brings the toolpath's back.
        var machinePath = new[] { new ToolpathSegment(Vector3.Zero, Vector3.UnitX, MoveKind.Feed, 450) };
        viewport.ShowMachineView(machinePath, 0, Vector3.Zero);
        Assert.True(viewport.SpeedScale!.IsSingleSpeed);
        Assert.Equal(450f, viewport.SpeedScale.Slowest);
        var machineScale = viewport.SpeedScale;
        version = viewport.ToolpathVersion;
        viewport.ShowMachineView(machinePath, 1, Vector3.UnitY);
        Assert.Same(machineScale, viewport.SpeedScale);
        Assert.Equal(version, viewport.ToolpathVersion);

        viewport.HideMachineView();
        Assert.Equal(800f, viewport.SpeedScale!.Slowest);
        Assert.Equal(6000f, viewport.SpeedScale.Fastest);

        viewport.SetToolpath(null);
        Assert.Null(viewport.SpeedScale);
    }

    [Fact]
    public void Legend_NamesTheScaleFromSlowestToFastest_OnlyWhileThePathIsShown()
    {
        var viewport = new ViewportViewModel();
        var legend = viewport.SpeedLegend;
        Assert.False(legend.IsShown);
        Assert.Empty(legend.Labels);

        viewport.SetToolpath(ThreeSpeeds());
        Assert.True(legend.IsShown);
        Assert.False(legend.IsSingleSpeed);
        Assert.Equal(new[] { "800", "1350", "1900", "2450", "3000" }, legend.Labels);

        var labels = legend.Labels;
        viewport.ShowToolpath = false;
        Assert.False(legend.IsShown);
        viewport.ShowToolpath = true;
        Assert.True(legend.IsShown);
        Assert.Same(labels, legend.Labels);

        var single = new Toolpath();
        single.Add(new ToolpathSegment(Vector3.Zero, Vector3.UnitX, MoveKind.Feed, 800));
        viewport.SetToolpath(single);
        Assert.True(legend.IsShown);
        Assert.True(legend.IsSingleSpeed);
        Assert.Equal(new[] { "800" }, legend.Labels);

        viewport.SetToolpath(null);
        Assert.False(legend.IsShown);
        Assert.Empty(legend.Labels);
    }

    [Fact]
    public void LegendLabels_AreInvariant_AndShortForAnyNumber()
    {
        var culture = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("282.84", SpeedLegendViewModel.Format(282.842712f));
            Assert.Equal("1131.4", SpeedLegendViewModel.Format(1131.3708f));
            Assert.Equal("3000", SpeedLegendViewModel.Format(3000f));
            Assert.Equal("1E+30", SpeedLegendViewModel.Format(1e30f));
            Assert.True(SpeedLegendViewModel.Format(-1.23456789e-30f).Length <= 12);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // The legend over the viewport: at its top left, small, labels from the slowest (top) to the fastest
    // (bottom), the bar's ends at the centres of the first and the last label, and a drag that starts on it
    // still turns the camera. Captured to out/ui-captures/ for the visual check.
    [AvaloniaFact]
    public void Legend_LiesAtTheViewportsLeft_AndPassesThePointerToTheViewport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-speed-legend-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel, Width = RenderCaptureTests.Width, Height = RenderCaptureTests.Height };
        try
        {
            window.Show();
            var legend = window.GetVisualDescendants().OfType<SpeedLegendView>().Single();
            Assert.False(legend.IsEffectivelyVisible);

            viewModel.Viewport.SetToolpath(ThreeSpeeds());
            window.UpdateLayout();
            Assert.True(legend.IsEffectivelyVisible);
            Assert.False(legend.IsHitTestVisible);

            var host = window.FindControl<Border>("ViewportHost")!;
            var origin = legend.TranslatePoint(default, host)!.Value;
            Assert.Equal(8, origin.X, 3);
            Assert.Equal(8, origin.Y, 3);
            Assert.True(legend.Bounds.Width < host.Bounds.Width * 0.2, $"legend {legend.Bounds.Width} wide in {host.Bounds.Width}");
            Assert.True(legend.Bounds.Height < host.Bounds.Height * 0.3, $"legend {legend.Bounds.Height} high in {host.Bounds.Height}");

            var rows = legend.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("legend-row"))
                .OrderBy(p => p.TranslatePoint(default, legend)!.Value.Y).ToList();
            Assert.Equal(new[] { "800", "1350", "1900", "2450", "3000" }, rows.Select(r => r.GetVisualDescendants().OfType<TextBlock>().Single().Text));
            var bar = legend.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SpeedBar");
            Assert.True(bar.IsEffectivelyVisible);
            Assert.False(legend.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SpeedSwatch").IsEffectivelyVisible);
            Assert.Equal(Center(rows[0], legend), bar.TranslatePoint(default, legend)!.Value.Y, 0.5);
            Assert.Equal(Center(rows[^1], legend), bar.TranslatePoint(new Point(0, bar.Bounds.Height), legend)!.Value.Y, 0.5);
            Assert.Same(Avalonia.Application.Current!.FindResource("ToolpathSpeedBrush"), bar.Background);

            Capture(window, "viewport-speed-legend.png");

            var viewport = window.GetVisualDescendants().OfType<Viewport3DControl>().Single();
            var yaw = viewport.Camera.Yaw;
            var start = legend.TranslatePoint(new Point(legend.Bounds.Width / 2, legend.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Right);
            window.MouseMove(start + new Point(40, 0), RawInputModifiers.RightMouseButton);
            window.MouseUp(start + new Point(40, 0), MouseButton.Right);
            Assert.Equal(yaw - 40 * Viewport3DControl.OrbitDegreesPerPixel, viewport.Camera.Yaw, 3);

            var single = new Toolpath();
            single.Add(new ToolpathSegment(Vector3.Zero, Vector3.UnitX, MoveKind.Feed, 800));
            viewModel.Viewport.SetToolpath(single);
            window.UpdateLayout();
            Assert.False(bar.IsEffectivelyVisible);
            Assert.True(legend.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SpeedSwatch").IsEffectivelyVisible);
            Capture(window, "viewport-speed-legend-single.png");

            viewModel.Viewport.ShowToolpath = false;
            window.UpdateLayout();
            Assert.False(legend.IsEffectivelyVisible);
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

    private static double Center(Visual row, Visual relativeTo)
        => row.TranslatePoint(new Point(0, row.Bounds.Height / 2), relativeTo)!.Value.Y;

    private static void Capture(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(RenderCaptureTests.CaptureDirectory);
        frame.Save(Path.Combine(RenderCaptureTests.CaptureDirectory, name), PngBitmapEncoderOptions.Default);
    }
}
