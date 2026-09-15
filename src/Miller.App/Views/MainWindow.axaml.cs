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

    // The window size is user state, stored with the other preferences.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_viewModel is not null && WindowState == WindowState.Normal)
        {
            _viewModel.Settings.WindowWidth = Width;
            _viewModel.Settings.WindowHeight = Height;
            _viewModel.Settings.Save();
        }
    }
}
