using System.IO;
using System.Text.Json;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Security;
using LoLKarmaBook.Riot;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// The Riot API key the user typed, plus how they want the lookup narrowed. The
/// key is only ever written DPAPI-protected, never logged, and is loaded lazily so
/// a corrupt file surfaces as a message instead of crashing startup.
/// </summary>
public sealed class RiotKeyStore
{
    private readonly string _path;
    private readonly DpapiTextProtector _protector;
    private readonly DiagnosticLog _log;

    public RiotKeyStore(AppPaths paths, DpapiTextProtector protector, DiagnosticLog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.Root, "riot-api.json");
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string FilePath => _path;

    /// <summary>A saved key that could not be read, phrased for the UI. Null when nothing failed.</summary>
    public string? LoadError { get; private set; }

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string? ProtectedKey { get; set; }
        public string PlatformId { get; set; } = "EUW1";
        public string RiotId { get; set; } = string.Empty;
        public int MatchCount { get; set; } = 20;
        public int? QueueId { get; set; } = 420;
        public DateTimeOffset SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };


    public RiotLookupPreferences Load()
    {
        LoadError = null;
        if (!File.Exists(_path))
        {
            return new RiotLookupPreferences();
        }

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(_path), ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warn("riot", "读取 Riot API 设置失败：" + ex.GetType().Name);
            LoadError = "已保存的 Riot API 设置无法读取，已按空设置处理。";
            return new RiotLookupPreferences();
        }

        if (envelope is null)
        {
            return new RiotLookupPreferences();
        }

        string? key = null;
        if (!string.IsNullOrEmpty(envelope.ProtectedKey))
        {
            try
            {
                key = _protector.Unprotect(envelope.ProtectedKey);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                           or PlatformNotSupportedException or FormatException)
            {
                _log.Warn("riot", "Riot API Key 无法解密：" + ex.GetType().Name);
                LoadError = "已保存的 Riot API Key 无法用本机 DPAPI 解密（可能来自其他 Windows 账号或电脑），请重新填写。";
            }
        }

        var preferences = new RiotLookupPreferences(
            key,
            string.IsNullOrWhiteSpace(envelope.PlatformId) ? "EUW1" : envelope.PlatformId,
            envelope.RiotId ?? string.Empty,
            envelope.MatchCount,
            envelope.QueueId);
        return preferences;
    }

    /// <summary>
    /// Persists the key and the lookup choices. A blank key removes the stored
    /// secret but keeps the platform and Riot ID, so the next visit only asks for
    /// the key. Throws on failure; the caller reports it and never claims success.
    /// </summary>
    public void Save(string? apiKey, RiotLookupPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        // Only ever pass plaintext here; DPAPI seals it before it touches disk.
        string trimmed = (apiKey ?? string.Empty).Trim();

        var envelope = new Envelope
        {
            ProtectedKey = trimmed.Length == 0 ? null : _protector.Protect(trimmed),
            PlatformId = RiotPlatforms.Normalize(preferences.PlatformId) ?? "EUW1",
            RiotId = preferences.RiotId.Trim(),
            MatchCount = Math.Clamp(preferences.MatchCount, 1, 100),
            QueueId = preferences.QueueId,
            SavedAt = DateTimeOffset.UtcNow
        };

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target and move into place so a crash mid-write cannot
        // leave a half file that the next start fails to parse.
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(envelope, WriteOptions));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>The lookup choices the estimate page remembers. The key may be null.</summary>
public sealed record RiotLookupPreferences(
    string? ApiKey = null,
    string PlatformId = "EUW1",
    string RiotId = "",
    int MatchCount = 20,
    int? QueueId = 420)
{
    /// <summary>The same choices with the key stripped, for anything that leaves this class.</summary>
    public RiotLookupPreferences WithoutKey() => this with { ApiKey = null };
}
