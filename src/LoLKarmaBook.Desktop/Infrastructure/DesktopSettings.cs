using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLKarmaBook.Desktop.Infrastructure;

/// <summary>
/// Non-sensitive desktop preferences. Never contains note text, identity values,
/// tokens or client secrets.
/// </summary>
public sealed class DesktopSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Last selected profile id (validated by storage on open).</summary>
    public string? LastProfileId { get; set; }

    /// <summary>Windows|Left|Width|Height. Empty means: default size, centred.</summary>
    public string? WindowPlacement { get; set; }

    /// <summary>Optional always-on-top. Off by default; the user chooses.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Default reminder validity, 365 days. User may change it.</summary>
    public int DefaultReminderDays { get; set; } = NoteCategoriesDefaultDays;

    public const int NoteCategoriesDefaultDays = 365;

    /// <summary>Minimising (and closing) keeps the app in the notification area. On by default.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Last imported performance dataset path (display only).</summary>
    public string? LastPerformanceImport { get; set; }

    /// <summary>Optional client folder selected by the user; no client credentials are stored.</summary>
    public string? ClientDirectory { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static DesktopSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DesktopSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            DesktopSettings? settings = JsonSerializer.Deserialize<DesktopSettings>(json, JsonOptions);
            if (settings is null)
            {
                return new DesktopSettings();
            }

            settings.Sanitize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt settings file must not prevent the app from starting.
            return new DesktopSettings();
        }
    }

    public void Save(string path)
    {
        Sanitize();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public void Sanitize()
    {
        if (SchemaVersion != 1)
        {
            SchemaVersion = 1;
        }

        if (DefaultReminderDays is < 0 or > 3650)
        {
            DefaultReminderDays = NoteCategoriesDefaultDays;
        }

        if (!OperatingSystem.IsWindows())
        {
            // No notification area outside Windows; keep the preference meaningful.
            MinimizeToTray = false;
        }

        if (LastProfileId is { Length: > 64 })
        {
            LastProfileId = null;
        }

        if (WindowPlacement is { Length: > 128 })
        {
            WindowPlacement = null;
        }

        if (ClientDirectory is not null)
        {
            ClientDirectory = ClientDirectory.Trim();
            if (ClientDirectory.Length is 0 or > 32767 || ClientDirectory.Any(char.IsControl))
            {
                ClientDirectory = null;
            }
        }
    }
}
