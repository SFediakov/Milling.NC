using Avalonia.Controls;
using Miller.App.ViewModels;

namespace Miller.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private ProgramListingWindow? _listing;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
    }

    private void Attach(MainWindowViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ExitRequested -= OnExitRequested;
            _viewModel.AboutRequested -= OnAboutRequested;
            _viewModel.Machine.ListingRequested -= OnListingRequested;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ExitRequested += OnExitRequested;
            _viewModel.AboutRequested += OnAboutRequested;
            _viewModel.Machine.ListingRequested += OnListingRequested;
        }
    }

    private void OnExitRequested(object? sender, EventArgs e) => Close();

    private async void OnAboutRequested(object? sender, EventArgs e)
    {
        var about = new AboutWindow { DataContext = new AboutViewModel(_viewModel!.AppVersion) };
        await about.ShowDialog(this);
    }

    // One listing window at a time, owned by this window so it closes with it.
    private void OnListingRequested(object? sender, EventArgs e)
    {
        if (_listing is not null)
        {
            _listing.Activate();
            return;
        }

        _listing = new ProgramListingWindow { DataContext = _viewModel!.Machine.Listing };
        _listing.Closed += (_, _) => _listing = null;
        _listing.Show(this);
    }

    // The window size is user state, stored with the other preferences. ClientSize is the size
    // actually laid out; Width and Height are NaN until something sets them.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _viewModel?.Machine.Close();
        var size = ClientSize;
        var measured = double.IsFinite(size.Width) && size.Width > 0 && double.IsFinite(size.Height) && size.Height > 0;
        if (_viewModel is not null && WindowState == WindowState.Normal && measured)
        {
            _viewModel.Settings.WindowWidth = size.Width;
            _viewModel.Settings.WindowHeight = size.Height;
            _viewModel.Settings.Save();
        }
    }
}
