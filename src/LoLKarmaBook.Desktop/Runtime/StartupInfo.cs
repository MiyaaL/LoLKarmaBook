using System.IO;

namespace LoLKarmaBook.Desktop.Runtime;

/// <summary>
/// Launch overrides used by developers and by the automated UI smoke check.
/// Normal launches enable real-client detection; smoke runs keep it disabled.
/// </summary>
public sealed class StartupInfo
{
    /// <summary>Override the application data root (e.g. for tests).</summary>
    public string? DataRoot { get; init; }

    /// <summary>Render the window off-screen and exit after N milliseconds.</summary>
    public int SmokeMilliseconds { get; init; }

    /// <summary>On failure, write the exception to this file and exit with code 1.</summary>
    public string? FailureLogPath { get; init; }

    public static StartupInfo Parse(IReadOnlyList<string> args)
    {
        string? dataRoot = null;
        int smoke = 0;
        string? failureLog = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;

            if (string.Equals(arg, "--data-root", StringComparison.OrdinalIgnoreCase))
            {
                string? value = Next();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    dataRoot = Path.GetFullPath(value);
                }
            }
            else if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                string? value = Next();
                smoke = int.TryParse(value, out int ms) ? Math.Clamp(ms, 500, 120_000) : 4000;
            }
            else if (string.Equals(arg, "--failure-log", StringComparison.OrdinalIgnoreCase))
            {
                string? value = Next();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    failureLog = Path.GetFullPath(value);
                }
            }
        }

        return new StartupInfo { DataRoot = dataRoot, SmokeMilliseconds = smoke, FailureLogPath = failureLog };
    }
}
