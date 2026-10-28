using System.Text.RegularExpressions;

namespace LoLKarmaBook.Storage;

/// <summary>
/// Validates profile ids and keeps profile databases under the configured root.
/// Each profile gets its own SQLite database; mutable player data never crosses
/// spaces.
/// </summary>
public static class ProfilePaths
{
    public const string ProfilesDirectoryName = "profiles";
    public const string DatabaseFileName = "lolkarmabook.db";
    public const int MaxProfileIdLength = 64;

    private static readonly Regex ProfileIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ValidateProfileId(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("A profile id is required.", nameof(profileId));
        }

        string trimmed = profileId.Trim();
        if (trimmed.Length > MaxProfileIdLength)
        {
            throw new ArgumentException(
                $"Profile id is too long (max {MaxProfileIdLength}).",
                nameof(profileId));
        }

        if (!ProfileIdPattern.IsMatch(trimmed))
        {
            throw new ArgumentException(
                "Profile id must use only ASCII letters, digits, '.', '_' or '-', and start with a letter or digit.",
                nameof(profileId));
        }

        if (trimmed.StartsWith('.'))
        {
            throw new ArgumentException("Profile id must start with a letter or digit.", nameof(profileId));
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            throw new ArgumentException("Profile id must not end with a dot or space.", nameof(profileId));
        }

        string stem = trimmed.Split('.')[0];
        if (ReservedWindowsNames.Contains(stem))
        {
            throw new ArgumentException("Profile id uses a reserved Windows device name.", nameof(profileId));
        }

        return trimmed;
    }

    public static string GetProfilesDirectory(string rootDirectory)
    {
        string root = NormalizeRoot(rootDirectory);
        string profiles = Path.GetFullPath(Path.Combine(root, ProfilesDirectoryName));
        EnsureUnderRoot(root, profiles, nameof(rootDirectory));
        return profiles;
    }

    public static string GetProfileDirectory(string rootDirectory, string? profileId)
    {
        string id = ValidateProfileId(profileId);
        string root = NormalizeRoot(rootDirectory);
        string profileDirectory = Path.GetFullPath(
            Path.Combine(GetProfilesDirectory(root), id));
        EnsureUnderRoot(root, profileDirectory, nameof(profileId));
        return profileDirectory;
    }

    public static string GetDatabasePath(string rootDirectory, string? profileId)
    {
        string profileDirectory = GetProfileDirectory(rootDirectory, profileId);
        string databasePath = Path.GetFullPath(
            Path.Combine(profileDirectory, DatabaseFileName));
        EnsureUnderRoot(rootDirectory, databasePath, nameof(profileId));
        return databasePath;
    }

    public static string NormalizeRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        }

        string fullPath = Path.GetFullPath(rootDirectory.Trim());
        if (File.Exists(fullPath))
        {
            throw new ArgumentException("The root path is a file, not a directory.", nameof(rootDirectory));
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static void EnsureUnderRoot(string rootDirectory, string candidatePath, string parameterName)
    {
        string root = ResolvePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)));
        string candidate = ResolvePath(Path.GetFullPath(candidatePath));
        string comparisonRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.Equals(root, comparison) &&
            !candidate.StartsWith(comparisonRoot, comparison))
        {
            throw new ArgumentException(
                "The path escapes the configured LoLKarmaBook root directory.",
                parameterName);
        }
    }

    /// <summary>
    /// Resolves the longest existing ancestor through symlinks so a link inside
    /// the root cannot point outside it.
    /// </summary>
    private static string ResolvePath(string fullPath)
    {
        string? existing = fullPath;
        var suffix = new List<string>();
        while (existing is not null && !Directory.Exists(existing) && !File.Exists(existing))
        {
            suffix.Add(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing);
        }

        if (existing is null)
        {
            return fullPath;
        }

        string resolved = ResolveLinkTarget(existing);
        for (int i = suffix.Count - 1; i >= 0; i--)
        {
            resolved = Path.Combine(resolved, suffix[i]);
        }

        return Path.GetFullPath(resolved);
    }

    private static string ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            info.Refresh();
            if (info.LinkTarget is null)
            {
                return path;
            }

            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? path : Path.GetFullPath(target.FullName);
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }
    }
}
