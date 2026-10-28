using System.Text;
using Microsoft.Data.Sqlite;
using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;
using static LoLKarmaBook.Core.Tests.TestSupport;

namespace LoLKarmaBook.Core.Tests;

internal static class PolicyTests
{
    public static async Task LiveSource_MatchesStableIdentityAndDeduplicatesAcrossRestart()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        string root = temp.Combine("root");
        PlayerIdentity identity = PlayerIdentity.Create("test-operator", "live-account", "realm-a", "puuid", "account-x-123");
        PlayerIdentity sameNameIdentity = PlayerIdentity.Create("test-operator", "live-account", "realm-a", "puuid", "account-y-456");
        PlayerIdentity otherNamespace = PlayerIdentity.Create("test-operator", "other-account", "realm-a", "puuid", identity.Value);
        MatchSession previous = CustomMatch(
            "live-previous-local",
            "live-previous-external",
            new[] { Participant.Visible("previous-x", ParticipantSide.Ally, "旧名字", identity) },
            providerInstanceId: "live-provider",
            sourceKind: SourceKind.Live);
        MatchSession current = CustomMatch(
            "live-current-local",
            "live-current-external",
            new[]
            {
                Participant.Visible("renamed-x", ParticipantSide.Enemy, "当前名字", identity),
                Participant.Visible("same-name-y", ParticipantSide.Enemy, "旧名字", sameNameIdentity),
                Participant.Visible("other-namespace", ParticipantSide.Enemy, "当前名字", otherNamespace),
                Participant.VisibleNameOnly("name-only", ParticipantSide.Enemy, "当前名字"),
                Participant.Hidden("hidden", ParticipantSide.Enemy)
            },
            providerInstanceId: "live-provider",
            sourceKind: SourceKind.Live,
            sequence: 11);
        var store = await OpenStoreAsync(root, "live-reminders", protector);
        Guid playerId;
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await service.SaveNoteAsync(previous, "previous-x", Draft(requestId: "live-history"));
            playerId = note.PlayerId!.Value;
            IReadOnlyList<Reminder> preview = await service.FindRemindersAsync(current);
            Check.Equal(1, preview.Count, "only the same stable account should match");
            Check.Equal("renamed-x", preview[0].ParticipantKey);
            Check.Equal("当前名字", preview[0].CurrentDisplayId);
            Check.Null(
                await store.GetAlertReceiptStateAsync(current.MatchKey, playerId),
                "preview must not consume a reminder");

