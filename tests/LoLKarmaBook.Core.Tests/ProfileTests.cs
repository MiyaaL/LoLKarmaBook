using Microsoft.Data.Sqlite;
using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;
using static LoLKarmaBook.Core.Tests.TestSupport;

namespace LoLKarmaBook.Core.Tests;

internal static class ProfileTests
{
    public static async Task ProfilesUseSeparateDatabases_AndDeletesStayScoped()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        var storeA = await OpenStoreAsync(root, "space-a", protector);
        var storeB = await OpenStoreAsync(root, "space-b", protector);
        try
        {
            Check.NotEqual(storeA.DatabasePath, storeB.DatabasePath);
            Check.True(storeA.DatabasePath.Contains("space-a"), "database path should include the profile id");
            Check.True(File.Exists(storeA.DatabasePath), "profile A database must exist");
            Check.True(File.Exists(storeB.DatabasePath), "profile B database must exist");

            var serviceA = new NotebookService(storeA, clock);
            var serviceB = new NotebookService(storeB, clock);

            StoredNote noteA = await SaveXInMatchAAsync(serviceA, scenario);
            StoredNote noteB = await SaveXInMatchAAsync(serviceB, scenario);

            Check.Equal(1, (await serviceA.ListNotesAsync()).Count);
            Check.Equal(1, (await serviceB.ListNotesAsync()).Count);
            Check.Null(
                await serviceA.GetNoteAsync(noteB.NoteId),
                "a note from another profile must not be visible");
            Check.False(
                await serviceA.DeleteNoteAsync(noteB.NoteId),
                "deleting another profile's note must not report success");

            Guid? playerA = await storeA.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity);
            Guid? playerB = await storeB.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity);
            Check.NotNull(playerA);
            Check.NotNull(playerB);
            Check.NotEqual(playerA!.Value, playerB!.Value, "profiles must not share player rows");

            Check.True(await serviceA.DeleteNoteAsync(noteA.NoteId), "profile A delete must succeed");
            Check.Equal(0, (await serviceA.ListNotesAsync()).Count);
            Check.Equal(1, (await serviceB.ListNotesAsync()).Count);
            Check.NotNull(await serviceB.GetNoteAsync(noteB.NoteId));

            Check.Equal(
                1,
                (await serviceB.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "profile B reminders must be unaffected by profile A deletion");
        }
        finally
        {
            await storeA.DisposeAsync();
            await storeB.DisposeAsync();
        }
    }

    public static async Task ProfileValidation_RejectsUnsafeIdsAndPaths()
    {
        foreach (string unsafeId in new[]
                 {
                     "",
                     " ",
                     "../escape",
                     "a/b",
                     "a\\b",
                     ".",
                     "..",
                     ".hidden",
                     "trailing.",
                     "CON",
                     "aux",
                     "nul.txt",
                     "bad:id",
                     "with space",
                     new string('a', 65)
                 })
        {
            Check.Throws<ArgumentException>(
                () => ProfilePaths.ValidateProfileId(unsafeId),
                "unsafe profile id must be rejected: " + unsafeId);
        }

        Check.Equal("valid.Profile_1-2", ProfilePaths.ValidateProfileId("valid.Profile_1-2"));

        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        string root = temp.Combine("root");
        await Check.ThrowsAsync<ArgumentException>(
            () => SqliteProfileStore.OpenAsync(root, "../outside", protector));

        // OpenDatabaseAsync intentionally accepts an arbitrary explicit absolute
        // path (documented embedded/test contract): a valid new path is created
        // with the correct owner marker. Relative paths and invalid profile ids
        // are still rejected before any file is created.
        string arbitrary = temp.Combine("explicit", "arbitrary.db");
        await Check.ThrowsAsync<ArgumentException>(
            () => SqliteProfileStore.OpenDatabaseAsync("relative.db", "valid", protector));
        await Check.ThrowsAsync<ArgumentException>(
            () => SqliteProfileStore.OpenDatabaseAsync(arbitrary, "../bad", protector));
        Check.False(File.Exists(arbitrary), "rejected arguments must not create a database");

        var explicitStore = await SqliteProfileStore.OpenDatabaseAsync(
            arbitrary,
            "valid",
            protector);
        string explicitPath = explicitStore.DatabasePath;
        await explicitStore.DisposeAsync();
        Check.True(File.Exists(explicitPath), "a valid explicit path must be created");
        Check.Equal(
            "valid",
            await SqliteProfileMaintenance.ReadProfileIdAsync(explicitPath),
            "the explicit database must carry the profile owner marker");

        // Owner marker mismatch: the same file cannot be reopened as another profile.
        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileStore.OpenDatabaseAsync(explicitPath, "other", protector),
            "an explicit path owned by another profile must be rejected");
        await using (SqliteConnection probe = new($"Data Source={explicitPath};Pooling=False"))
        {
            await probe.OpenAsync();
            using SqliteCommand command = probe.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'profile_id';";
            Check.Equal("valid", (string?)await command.ExecuteScalarAsync());
        }

        // A canonical path under a root is also accepted and lands in the profile dir.
        Directory.CreateDirectory(root);
        string canonical = ProfilePaths.GetDatabasePath(root, "valid");
        var canonicalStore = await SqliteProfileStore.OpenDatabaseAsync(
            canonical,
            "valid",
            protector,
            root);
        await canonicalStore.DisposeAsync();
        Check.True(File.Exists(canonical), "the canonical path must be created");
        Check.Equal("valid", await SqliteProfileMaintenance.ReadProfileIdAsync(canonical));

        File.WriteAllText(temp.Combine("file-root"), "not a directory");
        await Check.ThrowsAsync<ArgumentException>(
            () => SqliteProfileStore.OpenAsync(temp.Combine("file-root"), "valid", protector));

        Directory.CreateDirectory(root);
        Check.Throws<ArgumentException>(
            () => ProfilePaths.GetDatabasePath(root, "../escape"));

        // OpenAsync must refuse a root whose profiles directory escapes via a
        // link, and must not write the database outside the configured root.
        Directory.CreateDirectory(root);
        string linkedProfiles = Path.Combine(root, ProfilePaths.ProfilesDirectoryName);
        string escapedProfiles = temp.Combine("escaped-profiles");
        try
        {
            Directory.CreateSymbolicLink(linkedProfiles, escapedProfiles);
            await Check.ThrowsAsync<ArgumentException>(
                () => SqliteProfileStore.OpenAsync(root, "linked", protector),
                "a linked profiles directory escaping the root must be rejected");
            Check.False(
                File.Exists(Path.Combine(escapedProfiles, "linked", ProfilePaths.DatabaseFileName)),
                "no database may be created outside the root");
        }
        catch (IOException)
        {
            // Symlinks unsupported on this host; other containment checks still run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }

        string rootWithTrailing = root + Path.DirectorySeparatorChar;
        Check.True(
            ProfilePaths.GetDatabasePath(rootWithTrailing, "alpha")
                .StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "trailing separators must not weaken containment");

        // Path containment rejects siblings that merely share a name prefix
        // (e.g. "<root>-evil" must not pass as being under "<root>").
        Check.Throws<ArgumentException>(
            () => ProfilePaths.EnsureUnderRoot(root, root + "-evil", "test"));
        ProfilePaths.EnsureUnderRoot(root, ProfilePaths.GetDatabasePath(root, "alpha"), "test");

        // A dangling symlink under the profile directory may not escape the root.
        string target = temp.Combine("outside-target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(ProfilePaths.GetProfileDirectory(root, "alpha"), "leak");
        try
        {
            Directory.CreateSymbolicLink(link, target);
            Check.Throws<ArgumentException>(
                () => ProfilePaths.EnsureUnderRoot(root, Path.Combine(link, "x.db"), "test"),
                "a symlink inside the root may not escape it");
        }
        catch (IOException)
        {
            // Symlink creation may be unsupported on the host; containment is
            // still verified with the lexical checks above.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above.
        }
    }

    public static async Task DatabaseBoundToProfile_RejectsMismatch()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        string root = temp.Combine("root");

        var store = await OpenStoreAsync(root, "alpha", protector);
        string path = store.DatabasePath;
        await store.DisposeAsync();

        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileStore.OpenDatabaseAsync(path, "beta", protector),
            "opening profile alpha's database as beta must fail");

        // Unknown/corrupt databases are rejected instead of being overwritten.
        string foreignPath = temp.Combine("foreign.db");
        await using (var connection = new SqliteConnection($"Data Source={foreignPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileStore.OpenDatabaseAsync(foreignPath, "alpha", protector));

        await using (var connection = new SqliteConnection($"Data Source={foreignPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='notes';";
            object? count = await command.ExecuteScalarAsync();
            Check.Equal(0L, Convert.ToInt64(count), "foreign database must not be modified");
        }
    }

    public static async Task CorruptOrUnsupportedDatabase_FailsClosed()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        string root = temp.Combine("root");

        var store = await OpenStoreAsync(root, "corrupt", protector);
        string path = store.DatabasePath;
        await store.DisposeAsync();

        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '99' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileStore.OpenDatabaseAsync(path, "corrupt", protector),
            "a newer schema version must not be opened");

        // An older schema version must fail closed with a clear message and leave
        // user data untouched (no silent delete or rewrite).
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        InvalidOperationException older = await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileStore.OpenDatabaseAsync(path, "corrupt", protector),
            "an older schema version must not be opened silently");
        Check.Contains("schema version", older.Message);
        Check.Contains("not modified", older.Message);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Check.Equal("1", (string?)await command.ExecuteScalarAsync(), "the file must not be rewritten");
        }

        string notADatabase = temp.Combine("garbage.db");
        await File.WriteAllTextAsync(notADatabase, "this is not a sqlite database at all");
        await Check.ThrowsAnyAsync<SqliteException>(
            () => SqliteProfileStore.OpenDatabaseAsync(notADatabase, "garbage", protector));
        Check.True(
            File.Exists(notADatabase),
            "a failed open must not delete the user's file");
    }

    public static async Task BackupAndRestore_AreConsistentAndProfileBound()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        var storeA = await OpenStoreAsync(root, "backup-a", protector);
        string databaseA = storeA.DatabasePath;
        Guid noteId;
        try
        {
            var service = new NotebookService(storeA, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario, body: "backup body");
            noteId = note.NoteId;
        }
        finally
        {
            await storeA.DisposeAsync();
        }

        string backupPath = temp.Combine("backups", "backup-a.db");
        await SqliteProfileMaintenance.BackupAsync(databaseA, backupPath, "backup-a");
        Check.True(File.Exists(backupPath), "backup file must exist");
        Check.Equal("backup-a", await SqliteProfileMaintenance.ReadProfileIdAsync(backupPath));

        await Check.ThrowsAsync<IOException>(
            () => SqliteProfileMaintenance.BackupAsync(databaseA, backupPath, "backup-a"),
            "an existing backup destination must not be overwritten");

        // Delete the note, then restore and check it returns.
        var reopened = await OpenStoreAsync(root, "backup-a", protector);
        try
        {
            var service = new NotebookService(reopened, clock);
            Check.True(await service.DeleteNoteAsync(noteId), "delete must succeed");
            Check.Equal(0, (await service.ListNotesAsync()).Count);
        }
        finally
        {
            await reopened.DisposeAsync();
        }

        await SqliteProfileMaintenance.RestoreAsync(backupPath, databaseA, "backup-a", protector);
        var restored = await OpenStoreAsync(root, "backup-a", protector);
        try
        {
            var service = new NotebookService(restored, clock);
            StoredNote? note = await service.GetNoteAsync(noteId);
            Check.NotNull(note, "the note from the backup must be restored");
            Check.Equal("backup body", note!.Body);
        }
        finally
        {
            await restored.DisposeAsync();
        }

        // Profile binding: A's backup cannot be restored into B.
        var storeB = await OpenStoreAsync(root, "backup-b", protector);
        string databaseB = storeB.DatabasePath;
        Guid noteB;
        try
        {
            var service = new NotebookService(storeB, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario, body: "b body");
            noteB = note.NoteId;
        }
        finally
        {
            await storeB.DisposeAsync();
        }

        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileMaintenance.RestoreAsync(backupPath, databaseB, "backup-b", protector),
            "restoring profile A into profile B must fail");
        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileMaintenance.BackupAsync(databaseB, temp.Combine("wrong.db"), "backup-a"),
            "backing up B while claiming A must fail");

        var verifyB = await OpenStoreAsync(root, "backup-b", protector);
        try
        {
            var service = new NotebookService(verifyB, clock);
            var notes = await service.ListNotesAsync();
            Check.Equal(1, notes.Count);
            Check.Equal(noteB, notes[0].NoteId, "profile B must be untouched by the failed restore");
        }
        finally
        {
            await verifyB.DisposeAsync();
        }

        string otherProfileTarget = ProfilePaths.GetDatabasePath(root, "backup-c");
        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileMaintenance.RestoreAsync(backupPath, otherProfileTarget, "backup-c", protector),
            "restoring into a non-existent profile C database must fail before writing");
        Check.False(File.Exists(otherProfileTarget), "failed restore must not create the target");
    }

    public static async Task RestoreValidation_RejectsInvalidBackups_WithoutTouchingTarget()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        using var otherProtector = CreateDifferentProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        // A healthy, current target (profile "restore") with one note.
        var store = await OpenStoreAsync(root, "restore", protector);
        string target = store.DatabasePath;
        Guid targetNoteId;
        try
        {
            var service = new NotebookService(store, clock);
            targetNoteId = (await SaveXInMatchAAsync(service, scenario, body: "target body")).NoteId;
        }
        finally
        {
            await store.DisposeAsync();
        }

        await SqliteProfileMaintenance.BackupAsync(target, temp.Combine("good.db"), "restore");
        byte[] targetBytesBefore = File.ReadAllBytes(target);

        // 0. Null protector is rejected before anything happens.
        await Check.ThrowsAsync<ArgumentNullException>(
            () => SqliteProfileMaintenance.RestoreAsync(
                temp.Combine("good.db"),
                target,
                "restore",
                null!),
            "restore must require a protector");

        // 1. Backup written with a different protector: its lookup key cannot be
        //    decrypted with ours, so restore must fail before touching the target.
        var otherStore = await OpenStoreAsync(root, "restore-other", otherProtector);
        string otherDb = otherStore.DatabasePath;
        try
        {
            var otherService = new NotebookService(otherStore, clock);
            await SaveXInMatchAAsync(
                otherService,
                scenario,
                body: "other key body",
                requestId: "other-key-request");
        }
        finally
        {
            await otherStore.DisposeAsync();
        }

        string wrongKeyBackup = temp.Combine("wrong-key.db");
        await SqliteProfileMaintenance.BackupAsync(otherDb, wrongKeyBackup, "restore-other");
        await RewriteProfileMarkerAsync(wrongKeyBackup, "restore");
        await AssertRestoreRejectedAndTargetIntactAsync(
            wrongKeyBackup,
            target,
            protector,
            "lookup key cannot be decrypted");

        // 2. Schema v1 backup (marker matches, key valid) must be rejected.
        string v1Backup = temp.Combine("v1.db");
        await SqliteProfileMaintenance.BackupAsync(target, v1Backup, "restore");
        await ExecuteSqlAsync(v1Backup, "UPDATE meta SET value = '1' WHERE key = 'schema_version';");
        await AssertRestoreRejectedAndTargetIntactAsync(v1Backup, target, protector, "schema version 1");

        // 3. A newer schema version must also be rejected.
        string v5Backup = temp.Combine("v5.db");
        await SqliteProfileMaintenance.BackupAsync(target, v5Backup, "restore");
        await ExecuteSqlAsync(v5Backup, "UPDATE meta SET value = '5' WHERE key = 'schema_version';");
        await AssertRestoreRejectedAndTargetIntactAsync(v5Backup, target, protector, "schema version 5");

        // 3b. A v2 backup predates the player-name column. It must still restore,
        //     and the store upgrades the restored file when it is opened.
        string v2Backup = temp.Combine("v2.db");
        await SqliteProfileMaintenance.BackupAsync(target, v2Backup, "restore");
        await ExecuteSqlAsync(v2Backup, "ALTER TABLE notes DROP COLUMN player_display_name_protected;");
        await ExecuteSqlAsync(v2Backup, "UPDATE meta SET value = '2' WHERE key = 'schema_version';");
        string v2TargetDirectory = temp.Combine("v2-target");
        Directory.CreateDirectory(v2TargetDirectory);
        string v2Target = Path.Combine(v2TargetDirectory, "lolkarmabook.db");
        File.Copy(target, v2Target);
        await SqliteProfileMaintenance.RestoreAsync(v2Backup, v2Target, "restore", protector);
        var upgraded = await SqliteProfileStore.OpenDatabaseAsync(v2Target, "restore", protector);
        try
        {
            Check.Equal(
                1,
                (await new NotebookService(upgraded, clock).ListNotesAsync()).Count,
                "the v2 backup's note must survive the upgrade");
        }
        finally
        {
            await upgraded.DisposeAsync();
        }

        // 3c. A v3 backup predates the tag table. It must still restore, and the
        //     store creates the table when the restored file is opened.
        string v3Backup = temp.Combine("v3.db");
        await SqliteProfileMaintenance.BackupAsync(target, v3Backup, "restore");
        await ExecuteSqlAsync(v3Backup, "DROP TABLE note_tags;");
        await ExecuteSqlAsync(v3Backup, "UPDATE meta SET value = '3' WHERE key = 'schema_version';");
        string v3TargetDirectory = temp.Combine("v3-target");
        Directory.CreateDirectory(v3TargetDirectory);
        string v3Target = Path.Combine(v3TargetDirectory, "lolkarmabook.db");
        File.Copy(target, v3Target);
        await SqliteProfileMaintenance.RestoreAsync(v3Backup, v3Target, "restore", protector);
        var upgradedV3 = await SqliteProfileStore.OpenDatabaseAsync(v3Target, "restore", protector);
        try
        {
            Check.Equal(
                1,
                (await new NotebookService(upgradedV3, clock).ListNotesAsync()).Count,
                "the v3 backup's note must survive the upgrade");
        }
        finally
        {
            await upgradedV3.DisposeAsync();
        }

        // 4. Missing required table must be rejected.
        string noNotesBackup = temp.Combine("no-notes.db");
        await SqliteProfileMaintenance.BackupAsync(target, noNotesBackup, "restore");
        await ExecuteSqlAsync(noNotesBackup, "DROP TABLE note_categories;");
        await AssertRestoreRejectedAndTargetIntactAsync(noNotesBackup, target, protector, "note_categories");

        // 5. Missing required column must be rejected.
        string noColumnBackup = temp.Combine("no-column.db");
        await SqliteProfileMaintenance.BackupAsync(target, noColumnBackup, "restore");
        await ExecuteSqlAsync(
            noColumnBackup,
            "ALTER TABLE notes DROP COLUMN source_kind;");
        await AssertRestoreRejectedAndTargetIntactAsync(noColumnBackup, target, protector, "source_kind");

        // The valid target still opens, keeps its identity lookup key and the
        // original note after all rejected restores.
        var reopened = await OpenStoreAsync(root, "restore", protector);
        try
        {
            Check.NotNull(
                await reopened.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity),
                "the target's lookup key must be unchanged");
            var service = new NotebookService(reopened, clock);
            StoredNote? note = await service.GetNoteAsync(targetNoteId);
            Check.NotNull(note, "the target note must still exist");
            Check.Equal("target body", note!.Body);
            Check.Equal(1, (await service.ListNotesAsync()).Count);
        }
        finally
        {
            await reopened.DisposeAsync();
        }

        // The target's main database file is byte-identical. (WAL -shm files are
        // transient lock state that SQLite creates/removes on open; they are not
        // part of the persisted content and are excluded.)
        Check.True(
            targetBytesBefore.Length > 0 &&
            File.ReadAllBytes(target).AsSpan().SequenceEqual(targetBytesBefore),
            "rejected restores must not change the target database file");

        // Sanity: the valid backup does restore, proving the rejections above
        // are caused by the defects injected, not by the restore path.
        string freshTarget = temp.Combine("fresh", "lolkarmabook.db");
        Directory.CreateDirectory(Path.GetDirectoryName(freshTarget)!);
        await SqliteProfileMaintenance.RestoreAsync(
            temp.Combine("good.db"),
            freshTarget,
            "restore",
            protector);
        var freshStore = await SqliteProfileStore.OpenDatabaseAsync(freshTarget, "restore", protector);
        try
        {
            Check.NotNull(await freshStore.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity));
            Check.Equal(1, (await freshStore.ListNotesAsync(NoteQuery.All)).Count);
        }
        finally
        {
            await freshStore.DisposeAsync();
        }
    }

    private static async Task AssertRestoreRejectedAndTargetIntactAsync(
        string backupPath,
        string targetPath,
        ITextProtector protector,
        string expectedMessageFragment)
    {
        byte[] before = File.ReadAllBytes(targetPath);
        InvalidOperationException error = await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileMaintenance.RestoreAsync(backupPath, targetPath, "restore", protector),
            "an invalid backup must be rejected: " + Path.GetFileName(backupPath));
        Check.Contains(expectedMessageFragment, error.Message);
        Check.True(
            File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(before),
            "a rejected restore must not modify the target: " + Path.GetFileName(backupPath));
    }

    private static async Task RewriteProfileMarkerAsync(string databasePath, string profileId)
    {
        await ExecuteSqlAsync(
            databasePath,
            "UPDATE meta SET value = '" + profileId + "' WHERE key = 'profile_id';");
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
