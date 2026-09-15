using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Miller.App;

public partial class App : Application
{
    public const string WindowTitle = "Miller";
    public const double DefaultWindowWidth = 1280;
    public const double DefaultWindowHeight = 720;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = WindowTitle,
                Width = DefaultWindowWidth,
                Height = DefaultWindowHeight,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
