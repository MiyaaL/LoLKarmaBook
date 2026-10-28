using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;
using static LoLKarmaBook.Core.Tests.TestSupport;

namespace LoLKarmaBook.Core.Tests;

internal static class ReminderTests
{
    public static async Task MatchB_RemindsRenamedXOnly_WithoutOldName()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "reencounter", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(
                    categories: new[] { NoteCategory.AfkLeave, NoteCategory.VerbalAbuse },
                    body: "挂机+辱骂"));

            ReminderEvaluation evaluation = await service.EvaluateRemindersAsync(scenario.MatchB);
            Check.True(evaluation.IsEnabled, "synthetic reminders must be enabled");
            Check.Equal(1, evaluation.Reminders.Count, "only X (renamed, same id) may match");

            Reminder reminder = evaluation.Reminders[0];
            Check.Equal(note.PlayerId, reminder.PlayerId);
            Check.Equal(SyntheticScenario.PlayerXIdentity, reminder.Identity);
            Check.Equal(SyntheticScenario.MatchBRenamedXParticipantKey, reminder.ParticipantKey);
            Check.Equal(ParticipantSide.Enemy, reminder.Side);
            Check.Equal(SyntheticScenario.PlayerXRenamedDisplayName, reminder.CurrentDisplayId);

            Check.Equal(2, reminder.ActiveCategories.Count);
            Check.True(reminder.ActiveCategories.Contains(NoteCategory.AfkLeave), "AFK category expected");
            Check.True(
                reminder.ActiveCategories.Contains(NoteCategory.VerbalAbuse),
                "verbal abuse category expected");
            Check.Equal(1, reminder.DistinctSessionCount, "one match with two categories counts once");
            Check.Equal(1, reminder.Notes.Count);

            string displayed = string.Join("|", reminder.Notes.SelectMany(n => n.Categories));
            Check.False(
                displayed.Contains(SyntheticScenario.PlayerXOriginalDisplayName, StringComparison.Ordinal),
                "reminders must not expose the old name");

            foreach (string excludedKey in new[]
                     {
                         SyntheticScenario.MatchBSameNameYParticipantKey,
                         SyntheticScenario.MatchBNameOnlyParticipantKey,
                         SyntheticScenario.MatchBHiddenParticipantKey,
                         SyntheticScenario.MatchBSelfParticipantKey
                     })
            {
                Check.False(
                    evaluation.Reminders.Any(r => r.ParticipantKey == excludedKey),
                    "participant must not match: " + excludedKey);
            }
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ListingAndFindDoNotConsume_AlertsDedupeAcrossRestart()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        var store = await OpenStoreAsync(root, "dedupe", protector);
        Guid playerId;
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);
            playerId = note.PlayerId!.Value;

            // Listing and previewing are not delivery.
            Check.Equal(1, (await service.ListNotesAsync()).Count);
            Check.Equal(1, (await service.FindRemindersAsync(scenario.MatchB)).Count);
            Check.Equal(1, (await service.FindRemindersAsync(scenario.MatchB)).Count);
            Check.Null(await store.GetAlertReceiptStateAsync(scenario.MatchB.MatchKey, playerId));

            Check.Equal(1, (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count);
            Check.Equal(
                ReminderReceiptState.Shown,
                await store.GetAlertReceiptStateAsync(scenario.MatchB.MatchKey, playerId));

            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "a second evaluation in the same process must not repeat the alert");
        }
        finally
        {
            await store.DisposeAsync();
        }

        // Restart: new store instance, same database, reconnect with the same match key.
        store = await OpenStoreAsync(root, "dedupe", protector);
        try
        {
            var restarted = new NotebookService(store, clock);
            Check.Equal(
                0,
                (await restarted.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "a re-delivered session with the same match key must not re-alert");
            Check.Equal(
                1,
                (await restarted.FindRemindersAsync(scenario.MatchB)).Count,
                "preview still reports eligible history without consuming it");

            Check.Equal(
                ReminderReceiptState.Shown,
                await store.GetAlertReceiptStateAsync(scenario.MatchB.MatchKey, playerId));
        }
        finally
        {
            await store.DisposeAsync();
        }

        // A genuinely different match (different key) alerts again.
        store = await OpenStoreAsync(root, "dedupe", protector);
        try
        {
            var service = new NotebookService(store, clock);
            MatchSession otherMatch = CustomMatch(
                "dedupe-other-local",
                "dedupe-other-external",
                new[]
                {
                    Participant.Visible(
                        SyntheticScenario.MatchBRenamedXParticipantKey,
                        ParticipantSide.Enemy,
                        SyntheticScenario.PlayerXRenamedDisplayName,
                        SyntheticScenario.PlayerXIdentity)
                });
            Check.Equal(1, (await service.EvaluateRemindersAsync(otherMatch)).Reminders.Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task SameMatchIdDifferentLocalSession_StillDedupes()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "reconnect", protector);
        try
        {
            var service = new NotebookService(store, clock);
            await SaveXInMatchAAsync(service, scenario);
            Check.Equal(1, (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count);

            MatchSession reconnected = CustomMatch(
                "synthetic-local-b-after-reconnect",
                SyntheticScenario.MatchBExternalId,
                scenario.MatchB.Participants,
                sequence: 99);
            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(reconnected)).Reminders.Count,
                "the same external match id must dedupe across reconnect/restart");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task CurrentSessionNotesAreExcluded_AndMultiCategoryCountsOneSession()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "current-session", protector);
        try
        {
            var service = new NotebookService(store, clock);
            Check.Equal(
                0,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "no history yet");

            // Three categories on the same current match => one session.
            var draft = new NoteDraft(
                "current-1",
                new[] { NoteCategory.AfkLeave, NoteCategory.PassivePlay, NoteCategory.VerbalAbuse },
                "current match");
            StoredNote currentNote = await service.SaveNoteAsync(
                scenario.MatchB,
                SyntheticScenario.MatchBRenamedXParticipantKey,
                draft);
            Check.Equal(
                0,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "a note saved in this match must not remind about itself");

            await service.SaveNoteAsync(
                scenario.MatchB,
                SyntheticScenario.MatchBRenamedXParticipantKey,
                new NoteDraft("current-2", new[] { NoteCategory.LowSkill }, "second event"));

            StoredNote history = await SaveXInMatchAAsync(service, scenario);
            Check.Equal(history.PlayerId, currentNote.PlayerId);

            Reminder reminder = (await service.FindRemindersAsync(scenario.MatchB)).Single();
            Check.Equal(1, reminder.DistinctSessionCount, "the current match must not count as history");
            Check.Equal(1, reminder.Notes.Count);

            // Later match sees both recorded matches as two distinct sessions.
            MatchSession later = CustomMatch(
                "later-local",
                "later-external",
                new[]
                {
                    Participant.Visible(
                        "later-x",
                        ParticipantSide.Enemy,
                        "演示玩家X-第三局",
                        SyntheticScenario.PlayerXIdentity)
                });
            Reminder laterReminder = (await service.FindRemindersAsync(later)).Single();
            Check.Equal(2, laterReminder.DistinctSessionCount, "two distinct recorded matches");
            // History note: AFK + VerbalAbuse. Current-session notes: AFK, Passive,
            // Verbal, LowSkill. Distinct categories: AFK, Passive, Verbal, LowSkill.
            Check.Equal(4, laterReminder.ActiveCategories.Count);
            Check.Equal(3, laterReminder.Notes.Count, "three notes across two matches");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ExpiredArchivedRetractedNotesDoNotRemind()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "expiry", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote shortLived = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(requestId: "expiry-short", reminderDays: 30));

            clock.Advance(TimeSpan.FromDays(29));
            Check.Equal(
                1,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "day 29 of a 30-day reminder is still valid");

            clock.Advance(TimeSpan.FromDays(2));
            Check.Equal(
                0,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "day 31 is expired");
            Check.True(
                !shortLived.IsActive(clock.UtcNow),
                "IsActive must reflect category expiry");
            Check.Equal(
                1,
                (await service.ListNotesAsync()).Count,
                "expiry must not delete the note; it stays listable");

            StoredNote longLived = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(requestId: "expiry-long"));
            Check.Equal(
                1,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "the 365-day reminder is still valid");

            clock.Advance(TimeSpan.FromDays(340));
            Check.Equal(
                1,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "day 340 of a 365-day reminder is still valid");

            clock.Advance(TimeSpan.FromDays(30));
            Check.Equal(
                0,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "day 370 is beyond the 365-day default");

            StoredNote archived = await service.ArchiveNoteAsync(longLived.NoteId, longLived.Revision);
            Check.Equal(NoteStatus.Archived, archived.Status);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ReminderDaysZero_ExpiresImmediately_AndNegativeIsRejected()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "zero-days", protector);
        try
        {
            var service = new NotebookService(store, clock);
            await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(reminderDays: 0));
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);

            await Check.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    SyntheticScenario.MatchAXParticipantKey,
                    Draft(reminderDays: -1)));
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.SaveNoteAsync(
                    scenario.MatchA,
                    SyntheticScenario.MatchAXParticipantKey,
                    Draft(reminderDays: 3651)));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task IdentityMatching_IsScopedByNameSpaceRealmKindAndValue()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "namespace", protector);
        try
        {
            var service = new NotebookService(store, clock);
            await SaveXInMatchAAsync(service, scenario);

            PlayerIdentity otherNamespace = PlayerIdentity.Create(
                SyntheticScenario.Operator,
                "other-namespace",
                SyntheticScenario.RealmScope,
                SyntheticScenario.IdKind,
                SyntheticScenario.PlayerXIdentity.Value);
            PlayerIdentity otherRealm = PlayerIdentity.Create(
                SyntheticScenario.Operator,
                SyntheticScenario.IdentityNamespace,
                "other-realm",
                SyntheticScenario.IdKind,
                SyntheticScenario.PlayerXIdentity.Value);
            PlayerIdentity otherKind = PlayerIdentity.Create(
                SyntheticScenario.Operator,
                SyntheticScenario.IdentityNamespace,
                SyntheticScenario.RealmScope,
                "summoner-id",
                SyntheticScenario.PlayerXIdentity.Value);
            PlayerIdentity otherOperator = PlayerIdentity.Create(
                "other-operator",
                SyntheticScenario.IdentityNamespace,
                SyntheticScenario.RealmScope,
                SyntheticScenario.IdKind,
                SyntheticScenario.PlayerXIdentity.Value);

            foreach ((string label, PlayerIdentity identity) in new[]
                     {
                         ("namespace", otherNamespace),
                         ("realm", otherRealm),
                         ("kind", otherKind),
                         ("operator", otherOperator)
                     })
            {
                MatchSession match = CustomMatch(
                    "ns-local-" + label,
                    "ns-ext-" + label,
                    new[]
                    {
                        Participant.Visible(
                            "ns-" + label,
                            ParticipantSide.Enemy,
                            SyntheticScenario.PlayerXOriginalDisplayName,
                            identity)
                    });
                Check.Equal(
                    0,
                    (await service.FindRemindersAsync(match)).Count,
                    "a different " + label + " must not match even with the same value");
                Check.Null(await store.FindPlayerIdAsync(identity), "no player row for " + label);
            }

            MatchSession sameIdentity = CustomMatch(
                "ns-same",
                "ns-same-ext",
                new[]
                {
                    Participant.Visible(
                        "ns-same",
                        ParticipantSide.Enemy,
                        "完全不同的显示名",
                        PlayerIdentity.Create(
                            SyntheticScenario.Operator,
                            SyntheticScenario.IdentityNamespace,
                            SyntheticScenario.RealmScope,
                            SyntheticScenario.IdKind,
                            SyntheticScenario.PlayerXIdentity.Value))
                });
            Check.Equal(1, (await service.FindRemindersAsync(sameIdentity)).Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task HiddenAndNameOnly_AreNeverMatchedOrLeaked()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "hidden", protector);
        try
        {
            var service = new NotebookService(store, clock);
            await SaveXInMatchAAsync(service, scenario);

            Participant hidden = Participant.Hidden("hidden-1", ParticipantSide.Enemy);
            Check.Equal(RosterVisibility.Hidden, hidden.Visibility);
            Check.Null(hidden.Identity);
            Check.Null(hidden.CurrentDisplayId);
            Check.Null(hidden.Champion);

            Participant unknown = Participant.UnknownVisibility("unknown-1", ParticipantSide.Enemy);
            Check.Equal(RosterVisibility.Unknown, unknown.Visibility);
            Check.Null(unknown.Identity);
            Check.Null(unknown.CurrentDisplayId);

            Check.Throws<ArgumentException>(
                () => new MatchSession(
                    "hidden-session",
                    SourceKind.Synthetic,
                    "provider",
                    1,
                    1,
                    clock.UtcNow,
                    GamePhase.InGame,
                    null,
                    null,
                    null,
                    true,
                    new[]
                    {
                        Participant.Visible(
                            SyntheticScenario.MatchBRenamedXParticipantKey,
                            ParticipantSide.Enemy,
                            SyntheticScenario.PlayerXRenamedDisplayName,
                            SyntheticScenario.PlayerXIdentity),
                        Participant.Hidden(
                            SyntheticScenario.MatchBRenamedXParticipantKey,
                            ParticipantSide.Ally)
                    }),
                "duplicate participant keys are rejected");

            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    scenario.MatchB,
                    SyntheticScenario.MatchBHiddenParticipantKey,
                    Draft()),
                "notes cannot be bound to hidden participants");

            StoredNote nameOnly = await service.SaveNoteAsync(
                scenario.MatchB,
                SyntheticScenario.MatchBNameOnlyParticipantKey,
                Draft(requestId: "name-only-note"));
            Check.Null(nameOnly.PlayerId, "a name-only note must not create a stable identity");

            // A later session with the same display name cannot auto-match the name-only note.
            MatchSession sameName = CustomMatch(
                "name-only-later",
                "name-only-later-ext",
                new[]
                {
                    Participant.VisibleNameOnly(
                        "name-only-candidate",
                        ParticipantSide.Enemy,
                        SyntheticScenario.NameOnlyDisplayName)
                });
            Check.Equal(0, (await service.FindRemindersAsync(sameName)).Count);

            IReadOnlyList<Reminder> all = await service.FindRemindersAsync(
                scenario.MatchB,
                new ReminderOptions { Audience = ReminderAudience.Both });
            Check.Equal(1, all.Count);
            Check.Equal(SyntheticScenario.MatchBRenamedXParticipantKey, all[0].ParticipantKey);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ManualNotesNeverAutoMatch_EvenRenamedOrRestored()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "manual", protector);
        try
        {
            var service = new NotebookService(store, clock);
            MatchSession manual = ManualSessionFactory.Create(
                "manual-session-1",
                new[]
                {
                    Participant.Visible(
                        SyntheticScenario.MatchAXParticipantKey,
                        ParticipantSide.Enemy,
                        SyntheticScenario.PlayerXOriginalDisplayName,
                        SyntheticScenario.PlayerXIdentity)
                },
                clock.UtcNow);

            StoredNote note = await service.SaveNoteAsync(
                manual,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(categories: new[] { NoteCategory.PassivePlay }));
            Check.Null(note.PlayerId, "manual notes must not bind a stable identity");
            Check.Equal(SourceKind.Manual, manual.SourceKind);

            ReminderEvaluation evaluation = await service.EvaluateRemindersAsync(manual);
            Check.Equal(ReminderSupport.DisabledManual, evaluation.Support);
            Check.False(evaluation.IsEnabled, "manual reminders are disabled");
            Check.Equal(0, evaluation.Reminders.Count);

            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "manual notes must not leak into synthetic matching");
            Check.Equal(1, (await service.ListNotesAsync()).Count);
            Check.Null(await store.FindPlayerIdAsync(SyntheticScenario.PlayerXIdentity));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ManualIdentityCannotBeForgedThroughAParticipantFactory()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "manual-forge", protector);
        try
        {
            var service = new NotebookService(store, clock);
            // Even a caller-built manual session carrying a stable identity must
            // not create a player row, and must not match later rosters.
            MatchSession forged = new(
                "forged-session",
                SourceKind.Manual,
                "manual-local",
                0,
                0,
                clock.UtcNow,
                GamePhase.Unknown,
                null,
                null,
                null,
                true,
                new[]
                {
                    Participant.Visible(
                        "forged-x",
                        ParticipantSide.Enemy,
                        SyntheticScenario.PlayerXRenamedDisplayName,
                        SyntheticScenario.PlayerXIdentity)
                });

            StoredNote note = await service.SaveNoteAsync(forged, "forged-x", Draft());
            Check.Null(note.PlayerId, "manual source must drop even a supplied stable identity");
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
    public static async Task SameRequestIdInDifferentSession_IsRejected()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "request-binding", protector);
        try
        {
            var service = new NotebookService(store, clock);
            var draft = new NoteDraft("bound-request", new[] { NoteCategory.AfkLeave }, "same body");

            StoredNote first = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                draft);
            StoredNote retry = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                draft);
            Check.Equal(first.NoteId, retry.NoteId, "identical request in the same match is idempotent");

            MatchSession otherLocal = CustomMatch(
                "other-local",
                SyntheticScenario.MatchAExternalId,
                scenario.MatchA.Participants);
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    otherLocal,
                    SyntheticScenario.MatchAXParticipantKey,
                    draft),
                "same request id under a different local session must be rejected");

            MatchSession otherExternal = CustomMatch(
                SyntheticScenario.MatchALocalSessionId,
                "other-external",
                scenario.MatchA.Participants);
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    otherExternal,
                    SyntheticScenario.MatchAXParticipantKey,
                    draft),
                "same request id under a different external match must be rejected");

            MatchSession otherSource = CustomMatch(
                SyntheticScenario.MatchALocalSessionId,
                SyntheticScenario.MatchAExternalId,
                scenario.MatchA.Participants,
                sourceKind: SourceKind.Manual);
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(
                    otherSource,
                    SyntheticScenario.MatchAXParticipantKey,
                    draft),
                "same request id from a different source kind must be rejected");

            Check.Equal(1, (await service.ListNotesAsync()).Count);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task IgnoredReceiptHidesPreview_ShownReceiptDoesNot()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        string root = temp.Combine("root");

        var store = await OpenStoreAsync(root, "preview-receipts", protector);
        try
        {
            var service = new NotebookService(store, clock);
            await SaveXInMatchAAsync(service, scenario);

            // Shown receipt: normal history stays visible in preview, no re-notify.
            Check.Equal(1, (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count);
            Check.Equal(
                1,
                (await service.FindRemindersAsync(scenario.MatchB)).Count,
                "a shown alert must remain visible in the detail preview");
            Check.Equal(
                0,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "a shown alert must not notify twice");

            // Ignore: hidden from both preview and delivery.
            Check.True(
                await service.IgnoreForSessionAsync(scenario.MatchB, SyntheticScenario.PlayerXIdentity),
                "ignore must persist");
            Check.Equal(0, (await service.FindRemindersAsync(scenario.MatchB)).Count);
            Check.Equal(0, (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count);
        }
        finally
        {
            await store.DisposeAsync();
        }

        store = await OpenStoreAsync(root, "preview-receipts", protector);
        try
        {
            var restarted = new NotebookService(store, clock);
            Check.Equal(
                0,
                (await restarted.FindRemindersAsync(scenario.MatchB)).Count,
                "an ignored receipt must hide the preview after a restart");
            Check.Equal(
                0,
                (await restarted.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "an ignored receipt must suppress delivery after a restart");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task ActiveRosterPhases_RemindAndInactivePhasesDoNotConsumeReceipts()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "phase-gate", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await SaveXInMatchAAsync(service, scenario);
            Guid playerId = note.PlayerId!.Value;

            GamePhase[] activePhases = { GamePhase.ChampionSelect, GamePhase.Loading, GamePhase.InGame };
            foreach (SourceKind source in new[] { SourceKind.Synthetic, SourceKind.Live })
            {
                foreach (GamePhase phase in Enum.GetValues<GamePhase>())
                {
                    string sessionKey = $"phase-{source}-{phase}";
                    MatchSession session = new(
                        sessionKey,
                        source,
                        "phase-provider",
                        1,
                        2,
                        clock.UtcNow,
                        phase,
                        queueId: 420,
                        mapId: 11,
                        externalMatchId: sessionKey,
                        rosterComplete: true,
                        scenario.MatchB.Participants);
                    bool active = activePhases.Contains(phase);
                    // Post-game keeps the roster for the end-of-match preview only.
                    bool previewable = active || ReminderPolicy.IsFinishedPhase(phase);

                    Check.Equal(active, ReminderPolicy.IsActiveRosterPhase(phase));
                    Check.Equal(previewable ? 1 : 0, (await service.FindRemindersAsync(session)).Count,
                        $"{source}/{phase} preview did not follow roster state");
                    Check.Null(await store.GetAlertReceiptStateAsync(session.MatchKey, playerId),
                        "preview must not create a receipt");

                    ReminderEvaluation evaluation = await service.EvaluateRemindersAsync(session);
                    Check.Equal(active, evaluation.IsEnabled);
                    Check.Equal(active ? 1 : 0, evaluation.Reminders.Count);
                    if (active)
                    {
                        Check.Equal(ReminderSupport.Enabled, evaluation.Support);
                        Check.Equal(ReminderReceiptState.Shown,
                            await store.GetAlertReceiptStateAsync(session.MatchKey, playerId));
                        Check.Equal(0, (await service.EvaluateRemindersAsync(session)).Reminders.Count,
                            $"{source}/{phase} must not notify twice");
                        Check.Equal(1, (await service.FindRemindersAsync(session)).Count);
                        Check.True(await service.IgnoreForSessionAsync(session, SyntheticScenario.PlayerXIdentity),
                            "an active roster may persist ignore state");
                        Check.Equal(0, (await service.FindRemindersAsync(session)).Count);
                    }
                    else
                    {
                        Check.Equal(ReminderSupport.DisabledPhase, evaluation.Support);
                        Check.Null(await store.GetAlertReceiptStateAsync(session.MatchKey, playerId),
                            $"{source}/{phase} must not create a receipt");
                        Check.False(await service.IgnoreForSessionAsync(session, SyntheticScenario.PlayerXIdentity),
                            $"{source}/{phase} must not accept an ignore receipt");
                    }
                }
            }

            Check.Equal(
                1,
                (await service.EvaluateRemindersAsync(scenario.MatchB)).Reminders.Count,
                "independent match keys must not consume a later match's alert");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    public static async Task LiveSession_SavesNotesIdempotently()
    {
        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var store = await OpenStoreAsync(temp.Combine("root"), "live-save", protector);
        try
        {
            var service = new NotebookService(store, clock);
            PlayerIdentity identity = PlayerIdentity.Create("test-operator", "live-account", "realm-a", "puuid", "account-x-123");
            MatchSession live = CustomMatch(
                "live-save-local",
                "live-save-external",
                new[] { Participant.Visible("live-x", ParticipantSide.Enemy, "玩家X", identity) },
                providerInstanceId: "live-provider",
                sourceKind: SourceKind.Live);

            NoteDraft draft = Draft(requestId: "live-request", body: "真实来源记录");
            StoredNote first = await service.SaveNoteAsync(live, "live-x", draft);
            StoredNote replayed = await service.SaveNoteAsync(live, "live-x", draft);
            Check.Equal(SourceKind.Live, first.SourceKind);
            Check.Equal(NoteStatus.Active, first.Status);
            Check.NotNull(first.PlayerId);
            Check.Equal(first.NoteId, replayed.NoteId, "retrying the same live request must not duplicate a note");
            Check.Equal(first.PlayerId, await store.FindPlayerIdAsync(identity));
            Check.Equal(1, (await service.ListNotesAsync()).Count);
            Check.Equal(0, (await service.FindRemindersAsync(live)).Count,
                "a note created in the current match must not immediately remind");
            await Check.ThrowsAsync<InvalidOperationException>(
                () => service.SaveNoteAsync(live, "live-x", Draft(requestId: "live-request", body: "不同内容")),
                "a conflicting replay must remain rejected");

            // Manual authoring remains allowed.
            MatchSession manual = ManualSessionFactory.Create(
                "manual-after-live",
                new[]
                {
                    Participant.VisibleNameOnly("manual-target", ParticipantSide.Enemy, "手工人")
                },
                clock.UtcNow);
            StoredNote manualNote = await service.SaveNoteAsync(
                manual,
                "manual-target",
                Draft(requestId: "manual-allowed"));
            Check.Equal(NoteStatus.Active, manualNote.Status);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
}
