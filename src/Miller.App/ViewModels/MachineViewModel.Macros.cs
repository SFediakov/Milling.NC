using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Miller.Application.Macros;

namespace Miller.App.ViewModels;

// The macros of the machine tab: one button per macro and an editor. The macro file is a cluster of
// its own (MacroService); a file that cannot be read leaves the list empty with the reason in Message
// and the rest of the tab works on. The lists are republished only on load, save and delete, and the
// edited index is set again afterwards, because a ComboBox resets its selection when its list changes.
public sealed partial class MachineViewModel
{
    public const string MacroDelayMessage = "The delay must be a whole number of milliseconds.";
    public const string MacroDeleteQuestion = "Delete the macro '{0}'?";

    [ObservableProperty]
    private IReadOnlyList<MachineMacro> _macros = Array.Empty<MachineMacro>();

    [ObservableProperty]
    private IReadOnlyList<string> _macroNames = Array.Empty<string>();

    // The macro in the editor, -1 for a new one.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteMacroCommand))]
    private int _macroIndex = -1;

    [ObservableProperty]
    private string _macroName = string.Empty;

    // Milliseconds after each answer before the next command goes out.
    [ObservableProperty]
    private float _macroDelay;

    [ObservableProperty]
    private string _macroText = string.Empty;

    private bool CanDeleteMacro => MacroIndex >= 0 && MacroIndex < Macros.Count;

    private void LoadMacros()
    {
        try
        {
            _macroService.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Message = $"Macros not loaded: {ex.Message}";
        }

        PublishMacros(-1);
    }

    private void PublishMacros(int index)
    {
        Macros = _macroService.Macros;
        MacroNames = Macros.Select(m => m.Name).ToList();
        MacroIndex = index;
    }

    partial void OnMacroIndexChanged(int value)
    {
        if (value >= 0 && value < Macros.Count)
        {
            var macro = Macros[value];
            MacroName = macro.Name;
            MacroDelay = macro.DelayMs;
            MacroText = string.Join('\n', macro.Lines);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunNow))]
    private void RunMacro(MachineMacro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        Try(() => _machine.RunMacro(macro));
    }

    [RelayCommand]
    private void NewMacro()
    {
        MacroIndex = -1;
        MacroName = string.Empty;
        MacroDelay = 0;
        MacroText = string.Empty;
    }

    [RelayCommand]
    private void SaveMacro()
    {
        Message = null;
        if (MacroDelay != MathF.Floor(MacroDelay))
        {
            Message = MacroDelayMessage;
            return;
        }

        try
        {
            var saved = _macroService.Save(MacroName, (int)MacroDelay, MacroText, CanDeleteMacro ? Macros[MacroIndex].Name : null);
            PublishMacros(_macroService.Macros.ToList().IndexOf(saved));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteMacro))]
    private async Task DeleteMacroAsync()
    {
        var name = Macros[MacroIndex].Name;
        if (!await _confirm.ConfirmAsync(string.Format(CultureInfo.InvariantCulture, MacroDeleteQuestion, name)))
        {
            return;
        }

        Message = null;
        try
        {
            _macroService.Delete(name);
            PublishMacros(-1);
            NewMacro();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
    }
}
