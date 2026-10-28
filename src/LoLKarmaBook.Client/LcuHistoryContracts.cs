using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Client;

public enum LcuHistoryStatus { Success, NotRunning, CredentialsUnavailable, NotLoggedIn, AccountChanged, Unavailable, Error }

public sealed record LcuHistoryResult(LcuHistoryStatus Status, string Message, ClientAccount? Account,
    PerformanceDataset? Dataset, int ScannedMatches, int SkippedMatches);

/// <summary>Read-only history of the currently signed-in local account. No Riot API key is needed.</summary>
public interface ILcuHistoryService : IDisposable
{
    Task<LcuHistoryResult> GetHistoryAsync(string? clientDirectory, int count = 20, int? queueId = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}
