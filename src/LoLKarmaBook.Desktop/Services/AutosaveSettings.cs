using System.IO;
using System.Text.Json;
using LoLKarmaBook.Desktop.Infrastructure;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// Unsaved form text (category selection, note body, event time) written
/// continuously so a crash, a mistaken close or a disk error cannot silently lose
/// what the user typed. The journal is stored in the profile directory and its
/// payload is DPAPI-protected with the same protector used for stored notes; there
/// is no plaintext fallback. If a write fails, the caller surfaces the error to
/// the user and it is never reported as a successful autosave.
/// </summary>
public sealed class AutosaveJournal
{
    private readonly string _path;
    private readonly Security.DpapiTextProtector _protector;
    private readonly DiagnosticLog _log;
    private readonly object _gate = new();

    public AutosaveJournal(string profileDirectory, Security.DpapiTextProtector protector, DiagnosticLog log)
    {
        Directory.CreateDirectory(profileDirectory);
        _path = Path.Combine(profileDirectory, "autosave-draft.json");
        _protector = protector;
        _log = log;
    }

    public string FilePath => _path;

    public sealed class Draft
    {
        public string Scenario { get; set; } = "";
        public string ParticipantKey { get; set; } = "";
        public List<string> Categories { get; set; } = new();
        public string? Body { get; set; }

        /// <summary>Official champion/item tags selected in the editor.</summary>
        public List<DraftTag> Tags { get; set; } = new();

        public long? EventTimeUnixSeconds { get; set; }
        public int ReminderDays { get; set; } = 365;
        public DateTimeOffset SavedAt { get; set; }
        public Guid? EditingNoteId { get; set; }

        /// <summary>
        /// Idempotency id of the save request that was pending when this draft was
        /// written. Reusing it after a restart makes an uncertain save replay as the
        /// same record instead of creating a duplicate.
        /// </summary>
        public string? PendingRequestId { get; set; }
    }

    /// <summary>Serializable form of one official tag (kind + official id + name).</summary>
    public sealed class DraftTag
    {
        public int Kind { get; set; }
        public int RefId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string? ProtectedPayload { get; set; }
        public DateTimeOffset SavedAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
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

    /// <summary>Persists the draft. Throws on failure so the UI can report it.</summary>
    public void Save(Draft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string payload = JsonSerializer.Serialize(draft, JsonOptions);
        var envelope = new Envelope
        {
            ProtectedPayload = _protector.Protect(payload),
            SavedAt = DateTimeOffset.UtcNow
        };

        lock (_gate)
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(envelope, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            string archive = ContextPath(draft.Scenario);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            File.Copy(_path, archive, overwrite: true);
        }
    }

    public Draft? Load(string? context = null)
    {
        Draft? primary = ReadDraft(_path);
        if (context is null || primary?.Scenario == context) return primary;
        return ReadDraft(ContextPath(context));
    }

    private string ContextPath(string context)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(context))).ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(_path)!, "drafts", hash + ".json");
    }

    private Draft? ReadDraft(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            Envelope? envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), ReadOptions);
            if (envelope?.ProtectedPayload is null)
            {
                return null;
            }

            Draft? draft = JsonSerializer.Deserialize<Draft>(
                _protector.Unprotect(envelope.ProtectedPayload),
                ReadOptions);
            return draft;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException
                                       or PlatformNotSupportedException)
        {
            // Keep the file for inspection rather than deleting evidence.
            _log.Warn("autosave", "自动保存草稿无法恢复。", ex);
            return null;
        }
    }

    public void Clear(string? context = null)
    {
        lock (_gate)
        {
            try
            {
                Draft? primary = ReadDraft(_path);
                string? targetContext = context ?? primary?.Scenario;
                if (context is null || primary?.Scenario == context) File.Delete(_path);
                if (targetContext is not null) File.Delete(ContextPath(targetContext));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("autosave", "自动保存草稿清理失败。", ex);
            }
        }
    }
}
