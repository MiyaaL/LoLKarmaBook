using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Hexdata;

/// <summary>
/// Keeps every report the app has ever received: the site's exact JSON plus a small
/// index, so past scores, matches and both teams can be read again without network.
/// </summary>
public sealed class HexdataReportCache
{
    private static readonly JsonSerializerOptions IndexJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _root;
    private readonly object _gate = new();

    public HexdataReportCache(string root)
    {
        _root = root;
    }

    public string Root => _root;

    public string ReportsDirectory => Path.Combine(_root, "reports");

    private string IndexPath => Path.Combine(_root, "index.json");

    /// <summary>Writes the report and returns its index entry. Never overwrites an older copy.</summary>
    public HexdataSavedReport Save(HexdataReport report, string query, DateTimeOffset now)
    {
        Directory.CreateDirectory(ReportsDirectory);
        string stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string slug = Slug(string.IsNullOrWhiteSpace(report.ResolvedGameId) ? query : report.ResolvedGameId);
        string fileName = $"{stamp}-{slug}.json";
        string path = Path.Combine(ReportsDirectory, fileName);
        for (int suffix = 2; File.Exists(path); suffix++)
        {
            fileName = $"{stamp}-{slug}-{suffix}.json";
            path = Path.Combine(ReportsDirectory, fileName);
        }

        File.WriteAllText(path, report.RawJson, new UTF8Encoding(false));

        var entry = new HexdataSavedReport(
            fileName,
            report.ResolvedGameId,
            report.RegionKey,
            report.Estimate?.Score,
            report.Estimate?.Percentile,
            query,
            now,
            report.IsCollecting);

        lock (_gate)
        {
            List<HexdataSavedReport> entries = ReadIndex().ToList();
            entries.Insert(0, entry);
            if (entries.Count > 200)
            {
                entries = entries.Take(200).ToList();
            }

            WriteIndex(entries);
        }

        return entry;
    }

    public IReadOnlyList<HexdataSavedReport> List()
    {
        lock (_gate)
        {
            return ReadIndex();
        }
    }

    /// <summary>Reads a saved report back; null when the file is gone or unreadable.</summary>
    public HexdataReport? Load(string fileName)
    {
        try
        {
            string path = Path.Combine(ReportsDirectory, Path.GetFileName(fileName));
            return File.Exists(path) ? HexdataReport.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HexdataException)
        {
            return null;
        }
    }

    public IReadOnlyList<HexdataSavedReport> ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return Array.Empty<HexdataSavedReport>();
            }

            return JsonSerializer.Deserialize<List<HexdataSavedReport>>(File.ReadAllText(IndexPath), IndexJson)
                   ?? new List<HexdataSavedReport>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<HexdataSavedReport>();
        }
    }

    private void WriteIndex(List<HexdataSavedReport> entries)
    {
        Directory.CreateDirectory(_root);
        string temporary = IndexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, IndexJson), new UTF8Encoding(false));
        File.Move(temporary, IndexPath, overwrite: true);
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-');
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "report" : slug.Length > 60 ? slug[..60] : slug;
    }
}

public sealed record HexdataSavedReport(
    string FileName,
    string GameId,
    string RegionKey,
    double? Score,
    double? Percentile,
    string Query,
    DateTimeOffset SavedAt,
    bool IsCollecting);

/// <summary>Stores the Hexdata session token under DPAPI, next to the other user data.</summary>
public sealed class HexdataSessionStore
{
    private readonly string _path;
    private readonly ITextProtector _protector;

    public HexdataSessionStore(string root, ITextProtector protector)
    {
        _path = Path.Combine(root, "session.json");
        _protector = protector;
    }

    public HexdataSession? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("protectedToken", out JsonElement token) || token.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string value = _protector.Unprotect(token.GetString() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string? email = root.TryGetProperty("email", out JsonElement emailElement) && emailElement.ValueKind == JsonValueKind.String
                ? emailElement.GetString()
                : null;
            return new HexdataSession(value, email);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or System.Security.Cryptography.CryptographicException
                                       or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void Save(string token, string? email)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string json = JsonSerializer.Serialize(new
        {
            protectedToken = _protector.Protect(token),
            email,
            savedAt = DateTimeOffset.UtcNow
        });
        File.WriteAllText(_path, json, new UTF8Encoding(false));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record HexdataSession(string Token, string? Email);
