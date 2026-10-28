using System.Security.Cryptography;
using System.Text;
using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Core.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lolkarmabook-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts)
    {
        string result = Path;
        foreach (string part in parts)
        {
            result = System.IO.Path.Combine(result, part);
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best effort; test results do not depend on it.
        }
    }
}

internal sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset? initial = null)
    {
        UtcNow = initial ?? new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

internal static class TestSupport
{
    public static SyntheticTestProtector CreateProtector()
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("lolkarmabook-core-tests-only"));
        return SyntheticTestProtector.CreateForTestsOnly(key);
    }

    public static SyntheticTestProtector CreateDifferentProtector()
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("lolkarmabook-core-tests-different"));
        return SyntheticTestProtector.CreateForTestsOnly(key);
    }

    public static async Task<SqliteProfileStore> OpenStoreAsync(
        string root,
        string profileId,
        ITextProtector protector)
    {
        return await SqliteProfileStore.OpenAsync(root, profileId, protector)
            .ConfigureAwait(false);
    }

    public static NoteDraft Draft(
        string? requestId = null,
        IReadOnlyList<NoteCategory>? categories = null,
        string? body = null,
        DateTimeOffset? eventTime = null,
        int reminderDays = NoteCategories.DefaultReminderDays)
    {
        return new NoteDraft(
            requestId ?? Guid.NewGuid().ToString("N"),
            categories ?? new[] { NoteCategory.AfkLeave },
            body,
            eventTime,
            reminderDays);
    }

    public static async Task<StoredNote> SaveXInMatchAAsync(
        NotebookService service,
        SyntheticScenario scenario,
        string? body = "历史记录",
        int reminderDays = NoteCategories.DefaultReminderDays,
        string? requestId = null,
        NoteSaveMode mode = NoteSaveMode.Active)
    {
        return await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(
                    requestId: requestId,
                    categories: new[] { NoteCategory.AfkLeave, NoteCategory.VerbalAbuse },
                    body: body,
                    reminderDays: reminderDays),
                mode)
            .ConfigureAwait(false);
    }

    public static MatchSession CustomMatch(
        string localSessionId,
        string externalMatchId,
        IEnumerable<Participant> participants,
        string providerInstanceId = SyntheticScenario.ProviderInstanceId,
        SourceKind sourceKind = SourceKind.Synthetic,
        long generation = 1,
        long sequence = 10,
        DateTimeOffset? observedAt = null)
    {
        return new MatchSession(
            localSessionId,
            sourceKind,
            providerInstanceId,
            generation,
            sequence,
            observedAt ?? new DateTimeOffset(2026, 10, 26, 13, 0, 0, TimeSpan.Zero),
            GamePhase.InGame,
            queueId: 420,
            mapId: 11,
            externalMatchId,
            rosterComplete: true,
            participants);
    }

    public static bool ContainsUtf8(ReadOnlySpan<byte> haystack, string needle)
    {
        byte[] pattern = Encoding.UTF8.GetBytes(needle);
        return haystack.IndexOf(pattern) >= 0;
    }

    public static byte[] ReadAllDatabaseBytes(string databasePath)
    {
        using var buffer = new MemoryStream();
        foreach (string path in new[]
                 {
                     databasePath,
                     databasePath + "-wal",
                     databasePath + "-shm",
                     databasePath + "-journal"
                 })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            buffer.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }
}
