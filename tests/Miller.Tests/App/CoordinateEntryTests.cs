using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Miller.App.Controls;
using Miller.App.ViewModels;
using Miller.App.Views;
using Miller.Core.Setup;
using Miller.Tests.Fixtures;
using Xunit;

namespace Miller.Tests.App;

// A negative coordinate typed one character at a time into every coordinate field of the real
// views: the sign shows no error, every digit reaches the project, the field keeps the keyboard
// focus and the model selection does not move while typing.
public sealed class CoordinateEntryTests
{
    private const string Typed = "-12.5";
    private const float Expected = -12.5f;

    [AvaloniaFact]
    public async Task ModelsTab_OffsetFields_TakeANegativeValueTypedCharacterByCharacter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-coord-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vm = TestServices.MainWindowViewModel(root);
        var stl = Path.Combine(root, "box.stl");
        File.WriteAllText(stl, TestMeshes.AsciiCubeText());
        Assert.True(await vm.OpenStlFileAsync(stl));
        var view = new ModelsView { DataContext = vm.Models };
        var window = new Window { Width = 400, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var selectionChanges = 0;
            vm.Models.PropertyChanged += (_, e) => selectionChanges += e.PropertyName == nameof(ModelsViewModel.SelectedIndex) ? 1 : 0;
            var placement = vm.Project.Current.Models[0];
            TypeInto(window, view.FindControl<NumericBox>("OffsetXBox")!, () => placement.Offset.X);
            TypeInto(window, view.FindControl<NumericBox>("OffsetYBox")!, () => placement.Offset.Y);
            TypeInto(window, view.FindControl<NumericBox>("OffsetZBox")!, () => placement.Offset.Z);
            Assert.Equal(new Vector3(Expected, Expected, Expected), placement.Offset);
            Assert.Equal(0, selectionChanges);
            Assert.Equal(0, vm.Models.SelectedIndex);
            Assert.True(vm.Project.IsDirty);
        }
        finally
        {
            window.Close();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void AxesTab_CustomZeroFields_TakeANegativeValueTypedCharacterByCharacter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-coord-{Guid.NewGuid():N}");
        var vm = TestServices.MainWindowViewModel(root);
        vm.Axes.OriginMode = OriginMode.Custom;
        var view = new AxisSettingsView { DataContext = vm.Axes };
        var window = new Window { Width = 400, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var axes = vm.Project.Current.Axes;
            TypeInto(window, view.FindControl<NumericBox>("OffsetXBox")!, () => axes.CustomOffset.X);
            TypeInto(window, view.FindControl<NumericBox>("OffsetYBox")!, () => axes.CustomOffset.Y);
            TypeInto(window, view.FindControl<NumericBox>("OffsetZBox")!, () => axes.CustomOffset.Z);
            Assert.Equal(new Vector3(Expected, Expected, Expected), axes.CustomOffset);
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

    [AvaloniaFact]
    public void StockTab_ExplicitOriginFields_TakeANegativeValueTypedCharacterByCharacter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"miller-coord-{Guid.NewGuid():N}");
        var vm = TestServices.MainWindowViewModel(root);
        vm.Stock.Placement = StockPlacement.Explicit;
        var view = new StockSettingsView { DataContext = vm.Stock };
        var window = new Window { Width = 400, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        try
        {
            var stock = vm.Project.Current.Stock;
            TypeInto(window, view.FindControl<NumericBox>("OriginXBox")!, () => stock.ExplicitOrigin.X);
            TypeInto(window, view.FindControl<NumericBox>("OriginYBox")!, () => stock.ExplicitOrigin.Y);
            TypeInto(window, view.FindControl<NumericBox>("OriginZBox")!, () => stock.ExplicitOrigin.Z);
            Assert.Equal(new Vector3(Expected, Expected, Expected), stock.ExplicitOrigin);
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

    // Replaces the field content the way a user does: select all, then one key per character. After
    // every key the text is what was typed so far, the box is still focused and the project holds
    // the number typed so far (the sign alone leaves the previous value).
    private static void TypeInto(Window window, NumericBox box, Func<float> projectValue)
    {
        Assert.True(box.IsEnabled);
        var before = projectValue();
        box.Focus();
        box.SelectAll();
        for (var k = 1; k <= Typed.Length; k++)
        {
            window.KeyTextInput(Typed[k - 1].ToString());
            var soFar = Typed[..k];
            Assert.Equal(soFar, box.Text);
            Assert.True(box.IsFocused, $"focus lost after '{soFar}'");
            Assert.False(DataValidationErrors.GetHasErrors(box), $"error shown after '{soFar}'");
            Assert.Equal(NumericBox.TryParse(soFar, out var parsed) ? parsed : before, projectValue());
        }

        Assert.Equal(Expected, projectValue());
    }
}
