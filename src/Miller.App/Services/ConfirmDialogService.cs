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

    // Yes / No; closing the dialog with the window button is No.
    Task<bool> ConfirmAsync(string question);
}

// Modal questions bound to the main window: Save / Discard / Cancel for unsaved changes, Yes / No
// before an action on the machine.
public sealed class ConfirmDialogService : IConfirmDialogService
{
    public const string Question = "The project has unsaved changes. Save them?";
    private const double DialogWidth = 420;

    private readonly Func<Window?> _owner;

    public ConfirmDialogService(Func<Window?> owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public Task<SaveDecision> AskSaveChangesAsync()
        => AskAsync(Question, new[] { ("Save", SaveDecision.Save), ("Discard", SaveDecision.Discard), ("Cancel", SaveDecision.Cancel) }, SaveDecision.Save, SaveDecision.Cancel);

    public async Task<bool> ConfirmAsync(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        return await AskAsync(question, new[] { ("Yes", true), ("No", false) }, true, false);
    }

    private async Task<T> AskAsync<T>(string question, (string Label, T Value)[] answers, T accept, T cancel)
    {
        var owner = _owner() ?? throw new InvalidOperationException("The main window is not available for the confirm dialog.");
        var dialog = new Window
        {
            Title = App.WindowTitle,
            Width = DialogWidth,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, value) in answers)
        {
            var button = new Button { Content = label, IsDefault = Equals(value, accept), IsCancel = Equals(value, cancel) };
            button.Click += (_, _) => dialog.Close(value);
            buttons.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 12,
            Children = { new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons },
        };
        // The window button closes with default(T), the cancel answer of both questions (Cancel, No).
        return await dialog.ShowDialog<T>(owner);
    }
}
