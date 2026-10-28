using LoLKarmaBook.Core.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Test)[] Tests =
    {
        // Real SQLite persistence and reopening.
        ("Persist.SaveAndReopen", PersistenceTests.SaveAndReopen_PersistsNoteWithDefault365Days),
        ("Persist.IdempotentRequest", PersistenceTests.SaveIsIdempotent_AndConflictingRequestIdFails),
        ("Persist.EditRevisionAndInvalid", PersistenceTests.Edit_RevisionConflictAndInvalidInputLeaveNoteIntact),
        ("Persist.ArchiveRetractDelete", PersistenceTests.ArchiveRetractDelete_ChangeReminderAndListState),
        ("Persist.DraftPublish", PersistenceTests.DraftIsNotRemindable_UntilPublished),
        ("Persist.TransactionRollback", PersistenceTests.TransactionFailureRollsBackNoteAndPlayer),
        ("Persist.ListFilters", PersistenceTests.ListQueryFilters_Work),
        ("Persist.ClearAll", PersistenceTests.ClearAll_RemovesNotesPlayersAndReceipts),
        ("Persist.PlayerDisplayName", PersistenceTests.PlayerDisplayName_IsStoredProtected_AndV2ProfilesUpgrade),
        ("Persist.Tags", PersistenceTests.Tags_AreStoredUpdatedAndCleared),
        ("Persist.TagMigration", PersistenceTests.TagMigrationFromV3_KeepsExistingNotes),

        // Profile separation, validation, backup/restore.
        ("Profile.Separation", ProfileTests.ProfilesUseSeparateDatabases_AndDeletesStayScoped),
        ("Profile.Validation", ProfileTests.ProfileValidation_RejectsUnsafeIdsAndPaths),
        ("Profile.Binding", ProfileTests.DatabaseBoundToProfile_RejectsMismatch),
        ("Profile.CorruptDb", ProfileTests.CorruptOrUnsupportedDatabase_FailsClosed),
        ("Profile.BackupRestore", ProfileTests.BackupAndRestore_AreConsistentAndProfileBound),
        ("Profile.RestoreValidation", ProfileTests.RestoreValidation_RejectsInvalidBackups_WithoutTouchingTarget),

        // Reminder identity and delivery rules.
        ("Reminder.Reencounter", ReminderTests.MatchB_RemindsRenamedXOnly_WithoutOldName),
        ("Reminder.Dedupe", ReminderTests.ListingAndFindDoNotConsume_AlertsDedupeAcrossRestart),
        ("Reminder.Reconnect", ReminderTests.SameMatchIdDifferentLocalSession_StillDedupes),
        ("Reminder.CurrentSession", ReminderTests.CurrentSessionNotesAreExcluded_AndMultiCategoryCountsOneSession),
        ("Reminder.Expiry", ReminderTests.ExpiredArchivedRetractedNotesDoNotRemind),
        ("Reminder.ZeroDays", ReminderTests.ReminderDaysZero_ExpiresImmediately_AndNegativeIsRejected),
        ("Reminder.NamespaceScope", ReminderTests.IdentityMatching_IsScopedByNameSpaceRealmKindAndValue),
        ("Reminder.HiddenNameOnly", ReminderTests.HiddenAndNameOnly_AreNeverMatchedOrLeaked),
        ("Reminder.Manual", ReminderTests.ManualNotesNeverAutoMatch_EvenRenamedOrRestored),
        ("Reminder.ManualForge", ReminderTests.ManualIdentityCannotBeForgedThroughAParticipantFactory),

        // Policy, validation, privacy, sessions.
        ("Reminder.LiveIdentityAndDedupe", PolicyTests.LiveSource_MatchesStableIdentityAndDeduplicatesAcrossRestart),
        ("Policy.StableBinding", PolicyTests.SyntheticNotesCanBeSaved_ButOnlyStableParticipantsBindIdentity),
        ("Policy.InvalidInputs", PolicyTests.InvalidInputs_AreRejectedBeforePersistence),
        ("Policy.IdentityValidation", () => { PolicyTests.IdentityValidation_RejectsPlaceholders(); return Task.CompletedTask; }),
        ("Policy.NoPlaintext", PolicyTests.SensitiveValues_AreNotStoredInPlaintext),
        ("Policy.WrongProtector", PolicyTests.WrongProtector_CannotReadSensitiveValues),
        ("Policy.TestProtector", () => { PolicyTests.TestProtector_RoundTripsAndFailsClosed(); return Task.CompletedTask; }),
        ("Policy.Filters", PolicyTests.AudienceMuteAndIgnore_AreAppliedAndPersist),
        ("Policy.SessionRegistry", () => { PolicyTests.SessionRegistry_RejectsStaleAndForeignProviderSnapshots(); return Task.CompletedTask; }),
        ("Policy.Immutability", PolicyTests.NoteQueryBinding_AndSessionImmutability),
        ("Policy.HmacLookup", PolicyTests.IdentityLookup_UsesProfileLocalProtectedHmacKey),
        ("Reminder.RequestBinding", ReminderTests.SameRequestIdInDifferentSession_IsRejected),
        ("Reminder.ReceiptPreview", ReminderTests.IgnoredReceiptHidesPreview_ShownReceiptDoesNot),
        ("Reminder.ActiveRosterPhases", ReminderTests.ActiveRosterPhases_RemindAndInactivePhasesDoNotConsumeReceipts),
        // Tag search: official entries only, reachable by folk names.
        ("Tag.OfficialAndFolk", () => { TagTests.OfficialNameAndFolkNamesBothFindTheSameEntry(); return Task.CompletedTask; }),
        ("Tag.Ranking", () => { TagTests.RankingPrefersStrongerMatches(); return Task.CompletedTask; }),
        ("Tag.OfficialOnly", () => { TagTests.OnlyOfficialEntriesCanBeReturned(); return Task.CompletedTask; }),
        ("Tag.EmptyAndMissing", () => { TagTests.EmptyQueryListsCandidatesAndNoMatchReturnsNothing(); return Task.CompletedTask; }),
        ("Tag.Normalize", () => { TagTests.NormalizeIgnoresSeparatorsAndCase(); return Task.CompletedTask; }),        ("Persist.LiveSaveIdempotent", ReminderTests.LiveSession_SavesNotesIdempotently),
        ("Tag.CatalogPinyin", () => { TagCatalogTests.PinyinInitialsAndFullPinyinFindChampions(); return Task.CompletedTask; }),
        ("Tag.CatalogFolk", () => { TagCatalogTests.FolkNamesRealNamesAndEnglishFindTheOfficialEntry(); return Task.CompletedTask; }),
        ("Tag.CatalogOfficialOnly", () => { TagCatalogTests.OnlyOfficialEntriesAndNoSelfAliasesAreReturned(); return Task.CompletedTask; }),
        ("Tag.CatalogBuiltIn", () => { TagCatalogTests.BuiltInSnapshotProvidesChampionsItemsAndAliases(); return Task.CompletedTask; }),
        ("Tag.CatalogLcuParse", () => { TagCatalogTests.LcuPayloadsAreParsedIntoOfficialSeeds(); return Task.CompletedTask; }),
        ("Trend.Normalize", () => { ScoreTrendTests.OrdersSamplesAndNormalizesCoordinates(); return Task.CompletedTask; }),
        ("Trend.Flat", () => { ScoreTrendTests.FlatSeriesStaysInTheMiddle(); return Task.CompletedTask; }),
        ("Trend.Edge", () => { ScoreTrendTests.SingleAndEmptySeriesAreSafe(); return Task.CompletedTask; }),
        ("Trend.EqualTimes", () => { ScoreTrendTests.EqualTimestampsDoNotDivideByZero(); return Task.CompletedTask; })
    };

    public static async Task<int> Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        var selected = Tests
            .Where(test => filter is null ||
                           test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"No tests matched filter '{filter}'.");
            return 2;
        }

        int failed = 0;
        var failures = new List<string>();
        foreach ((string name, Func<Task> test) in selected)
        {
            try
            {
                await test().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add(name);
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine("     " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{selected.Length - failed}/{selected.Length} tests passed" +
            (failed == 0 ? "." : $"; {failed} failed: {string.Join(", ", failures)}"));

        return failed == 0 ? 0 : 1;
    }
}
