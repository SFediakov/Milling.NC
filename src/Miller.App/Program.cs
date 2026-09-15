using Avalonia;

namespace Miller.App;

public static class Program
{
    public const string AppVersion = "Build_1.0.10";
    public const string VersionFlag = "--version";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == VersionFlag)
        {
            Console.WriteLine(AppVersion);
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
