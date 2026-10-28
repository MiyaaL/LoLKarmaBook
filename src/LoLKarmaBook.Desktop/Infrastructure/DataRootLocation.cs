using System.IO;
using System.Text.Json;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.Infrastructure;

/// <summary>
/// Where the writable data root (`userdata`) lives. The default is the install
/// directory; a user-chosen folder is remembered in a small pointer file under
/// the current Windows user's profile, because the install directory may be
/// read-only or on a drive the user does not want to fill. The pointer holds one
/// path and no note content.
/// </summary>
public static class DataRootLocation
{
    public const string DefaultFolderName = "userdata";

    private const string PointerFileName = "location.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>%APPDATA%\LoLKarmaBook\location.json (testable via <paramref name="appDataDirectory"/>).</summary>
    public static string GetPointerPath(string? appDataDirectory = null)
    {
        string directory = appDataDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
                "LoLKarmaBook");
        return Path.Combine(directory, PointerFileName);
    }

    /// <summary>
    /// The selected data root, or null when the default (install directory) is in
    /// use. A pointer that cannot be read is ignored so the app still starts.
    /// </summary>
    public static string? ReadSelectedRoot(string pointerPath) => Read(pointerPath).DataRoot;

    public static PathOverrides Read(string pointerPath)
    {
        if (!File.Exists(pointerPath)) return new PathOverrides();
        try
        {
            using FileStream stream = File.OpenRead(pointerPath);
            PathOverrides file = JsonSerializer.Deserialize<PathOverrides>(stream, JsonOptions) ?? new PathOverrides();
            file.Sanitize();
            return file;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new PathOverrides();
        }
    }

    /// <summary>Points future launches at <paramref name="dataRoot"/>.</summary>
    public static void WriteSelectedRoot(string pointerPath, string dataRoot)
    {
        PathOverrides settings = Read(pointerPath);
        settings.DataRoot = Normalize(dataRoot)
            ?? throw new ArgumentException("数据根目录不能为空。", nameof(dataRoot));
        Write(pointerPath, settings);
    }

    public static void Write(string pointerPath, PathOverrides settings)
    {
        settings = settings.Clone();
        settings.Sanitize();
        string? directory = Path.GetDirectoryName(pointerPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = pointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, JsonOptions);
        }

        File.Move(temp, pointerPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Removes the pointer so the next launch uses the install directory.</summary>
    public static void ClearSelectedRoot(string pointerPath)
    {
        PathOverrides settings = Read(pointerPath);
        settings.DataRoot = null;
        if (settings.SettingsFile is null && settings.LogDirectory is null && settings.ProfileDatabases.Count == 0)
        {
            if (File.Exists(pointerPath)) File.Delete(pointerPath);
        }
        else Write(pointerPath, settings);
    }

    /// <summary>Rejects relative, control-character and over-long paths before they are stored.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string trimmed = path.Trim();
        if (trimmed.Length > 32767 || trimmed.Any(char.IsControl) || !Path.IsPathFullyQualified(trimmed)) return null;
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

}

public sealed class PathOverrides
{
    public int SchemaVersion { get; set; } = 1;
    public string? DataRoot { get; set; }
    public string? SettingsFile { get; set; }
    public string? LogDirectory { get; set; }
    public Dictionary<string, string> ProfileDatabases { get; set; } = new(StringComparer.Ordinal);

    public PathOverrides Clone() => new()
    {
        DataRoot = DataRoot, SettingsFile = SettingsFile, LogDirectory = LogDirectory,
        ProfileDatabases = new(ProfileDatabases ?? new(), StringComparer.Ordinal)
    };

    public void Sanitize()
    {
        SchemaVersion = 1;
        DataRoot = DataRootLocation.Normalize(DataRoot);
        SettingsFile = DataRootLocation.Normalize(SettingsFile);
        LogDirectory = DataRootLocation.Normalize(LogDirectory);
        var valid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in ProfileDatabases ?? new())
        {
            try
            {
                string id = ProfilePaths.ValidateProfileId(pair.Key);
                if (DataRootLocation.Normalize(pair.Value) is { } path) valid[id] = path;
            }
            catch (ArgumentException) { }
        }
        ProfileDatabases = valid;
    }
}
