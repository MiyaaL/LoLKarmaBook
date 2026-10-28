using System.IO;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.Infrastructure;

/// <summary>
/// Application data belongs to the selected installation directory. A caller
/// may explicitly choose another root (for example, an isolated smoke test).
/// </summary>
public sealed class AppPaths
{
    private readonly PathOverrides _overrides;

    public AppPaths(string dataRoot, PathOverrides? overrides = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Root = Path.GetFullPath(dataRoot);
        _overrides = overrides?.Clone() ?? new PathOverrides();
    }

    public static AppPaths CreateDefault() => CreateDefault(AppContext.BaseDirectory);

    public static AppPaths CreateDefault(string installationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        return new AppPaths(Path.Combine(Path.GetFullPath(installationDirectory), "userdata"));
    }

    /// <summary>For example, D:\LoLKarmaBook\userdata.</summary>
    public string Root { get; }

    /// <summary>Profile databases live under this root; passed to SqliteProfileStore.</summary>
    public string DataRoot => Path.Combine(Root, "data");

    public string SettingsFile => _overrides.SettingsFile ?? Path.Combine(Root, "desktop-settings.json");

    public string LogDirectory => _overrides.LogDirectory ?? Path.Combine(Root, "logs");

    public string GetDatabasePath(string profileId)
    {
        string id = ProfilePaths.ValidateProfileId(profileId);
        return _overrides.ProfileDatabases.TryGetValue(id, out string? path)
            ? path : ProfilePaths.GetDatabasePath(DataRoot, id);
    }

    public static AppPaths FromOverrides(PathOverrides overrides, string? installationDirectory = null) =>
        new(overrides.DataRoot ?? CreateDefault(installationDirectory ?? AppContext.BaseDirectory).Root, overrides);

    public PathOverrides GetOverrides()
    {
        PathOverrides result = _overrides.Clone();
        result.DataRoot = Root;
        return result;
    }

    public string BackupDirectory => Path.Combine(Root, "backups");

    public string ExportDirectory => Path.Combine(Root, "exports");

    public void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(BackupDirectory);
            Directory.CreateDirectory(ExportDirectory);

            // An existing directory may still be read-only for this user. Check
            // the actual root rather than silently saving elsewhere.
            using var probe = new FileStream(
                Path.Combine(Root, ".write-check-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DataDirectoryUnavailableException(ex);
        }
    }
}

public sealed class DataDirectoryUnavailableException : IOException
{
    public DataDirectoryUnavailableException(Exception innerException)
        : base("无法写入程序的数据目录。请选择可写的安装文件夹（例如 D 盘），或用 --data-root 指定数据目录。", innerException)
    {
    }
}
