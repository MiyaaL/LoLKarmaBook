using System.IO;

namespace LoLKarmaBook.Desktop.Infrastructure;

/// <summary>
/// Minimal rotating diagnostic log. It intentionally records only status codes,
/// durations, versions and exception *types*: never note text, display names,
/// identity values, file contents or credentials.
/// </summary>
public sealed class DiagnosticLog
{
    private const long MaxBytes = 512 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public DiagnosticLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "desktop.log");
    }

    public string Path => _path;

    public void Info(string area, string message) => Write("INFO", area, message, null);

    public void Warn(string area, string message, Exception? exception = null) => Write("WARN", area, message, exception);

    public void Error(string area, string message, Exception? exception = null) => Write("ERROR", area, message, exception);

    private void Write(string level, string area, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    string previous = _path + ".1";
                    File.Delete(previous);
                    File.Move(_path, previous);
                }

                string line = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0:O} {1} [{2}] {3}",
                    DateTimeOffset.UtcNow,
                    level,
                    area,
                    message);
                if (exception is not null)
                {
                    // Type + diagnostic code only; messages can echo user input.
                    line += $" ({exception.GetType().Name})";
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the UI flow.
        }
    }

    /// <summary>User-facing, non-sensitive tail for the Settings page.</summary>
    public string ReadTail(int maxLines = 8)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    return "（暂无日志）";
                }

                string[] lines = File.ReadAllLines(_path);
                return lines.Length == 0
                    ? "（暂无日志）"
                    : string.Join(Environment.NewLine, lines[^Math.Min(maxLines, lines.Length)..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "（日志不可读）";
        }
    }
}
