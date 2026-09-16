using Avalonia.Controls;
using Avalonia.Input;
using Miller.App.ViewModels;

namespace Miller.App.Views;

// A left press on the progress bar maps its x position to a fraction of the toolpath and asks the
// view model to seek there.
public partial class SimulationControlsView : UserControl
{
    public SimulationControlsView()
    {
        InitializeComponent();
        SimulationProgress.PointerPressed += OnProgressPressed;
    }

    public static float FractionAt(double x, double width) => width > 0 ? (float)Math.Clamp(x / width, 0.0, 1.0) : 0f;

    private void OnProgressPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SimulationViewModel viewModel || !e.GetCurrentPoint(SimulationProgress).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var fraction = FractionAt(e.GetPosition(SimulationProgress).X, SimulationProgress.Bounds.Width);
        if (viewModel.SeekCommand.CanExecute(fraction))
        {
            viewModel.SeekCommand.Execute(fraction);
            e.Handled = true;
        }
    }
}
