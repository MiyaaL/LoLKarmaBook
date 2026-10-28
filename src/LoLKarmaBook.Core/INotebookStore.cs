namespace LoLKarmaBook.Core;

/// <summary>
/// Persistence contract consumed by <see cref="NotebookService"/>. Every store
/// instance is scoped to exactly one profile/space. Implementations must enforce
/// atomic note+category+player writes, idempotent request ids and revision
/// checks.
/// </summary>
public interface INotebookStore
{
    Task<StoredNote> CreateNoteAsync(NewNoteRecord record, CancellationToken cancellationToken = default);

    Task<StoredNote> UpdateNoteAsync(NoteUpdateRecord record, CancellationToken cancellationToken = default);

    Task<StoredNote> SetNoteStatusAsync(
        Guid noteId,
        NoteStatus status,
        int expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<StoredNote?> GetNoteAsync(Guid noteId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredNote>> ListNotesAsync(
        NoteQuery query,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteNoteAsync(Guid noteId, CancellationToken cancellationToken = default);

    Task<Guid?> FindPlayerIdAsync(PlayerIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns Active notes for the player, excluding notes saved in the current
    /// session. The caller still applies category expiry and audience filters.
    /// </summary>
    Task<IReadOnlyList<StoredNote>> ListReminderCandidatesAsync(
        PlayerIdentity identity,
        DateTimeOffset now,
        string? currentExternalMatchId,
        string currentLocalSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns true only when a new receipt row was inserted.</summary>
    Task<bool> TryCreateAlertReceiptAsync(
        string sessionKey,
        string localSessionId,
        Guid playerId,
        ReminderReceiptState state,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<bool> SetAlertReceiptStateAsync(
        string sessionKey,
        string localSessionId,
        Guid playerId,
        ReminderReceiptState state,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<ReminderReceiptState?> GetAlertReceiptStateAsync(
        string sessionKey,
        Guid playerId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes all notes, categories, receipts, players and journal rows
    /// in this profile. Never touches another profile database.</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}

public sealed record NewNoteRecord(
    string RequestId,
    string RequestFingerprint,
    PlayerIdentity? Identity,
    string? SourceLocalSessionId,
    string? ExternalMatchId,
    SourceKind SourceKind,
    DateTimeOffset EventTime,
    string Body,
    IReadOnlyList<NoteCategoryEntry> Categories,
    DateTimeOffset CreatedAt,
    NoteStatus Status,
    int ReminderDays,
    string? PlayerDisplayName = null,
    IReadOnlyList<NoteTag>? Tags = null);

public sealed record NoteUpdateRecord(
    Guid NoteId,
    int ExpectedRevision,
    DateTimeOffset EventTime,
    string Body,
    IReadOnlyList<NoteCategoryEntry> Categories,
    DateTimeOffset UpdatedAt,
    int ReminderDays,
    IReadOnlyList<NoteTag>? Tags = null);
