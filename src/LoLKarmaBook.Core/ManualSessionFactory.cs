namespace LoLKarmaBook.Core;

/// <summary>
/// Builds manual sessions. Manual data is local note-taking only; it never
/// auto-matches and is never treated as a synthetic or live observation.
/// </summary>
public static class ManualSessionFactory
{
    public const string DefaultProviderInstanceId = "manual-local";

    public static MatchSession Create(
        string localSessionId,
        IEnumerable<Participant> participants,
        DateTimeOffset? observedAt = null,
        string providerInstanceId = DefaultProviderInstanceId,
        GamePhase phase = GamePhase.Unknown,
        int? queueId = null,
        int? mapId = null)
    {
        return new MatchSession(
            localSessionId,
            SourceKind.Manual,
            providerInstanceId,
            0,
            0,
            observedAt ?? DateTimeOffset.UtcNow,
            phase,
            queueId,
            mapId,
            externalMatchId: null,
            rosterComplete: true,
            participants);
    }
}
