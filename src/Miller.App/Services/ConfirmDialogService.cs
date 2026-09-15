using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Miller.App.Services;

// Cancel is first so that closing the dialog with the window button yields Cancel.
public enum SaveDecision
{
    Cancel,
    Save,
    Discard,
}

public interface IConfirmDialogService
{
    Task<SaveDecision> AskSaveChangesAsync();
}

// Modal Save / Discard / Cancel question bound to the main window.
public sealed class ConfirmDialogService : IConfirmDialogService
{
    public const string Question = "The project has unsaved changes. Save them?";
    private const double DialogWidth = 420;
    private const double DialogHeight = 150;

    private readonly Func<Window?> _owner;

    public ConfirmDialogService(Func<Window?> owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task<SaveDecision> AskSaveChangesAsync()
    {
        var owner = _owner() ?? throw new InvalidOperationException("The main window is not available for the confirm dialog.");
        var dialog = new Window
        {
            Title = App.WindowTitle,
            Width = DialogWidth,
            Height = DialogHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, decision) in new[] { ("Save", SaveDecision.Save), ("Discard", SaveDecision.Discard), ("Cancel", SaveDecision.Cancel) })
        {
            var button = new Button { Content = label, IsDefault = decision == SaveDecision.Save, IsCancel = decision == SaveDecision.Cancel };
            button.Click += (_, _) => dialog.Close(decision);
            buttons.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 12,
            Children = { new TextBlock { Text = Question }, buttons },
        };
        return await dialog.ShowDialog<SaveDecision>(owner);
    }
}
