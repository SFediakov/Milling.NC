using System.Collections.Specialized;
using Avalonia.Controls;
using Miller.App.ViewModels;

namespace Miller.App.Views;

// The console list follows its newest line.
public partial class MachineView : UserControl
{
    private MachineViewModel? _viewModel;

    public MachineView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MachineViewModel);
    }

    private void Attach(MachineViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.ConsoleLines.CollectionChanged -= OnConsoleChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.ConsoleLines.CollectionChanged += OnConsoleChanged;
        }
    }

    private void OnConsoleChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel is { ConsoleLines.Count: > 0 } viewModel)
        {
            ConsoleList.ScrollIntoView(viewModel.ConsoleLines.Count - 1);
        }
    }
}
