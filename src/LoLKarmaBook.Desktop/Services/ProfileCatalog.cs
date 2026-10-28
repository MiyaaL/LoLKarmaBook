using System.IO;
using System.Text.RegularExpressions;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>Built-in spaces. Demo and real-looking notes never share a database.</summary>
public sealed class ProfileDescriptor
{
    public ProfileDescriptor(string id, string title, string description, bool isDemo)
    {
        Id = id;
        Title = title;
        Description = description;
        IsDemo = isDemo;
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>Demo spaces are for the synthetic A/B scenario only.</summary>
    public bool IsDemo { get; }
}

/// <summary>
/// Discovers profile databases under the data root. Only directories whose name
/// is a valid storage profile id are listed; anything else is ignored rather than
/// opened.
/// </summary>
public static partial class ProfileCatalog
{
    public const string DefaultProfileId = "default";
    public const string DemoProfileId = "synthetic-demo";

    public static IReadOnlyList<ProfileDescriptor> BuiltIn { get; } = new[]
    {
        new ProfileDescriptor(
            DefaultProfileId,
            "默认空间",
            "等待自动连接 LOL 账号；连接后使用该账号的独立记录空间。",
            isDemo: false),
};

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdPattern();

    public static bool IsValidProfileId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ProfileIdPattern().IsMatch(id);

    /// <summary>Built-in entries first, then any additional valid profile directories.</summary>
    public static IReadOnlyList<ProfileDescriptor> Discover(string dataRoot, IReadOnlyDictionary<string, string>? databaseOverrides = null)
    {
        var result = new List<ProfileDescriptor>(BuiltIn);
        foreach (var pair in databaseOverrides ?? new Dictionary<string, string>())
        {
            if (!IsValidProfileId(pair.Key) || !File.Exists(pair.Value) || result.Any(p => p.Id == pair.Key)) continue;
            result.Add(new ProfileDescriptor(pair.Key, pair.Key.StartsWith("account-", StringComparison.Ordinal)
                ? "账号空间" : "空间 " + pair.Key, "", false));
        }
        string profilesRoot = Path.Combine(dataRoot, "profiles");
        if (!Directory.Exists(profilesRoot))
        {
            return result;
        }

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(profilesRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (string dir in dirs.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string id = Path.GetFileName(dir);
            if (!IsValidProfileId(id) || result.Any(p => p.Id == id))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(dir, "lolkarmabook.db")))
            {
                continue;
            }

            result.Add(new ProfileDescriptor(
                id,
                ReadableTitle(id),
                "此电脑上的其他笔记空间。",
                isDemo: false));
        }

        return result;
    }

    /// <summary>"account-&lt;hash&gt;" becomes "账号空间 &lt;short&gt;" so the space list is not a wall of hex.</summary>
    private static string ReadableTitle(string id)
    {
        if (id.StartsWith("account-", StringComparison.Ordinal) && id.Length > 16)
        {
            return "账号空间";
        }

        return "空间 " + id;
    }

    public static string GetProfileDirectory(string dataRoot, string profileId) =>
        Path.Combine(dataRoot, "profiles", profileId);

    public static string GetDatabasePath(string dataRoot, string profileId) =>
        Path.Combine(GetProfileDirectory(dataRoot, profileId), "lolkarmabook.db");
}
