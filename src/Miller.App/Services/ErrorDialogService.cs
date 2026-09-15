using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Miller.Application.Services;

namespace Miller.App.Services;

// Abstraction so view models can be tested without a window.
public interface IErrorDialogService
{
    Task ShowAsync(Exception exception);
}

// Logs the exception and shows it in a modal window with a copyable details box.
public sealed class ErrorDialogService : IErrorDialogService
{
    public const string Title = "Miller - error";
    private const double DialogWidth = 560;
    private const double DialogHeight = 360;
    private const double DetailsHeight = 220;

    private readonly LogService _log;
    private readonly Func<Window?> _owner;

    public ErrorDialogService(LogService log, Func<Window?> owner)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task ShowAsync(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _log.Error(exception.Message, exception);
        var owner = _owner() ?? throw new InvalidOperationException("The main window is not available for the error dialog.");

        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var dialog = new Window
        {
            Title = Title,
            Width = DialogWidth,
            Height = DialogHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap },
                    new TextBox
                    {
                        Text = exception.ToString(),
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Height = DetailsHeight,
                    },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }
}
