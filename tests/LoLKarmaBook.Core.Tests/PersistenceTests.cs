using Microsoft.Data.Sqlite;
using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;
using static LoLKarmaBook.Core.Tests.TestSupport;

namespace LoLKarmaBook.Core.Tests;

internal static class PersistenceTests
{
    public static async Task SaveAndReopen_PersistsNoteWithDefault365Days()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        var store = await OpenStoreAsync(root, "default", protector);
        StoredNote note;
        try
        {
            var service = new NotebookService(store, clock);
            var draft = new NoteDraft(
                "req-persist-1",
                new[] { NoteCategory.AfkLeave, NoteCategory.VerbalAbuse },
                "  18:40 离开约 6 分钟  ");
            note = await service.SaveNoteAsync(scenario.MatchA, SyntheticScenario.MatchAXParticipantKey, draft);

            Check.NotEqual(Guid.Empty, note.NoteId);
            Check.Equal(NoteStatus.Active, note.Status);
            Check.Equal(1, note.Revision);
            Check.Equal("18:40 离开约 6 分钟", note.Body);
            Check.NotNull(note.PlayerId);
            Check.Equal(2, note.Categories.Count);
            Check.Equal(clock.UtcNow, note.CreatedAt);
            DateTimeOffset expectedExpiry = clock.UtcNow.AddDays(365);
            Check.True(
                note.Categories.All(entry => entry.ReminderExpiresAt == expectedExpiry),
                "all categories must default to a 365-day reminder");
            Check.Equal(SyntheticScenario.MatchALocalSessionId, note.SourceLocalSessionId);
            Check.Equal(SyntheticScenario.MatchAExternalId, note.ExternalMatchId);
        }
        finally
        {
            await store.DisposeAsync();
        }

        store = await OpenStoreAsync(root, "default", protector);
        try
        {
            var service = new NotebookService(store, clock);
            var notes = await service.ListNotesAsync();
            Check.Equal(1, notes.Count);
            StoredNote reopened = notes[0];
            Check.Equal(note.NoteId, reopened.NoteId);
            Check.Equal(note.PlayerId, reopened.PlayerId);
            Check.Equal(note.Body, reopened.Body);
            Check.Equal(note.Categories.Count, reopened.Categories.Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task SaveIsIdempotent_AndConflictingRequestIdFails()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "idempotent", protector);
        try
        {
            var service = new NotebookService(store, clock);
            var draft = new NoteDraft("same-request", new[] { NoteCategory.AfkLeave }, "one");

            StoredNote first = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                draft);
            StoredNote second = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                draft);

            Check.Equal(first.NoteId, second.NoteId, "the same request key must return the same note");
            Check.Equal(1, (await service.ListNotesAsync()).Count);

