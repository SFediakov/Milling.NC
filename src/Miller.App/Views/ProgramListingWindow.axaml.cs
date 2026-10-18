using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Miller.App.ViewModels;

namespace Miller.App.Views;

// Brings the executing row into view whenever it moves while Follow is on.
public partial class ProgramListingWindow : Window
{
    private ProgramListingViewModel? _viewModel;

    public ProgramListingWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ProgramListingViewModel);
        Closed += (_, _) => Attach(null);
    }

    private void Attach(ProgramListingViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            FollowRow();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramListingViewModel.CurrentRow) or nameof(ProgramListingViewModel.Follow))
        {
            FollowRow();
        }
    }

    private void FollowRow()
    {
        if (_viewModel is { Follow: true, CurrentRow: >= 0 } viewModel)
        {
            var row = viewModel.CurrentRow;
            Dispatcher.UIThread.Post(() =>
            {
                if (row < RowsList.ItemCount)
                {
                    RowsList.ScrollIntoView(row);
                }
            });
        }
    }
}
