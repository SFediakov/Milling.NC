using Avalonia.Controls;
using Miller.App.ViewModels;

namespace Miller.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

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
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ExitRequested += OnExitRequested;
        }
    }

    private void OnExitRequested(object? sender, EventArgs e) => Close();

    // The window size is user state, stored with the other preferences. ClientSize is the size
    // actually laid out; Width and Height are NaN until something sets them.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
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
