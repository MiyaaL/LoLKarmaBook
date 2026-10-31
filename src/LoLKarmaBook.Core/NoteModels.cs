using System.Collections.ObjectModel;

namespace LoLKarmaBook.Core;

public enum NoteStatus
{
    Draft = 1,
    Active = 2,
    Archived = 3,
    Retracted = 4
}

public enum NoteSaveMode
{
    Active = 1,
    Draft = 2
}

public enum ReminderReceiptState
{
    Shown = 1,
    Ignored = 2
}

/// <summary>
/// Input for a new note. Validation and identity resolution happen in
/// <see cref="NotebookService"/> before any transaction starts.
/// </summary>
public sealed record NoteDraft(
    string RequestId,
    IReadOnlyList<NoteCategory> Categories,
    string? Body = null,
    DateTimeOffset? EventTime = null,
    int ReminderDays = NoteCategories.DefaultReminderDays,
    IReadOnlyList<NoteTag>? Tags = null,
    bool MergeWithExisting = false);

/// <summary>Input for editing an existing active/draft note.</summary>
public sealed record NoteEdit(
    IReadOnlyList<NoteCategory> Categories,
    string? Body = null,
    DateTimeOffset? EventTime = null,
    int ReminderDays = NoteCategories.DefaultReminderDays,
    IReadOnlyList<NoteTag>? Tags = null);

public sealed record NoteCategoryEntry(
    NoteCategory Category,
    DateTimeOffset ReminderExpiresAt);

/// <summary>
/// A persisted note. It deliberately contains no historical display name: the UI
/// shows local ids, categories and dates. Current names may only come from the
/// current session.
/// </summary>
public sealed class StoredNote
{
    public Guid NoteId { get; }
    public string RequestId { get; }
    public Guid? PlayerId { get; }
    public IReadOnlyList<NoteCategoryEntry> Categories { get; }
    public string Body { get; }
    public DateTimeOffset EventTime { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public NoteStatus Status { get; }
    public int Revision { get; }
    public string? SourceLocalSessionId { get; }
    public string? ExternalMatchId { get; }

    /// <summary>
    /// Where the note was authored. Manual notes never auto-match, regardless of
    /// how a later session is labeled.
    /// </summary>
    public SourceKind SourceKind { get; }

    /// <summary>
    /// The player's in-game id (name#tag) as seen when the note was written, so a
    /// record can be identified by the person instead of an internal number.
    /// Null for records saved before this field existed.
    /// </summary>
    public string? PlayerDisplayName { get; }

    /// <summary>Official champion/item tags attached to this record.</summary>
    public IReadOnlyList<NoteTag> Tags { get; }

    public StoredNote(
        Guid noteId,
        string requestId,
        Guid? playerId,
        IEnumerable<NoteCategoryEntry> categories,
        string body,
        DateTimeOffset eventTime,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        NoteStatus status,
        int revision,
        string? sourceLocalSessionId,
        string? externalMatchId,
        SourceKind sourceKind = SourceKind.Synthetic,
        string? playerDisplayName = null,
        IEnumerable<NoteTag>? tags = null)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("A request id is required.", nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(categories);
        NoteCategoryEntry[] copied = categories
            .OrderBy(entry => entry.Category)
            .ToArray();
        if (copied.Length == 0)
        {
            throw new ArgumentException("A note must have at least one category.", nameof(categories));
        }

        for (int i = 1; i < copied.Length; i++)
        {
            if (copied[i - 1].Category == copied[i].Category)
            {
                throw new ArgumentException("Duplicate note category.", nameof(categories));
            }
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown note status.");
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision must be at least 1.");
        }

        NoteId = noteId;
        RequestId = requestId;
        PlayerId = playerId;
        Categories = new ReadOnlyCollection<NoteCategoryEntry>(copied);
        Body = body ?? string.Empty;
        EventTime = eventTime;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Status = status;
        Revision = revision;
        SourceLocalSessionId = sourceLocalSessionId;
        ExternalMatchId = externalMatchId;
        SourceKind = sourceKind;
        PlayerDisplayName = string.IsNullOrWhiteSpace(playerDisplayName) ? null : playerDisplayName.Trim();
        Tags = new ReadOnlyCollection<NoteTag>((tags ?? Array.Empty<NoteTag>()).ToArray());
    }

    public bool IsActive(DateTimeOffset now) =>
        Status == NoteStatus.Active && GetActiveCategories(now).Count > 0;

    public IReadOnlyList<NoteCategory> GetActiveCategories(DateTimeOffset now)
    {
        var active = new List<NoteCategory>();
        foreach (NoteCategoryEntry entry in Categories)
        {
            if (entry.ReminderExpiresAt > now)
            {
                active.Add(entry.Category);
            }
        }

        return active;
    }

    /// <summary>
    /// Counts distinct recorded matches/sessions for the reminder summary. A
    /// multi-category note still counts once.
    /// </summary>
    public string DeduplicationSessionKey =>
        !string.IsNullOrWhiteSpace(ExternalMatchId)
            ? "ext:" + ExternalMatchId
            : !string.IsNullOrWhiteSpace(SourceLocalSessionId)
                ? "local:" + SourceLocalSessionId
                : "note:" + NoteId.ToString("N");
}

public sealed record NoteQuery
{
    public IReadOnlyCollection<NoteCategory>? Categories { get; init; }
    public NoteStatus? Status { get; init; }
    public Guid? PlayerId { get; init; }
    public DateTimeOffset? EventFrom { get; init; }
    public DateTimeOffset? EventTo { get; init; }

