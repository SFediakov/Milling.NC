using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Miller.App.Controls;
using Miller.App.Views;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-218: the Cutting tab sets the travel rate apart from the milling (feed) rate; a value that is not
// positive is shown as an error under the box.
public sealed class CuttingTravelRateViewTests
{
    [AvaloniaFact]
    public void TheCuttingTab_SetsTheTravelRate_AndShowsItsError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-travel-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>("CuttingTab");
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetLogicalDescendants().OfType<CuttingParametersView>().Distinct().Single();
            var travel = panel.FindControl<NumericBox>("TravelRateBox")!;
            var feed = panel.FindControl<NumericBox>("FeedRateBox")!;

            travel.Value = 2400f;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2400f, viewModel.Project.Current.Parameters.TravelRate);
            Assert.Equal(viewModel.Project.Current.Parameters.FeedRate, feed.Value);
            Assert.Null(viewModel.Cutting.TravelRateError);

            viewModel.Cutting.TravelRate = 0f;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0f, travel.Value);
            Assert.NotNull(viewModel.Cutting.TravelRateError);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == viewModel.Cutting.TravelRateError);
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
