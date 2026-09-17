using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Miller.App.Controls;

// A text box for one float. The text the user types stays as typed: every keystroke that parses
// is written to Value (and through the binding to the view model), a keystroke that does not parse
// marks the box with a data validation error and leaves Value alone. The text is rewritten only
// when Value changes from outside (a project load, an alignment button, a clamp), so "0.00" is
// not turned into "0" while it is being typed and the caret stays where it is.
public sealed class NumericBox : TextBox
{
    public static readonly StyledProperty<float> ValueProperty =
        AvaloniaProperty.Register<NumericBox, float>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    private bool _syncing;

    public NumericBox()
    {
        Text = Format(Value);
    }

    // The Fluent TextBox template and the TextBox styles of Theme.axaml apply unchanged.
    protected override Type StyleKeyOverride => typeof(TextBox);

    public float Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_syncing)
        {
            return;
        }

        if (change.Property == ValueProperty)
        {
            Sync(() =>
            {
                Text = Format(change.GetNewValue<float>());
                DataValidationErrors.ClearErrors(this);
            });
        }
        else if (change.Property == TextProperty)
        {
            var text = change.GetNewValue<string?>();
            Sync(() =>
            {
                if (TryParse(text, out var parsed))
                {
                    Value = parsed;
                    DataValidationErrors.ClearErrors(this);
                }
                else
                {
                    DataValidationErrors.SetErrors(this, new[] { $"'{text}' is not a number." });
                }
            });
        }
    }

    private void Sync(Action action)
    {
        _syncing = true;
        try
        {
            action();
        }
        finally
        {
            _syncing = false;
        }
    }
}
