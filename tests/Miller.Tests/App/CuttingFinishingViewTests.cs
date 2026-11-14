using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Miller.App.Controls;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-221 and T-222: the Cutting tab sets the XY and Z rates of milling and travel, the number of finishing
// passes and their pattern; a pass count that is no whole number from 0 to 5 is shown as an error.
public sealed class CuttingFinishingViewTests
{
    [AvaloniaFact]
    public void TheCuttingTab_SetsTheZRates_TheFinishingPassesAndThePattern()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-finishing-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>("CuttingTab");
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetLogicalDescendants().OfType<CuttingParametersView>().Distinct().Single();
            var parameters = () => viewModel.Project.Current.Parameters;

            panel.FindControl<NumericBox>("FeedRateZBox")!.Value = 250f;
            panel.FindControl<NumericBox>("TravelRateZBox")!.Value = 900f;
            panel.FindControl<NumericBox>("FinishingPassesBox")!.Value = 2f;
            var patterns = panel.FindControl<ComboBox>("FinishingPatternBox")!;
            Assert.Equal(CuttingParametersViewModel.FinishingPatterns, patterns.ItemsSource);
            patterns.SelectedItem = FinishingPattern.DiagonalRows;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((250f, 900f, 2f, FinishingPattern.DiagonalRows), (parameters().FeedRateZ, parameters().TravelRateZ, parameters().FinishingPasses, parameters().FinishingPattern));
            Assert.Null(viewModel.Cutting.FinishingPassesError);

            viewModel.Cutting.FinishingPasses = 2.5f;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(viewModel.Cutting.FinishingPassesError);
            Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == viewModel.Cutting.FinishingPassesError);
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
