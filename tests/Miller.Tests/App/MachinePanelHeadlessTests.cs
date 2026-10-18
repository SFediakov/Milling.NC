using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Machine;
using Miller.Machine.Grbl;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// T-197 and T-199 in the window: every display text of the machine tab is selectable and keeps its
// style, the work coordinate panel is there, selecting the tab switches the viewport to the machine
// view, and the listing window opens once, virtualizes its rows and brings the executing row into view.
public sealed class MachinePanelHeadlessTests
{
    [AvaloniaFact]
    public void MachineTab_TextsAreSelectable_KeepTheirStyles_AndSwitchTheViewport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-panel-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            Assert.False(viewModel.Machine.IsViewActive);
            tabs.SelectedItem = window.FindControl<TabItem>("MachineTab");
            Dispatcher.UIThread.RunJobs();
            Assert.True(viewModel.Machine.IsViewActive);
            Assert.True(viewModel.Viewport.MachineView);

            var panel = window.GetLogicalDescendants().OfType<MachineView>().Distinct().Single();
            // Button and header captions (AccessText) stay clickable; every other text is selectable.
            var texts = panel.GetLogicalDescendants().OfType<TextBlock>().Where(t => t is not Avalonia.Controls.Primitives.AccessText).Distinct().ToList();
            Assert.True(texts.Count > 40, $"{texts.Count} texts");
            Assert.All(texts, t => Assert.IsType<SelectableTextBlock>(t));
            Assert.Equal(FontWeight.SemiBold, panel.FindControl<SelectableTextBlock>("WorkXText")!.FontWeight);
            Assert.Equal(FontWeight.Bold, panel.FindControl<SelectableTextBlock>("StateText")!.FontWeight);
            Assert.Equal(new Thickness(0, 0, 8, 0), texts.First(t => t.Classes.Contains("label")).Margin);
            foreach (var name in new[] { "WorkValueXBox", "WorkValueYBox", "WorkValueZBox", "SetWorkXButton", "SetWorkAllButton", "ReadWorkButton", "ShowListingButton", "SaveProgressButton" })
            {
                Assert.NotNull(panel.FindControl<Control>(name));
            }

            viewModel.Machine.ConsoleLines.Add("in   ok");
            panel.FindControl<Expander>("ConsoleSection")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var console = panel.FindControl<ListBox>("ConsoleList")!;
            Assert.Contains(console.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "in   ok");

            tabs.SelectedItem = window.FindControl<TabItem>("ToolTab");
            Dispatcher.UIThread.RunJobs();
            Assert.False(viewModel.Machine.IsViewActive);
            Assert.False(viewModel.Viewport.MachineView);
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
    public void ListingWindow_OpensOnce_VirtualizesRows_AndFollowsTheExecutingRow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-listing-{Guid.NewGuid():N}");
        var viewModel = TestServices.MainWindowViewModel(root);
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            window.Show();
            viewModel.Machine.ShowListingCommand.Execute(null);
            viewModel.Machine.ShowListingCommand.Execute(null);
            var listing = Assert.Single(window.OwnedWindows.OfType<ProgramListingWindow>());
            Assert.Same(viewModel.Machine.Listing, listing.DataContext);

            var program = GrblProgram.Parse("long.nc", string.Concat(Enumerable.Range(1, 20_000).Select(k => $"G1 X{k} F900\n")));
            var listingModel = viewModel.Machine.Listing;
            listingModel.Show(program, null);
            Dispatcher.UIThread.RunJobs();
            var rows = listing.FindControl<ItemsControl>("RowsList")!;
            Assert.Equal(20_000, rows.ItemCount);
            Assert.InRange(rows.GetRealizedContainers().Count(), 1, 500);

            listingModel.Show(program, new JobProgress(MachineJobKind.Program, "long.nc", JobState.Running, 20_001, 15_001, 15_000, 15_000, 1, 0, null));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(14_999, listingModel.CurrentRow);
            Assert.NotNull(rows.ContainerFromIndex(14_999));
            Assert.Equal("Executing line 15000 of 20000: G1 X15000 F900", listingModel.Description);
            Assert.InRange(rows.GetRealizedContainers().Count(), 1, 500);
            Assert.Contains(listing.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("executing"));

            listing.Close();
            viewModel.Machine.ShowListingCommand.Execute(null);
            Assert.Single(window.OwnedWindows.OfType<ProgramListingWindow>());
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
