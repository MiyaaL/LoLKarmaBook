using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Storage;

/// <summary>
/// SQLite implementation of <see cref="INotebookStore"/>, permanently scoped to
/// one profile database. Sensitive text (note body, stable identity value) is
/// protected through the injected <see cref="ITextProtector"/>. Exact identity
/// lookup uses a profile-local HMAC index whose random key is itself stored only
/// protected. Metadata and categories are stored as plaintext; this is not
/// full-database encryption.
/// </summary>
public sealed class SqliteProfileStore : INotebookStore, IAsyncDisposable
{
    /// <summary>
    /// v1: SHA-256 value index (unreleased demo). v2: keyed HMAC identity index
    /// with a random per-profile lookup key protected by the injected protector.
    /// v3: the player's in-game id on each note. v4: official champion/item tags.
    /// </summary>
    private const int SchemaVersion = 4;

    /// <summary>Schema version written by this build.</summary>
    internal static int CurrentSchemaVersion => SchemaVersion;

    internal const string LookupKeyMetaName = "identity_lookup_key";

    private readonly string _databasePath;
    private readonly string _profileId;
    private readonly ITextProtector _protector;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private byte[]? _lookupKey;

    private bool _initialized;
    private bool _disposed;

    public string ProfileId => _profileId;
    public string DatabasePath => _databasePath;