            var differentBody = new NoteDraft("same-request", new[] { NoteCategory.AfkLeave }, "two");
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    SyntheticScenario.MatchAXParticipantKey,
                    differentBody));

            var differentParticipant = new NoteDraft("same-request", new[] { NoteCategory.AfkLeave }, "one");
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    "ally-mate",
                    differentParticipant));

            StoredNote unchanged = (await service.GetNoteAsync(first.NoteId))!;
            Check.Equal("one", unchanged.Body);
            Check.Equal(first.PlayerId, unchanged.PlayerId);
            Check.Equal(1, (await service.ListNotesAsync()).Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task Edit_RevisionConflictAndInvalidInputLeaveNoteIntact()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "edit", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);
            Guid? originalPlayerId = note.PlayerId;
            string? originalSession = note.SourceLocalSessionId;

            var edit = new NoteEdit(
                new[] { NoteCategory.LowSkill, NoteCategory.OddBuild },
                "updated body",
                clock.UtcNow.AddMinutes(-5),
                ReminderDays: 30);

            StoredNote updated = await service.UpdateNoteAsync(note.NoteId, note.Revision, edit);
            Check.Equal(2, updated.Revision);
            Check.Equal("updated body", updated.Body);
            Check.Equal(originalPlayerId, updated.PlayerId);
            Check.Equal(originalSession, updated.SourceLocalSessionId);
            Check.Equal(2, updated.Categories.Count);
            Check.True(
                updated.Categories.All(entry => entry.ReminderExpiresAt == clock.UtcNow.AddDays(30)),
                "edit should apply the requested reminder duration");

            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.UpdateNoteAsync(
                    note.NoteId,
                    note.Revision,
                    new NoteEdit(new[] { NoteCategory.AfkLeave }, "stale edit")));

            StoredNote stillUpdated = (await service.GetNoteAsync(note.NoteId))!;
            Check.Equal("updated body", stillUpdated.Body);
            Check.Equal(2, stillUpdated.Revision);

            await Check.ThrowsAsync<ArgumentException>(
                () => service.UpdateNoteAsync(
                    note.NoteId,
                    updated.Revision,
                    new NoteEdit(new[] { NoteCategory.LowSkill }, new string('x', 501))));

            StoredNote afterInvalid = (await service.GetNoteAsync(note.NoteId))!;
            Check.Equal("updated body", afterInvalid.Body);
            Check.Equal(2, afterInvalid.Revision);

            StoredNote archived = await service.ArchiveNoteAsync(note.NoteId, afterInvalid.Revision);
            Check.Equal(NoteStatus.Archived, archived.Status);
            Check.Equal(3, archived.Revision);

            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.UpdateNoteAsync(
                    note.NoteId,
                    archived.Revision,
                    new NoteEdit(new[] { NoteCategory.AfkLeave }, "cannot edit archived")));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ArchiveRetractDelete_ChangeReminderAndListState()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "state", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote first = await SaveXInMatchAAsync(service, scenario, requestId: "state-1");
            Check.Equal(1, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            StoredNote archived = await service.ArchiveNoteAsync(first.NoteId, first.Revision);
            Check.Equal(NoteStatus.Archived, archived.Status);
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            // Retracting an archived record is allowed and keeps the record.
            StoredNote retractedArchived = await service.RetractNoteAsync(archived.NoteId, archived.Revision);
            Check.Equal(NoteStatus.Retracted, retractedArchived.Status);
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            StoredNote second = await SaveXInMatchAAsync(service, scenario, requestId: "state-2");
            Check.Equal(1, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            StoredNote retracted = await service.RetractNoteAsync(second.NoteId, second.Revision);
            Check.Equal(NoteStatus.Retracted, retracted.Status);
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            Check.True(await service.DeleteNoteAsync(first.NoteId), "delete must succeed");
            Check.Null(await service.GetNoteAsync(first.NoteId));
            Check.False(await service.DeleteNoteAsync(first.NoteId), "deleting twice must report false");

            var remaining = await service.ListNotesAsync();
            Check.Equal(1, remaining.Count);
            Check.Equal(second.NoteId, remaining[0].NoteId);
            Check.Equal(NoteStatus.Retracted, remaining[0].Status);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task DraftIsNotRemindable_UntilPublished()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "draft", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote draft = await SaveXInMatchAAsync(
                service,
                scenario,
                mode: NoteSaveMode.Draft);

            Check.Equal(NoteStatus.Draft, draft.Status);
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);
            Check.Equal(
                1,
                (await service.ListNotesAsync(new NoteQuery { Status = NoteStatus.Draft })).Count);

            StoredNote published = await service.PublishDraftAsync(draft.NoteId, draft.Revision);
            Check.Equal(NoteStatus.Active, published.Status);
            Check.Equal(2, published.Revision);
            Check.Equal(1, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.PublishDraftAsync(published.NoteId, published.Revision));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task TransactionFailureRollsBackNoteAndPlayer()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "atomic", protector);
        try
        {
            NoteCategoryEntry duplicate = new(
                NoteCategory.AfkLeave,
                clock.UtcNow.AddDays(1));
            var record = new NewNoteRecord(
                "atomic-request",
                "atomic-fingerprint",
                SyntheticScenario.PlayerXIdentity,
                "atomic-local",
                "atomic-ext",
                SourceKind.Synthetic,
                clock.UtcNow,
                "atomic body",
                new[] { duplicate, duplicate },
                clock.UtcNow,
                NoteStatus.Active,
                NoteCategories.DefaultReminderDays);

            SqliteException failure = await Check.ThrowsAnyAsync<SqliteException>(
                () => store.CreateNoteAsync(record));
            Check.Equal(19, failure.SqliteErrorCode, "expected a SQLite constraint failure");
            Check.Equal(0, (await store.ListNotesAsync(NoteQuery.All)).Count);
            Check.Null(
                await store.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity),
                "the player insert must roll back with the note transaction");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ListQueryFilters_Work()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "query", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote first = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "query-1",
                    new[] { NoteCategory.AfkLeave },
                    "older",
                    clock.UtcNow.AddHours(-2)));
            StoredNote second = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "query-2",
                    new[] { NoteCategory.VerbalAbuse, NoteCategory.LowSkill },
                    "middle",
                    clock.UtcNow.AddHours(-1)));
            _ = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "query-3",
                    new[] { NoteCategory.OddBuild },
                    "draft",
                    clock.UtcNow,
                    NoteCategories.DefaultReminderDays),
                NoteSaveMode.Draft);

            var byCategory = await service.ListNotesAsync(new NoteQuery
            {
                Categories = new[] { NoteCategory.VerbalAbuse }
            });
            Check.Equal(1, byCategory.Count);
            Check.Equal(second.NoteId, byCategory[0].NoteId);

            var drafts = await service.ListNotesAsync(new NoteQuery { Status = NoteStatus.Draft });
            Check.Equal(1, drafts.Count);
            Check.Equal(NoteStatus.Draft, drafts[0].Status);

            var from = clock.UtcNow.AddMinutes(-90);
            var recent = await service.ListNotesAsync(new NoteQuery { EventFrom = from });
            Check.Equal(2, recent.Count);
            Check.False(
                recent.Any(note => note.NoteId == first.NoteId),
                "the older note must be filtered out");

            var byPlayer = await service.ListNotesAsync(new NoteQuery { PlayerId = first.PlayerId });
            Check.Equal(3, byPlayer.Count);

            await Check.ThrowsAsync<ArgumentException>(
                () => service.ListNotesAsync(new NoteQuery { Categories = Array.Empty<NoteCategory>() }));
            await Check.ThrowsAsync<ArgumentException>(
                () => service.ListNotesAsync(new NoteQuery { Status = (NoteStatus)999 }));
            await Check.ThrowsAsync<ArgumentException>(
                () => service.ListNotesAsync(new NoteQuery
                {
                    EventFrom = clock.UtcNow,
                    EventTo = clock.UtcNow.AddDays(-1)
                }));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ClearAll_RemovesNotesPlayersAndReceipts()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "clear", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);
            Check.Equal(1, (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count);
            Check.NotNull(note.PlayerId);

            await service.ClearAllAsync();

            Check.Equal(0, (await service.ListNotesAsync()).Count);
            Check.Null(await store.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity));
            Check.Null(
                await store.GetAlertReceiptStateAsync(scenario.MatchB.MatchKey, note.PlayerId!.Value));
            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task PlayerDisplayName_IsStoredProtected_AndV2ProfilesUpgrade()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");
        string? expected = scenario.MatchA
            .FindParticipant(SyntheticScenario.MatchAXParticipantKey)!.CurrentDisplayId;
        Check.NotNull(expected, "the fixture must carry a player id");

        var store = await OpenStoreAsync(root, "names", protector);
        string databasePath = store.DatabasePath;
        Guid noteId;
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario, body: "named body", requestId: "names-1");
            noteId = note.NoteId;
            Check.Equal(expected, note.PlayerDisplayName, "the saved note must carry the player's game id");
            Check.Equal(expected, (await service.GetNoteAsync(noteId))!.PlayerDisplayName, "the game id must reload");
            Check.Equal(expected, (await service.ListNotesAsync()).Single().PlayerDisplayName, "the game id must list");

            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand raw = connection.CreateCommand();
            raw.CommandText = "SELECT player_display_name_protected FROM notes WHERE note_id = $id;";
            raw.Parameters.AddWithValue("$id", noteId.ToString("N"));
            string stored = (string)(await raw.ExecuteScalarAsync())!;
            Check.False(
                stored.Contains(expected!, StringComparison.Ordinal),
                "the game id must be stored protected, never as plaintext");
        }
        finally
        {
            await store.DisposeAsync();
        }

        // Rewrite the file as the previous build left it: schema v2, no name column.
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText =
                "ALTER TABLE notes DROP COLUMN player_display_name_protected; " +
                "UPDATE meta SET value = '2' WHERE key = 'schema_version';";
            await downgrade.ExecuteNonQueryAsync();
        }

        var upgraded = await OpenStoreAsync(root, "names", protector);
        try
        {
            var service = new NotebookService(upgraded, clock);
            IReadOnlyList<StoredNote> notes = await service.ListNotesAsync();
            Check.Equal(1, notes.Count, "a pre-upgrade note must survive the schema upgrade");
            Check.Equal("named body", notes[0].Body, "the note body must survive the schema upgrade");
            Check.Null(notes[0].PlayerDisplayName, "a note written before the upgrade has no game id");

            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand version = connection.CreateCommand();
            version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Check.Equal("4", (string?)await version.ExecuteScalarAsync(), "the profile must be upgraded in place");
            await using SqliteCommand column = connection.CreateCommand();
            column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('notes') WHERE name = 'player_display_name_protected';";
            Check.Equal(1L, Convert.ToInt64(await column.ExecuteScalarAsync()), "the new column must exist");
            await using SqliteCommand tagsTable = connection.CreateCommand();
            tagsTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'note_tags';";
            Check.Equal(1L, Convert.ToInt64(await tagsTable.ExecuteScalarAsync()), "the tag table must exist");
        }
        finally
        {
            await upgraded.DisposeAsync();
        }
    }

    public static async Task Tags_AreStoredUpdatedAndCleared()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");
        var noteTag = NoteTag.Champion(103, "九尾妖狐");
        var itemTag = NoteTag.Item(3031, "无尽之刃");

        var store = await OpenStoreAsync(root, "tags", protector);
        StoredNote note;
        try
        {
            var service = new NotebookService(store, clock);
            note = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "tags-1",
                    new[] { NoteCategory.AfkLeave },
                    "tagged",
                    Tags: new[] { noteTag, itemTag, noteTag }));
            Check.Equal(2, note.Tags.Count, "duplicate tags must collapse to one official entry");
            Check.Equal(TagKind.Champion, note.Tags[0].Kind);
            Check.Equal("九尾妖狐", note.Tags[0].OfficialName);
            Check.Equal(TagKind.Item, note.Tags[1].Kind);

            StoredNote updated = await service.UpdateNoteAsync(
                note.NoteId,
                note.Revision,
                new NoteEdit(
                    new[] { NoteCategory.AfkLeave },
                    "tagged",
                    Tags: new[] { itemTag }));
            Check.Equal(1, updated.Tags.Count);
            Check.Equal(3031, updated.Tags[0].RefId);

            StoredNote cleared = await service.UpdateNoteAsync(
                note.NoteId,
                updated.Revision,
                new NoteEdit(new[] { NoteCategory.AfkLeave }, "tagged"));
            Check.Equal(0, cleared.Tags.Count, "omitting tags must clear them");

            StoredNote retagged = await service.UpdateNoteAsync(
                note.NoteId,
                cleared.Revision,
                new NoteEdit(new[] { NoteCategory.AfkLeave }, "tagged", Tags: new[] { noteTag }));

            StoredNote? reloaded = await service.GetNoteAsync(retagged.NoteId);
            Check.NotNull(reloaded);
            Check.Equal(1, reloaded!.Tags.Count);
            Check.Equal(103, reloaded.Tags[0].RefId);
            Check.Equal("九尾妖狐", reloaded.Tags[0].OfficialName);
        }
        finally
        {
            await store.DisposeAsync();
        }

        store = await OpenStoreAsync(root, "tags", protector);
        try
        {
            var service = new NotebookService(store, clock);
            IReadOnlyList<StoredNote> notes = await service.ListNotesAsync();
            Check.Equal(1, notes.Count);
            Check.Equal(1, notes[0].Tags.Count, "tags must survive a reopen");
            Check.Equal(note.NoteId, notes[0].NoteId);

            await Check.ThrowsAsync<ArgumentException>(() => service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "tags-invalid-id",
                    new[] { NoteCategory.AfkLeave },
                    Tags: new[] { new NoteTag(TagKind.Champion, 0, "九尾妖狐") })));
            await Check.ThrowsAsync<ArgumentException>(() => service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "tags-invalid-kind",
                    new[] { NoteCategory.AfkLeave },
                    Tags: new[] { new NoteTag((TagKind)99, 103, "九尾妖狐") })));
            await Check.ThrowsAsync<ArgumentException>(() => service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "tags-too-many",
                    new[] { NoteCategory.AfkLeave },
                    Tags: Enumerable.Range(1, 13)
                        .Select(id => new NoteTag(TagKind.Champion, id, "英雄" + id))
                        .ToArray())));
            await Check.ThrowsAsync<InvalidOperationException>(() => service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "tags-1",
                    new[] { NoteCategory.AfkLeave },
                    "tagged",
                    Tags: new[] { NoteTag.Item(3157, "中娅沙漏") })));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task TagMigrationFromV3_KeepsExistingNotes()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");
        var store = await OpenStoreAsync(root, "tagmigrate", protector);
        string databasePath = store.DatabasePath;
        Guid noteId;
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft(
                    "tagmigrate-1",
                    new[] { NoteCategory.AfkLeave },
                    "pre-upgrade",
                    Tags: new[] { NoteTag.Champion(103, "九尾妖狐") }));
            noteId = note.NoteId;
        }
        finally
        {
            await store.DisposeAsync();
        }

        // Rewrite the profile as the previous build left it: schema v3, no tag table.
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText =
                "DROP TABLE note_tags; UPDATE meta SET value = '3' WHERE key = 'schema_version';";
            await downgrade.ExecuteNonQueryAsync();
        }

        var upgraded = await OpenStoreAsync(root, "tagmigrate", protector);
        try
        {
            var service = new NotebookService(upgraded, clock);
            IReadOnlyList<StoredNote> notes = await service.ListNotesAsync();
            Check.Equal(1, notes.Count, "the v3 note must survive the upgrade");
            Check.Equal(noteId, notes[0].NoteId);
            Check.Equal("pre-upgrade", notes[0].Body);
            Check.Equal(0, notes[0].Tags.Count, "old notes start without tags");

            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand version = connection.CreateCommand();
            version.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
            Check.Equal("4", (string?)await version.ExecuteScalarAsync(), "the profile must be upgraded in place");
            await using SqliteCommand table = connection.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'note_tags';";
            Check.Equal(1L, Convert.ToInt64(await table.ExecuteScalarAsync()), "the tag table must be created");

            StoredNote retagged = await service.UpdateNoteAsync(
                notes[0].NoteId,
                notes[0].Revision,
                new NoteEdit(
                    new[] { NoteCategory.AfkLeave },
                    "pre-upgrade",
                    Tags: new[] { NoteTag.Item(3031, "无尽之刃") }));
            Check.Equal(1, retagged.Tags.Count, "tags must be writable after the upgrade");
        }
        finally
        {
            await upgraded.DisposeAsync();
        }
    }
}
