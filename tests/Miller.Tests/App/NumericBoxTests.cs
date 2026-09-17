using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Miller.App.Controls;
using Miller.App.ViewModels;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// Typing in a numeric field: the text stays as typed, valid values reach the bound property at
// once, invalid text is flagged without touching the value or the focus, and an outside change of
// the value rewrites the text.
public sealed class NumericBoxTests
{
    private static (Window Window, NumericBox Box) Show(float value)
    {
        var box = new NumericBox { Value = value, Width = 120 };
        var window = new Window { Width = 300, Height = 100, Content = new StackPanel { Children = { box } } };
        window.Show();
        window.UpdateLayout();
        box.Focus();
        return (window, box);
    }

    // Replaces the content the way a user does: select all, then type (or delete for an empty text).
    private static void Type(Window window, NumericBox box, string text)
    {
        box.SelectAll();
        if (text.Length == 0)
        {
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            return;
        }

        window.KeyTextInput(text);
    }

    [AvaloniaFact]
    public void TypingZeroPointZeroZero_KeepsTheText_AndSetsTheValueToZero()
    {
        var (window, box) = Show(6f);
        try
        {
            Assert.Equal("6", box.Text);
            Type(window, box, "0.00");
            Assert.Equal("0.00", box.Text);
            Assert.Equal(0f, box.Value);
            Assert.False(DataValidationErrors.GetHasErrors(box));
            Assert.True(box.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void InvalidText_KeepsTextAndValue_ShowsAnError_AndClearsItOnTheNextValidText()
    {
        var (window, box) = Show(2.5f);
        try
        {
            Type(window, box, "abc");
            Assert.Equal("abc", box.Text);
            Assert.Equal(2.5f, box.Value);
            Assert.True(DataValidationErrors.GetHasErrors(box));
            Assert.True(box.IsFocused);

            Type(window, box, "1.");
            Assert.Equal("1.", box.Text);
            Assert.Equal(1f, box.Value);
            Assert.False(DataValidationErrors.GetHasErrors(box));

            Type(window, box, string.Empty);
            Assert.Equal(string.Empty, box.Text);
            Assert.Equal(1f, box.Value);
            Assert.True(DataValidationErrors.GetHasErrors(box));

            Type(window, box, "-3.25");
            Assert.Equal(-3.25f, box.Value);
            Assert.False(DataValidationErrors.GetHasErrors(box));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OutsideValueChange_RewritesTheText_AndClearsAnError()
    {
        var (window, box) = Show(1f);
        try
        {
            Type(window, box, "x");
            Assert.True(DataValidationErrors.GetHasErrors(box));
            box.Value = 12.5f;
            Assert.Equal("12.5", box.Text);
            Assert.False(DataValidationErrors.GetHasErrors(box));
            // An equal value written back (the view model echo) does not touch the text.
            Type(window, box, "12.50");
            box.Value = 12.5f;
            Assert.Equal("12.50", box.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BoundToAViewModel_EveryValidKeystrokeWritesTheProject_AndTheTextIsKept()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-numeric-{Guid.NewGuid():N}");
        var vm = TestServices.MainWindowViewModel(root);
        var view = new Miller.App.Views.ToolSettingsView { DataContext = vm.Tool };
        var window = new Window { Width = 400, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var box = view.FindControl<NumericBox>("CutterDiameterBox")!;
            Assert.Equal("6", box.Text);
            box.Focus();
            Type(window, box, "4.50");
            Assert.Equal("4.50", box.Text);
            Assert.Equal(4.5f, vm.Project.Current.Tool.CutterDiameter);
            Assert.True(vm.Project.IsDirty);
            Assert.True(box.IsFocused);

            // Out of the validator's range: the message shows under the field, the text stays.
            Type(window, box, "-1");
            Assert.Equal("-1", box.Text);
            Assert.Equal(-1f, vm.Project.Current.Tool.CutterDiameter);
            Assert.NotNull(vm.Tool.CutterDiameterError);

            // A project reload rewrites the text.
            vm.NewProjectCommand.Execute(null);
            Assert.Equal("6", box.Text);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void ParseAndFormat_AreInvariant()
    {
        Assert.True(NumericBox.TryParse("1.5", out var v));
        Assert.Equal(1.5f, v);
        Assert.False(NumericBox.TryParse("1,5", out _));
        Assert.False(NumericBox.TryParse("NaN", out _));
        Assert.False(NumericBox.TryParse("Infinity", out _));
        Assert.Equal("0.2", NumericBox.Format(0.2f));
        Assert.Equal("800", NumericBox.Format(800f));
    }
}