    private SqliteProfileStore(string databasePath, string profileId, ITextProtector protector)
    {
        _databasePath = databasePath;
        _profileId = profileId;
        _protector = protector;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public static async Task<SqliteProfileStore> OpenAsync(
        string rootDirectory,
        string profileId,
        ITextProtector protector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        string validatedId = ProfilePaths.ValidateProfileId(profileId);
        string databasePath = ProfilePaths.GetDatabasePath(rootDirectory, validatedId);
        string profileDirectory = ProfilePaths.GetProfileDirectory(rootDirectory, validatedId);
        Directory.CreateDirectory(profileDirectory);
        return await OpenDatabaseAsync(
                databasePath,
                validatedId,
                protector,
                rootDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a profile database at an explicit path (tests, embedded callers).
    /// The path is still validated: relative paths are rejected, and when
    /// <paramref name="rootDirectory"/> is supplied the database must live under
    /// that root at the profile's canonical location. The stored profile id must
    /// match, so another space's database cannot be opened silently.
    /// </summary>
    public static async Task<SqliteProfileStore> OpenDatabaseAsync(
        string databasePath,
        string profileId,
        ITextProtector protector,
        string? rootDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        string validatedId = ProfilePaths.ValidateProfileId(profileId);
        if (!Path.IsPathFullyQualified(databasePath) && string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "An explicit database path must be absolute.",
                nameof(databasePath));
        }

        string fullPath = Path.GetFullPath(databasePath);
        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            string root = ProfilePaths.NormalizeRoot(rootDirectory);
            ProfilePaths.EnsureUnderRoot(root, fullPath, nameof(databasePath));
            string canonical = ProfilePaths.GetDatabasePath(root, validatedId);
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(canonical, fullPath, comparison))
            {
                throw new ArgumentException(
                    "The database path is not the profile's canonical location under the root.",
                    nameof(databasePath));
            }
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The database path must include a directory.", nameof(databasePath));
        }

        Directory.CreateDirectory(directory);
        var store = new SqliteProfileStore(fullPath, validatedId, protector);
        await store.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<StoredNote> CreateNoteAsync(
        NewNoteRecord record,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(record);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        ExistingRequest? existing = await FindExistingRequestAsync(
                connection,
                transaction,
                record.RequestId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is { } found)
        {
            if (!string.Equals(found.Fingerprint, record.RequestFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Request id '{record.RequestId}' was already used with a different payload.");
            }

            StoredNote existingNote = await LoadNoteAsync(
                    connection,
                    transaction,
                    found.NoteId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The idempotent note could not be loaded.");

            transaction.Commit();
            return existingNote;
        }

        Guid? playerId = null;
        if (record.Identity is not null)
        {
            playerId = await EnsurePlayerAsync(
                    connection,
                    transaction,
                    record.Identity,
                    record.CreatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        Guid noteId = Guid.NewGuid();
        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   """
                   INSERT INTO notes (
                       note_id, request_id, request_fingerprint, player_id,
                       source_local_session_id, external_match_id, body_protected,
                       event_time, created_at, updated_at, status, revision,
                       reminder_days, source_kind, player_display_name_protected)
                   VALUES (
                       $note_id, $request_id, $fingerprint, $player_id,
                       $source_local_session_id, $external_match_id, $body,
                       $event_time, $created_at, $updated_at, $status, 1,
                       $reminder_days, $source_kind, $display_name);
                   """))
        {
            AddParameter(command, "$note_id", noteId.ToString("N"));
            AddParameter(command, "$request_id", record.RequestId);
            AddParameter(command, "$fingerprint", record.RequestFingerprint);
            AddParameter(command, "$player_id", playerId?.ToString("N"));
            AddParameter(command, "$source_local_session_id", record.SourceLocalSessionId);
            AddParameter(command, "$external_match_id", record.ExternalMatchId);
            AddParameter(command, "$body", _protector.Protect(record.Body));
            AddParameter(command, "$display_name", record.PlayerDisplayName is { Length: > 0 } displayName
                ? _protector.Protect(displayName)
                : null);
            AddParameter(command, "$event_time", FormatTime(record.EventTime));
            AddParameter(command, "$created_at", FormatTime(record.CreatedAt));
            AddParameter(command, "$updated_at", FormatTime(record.CreatedAt));
            AddParameter(command, "$status", (int)record.Status);
            AddParameter(command, "$reminder_days", record.ReminderDays);
            AddParameter(command, "$source_kind", (int)record.SourceKind);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCategoriesAsync(
                connection,
                transaction,
                noteId,
                record.Categories,
                cancellationToken)
            .ConfigureAwait(false);

        await InsertTagsAsync(
                connection,
                transaction,
                noteId,
                record.Tags,
                cancellationToken)
            .ConfigureAwait(false);

        await InsertJournalAsync(
                connection,
                transaction,
                noteId,
                "create",
                record.CreatedAt,
                cancellationToken)
            .ConfigureAwait(false);

        StoredNote note = await LoadNoteAsync(connection, transaction, noteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The new note could not be loaded.");

        transaction.Commit();
        return note;
    }

    public async Task<StoredNote> UpdateNoteAsync(
        NoteUpdateRecord record,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(record);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        (NoteStatus Status, int Revision)? current = await ReadStatusAndRevisionAsync(
                connection,
                transaction,
                record.NoteId,
                cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            throw new KeyNotFoundException($"Note '{record.NoteId}' was not found in this profile.");
        }

        if (current.Value.Revision != record.ExpectedRevision)
        {
            throw new InvalidOperationException(
                $"Note revision conflict: expected {record.ExpectedRevision}, found {current.Value.Revision}.");
        }

        if (current.Value.Status is not (NoteStatus.Draft or NoteStatus.Active))
        {
            throw new InvalidOperationException(
                $"Only draft or active notes can be edited (current status: {current.Value.Status}).");
        }

        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   """
                   UPDATE notes
                   SET body_protected = $body,
                       event_time = $event_time,
                       updated_at = $updated_at,
                       revision = revision + 1,
                       reminder_days = $reminder_days
                   WHERE note_id = $note_id;
                   """))
        {
            AddParameter(command, "$body", _protector.Protect(record.Body));
            AddParameter(command, "$event_time", FormatTime(record.EventTime));
            AddParameter(command, "$updated_at", FormatTime(record.UpdatedAt));
            AddParameter(command, "$reminder_days", record.ReminderDays);
            AddParameter(command, "$note_id", record.NoteId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SqliteCommand deleteCategories = CreateCommand(
                   connection,
                   transaction,
                   "DELETE FROM note_categories WHERE note_id = $note_id;"))
        {
            AddParameter(deleteCategories, "$note_id", record.NoteId.ToString("N"));
            await deleteCategories.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SqliteCommand deleteTags = CreateCommand(
                   connection,
                   transaction,
                   "DELETE FROM note_tags WHERE note_id = $note_id;"))
        {
            AddParameter(deleteTags, "$note_id", record.NoteId.ToString("N"));
            await deleteTags.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCategoriesAsync(
                connection,
                transaction,
                record.NoteId,
                record.Categories,
                cancellationToken)
            .ConfigureAwait(false);

        await InsertTagsAsync(
                connection,
                transaction,
                record.NoteId,
                record.Tags,
                cancellationToken)
            .ConfigureAwait(false);

        await InsertJournalAsync(
                connection,
                transaction,
                record.NoteId,
                "edit",
                record.UpdatedAt,
                cancellationToken)
            .ConfigureAwait(false);

        StoredNote note = await LoadNoteAsync(connection, transaction, record.NoteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The updated note could not be loaded.");

        transaction.Commit();
        return note;
    }

    public async Task<StoredNote> SetNoteStatusAsync(
        Guid noteId,
        NoteStatus status,
        int expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown note status.");
        }

        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "Revision must be at least 1.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        (NoteStatus Status, int Revision)? current = await ReadStatusAndRevisionAsync(
                connection,
                transaction,
                noteId,
                cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            throw new KeyNotFoundException($"Note '{noteId}' was not found in this profile.");
        }

        if (current.Value.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                $"Note revision conflict: expected {expectedRevision}, found {current.Value.Revision}.");
        }

        if (!IsAllowedTransition(current.Value.Status, status))
        {
            throw new InvalidOperationException(
                $"Note status transition {current.Value.Status} -> {status} is not allowed.");
        }

        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   """
                   UPDATE notes
                   SET status = $status,
                       revision = revision + 1,
                       updated_at = $updated_at
                   WHERE note_id = $note_id;
                   """))
        {
            AddParameter(command, "$status", (int)status);
            AddParameter(command, "$updated_at", FormatTime(updatedAt));
            AddParameter(command, "$note_id", noteId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertJournalAsync(
                connection,
                transaction,
                noteId,
                status switch
                {
                    NoteStatus.Active => "publish",
                    NoteStatus.Archived => "archive",
                    NoteStatus.Retracted => "retract",
                    _ => "status"
                },
                updatedAt,
                cancellationToken)
            .ConfigureAwait(false);

        StoredNote note = await LoadNoteAsync(connection, transaction, noteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The updated note could not be loaded.");

        transaction.Commit();
        return note;
    }

    public async Task<StoredNote?> GetNoteAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await LoadNoteAsync(connection, null, noteId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredNote>> ListNotesAsync(
        NoteQuery query,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        query ??= NoteQuery.All;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var sql = new StringBuilder("SELECT n.note_id FROM notes n WHERE 1 = 1");
        using SqliteCommand command = CreateCommand(connection, null, string.Empty);

        if (query.Categories is not null)
        {
            if (query.Categories.Count == 0)
            {
                return Array.Empty<StoredNote>();
            }

            sql.Append(
                " AND EXISTS (SELECT 1 FROM note_categories nc " +
                "WHERE nc.note_id = n.note_id AND nc.category IN (");
            int index = 0;
            foreach (NoteCategory category in query.Categories)
            {
                if (index > 0)
                {
                    sql.Append(", ");
                }

                string parameterName = "$category" + index.ToString(CultureInfo.InvariantCulture);
                sql.Append(parameterName);
                AddParameter(command, parameterName, NoteCategories.GetCode(category));
                index++;
            }

            sql.Append("))");
        }

        if (query.Status is { } status)
        {
            sql.Append(" AND n.status = $status");
            AddParameter(command, "$status", (int)status);
        }

        if (query.PlayerId is { } playerId)
        {
            sql.Append(" AND n.player_id = $player_id");
            AddParameter(command, "$player_id", playerId.ToString("N"));
        }

        if (query.EventFrom is { } from)
        {
            sql.Append(" AND n.event_time >= $event_from");
            AddParameter(command, "$event_from", FormatTime(from));
        }

        if (query.EventTo is { } to)
        {
            sql.Append(" AND n.event_time <= $event_to");
            AddParameter(command, "$event_to", FormatTime(to));
        }

        sql.Append(" ORDER BY n.event_time DESC, n.created_at DESC;");
        command.CommandText = sql.ToString();

        var ids = new List<Guid>();
        using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var notes = new List<StoredNote>(ids.Count);
        foreach (Guid noteId in ids)
        {
            StoredNote? note = await LoadNoteAsync(connection, null, noteId, cancellationToken)
                .ConfigureAwait(false);
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    public async Task<bool> DeleteNoteAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("A note id is required.", nameof(noteId));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        int affected;
        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   "DELETE FROM notes WHERE note_id = $note_id;"))
        {
            AddParameter(command, "$note_id", noteId.ToString("N"));
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        return affected > 0;
    }

    public async Task<Guid?> FindPlayerIdAsync(
        PlayerIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await FindPlayerIdCoreAsync(
                connection,
                null,
                identity,
                ComputeLookup(identity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredNote>> ListReminderCandidatesAsync(
        PlayerIdentity identity,
        DateTimeOffset now,
        string? currentExternalMatchId,
        string currentLocalSessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(currentLocalSessionId))
        {
            throw new ArgumentException(
                "A current local session id is required.",
                nameof(currentLocalSessionId));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        Guid? playerId = await FindPlayerIdCoreAsync(
                connection,
                null,
                identity,
                ComputeLookup(identity),
                cancellationToken)
            .ConfigureAwait(false);
        if (playerId is null)
        {
            return Array.Empty<StoredNote>();
        }

        var ids = new List<Guid>();
        using (SqliteCommand command = CreateCommand(
                   connection,
                   null,
                   """
                   SELECT note_id
                   FROM notes
                   WHERE player_id = $player_id
                     AND status = $status
                     AND source_kind <> $manual_source
                     AND NOT (
                         (source_local_session_id IS NOT NULL
                          AND source_local_session_id <> ''
                          AND source_local_session_id = $local_session_id)
                         OR ($external_match_id IS NOT NULL
                             AND external_match_id IS NOT NULL
                             AND external_match_id <> ''
                             AND external_match_id = $external_match_id)
                     )
                   ORDER BY event_time DESC, created_at DESC;
                   """))
        {
            AddParameter(command, "$player_id", playerId.Value.ToString("N"));
            AddParameter(command, "$status", (int)NoteStatus.Active);
            AddParameter(command, "$manual_source", (int)SourceKind.Manual);
            AddParameter(command, "$local_session_id", currentLocalSessionId.Trim());
            AddParameter(
                command,
                "$external_match_id",
                string.IsNullOrWhiteSpace(currentExternalMatchId)
                    ? null
                    : currentExternalMatchId.Trim());

            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var notes = new List<StoredNote>(ids.Count);
        foreach (Guid noteId in ids)
        {
            StoredNote? note = await LoadNoteAsync(connection, null, noteId, cancellationToken)
                .ConfigureAwait(false);
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        _ = now; // expiry is applied by the service with its injected clock
        return notes;
    }

    public async Task<bool> TryCreateAlertReceiptAsync(
        string sessionKey,
        string localSessionId,
        Guid playerId,
        ReminderReceiptState state,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateReceiptArguments(sessionKey, localSessionId, playerId, state);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = CreateCommand(
            connection,
            null,
            """
            INSERT OR IGNORE INTO alert_receipts (
                session_key, local_session_id, player_id, state, created_at)
            VALUES ($session_key, $local_session_id, $player_id, $state, $created_at);
            """);
        AddParameter(command, "$session_key", sessionKey.Trim());
        AddParameter(command, "$local_session_id", localSessionId.Trim());
        AddParameter(command, "$player_id", playerId.ToString("N"));
        AddParameter(command, "$state", (int)state);
        AddParameter(command, "$created_at", FormatTime(createdAt));

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<bool> SetAlertReceiptStateAsync(
        string sessionKey,
        string localSessionId,
        Guid playerId,
        ReminderReceiptState state,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateReceiptArguments(sessionKey, localSessionId, playerId, state);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = CreateCommand(
            connection,
            null,
            """
            INSERT INTO alert_receipts (
                session_key, local_session_id, player_id, state, created_at)
            VALUES ($session_key, $local_session_id, $player_id, $state, $created_at)
            ON CONFLICT(session_key, player_id) DO UPDATE SET
                local_session_id = excluded.local_session_id,
                state = excluded.state,
                created_at = excluded.created_at;
            """);
        AddParameter(command, "$session_key", sessionKey.Trim());
        AddParameter(command, "$local_session_id", localSessionId.Trim());
        AddParameter(command, "$player_id", playerId.ToString("N"));
        AddParameter(command, "$state", (int)state);
        AddParameter(command, "$created_at", FormatTime(createdAt));

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<ReminderReceiptState?> GetAlertReceiptStateAsync(
        string sessionKey,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ArgumentException("A session key is required.", nameof(sessionKey));
        }

        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("A player id is required.", nameof(playerId));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = CreateCommand(
            connection,
            null,
            """
            SELECT state FROM alert_receipts
            WHERE session_key = $session_key AND player_id = $player_id
            LIMIT 1;
            """);
        AddParameter(command, "$session_key", sessionKey.Trim());
        AddParameter(command, "$player_id", playerId.ToString("N"));

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : (ReminderReceiptState)Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string table in new[]
                 {
                     "alert_receipts",
                     "note_tags",
                     "note_categories",
                     "change_journal",
                     "notes",
                     "players"
                 })
        {
            using SqliteCommand command = CreateCommand(
                connection,
                transaction,
                $"DELETE FROM {table};");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _initializationGate.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        using (SqliteCommand pragma = CreateCommand(connection, null, "PRAGMA journal_mode = WAL;"))
        {
            await pragma.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        bool metaExists = await TableExistsAsync(connection, "meta", cancellationToken)
            .ConfigureAwait(false);

        if (!metaExists)
        {
            int userTableCount = await CountUserTablesAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (userTableCount != 0)
            {
                throw new InvalidOperationException(
                    "The database is not a recognized LoLKarmaBook profile (missing meta table).");
            }

            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string? existingProfile = await ReadMetaAsync(connection, "profile_id", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(existingProfile))
            {
                throw new InvalidOperationException(
                    "The database is missing its profile id marker and cannot be opened.");
            }

            if (!string.Equals(existingProfile, _profileId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The database belongs to profile '{existingProfile}', not '{_profileId}'.");
            }

            string? versionText = await ReadMetaAsync(
                    connection,
                    "schema_version",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                throw new InvalidOperationException("The database schema version is missing or invalid.");
            }

            if (version > SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The database schema version {version} is newer than this build supports ({SchemaVersion}).");
            }

            if (version < SchemaVersion)
            {
                await UpgradeSchemaAsync(connection, version, cancellationToken).ConfigureAwait(false);
            }
        }

        // Load (or lazily create) the profile-local HMAC lookup key. The key never
        // leaves this object unencrypted on disk: meta stores it only through the
        // injected ITextProtector.
        _lookupKey = await LoadOrCreateLookupKeyAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<byte[]> LoadOrCreateLookupKeyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        string? protectedKey = await ReadMetaAsync(connection, LookupKeyMetaName, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(protectedKey))
        {
            byte[] existing;
            try
            {
                existing = Convert.FromBase64String(_protector.Unprotect(protectedKey));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new InvalidOperationException(
                    "The identity lookup key cannot be decrypted with the supplied protector.",
                    ex);
            }

            if (existing.Length != 32)
            {
                throw new InvalidOperationException("The identity lookup key has an invalid length.");
            }

            return existing;
        }

        // Only a brand-new database (no players yet) may create a key.
        int playerCount = await CountPlayersAsync(connection, cancellationToken).ConfigureAwait(false);
        if (playerCount != 0)
        {
            throw new InvalidOperationException(
                "The database is missing its identity lookup key while player rows exist. Refusing to open rather than silently re-keying.");
        }

        byte[] freshKey = RandomNumberGenerator.GetBytes(32);
        using (SqliteCommand insert = CreateCommand(
                   connection,
                   null,
                   "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);"))
        {
            AddParameter(insert, "$key", LookupKeyMetaName);
            AddParameter(insert, "$value", _protector.Protect(Convert.ToBase64String(freshKey)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return freshKey;
    }

    private static async Task<int> CountPlayersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM players;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Applies every schema step from <paramref name="fromVersion"/> to
    /// <see cref="SchemaVersion"/> in one transaction, so a v2 profile reaches v4
    /// without an intermediate reopen and every existing row is kept.
    /// </summary>
    private static async Task UpgradeSchemaAsync(
        SqliteConnection connection,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        // Schema v1 used a bare SHA-256 identity index. This is an unreleased
        // synthetic demo, so instead of silently rewriting user data we refuse to
        // open and leave the file untouched.
        if (fromVersion is not (2 or 3))
        {
            throw new InvalidOperationException(
                $"The database uses schema version {fromVersion}, but this build supports {SchemaVersion}. " +
                "The database was not modified. Back it up and remove the profile directory to start fresh.");
        }

        using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int version = fromVersion;
            if (version == 2)
            {
                // v3 adds the player's in-game id to each note so records can be
                // identified by name instead of an internal number.
                using (SqliteCommand alter = CreateCommand(
                           connection,
                           transaction,
                           "ALTER TABLE notes ADD COLUMN player_display_name_protected TEXT NULL;"))
                {
                    await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                version = 3;
            }

            if (version == 3)
            {
                // v4 adds official champion/item tags; an existing note keeps none.
                using (SqliteCommand create = CreateCommand(
                           connection,
                           transaction,
                           """
                           CREATE TABLE IF NOT EXISTS note_tags (
                               note_id TEXT NOT NULL REFERENCES notes(note_id) ON DELETE CASCADE,
                               kind INTEGER NOT NULL,
                               ref_id INTEGER NOT NULL,
                               official_name TEXT NOT NULL,
                               PRIMARY KEY (note_id, kind, ref_id)
                           );
                           """))
                {
                    await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                version = 4;
            }

            using (SqliteCommand update = CreateCommand(
                       connection,
                       transaction,
                       "UPDATE meta SET value = $version WHERE key = 'schema_version';"))
            {
                AddParameter(update, "$version", version.ToString(CultureInfo.InvariantCulture));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CreateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using (SqliteCommand schema = CreateCommand(
                   connection,
                   null,
                   """
                   CREATE TABLE meta (
                       key TEXT PRIMARY KEY,
                       value TEXT NOT NULL
                   );
                   CREATE TABLE players (
                       player_id TEXT PRIMARY KEY,
                       operator TEXT NOT NULL,
                       identity_namespace TEXT NOT NULL,
                       realm_scope TEXT NOT NULL,
                       id_kind TEXT NOT NULL,
                       value_protected TEXT NOT NULL,
                       value_lookup TEXT NOT NULL,
                       first_seen TEXT NOT NULL,
                       last_seen TEXT NOT NULL,
                       UNIQUE(operator, identity_namespace, realm_scope, id_kind, value_lookup)
                   );
                   CREATE TABLE notes (
                       note_id TEXT PRIMARY KEY,
                       request_id TEXT NOT NULL UNIQUE,
                       request_fingerprint TEXT NOT NULL,
                       player_id TEXT NULL REFERENCES players(player_id) ON DELETE SET NULL,
                       source_local_session_id TEXT NULL,
                       external_match_id TEXT NULL,
                       body_protected TEXT NOT NULL,
                       event_time TEXT NOT NULL,
                       created_at TEXT NOT NULL,
                       updated_at TEXT NOT NULL,
                       status INTEGER NOT NULL,
                       revision INTEGER NOT NULL,
                       reminder_days INTEGER NOT NULL,
                       source_kind INTEGER NOT NULL,
                       player_display_name_protected TEXT NULL
                   );
                   CREATE INDEX ix_notes_player ON notes(player_id, status);
                   CREATE INDEX ix_notes_event ON notes(event_time DESC);
                   CREATE INDEX ix_notes_session ON notes(source_local_session_id, external_match_id);
                   CREATE TABLE note_categories (
                       note_id TEXT NOT NULL REFERENCES notes(note_id) ON DELETE CASCADE,
                       category TEXT NOT NULL,
                       reminder_expires_at TEXT NOT NULL,
                       PRIMARY KEY (note_id, category)
                   );
                   CREATE TABLE note_tags (
                       note_id TEXT NOT NULL REFERENCES notes(note_id) ON DELETE CASCADE,
                       kind INTEGER NOT NULL,
                       ref_id INTEGER NOT NULL,
                       official_name TEXT NOT NULL,
                       PRIMARY KEY (note_id, kind, ref_id)
                   );
                   CREATE TABLE alert_receipts (
                       receipt_id INTEGER PRIMARY KEY AUTOINCREMENT,
                       session_key TEXT NOT NULL,
                       local_session_id TEXT NOT NULL,
                       player_id TEXT NOT NULL REFERENCES players(player_id) ON DELETE CASCADE,
                       state INTEGER NOT NULL,
                       created_at TEXT NOT NULL,
                       UNIQUE (session_key, player_id)
                   );
                   CREATE INDEX ix_receipts_player ON alert_receipts(player_id);
                   CREATE TABLE change_journal (
                       journal_id INTEGER PRIMARY KEY AUTOINCREMENT,
                       note_id TEXT NOT NULL REFERENCES notes(note_id) ON DELETE CASCADE,
                       action TEXT NOT NULL,
                       created_at TEXT NOT NULL,
                       detail TEXT NULL
                   );
                   CREATE INDEX ix_journal_note ON change_journal(note_id);
                   """))
        {
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SqliteCommand meta = CreateCommand(
                   connection,
                   null,
                   """
                   INSERT INTO meta (key, value) VALUES ('schema_version', $version);
                   INSERT INTO meta (key, value) VALUES ('profile_id', $profile_id);
                   """))
        {
            AddParameter(meta, "$version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            AddParameter(meta, "$profile_id", _profileId);
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task<Guid> EnsurePlayerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlayerIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string lookup = ComputeLookup(identity);
        Guid? existing = await FindPlayerIdCoreAsync(
                connection,
                transaction,
                identity,
                lookup,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is { } playerId)
        {
            using SqliteCommand update = CreateCommand(
                connection,
                transaction,
                "UPDATE players SET last_seen = $last_seen WHERE player_id = $player_id;");
            AddParameter(update, "$last_seen", FormatTime(now));
            AddParameter(update, "$player_id", playerId.ToString("N"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return playerId;
        }

        Guid newPlayerId = Guid.NewGuid();
        using SqliteCommand insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO players (
                player_id, operator, identity_namespace, realm_scope, id_kind,
                value_protected, value_lookup, first_seen, last_seen)
            VALUES (
                $player_id, $operator, $namespace, $realm, $kind,
                $value, $lookup, $first_seen, $last_seen);
            """);
        AddParameter(insert, "$player_id", newPlayerId.ToString("N"));
        AddParameter(insert, "$operator", identity.Operator);
        AddParameter(insert, "$namespace", identity.IdentityNamespace);
        AddParameter(insert, "$realm", identity.RealmScope);
        AddParameter(insert, "$kind", identity.Kind);
        AddParameter(insert, "$value", _protector.Protect(identity.Value));
        AddParameter(insert, "$lookup", lookup);
        AddParameter(insert, "$first_seen", FormatTime(now));
        AddParameter(insert, "$last_seen", FormatTime(now));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return newPlayerId;
    }

    private async Task<Guid?> FindPlayerIdCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PlayerIdentity identity,
        string lookup,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            SELECT player_id FROM players
            WHERE operator = $operator
              AND identity_namespace = $namespace
              AND realm_scope = $realm
              AND id_kind = $kind
              AND value_lookup = $lookup
            LIMIT 1;
            """);
        AddParameter(command, "$operator", identity.Operator);
        AddParameter(command, "$namespace", identity.IdentityNamespace);
        AddParameter(command, "$realm", identity.RealmScope);
        AddParameter(command, "$kind", identity.Kind);
        AddParameter(command, "$lookup", lookup);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Guid.Parse(Convert.ToString(result, CultureInfo.InvariantCulture)!);
    }

    private async Task<ExistingRequest?> FindExistingRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "SELECT note_id, request_fingerprint FROM notes WHERE request_id = $request_id LIMIT 1;");
        AddParameter(command, "$request_id", requestId);

        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExistingRequest(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1));
    }

    private async Task InsertCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid noteId,
        IReadOnlyList<NoteCategoryEntry> categories,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categories);
        foreach (NoteCategoryEntry entry in categories)
        {
            using SqliteCommand command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO note_categories (note_id, category, reminder_expires_at)
                VALUES ($note_id, $category, $expires_at);
                """);
            AddParameter(command, "$note_id", noteId.ToString("N"));
            AddParameter(command, "$category", NoteCategories.GetCode(entry.Category));
            AddParameter(command, "$expires_at", FormatTime(entry.ReminderExpiresAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid noteId,
        IReadOnlyList<NoteTag>? tags,
        CancellationToken cancellationToken)
    {
        if (tags is null || tags.Count == 0)
        {
            return;
        }

        foreach (NoteTag tag in tags)
        {
            using SqliteCommand command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO note_tags (note_id, kind, ref_id, official_name)
                VALUES ($note_id, $kind, $ref_id, $official_name);
                """);
            AddParameter(command, "$note_id", noteId.ToString("N"));
            AddParameter(command, "$kind", (int)tag.Kind);
            AddParameter(command, "$ref_id", tag.RefId);
            AddParameter(command, "$official_name", tag.OfficialName);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertJournalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid noteId,
        string action,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO change_journal (note_id, action, created_at, detail)
            VALUES ($note_id, $action, $created_at, NULL);
            """);
        AddParameter(command, "$note_id", noteId.ToString("N"));
        AddParameter(command, "$action", action);
        AddParameter(command, "$created_at", FormatTime(at));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredNote?> LoadNoteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        string noteIdText = noteId.ToString("N");
        string requestId;
        string? playerIdText;
        string bodyProtected;
        DateTimeOffset eventTime;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        NoteStatus status;
        int revision;
        string? sourceLocalSessionId;
        string? externalMatchId;
        SourceKind sourceKind;
        string? displayNameProtected;

        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   """
                   SELECT note_id, request_id, player_id, body_protected, event_time,
                          created_at, updated_at, status, revision,
                          source_local_session_id, external_match_id, source_kind,
                          player_display_name_protected
                   FROM notes
                   WHERE note_id = $note_id
                   LIMIT 1;
                   """))
        {
            AddParameter(command, "$note_id", noteIdText);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            requestId = reader.GetString(1);
            playerIdText = reader.IsDBNull(2) ? null : reader.GetString(2);
            bodyProtected = reader.GetString(3);
            eventTime = ParseTime(reader.GetString(4));
            createdAt = ParseTime(reader.GetString(5));
            updatedAt = ParseTime(reader.GetString(6));
            status = (NoteStatus)reader.GetInt32(7);
            revision = reader.GetInt32(8);
            sourceLocalSessionId = reader.IsDBNull(9) ? null : reader.GetString(9);
            externalMatchId = reader.IsDBNull(10) ? null : reader.GetString(10);
            sourceKind = (SourceKind)reader.GetInt32(11);
            displayNameProtected = reader.IsDBNull(12) ? null : reader.GetString(12);
        }

        var categories = new List<NoteCategoryEntry>();
        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   """
                   SELECT category, reminder_expires_at
                   FROM note_categories
                   WHERE note_id = $note_id
                   ORDER BY category;
                   """))
        {
            AddParameter(command, "$note_id", noteIdText);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string code = reader.GetString(0);
                if (!NoteCategories.TryParseCode(code, out NoteCategory category))
                {
                    throw new InvalidOperationException(
                        $"Unknown category code '{code}' in the profile database.");
                }

                categories.Add(new NoteCategoryEntry(category, ParseTime(reader.GetString(1))));
            }
        }

        var tags = new List<NoteTag>();
        using (SqliteCommand command = CreateCommand(
                   connection,
                   transaction,
                   """
                   SELECT kind, ref_id, official_name
                   FROM note_tags
                   WHERE note_id = $note_id
                   ORDER BY kind, ref_id;
                   """))
        {
            AddParameter(command, "$note_id", noteIdText);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tags.Add(new NoteTag(
                    (TagKind)reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2)));
            }
        }

        return new StoredNote(
            noteId,
            requestId,
            playerIdText is null ? null : Guid.Parse(playerIdText),
            categories,
            _protector.Unprotect(bodyProtected),
            eventTime,
            createdAt,
            updatedAt,
            status,
            revision,
            sourceLocalSessionId,
            externalMatchId,
            sourceKind,
            displayNameProtected is null ? null : _protector.Unprotect(displayNameProtected),
            tags);
    }

    private async Task<(NoteStatus Status, int Revision)?> ReadStatusAndRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid noteId,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            "SELECT status, revision FROM notes WHERE note_id = $note_id LIMIT 1;");
        AddParameter(command, "$note_id", noteId.ToString("N"));

        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ((NoteStatus)reader.GetInt32(0), reader.GetInt32(1));
    }

    private static bool IsAllowedTransition(NoteStatus from, NoteStatus to) =>
        (from, to) switch
        {
            (NoteStatus.Draft, NoteStatus.Active) => true,
            (NoteStatus.Draft, NoteStatus.Archived) => true,
            (NoteStatus.Draft, NoteStatus.Retracted) => true,
            (NoteStatus.Active, NoteStatus.Archived) => true,
            (NoteStatus.Active, NoteStatus.Retracted) => true,
            // Retracting an archived record means the same thing (stop reminders, keep
            // the record) and users reach for it right after archiving.
            (NoteStatus.Archived, NoteStatus.Retracted) => true,
            _ => false
        };

    private static void ValidateReceiptArguments(
        string sessionKey,
        string localSessionId,
        Guid playerId,
        ReminderReceiptState state)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ArgumentException("A session key is required.", nameof(sessionKey));
        }

        if (string.IsNullOrWhiteSpace(localSessionId))
        {
            throw new ArgumentException("A local session id is required.", nameof(localSessionId));
        }

        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("A player id is required.", nameof(playerId));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown receipt state.");
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        AddParameter(command, "$name", tableName);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<int> CountUserTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key LIMIT 1;";
        AddParameter(command, "$key", key);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        return command;
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private string ComputeLookup(PlayerIdentity identity)
    {
        byte[] key = _lookupKey
            ?? throw new InvalidOperationException("The profile store is not initialized.");
        return ComputeLookup(key, identity);
    }

    /// <summary>
    /// Keyed, profile-local HMAC-SHA256 over an unambiguous length-prefixed
    /// encoding of the full identity scope. Used only as an exact-match index; the
    /// original value is still stored protected and is not claimed anonymized.
    /// </summary>
    private static string ComputeLookup(byte[] key, PlayerIdentity identity)
    {
        var builder = new StringBuilder();
        foreach (string part in new[]
                 {
                     identity.Operator,
                     identity.IdentityNamespace,
                     identity.RealmScope,
                     identity.Kind,
                     identity.Value
                 })
        {
            builder.Append(part.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(part);
            builder.Append('|');
        }

        byte[] data = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(HMACSHA256.HashData(key, data));
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct ExistingRequest(Guid NoteId, string Fingerprint);
}
