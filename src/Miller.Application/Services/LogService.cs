using System.Globalization;

namespace Miller.Application.Services;

// Append-only text log next to the executable. One line per entry, UTC timestamp, then the
// exception text on following lines when there is one.
public sealed class LogService
{
    public const string FolderName = "logs";
    public const string FileName = "miller.log";

    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public LogService(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
    }

    public string Directory { get; }

    public string FilePath { get; }

    public static string DefaultDirectory() => Path.Combine(AppContext.BaseDirectory, FolderName);

    public void Info(string message) => Append("INFO", message, null);

    public void Error(string message, Exception? exception) => Append("ERROR", message, exception);

    private void Append(string level, string message, Exception? exception)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var line = $"{DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture)} {level} {message}";
        if (exception is not null)
        {
            line += "\n" + exception;
        }

        File.AppendAllText(FilePath, line + "\n");
    }
}
