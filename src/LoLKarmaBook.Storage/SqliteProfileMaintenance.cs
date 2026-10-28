using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Storage;

/// <summary>
/// Same-machine, profile-bound SQLite backup and restore. The online backup API
/// produces a consistent snapshot (WAL-safe), and both directions verify the
/// profile id marker before writing so one space cannot be restored into another.
/// Restore fully validates the backup (schema version, required tables/columns,
/// and decryption of the protected identity lookup key with the caller's
/// protector) before it touches the target database. Callers must close the
/// profile's <see cref="SqliteProfileStore"/> before restoring and reopen it
/// afterwards: the identity HMAC lookup key is cached per open store, so an
/// already-open store keeps using the old key after its database is replaced.
/// </summary>
public static class SqliteProfileMaintenance
{
    public static async Task BackupAsync(
        string databasePath,
        string destinationPath,
        string expectedProfileId,
        CancellationToken cancellationToken = default)
    {
        string profileId = ProfilePaths.ValidateProfileId(expectedProfileId);
        string sourcePath = RequirePath(databasePath, nameof(databasePath));
        string targetPath = RequirePath(destinationPath, nameof(destinationPath));

        if (PathsEqual(sourcePath, targetPath))
        {
            throw new ArgumentException("The backup destination must differ from the source database.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source profile database does not exist.", sourcePath);
        }

        if (File.Exists(targetPath))
        {
            throw new IOException("The backup destination already exists; choose a new file.");
        }

        string? targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await VerifyProfileAsync(sourcePath, profileId, cancellationToken).ConfigureAwait(false);

        bool completed = false;
        try
        {
            await using (SqliteConnection source = await OpenAsync(
                             sourcePath,
                             SqliteOpenMode.ReadOnly,
                             cancellationToken).ConfigureAwait(false))
            await using (SqliteConnection destination = await OpenAsync(
                             targetPath,
                             SqliteOpenMode.ReadWriteCreate,
                             cancellationToken).ConfigureAwait(false))
            {
                source.BackupDatabase(destination);
                await CheckpointAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            await VerifyProfileAsync(targetPath, profileId, cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(targetPath);
            }
        }
    }

    /// <summary>
    /// Replaces <paramref name="targetDatabasePath"/> with the backup contents.
    /// The backup is validated completely (profile marker, SQLite integrity,
    /// schema version, required tables/columns, and a 32-byte identity lookup
    /// key that the supplied <paramref name="protector"/> can decrypt) before
    /// the target is opened for writing; any validation failure leaves the
    /// target untouched. Callers must close the profile's store before calling
    /// and reopen it afterwards, because the store caches the lookup key.
    /// </summary>
    public static async Task RestoreAsync(
        string backupPath,
        string targetDatabasePath,
        string expectedProfileId,
        ITextProtector protector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        await RestoreCoreAsync(backupPath, targetDatabasePath, expectedProfileId, protector, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RestoreCoreAsync(
        string backupPath,
        string targetDatabasePath,
        string expectedProfileId,
        ITextProtector protector,
        CancellationToken cancellationToken)
    {
        string profileId = ProfilePaths.ValidateProfileId(expectedProfileId);
        string sourcePath = RequirePath(backupPath, nameof(backupPath));
        string targetPath = RequirePath(targetDatabasePath, nameof(targetDatabasePath));

        if (PathsEqual(sourcePath, targetPath))
        {
            throw new ArgumentException("The restore source and target must differ.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The backup file does not exist.", sourcePath);
        }

        // --- All validation happens before the target is opened for writing. ---
        await VerifyRestorableBackupAsync(
                sourcePath,
                profileId,
                protector,
                cancellationToken)
            .ConfigureAwait(false);

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            await VerifyProfileAsync(targetPath, profileId, cancellationToken).ConfigureAwait(false);
        }

        string? targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        await using (SqliteConnection source = await OpenAsync(
                         sourcePath,
                         SqliteOpenMode.ReadOnly,
                         cancellationToken).ConfigureAwait(false))
        await using (SqliteConnection target = await OpenAsync(
                         targetPath,
                         SqliteOpenMode.ReadWriteCreate,
                         cancellationToken).ConfigureAwait(false))
        {
            source.BackupDatabase(target);
            await CheckpointAsync(target, cancellationToken).ConfigureAwait(false);
        }

        await VerifyProfileAsync(targetPath, profileId, cancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
    }

    public static async Task<string> ReadProfileIdAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        string path = RequirePath(databasePath, nameof(databasePath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The profile database does not exist.", path);
        }

        await using SqliteConnection connection = await OpenAsync(
                path,
                SqliteOpenMode.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'profile_id' LIMIT 1;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException("The database is missing its profile id marker.");
        }

        return Convert.ToString(result, CultureInfo.InvariantCulture)!;
    }

    private static async Task VerifyProfileAsync(
        string databasePath,
        string expectedProfileId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(
                databasePath,
                SqliteOpenMode.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        using (SqliteCommand profile = connection.CreateCommand())
        {
            profile.CommandText = "SELECT value FROM meta WHERE key = 'profile_id' LIMIT 1;";
            object? result = await profile.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null or DBNull)
            {
                throw new InvalidOperationException("The database is missing its profile id marker.");
            }

            string actual = Convert.ToString(result, CultureInfo.InvariantCulture)!;
            if (!string.Equals(actual, expectedProfileId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The database belongs to profile '{actual}', not '{expectedProfileId}'.");
            }
        }

        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            object? result = await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            string status = Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite integrity check failed: {status}");
            }
        }
    }

    /// <summary>
    /// Schema version accepted by restore. Kept in sync with
    /// <c>SqliteProfileStore</c> (currently 4) via the storage test suite.
    /// </summary>
    internal const int RequiredSchemaVersion = 4;

    /// <summary>
    /// The oldest backup this build still restores. Older files are opened by the
    /// store, which upgrades them in place, so a v2 backup stays usable.
    /// </summary>
    internal const int MinRestorableSchemaVersion = 2;

    /// <summary>
    /// The required columns per table for schema version
    /// <see cref="RequiredSchemaVersion"/>.
    /// </summary>
    private static readonly (string Table, string[] Columns)[] RequiredColumns =
    {
        ("meta", new[] { "key", "value" }),
        ("players", new[]
        {
            "player_id", "operator", "identity_namespace", "realm_scope", "id_kind",
            "value_protected", "value_lookup", "first_seen", "last_seen"
        }),
        ("notes", new[]
        {
            "note_id", "request_id", "request_fingerprint", "player_id",
            "source_local_session_id", "external_match_id", "body_protected",
            "event_time", "created_at", "updated_at", "status", "revision",
            "reminder_days", "source_kind", "player_display_name_protected"
        }),
        ("note_categories", new[] { "note_id", "category", "reminder_expires_at" }),
        ("note_tags", new[] { "note_id", "kind", "ref_id", "official_name" }),
        ("alert_receipts", new[]
        {
            "receipt_id", "session_key", "local_session_id", "player_id", "state", "created_at"
        }),
        ("change_journal", new[] { "journal_id", "note_id", "action", "created_at", "detail" })
    };

    private static async Task VerifyRestorableBackupAsync(
        string databasePath,
        string expectedProfileId,
        ITextProtector protector,
        CancellationToken cancellationToken)
    {
        await VerifyProfileAsync(databasePath, expectedProfileId, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteConnection connection = await OpenAsync(
                databasePath,
                SqliteOpenMode.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        string? versionText = await ReadMetaValueAsync(
                connection,
                "schema_version",
                cancellationToken)
            .ConfigureAwait(false);
        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
        {
            throw new InvalidOperationException("The backup schema version is missing or invalid.");
        }

        if (version is < MinRestorableSchemaVersion or > RequiredSchemaVersion)
        {
            throw new InvalidOperationException(
                $"The backup uses schema version {version}, but this build restores " +
                $"{MinRestorableSchemaVersion} to {RequiredSchemaVersion}. The target was not modified.");
        }

        // v3 added the player display name; v4 added official tags. Older backups
        // are upgraded in place by the store when opened, so their missing tables
        // and columns are not treated as corruption.
        bool requiresDisplayName = version >= 3;
        bool requiresTags = version >= 4;

        // Required tables and columns: a file with the right marker but a
        // missing/mismatched schema must not replace a healthy target.
        foreach ((string table, string[] columns) in RequiredColumns)
        {
            if (!requiresTags && table == "note_tags")
            {
                continue;
            }

            HashSet<string> actual = await ReadColumnsAsync(connection, table, cancellationToken)
                .ConfigureAwait(false);
            if (actual.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The backup is missing the required table '{table}'.");
            }

            foreach (string column in columns)
            {
                if (!requiresDisplayName && column == "player_display_name_protected")
                {
                    continue;
                }

                if (!actual.Contains(column))
                {
                    throw new InvalidOperationException(
                        $"The backup table '{table}' is missing the required column '{column}'.");
                }
            }
        }

        // Every restore must validate the protected lookup key before opening the target.
        string? protectedKey = await ReadMetaValueAsync(
                connection,
                "identity_lookup_key",
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            throw new InvalidOperationException(
                "The backup is missing its identity lookup key. The target was not modified.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(protector.Unprotect(protectedKey));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "The backup's identity lookup key cannot be decrypted with the supplied protector. " +
                "The target was not modified.",
                ex);
        }

        try
        {
            if (key.Length != 32)
                throw new InvalidOperationException(
                    "The backup's identity lookup key has an invalid length. The target was not modified.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand command = connection.CreateCommand();
        // Table name is a compile-time constant from RequiredColumns, not user input.
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<string?> ReadMetaValueAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static async Task CheckpointAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenAsync(
        string path,
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string RequirePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            TryDeleteFile(path + "-wal");
            TryDeleteFile(path + "-shm");
        }
        catch
        {
            // Best-effort cleanup of a failed backup; the original exception wins.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort.
        }
    }
}