            ReminderEvaluation evaluation = await service.EvaluateRemindersAsync(current);
            Check.Equal(ReminderSupport.Enabled, evaluation.Support);
            Check.True(evaluation.IsEnabled, "live roster reminders should be enabled");
            Check.Equal(1, evaluation.Reminders.Count);
            Check.Equal(ReminderReceiptState.Shown, await store.GetAlertReceiptStateAsync(current.MatchKey, playerId));
            Check.Equal(0, (await service.EvaluateRemindersAsync(current)).Reminders.Count);
            Check.Equal(1, (await service.FindRemindersAsync(current)).Count, "shown history remains available in preview");
        }
        finally
        {
            await store.DisposeAsync();
        }

        store = await OpenStoreAsync(root, "live-reminders", protector);
        try
        {
            var restarted = new NotebookService(store, clock);
            MatchSession reconnected = CustomMatch(
                "live-reconnected-local",
                current.ExternalMatchId!,
                current.Participants,
                providerInstanceId: "live-reconnected-provider",
                sourceKind: SourceKind.Live,
                generation: 2);
            Check.Equal(0, (await restarted.EvaluateRemindersAsync(reconnected)).Reminders.Count,
                "restarting or reconnecting the same match must not notify twice");
            Check.True(await restarted.IgnoreForSessionAsync(reconnected, identity), "live ignore should persist");
            Check.Equal(ReminderReceiptState.Ignored, await store.GetAlertReceiptStateAsync(current.MatchKey, playerId));
            Check.Equal(0, (await restarted.FindRemindersAsync(reconnected)).Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task SyntheticNotesCanBeSaved_ButOnlyStableParticipantsBindIdentity()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "stable-binding", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote stable = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(requestId: "stable"));
            Check.NotNull(stable.PlayerId);

            StoredNote nameOnly = await service.SaveNoteAsync(
                scenario.MatchB,
                SyntheticScenario.MatchBNameOnlyParticipantKey,
                Draft(requestId: "name-only"));
            Check.Null(nameOnly.PlayerId);

            StoredNote binding = await service.SaveNoteAsync(
                scenario.MatchA,
                "ally-mate",
                Draft(requestId: "mate"));
            Check.NotNull(binding.PlayerId);
            Check.NotEqual(stable.PlayerId, binding.PlayerId);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task InvalidInputs_AreRejectedBeforePersistence()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "invalid", protector);
        try
        {
            var service = new NotebookService(store, clock);
            const string key = SyntheticScenario.MatchAXParticipantKey;

            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft("", new[] { NoteCategory.AfkLeave })));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft("   ", new[] { NoteCategory.AfkLeave })));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft(new string('r', 129), new[] { NoteCategory.AfkLeave })));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft("empty-categories", Array.Empty<NoteCategory>())));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft("null-categories", null!)));
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft("undefined-category", new[] { (NoteCategory)99 })));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft("long-body", new[] { NoteCategory.AfkLeave }, new string('字', 501))));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft(
                        "future-time",
                        new[] { NoteCategory.AfkLeave },
                        EventTime: clock.UtcNow.AddHours(1))));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft(
                        "default-time",
                        new[] { NoteCategory.AfkLeave },
                        EventTime: (DateTimeOffset?)default(DateTimeOffset))),
                "an explicitly supplied default time is not a valid event time");
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    key,
                    new NoteDraft(
                        "control-body",
                        new[] { NoteCategory.AfkLeave },
                        "bad\u0001body")));
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    "missing-participant",
                    Draft()));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.SaveNoteAsync(scenario.MatchA, "  ", Draft()));
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    scenario.MatchB,
                    SyntheticScenario.MatchBSelfParticipantKey,
                    Draft()),
                "the local player must not be noteable");

            Check.Equal(0, (await service.ListNotesAsync()).Count, "rejected input must persist nothing");

            // Exactly 500 characters and tolerant boundary values are accepted.
            StoredNote maxBody = await service.SaveNoteAsync(
                scenario.MatchA,
                key,
                new NoteDraft(
                    "max-body",
                    new[] { NoteCategory.AfkLeave },
                    new string('字', 500),
                    clock.UtcNow.AddMinutes(9)));
            Check.Equal(500, maxBody.Body.Length);

            StoredNote whitespaceBody = await service.SaveNoteAsync(
                scenario.MatchA,
                key,
                new NoteDraft("blank-body", new[] { NoteCategory.AfkLeave }, "   \n\t  "));
            Check.Equal(string.Empty, whitespaceBody.Body);

            StoredNote duplicateCategories = await service.SaveNoteAsync(
                scenario.MatchA,
                key,
                new NoteDraft(
                    "dupes",
                    new[] { NoteCategory.AfkLeave, NoteCategory.AfkLeave, NoteCategory.OddBuild }));
            Check.Equal(2, duplicateCategories.Categories.Count);

            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.UpdateNoteAsync(Guid.Empty, 1, new NoteEdit(new[] { NoteCategory.AfkLeave })));
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.UpdateNoteAsync(Guid.NewGuid(), 0, new NoteEdit(new[] { NoteCategory.AfkLeave })));
            await Check.ThrowsAsync<KeyNotFoundException>(
                () => service.UpdateNoteAsync(
                    Guid.NewGuid(),
                    1,
                    new NoteEdit(new[] { NoteCategory.AfkLeave })));
            await Check.ThrowsAnyAsync<ArgumentException>(
                () => service.GetNoteAsync(Guid.Empty));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static void IdentityValidation_RejectsPlaceholders()
    {
        foreach (string? value in new string?[]
                 {
                     null, "", "  ", "0", "0000", "unknown", "NULL", "none", "n/a", "-", "?",
                     "00000000-0000-0000-0000-000000000000", "placeholder", "hidden"
                 })
        {
            Check.False(
                PlayerIdentity.TryCreate("tencent-cn", "ns", "realm", "puuid", value, out _, out _),
                "placeholder value must be rejected: " + (value ?? "<null>"));
        }

        Check.True(PlayerIdentity.IsPlaceholderValue("00000000"), "all-zero value is a placeholder");

        foreach ((string? op, string? ns, string? realm, string? kind) in new[]
                 {
                     (null, "ns", "realm", "puuid"),
                     (" ", "ns", "realm", "puuid"),
                     ("op", "", "realm", "puuid"),
                     ("op", "ns", " ", "puuid"),
                     ("op", "ns", "realm", null),
                     ("op", "ns", "realm", "bad\nkind")
                 })
        {
            Check.False(
                PlayerIdentity.TryCreate(op, ns, realm, kind, "real-value-1", out _, out _),
                "component must be rejected");
        }

        PlayerIdentity identity = PlayerIdentity.Create(" op ", "ns", "realm", "puuid", " value ");
        Check.Equal("op", identity.Operator);
        Check.Equal("value", identity.Value);
        Check.Contains("puuid", identity.ScopeKey);

        PlayerIdentity same = PlayerIdentity.Create("op", "ns", "realm", "puuid", "value");
        PlayerIdentity differentCase = PlayerIdentity.Create("OP", "ns", "realm", "puuid", "value");
        Check.Equal(identity, same);
        Check.NotEqual(identity, differentCase);
    }

    public static async Task SensitiveValues_AreNotStoredInPlaintext()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "plaintext", protector);
        string databasePath = store.DatabasePath;
        const string secretBody = "唯一备注PLAINTEXT_PROBE_9f3";
        const string displayName = "不该保存的显示名PROBE";
        try
        {
            var service = new NotebookService(store, clock);
            var participant = Participant.Visible(
                "probe",
                ParticipantSide.Enemy,
                displayName,
                PlayerIdentity.Create(
                    "tencent-cn",
                    "probe-ns",
                    "probe-realm",
                    "puuid",
                    "IDENTITY_VALUE_PROBE_7a1"));
            MatchSession session = CustomMatch(
                "probe-local",
                "probe-external",
                new[] { participant });

            StoredNote note = await service.SaveNoteAsync(
                session,
                "probe",
                new NoteDraft("probe-request", new[] { NoteCategory.OddBuild }, secretBody));
            Check.NotNull(note.PlayerId);
        }
        finally
        {
            await store.DisposeAsync();
        }

        byte[] bytes = ReadAllDatabaseBytes(databasePath);
        Check.True(bytes.Length > 0, "database bytes must be readable");
        Check.False(
            ContainsUtf8(bytes, secretBody),
            "note body must not be stored in plaintext");
        Check.False(
            ContainsUtf8(bytes, "IDENTITY_VALUE_PROBE_7a1"),
            "identity value must not be stored in plaintext");
        Check.False(
            ContainsUtf8(bytes, displayName),
            "display names must not be persisted");
        Check.False(
            ContainsUtf8(bytes, SyntheticScenario.PlayerXOriginalDisplayName),
            "synthetic display names must not be persisted");
    }

    public static async Task WrongProtector_CannotReadSensitiveValues()
    {
        using var temp = new TempDirectory();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        using var right = CreateProtector();
        var store = await OpenStoreAsync(root, "wrong-key", right);
        Guid noteId;
        try
        {
            var service = new NotebookService(store, clock);
            noteId = (await SaveXInMatchAAsync(service, scenario, body: "secret body")).NoteId;
        }
        finally
        {
            await store.DisposeAsync();
        }

        using var wrong = CreateDifferentProtector();
        // The profile-local lookup key is itself protected; a different protector
        // cannot unlock it, so the profile fails closed at open time.
        InvalidOperationException openFailure = await Check.ThrowsAsync<InvalidOperationException>(
            () => OpenStoreAsync(root, "wrong-key", wrong),
            "opening with the wrong protector must fail closed");
        Check.Contains("lookup key", openFailure.Message);
        Check.True(File.Exists(Path.Combine(root, "profiles", "wrong-key", "lolkarmabook.db")),
            "the wrong-protector failure must not delete the database");
        _ = noteId;
    }

    public static void TestProtector_RoundTripsAndFailsClosed()
    {
        using var protector = CreateProtector();
        Check.True(protector.IsTestOnly, "the synthetic protector must identify itself as test-only");

        string protectedValue = protector.Protect("hello 世界");
        Check.NotEqual("hello 世界", protectedValue);
        Check.Equal("hello 世界", protector.Unprotect(protectedValue));
        Check.NotEqual(protectedValue, protector.Protect("hello 世界"), "nonces must be random");

        char[] tampered = protectedValue.ToCharArray();
        tampered[^2] = tampered[^2] == 'A' ? 'B' : 'A';
        Check.Throws<System.Security.Cryptography.CryptographicException>(
            () => protector.Unprotect(new string(tampered)));
        Check.Throws<System.Security.Cryptography.CryptographicException>(
            () => protector.Unprotect("not-a-protected-value"));
        Check.Throws<ArgumentException>(
            () => SyntheticTestProtector.CreateForTestsOnly(new byte[16]));
    }

    public static async Task AudienceMuteAndIgnore_AreAppliedAndPersist()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");
        var store = await OpenStoreAsync(root, "filters", protector);
        Guid playerId;
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);
            playerId = note.PlayerId!.Value;

            MatchSession allyX = CustomMatch(
                "filter-local",
                "filter-external",
                new[]
                {
                    Participant.Visible(
                        "ally-x",
                        ParticipantSide.Ally,
                        "队友X",
                        SyntheticScenario.PlayerXIdentity)
                });

            Check.Equal(
                1,
                (await service.FindRemindersAsync(
                    allyX,
                    new ReminderOptions { Audience = ReminderAudience.AlliesOnly })).Count,
                "an ally X is still reminded for allies-only");
            Check.Equal(
                0,
                (await service.FindRemindersAsync(
                    scenario.MatchB,
                    new ReminderOptions { Audience = ReminderAudience.AlliesOnly })).Count,
                "enemy X must be suppressed for allies-only");
            Check.Equal(
                1,
                (await service.FindRemindersAsync(
                    scenario.MatchB,
                    new ReminderOptions { Audience = ReminderAudience.Both })).Count,
                "both-sides mode includes enemy X");

            Check.Equal(
                0,
                (await service.FindRemindersAsync(
                    scenario.MatchB,
                    new ReminderOptions { MutedPlayerIds = new[] { playerId } })).Count,
                "muted players must be skipped");

            Check.Equal(
                1,
                (await service.FindRemindersAsync(
                    scenario.MatchB,
                    new ReminderOptions
                    {
                        MutedPlayerIds = new[] { Guid.NewGuid() }
                    })).Count,
                "muting a different player must not suppress X");

            Check.True(await service.IgnoreForSessionAsync(scenario.MatchB, SyntheticScenario.PlayerXIdentity), "ignore must persist");
            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "an ignored player must not alert");
            Check.Equal(
                ReminderReceiptState.Ignored,
                await store.GetAlertReceiptStateAsync(scenario.MatchB.MatchKey, playerId));
        }
        finally
        {
            await store.DisposeAsync();
        }

        store = await OpenStoreAsync(root, "filters", protector);
        try
        {
            var service = new NotebookService(store, clock);
            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "the ignore receipt must survive a restart");
            Check.False(
                await service.IgnoreForSessionAsync(
                    scenario.MatchB,
                    PlayerIdentity.Create("tencent-cn", "ns", "realm", "puuid", "never-saved")),
                "ignoring an unknown player must not create rows");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static void SessionRegistry_RejectsStaleAndForeignProviderSnapshots()
    {
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var registry = new SessionRegistry();

        MatchSession first = scenario.MatchA;
        Check.True(registry.TryAccept(first, out _), "first snapshot accepted");
        Check.Equal(first, registry.Current);
        Check.Null(registry.Previous);

        MatchSession staleSequence = new(
            SyntheticScenario.MatchALocalSessionId,
            SourceKind.Synthetic,
            SyntheticScenario.ProviderInstanceId,
            1,
            0,
            first.ObservedAt,
            GamePhase.InGame,
            null,
            null,
            SyntheticScenario.MatchAExternalId,
            true,
            first.Participants);
        Check.False(registry.TryAccept(staleSequence, out string? reason), "stale snapshot must be rejected");
        Check.Contains("stale", reason!);
        Check.Equal(first, registry.Current);

        MatchSession newer = scenario.MatchB;
        Check.True(registry.TryAccept(newer, out _), "newer snapshot accepted");
        Check.Equal(newer, registry.Current);
        Check.Equal(first, registry.Previous, "the old session becomes previous");

        MatchSession nextGeneration = new(
            SyntheticScenario.MatchBLocalSessionId,
            SourceKind.Synthetic,
            SyntheticScenario.ProviderInstanceId,
            2,
            0,
            first.ObservedAt.AddHours(2),
            GamePhase.Finished,
            null,
            null,
            SyntheticScenario.MatchBExternalId,
            true,
            scenario.MatchB.Participants);
        Check.True(registry.TryAccept(nextGeneration, out _), "a new generation is accepted");

        // Explicit account/client switch: late old-provider responses are rejected.
        registry.SwitchProvider("other-provider");
        Check.Null(registry.Current);
        MatchSession lateOld = new(
            "late-local",
            SourceKind.Synthetic,
            SyntheticScenario.ProviderInstanceId,
            3,
            0,
            first.ObservedAt.AddHours(3),
            GamePhase.InGame,
            null,
            null,
            "late-external",
            true,
            scenario.MatchB.Participants);
        Check.False(registry.TryAccept(lateOld, out string? foreignReason), "late old-provider snapshot must be rejected");
        Check.Contains("provider", foreignReason!);
        Check.Null(registry.Current);

        MatchSession otherProvider = new(
            "other-local",
            SourceKind.Synthetic,
            "other-provider",
            1,
            0,
            first.ObservedAt.AddHours(4),
            GamePhase.InGame,
            null,
            null,
            "other-external",
            true,
            scenario.MatchB.Participants);
        Check.True(registry.TryAccept(otherProvider, out _), "the new provider snapshot must be accepted");

        registry.Reset();
        Check.Null(registry.Current);
        Check.Null(registry.ActiveProviderInstanceId);
        _ = clock;
    }

    public static async Task NoteQueryBinding_AndSessionImmutability()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "immutable", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);

            // Mutating the returned category list must not change the stored note.
            var mutable = (ICollection<NoteCategoryEntry>)note.Categories;
            Check.Throws<NotSupportedException>(
                () => mutable.Add(new NoteCategoryEntry(NoteCategory.OddBuild, clock.UtcNow.AddDays(1))));
            StoredNote reloaded = (await service.GetNoteAsync(note.NoteId))!;
            Check.Equal(2, reloaded.Categories.Count);

            // Participant list is read-only.
            Check.Throws<NotSupportedException>(
                () => ((IList<Participant>)scenario.MatchA.Participants).Clear());
            Check.Equal(3, scenario.MatchA.Participants.Count);

            // StoredNote cannot be constructed with empty categories.
            Check.Throws<ArgumentException>(
                () => new StoredNote(
                    Guid.NewGuid(),
                    "r",
                    null,
                    Array.Empty<NoteCategoryEntry>(),
                    "",
                    clock.UtcNow,
                    clock.UtcNow,
                    clock.UtcNow,
                    NoteStatus.Active,
                    1,
                    null,
                    null));

            Check.Throws<ArgumentException>(
                () => Participant.Hidden("  ", ParticipantSide.Enemy));
            Check.Throws<ArgumentException>(
                () => Participant.VisibleNameOnly(
                    "x",
                    ParticipantSide.Enemy,
                    "name",
                    champion: new string('c', 65)));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
    public static async Task IdentityLookup_UsesProfileLocalProtectedHmacKey()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        var store = await OpenStoreAsync(root, "hmac", protector);
        string databasePath = store.DatabasePath;
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);
            Check.NotNull(note.PlayerId);
            Check.NotNull(await store.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity));
        }
        finally
        {
            await store.DisposeAsync();
        }

        string protectedKey;
        string lookup;
        await using (SqliteConnection connection = new($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using (SqliteCommand key = connection.CreateCommand())
            {
                key.CommandText = "SELECT value FROM meta WHERE key = 'identity_lookup_key';";
                protectedKey = (string)(await key.ExecuteScalarAsync())!;
            }

            using (SqliteCommand row = connection.CreateCommand())
            {
                row.CommandText = "SELECT value_lookup FROM players LIMIT 1;";
                lookup = (string)(await row.ExecuteScalarAsync())!;
            }
        }

        // The key is not stored in the clear, and it is not the bare SHA-256 of the
        // identity (the index must depend on the random key).
        Check.True(
            protectedKey.StartsWith("stp1:", StringComparison.Ordinal),
            "the key must be stored through the injected protector, not in the clear");
        string bareSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    string.Join(
                        '\u001F',
                        SyntheticScenario.PlayerXIdentity.Operator,
                        SyntheticScenario.PlayerXIdentity.IdentityNamespace,
                        SyntheticScenario.PlayerXIdentity.RealmScope,
                        SyntheticScenario.PlayerXIdentity.Kind,
                        SyntheticScenario.PlayerXIdentity.Value))));
        Check.NotEqual(bareSha, lookup, "the index must not be a bare SHA-256 of the identity");
        Check.Equal(64, lookup.Length, "HMAC-SHA256 hex is 64 characters");

        // Two profiles store different indexes for the same identity (per-profile key).
        var otherStore = await OpenStoreAsync(root, "hmac-other", protector);
        try
        {
            var otherService = new NotebookService(otherStore, clock);
            await SaveXInMatchAAsync(otherService, scenario, requestId: "hmac-other-request");
            string otherProtectedKey;
            string otherLookup;
            await using SqliteConnection connection = new($"Data Source={otherStore.DatabasePath};Pooling=False");
            await connection.OpenAsync();
            using (SqliteCommand key = connection.CreateCommand())
            {
                key.CommandText = "SELECT value FROM meta WHERE key = 'identity_lookup_key';";
                otherProtectedKey = (string)(await key.ExecuteScalarAsync())!;
            }

            using (SqliteCommand row = connection.CreateCommand())
            {
                row.CommandText = "SELECT value_lookup FROM players LIMIT 1;";
                otherLookup = (string)(await row.ExecuteScalarAsync())!;
            }

            Check.NotEqual(protectedKey, otherProtectedKey, "each profile has its own key");
            Check.NotEqual(lookup, otherLookup, "the same identity indexes differently per profile");
        }
        finally
        {
            await otherStore.DisposeAsync();
        }

        // Wrong protector cannot unlock the lookup key: fail closed, keep the file.
        using (var wrong = CreateDifferentProtector())
        {
            await Check.ThrowsAsync<InvalidOperationException>(
                () => SqliteProfileStore.OpenDatabaseAsync(databasePath, "hmac", wrong),
                "a key that cannot be decrypted must fail closed");
        }
        Check.True(File.Exists(databasePath), "a failed open must not delete the database");

        // Lost key with existing players: refuse rather than re-key silently.
        await using (SqliteConnection connection = new($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using SqliteCommand remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM meta WHERE key = 'identity_lookup_key';";
            await remove.ExecuteNonQueryAsync();
        }

        await Check.ThrowsAsync<InvalidOperationException>(
            () => SqliteProfileStore.OpenDatabaseAsync(databasePath, "hmac", protector),
            "a missing key with player rows must not be regenerated");

        // Backup carries the protected key, so a restored profile still matches.
        string restoredRoot = temp.Combine("restored");
        string backupPath = temp.Combine("hmac-backup.db");
        // Re-create a key so the profile is openable, then back up.
        await using (SqliteConnection connection = new($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using SqliteCommand reseed = connection.CreateCommand();
            reseed.CommandText = "DELETE FROM alert_receipts; DELETE FROM note_categories; DELETE FROM change_journal; DELETE FROM notes; DELETE FROM players;";
            await reseed.ExecuteNonQueryAsync();
        }

        var reopened = await OpenStoreAsync(root, "hmac", protector);
        try
        {
            var service = new NotebookService(reopened, clock);
            StoredNote fresh = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(requestId: "hmac-after-reseed"));
            Check.NotNull(fresh.PlayerId);
        }
        finally
        {
            await reopened.DisposeAsync();
        }

        await SqliteProfileMaintenance.BackupAsync(databasePath, backupPath, "hmac");
        string restoredDb = Path.Combine(restoredRoot, "lolkarmabook.db");
        Directory.CreateDirectory(restoredRoot);
        await SqliteProfileMaintenance.RestoreAsync(backupPath, restoredDb, "hmac", protector);
        var restoredStore = await SqliteProfileStore.OpenDatabaseAsync(restoredDb, "hmac", protector);
        try
        {
            Check.NotNull(
                await restoredStore.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity),
                "restored profile must keep its key and find the player");
            Check.Equal(1, (await restoredStore.ListNotesAsync(NoteQuery.All)).Count);
        }
        finally
        {
            await restoredStore.DisposeAsync();
        }
    }
}