    public static NoteQuery All { get; } = new();
}

public enum ReminderAudience
{
    Both = 1,
    AlliesOnly = 2
}

public sealed record ReminderOptions
{
    public ReminderAudience Audience { get; init; } = ReminderAudience.Both;
    public IReadOnlyCollection<Guid>? MutedPlayerIds { get; init; }

    public static ReminderOptions Default { get; } = new();
}

public sealed class ReminderNote
{
    public Guid NoteId { get; }
    public IReadOnlyList<NoteCategory> Categories { get; }
    public DateTimeOffset EventTime { get; }
    public string? SessionKey { get; }

    public ReminderNote(
        Guid noteId,
        IEnumerable<NoteCategory> categories,
        DateTimeOffset eventTime,
        string? sessionKey)
    {
        ArgumentNullException.ThrowIfNull(categories);
        NoteId = noteId;
        Categories = new ReadOnlyCollection<NoteCategory>(
            categories.Distinct().OrderBy(category => category).ToArray());
        EventTime = eventTime;
        SessionKey = sessionKey;
    }
}

public sealed class Reminder
{
    public Guid PlayerId { get; }
    public PlayerIdentity Identity { get; }
    public string ParticipantKey { get; }
    public ParticipantSide Side { get; }
    public string? CurrentDisplayId { get; }
    public IReadOnlyList<NoteCategory> ActiveCategories { get; }
    public IReadOnlyList<ReminderNote> Notes { get; }
    public DateTimeOffset MostRecentEventTime { get; }
    public int DistinctSessionCount { get; }

    public Reminder(
        Guid playerId,
        PlayerIdentity identity,
        string participantKey,
        ParticipantSide side,
        string? currentDisplayId,
        IEnumerable<NoteCategory> activeCategories,
        IEnumerable<ReminderNote> notes,
        DateTimeOffset mostRecentEventTime,
        int distinctSessionCount)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(activeCategories);
        ArgumentNullException.ThrowIfNull(notes);
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("A player id is required.", nameof(playerId));
        }

        if (string.IsNullOrWhiteSpace(participantKey))
        {
            throw new ArgumentException("A participant key is required.", nameof(participantKey));
        }

        PlayerId = playerId;
        Identity = identity;
        ParticipantKey = participantKey;
        Side = side;
        CurrentDisplayId = currentDisplayId;
        ActiveCategories = new ReadOnlyCollection<NoteCategory>(
            activeCategories.Distinct().OrderBy(category => category).ToArray());
        Notes = new ReadOnlyCollection<ReminderNote>(notes.ToArray());
        MostRecentEventTime = mostRecentEventTime;
        DistinctSessionCount = distinctSessionCount;
    }
}

public enum ReminderSupport
{
    Enabled = 1,
    DisabledManual = 2,
    UnsupportedSource = 4,
    DisabledPhase = 5
}

public sealed class ReminderEvaluation
{
    public ReminderSupport Support { get; }
    public bool IsEnabled => Support == ReminderSupport.Enabled;
    public IReadOnlyList<Reminder> Reminders { get; }
    public string Reason { get; }

    public ReminderEvaluation(
        ReminderSupport support,
        IEnumerable<Reminder>? reminders,
        string reason)
    {
        Support = support;
        Reminders = new ReadOnlyCollection<Reminder>((reminders ?? Array.Empty<Reminder>()).ToArray());
        Reason = reason ?? string.Empty;
    }

    public static ReminderEvaluation Enabled(IEnumerable<Reminder> reminders) =>
        new(ReminderSupport.Enabled, reminders, "Reminders are enabled.");

    public static ReminderEvaluation DisabledManual() =>
        new(
            ReminderSupport.DisabledManual,
            Array.Empty<Reminder>(),
            "Manual sessions only manage local notes; they never auto-match.");

    public static ReminderEvaluation Unsupported() =>
        new(
            ReminderSupport.UnsupportedSource,
            Array.Empty<Reminder>(),
            "The source kind is not supported by this build.");

    public static ReminderEvaluation DisabledForPhase(GamePhase phase) =>
        new(
            ReminderSupport.DisabledPhase,
            Array.Empty<Reminder>(),
            $"Phase {phase} has no active match roster for reminders.");
}
