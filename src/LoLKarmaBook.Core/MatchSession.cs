using System.Collections.ObjectModel;

namespace LoLKarmaBook.Core;

public enum SourceKind
{
    Synthetic = 1,
    Manual = 2,
    Live = 3
}

public enum GamePhase
{
    Unknown = 1,
    Lobby = 2,
    Matchmaking = 3,
    ChampionSelect = 4,
    Loading = 5,
    InGame = 6,
    Finished = 7,
    Disconnected = 8
}

/// <summary>
/// An immutable snapshot of one observed roster. Participant order is preserved
/// for display. Generation/sequence ordering is scoped to the provider instance.
/// </summary>
public sealed class MatchSession
{
    private readonly IReadOnlyDictionary<string, Participant> _participantsByKey;

    public string LocalSessionId { get; }
    public SourceKind SourceKind { get; }
    public string ProviderInstanceId { get; }
    public long Generation { get; }
    public long Sequence { get; }
    public DateTimeOffset ObservedAt { get; }
    public GamePhase Phase { get; }
    public int? QueueId { get; }
    public int? MapId { get; }
    public string? ExternalMatchId { get; }
    public bool RosterComplete { get; }
    public IReadOnlyList<Participant> Participants { get; }

    /// <summary>
    /// Stable key for reminder deduplication: the external match id when it is
    /// genuinely available, otherwise the local session id. The MVP never guesses
    /// that two local sessions are the same match.
    /// </summary>
    public string MatchKey => !string.IsNullOrWhiteSpace(ExternalMatchId)
        ? "ext:" + ExternalMatchId
        : "local:" + LocalSessionId;

    public MatchSession(
        string localSessionId,
        SourceKind sourceKind,
        string providerInstanceId,
        long generation,
        long sequence,
        DateTimeOffset observedAt,
        GamePhase phase,
        int? queueId,
        int? mapId,
        string? externalMatchId,
        bool rosterComplete,
        IEnumerable<Participant> participants)
    {
        if (string.IsNullOrWhiteSpace(localSessionId))
        {
            throw new ArgumentException("A local session id is required.", nameof(localSessionId));
        }

        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, "Unknown source kind.");
        }

        if (string.IsNullOrWhiteSpace(providerInstanceId))
        {
            throw new ArgumentException("A provider instance id is required.", nameof(providerInstanceId));
        }

        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation must be non-negative.");
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence must be non-negative.");
        }

        if (observedAt == default)
        {
            throw new ArgumentException("Observation time is required.", nameof(observedAt));
        }

        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown game phase.");
        }

        ArgumentNullException.ThrowIfNull(participants);
        Participant[] copied = participants.ToArray();
        var byKey = new Dictionary<string, Participant>(copied.Length, StringComparer.Ordinal);
        foreach (Participant participant in copied)
        {
            if (!byKey.TryAdd(participant.ParticipantKey, participant))
            {
                throw new ArgumentException(
                    $"Duplicate participant key '{participant.ParticipantKey}'.",
                    nameof(participants));
            }
        }

        LocalSessionId = localSessionId.Trim();
        SourceKind = sourceKind;
        ProviderInstanceId = providerInstanceId.Trim();
        Generation = generation;
        Sequence = sequence;
        ObservedAt = observedAt;
        Phase = phase;
        QueueId = queueId;
        MapId = mapId;
        ExternalMatchId = string.IsNullOrWhiteSpace(externalMatchId) ? null : externalMatchId.Trim();
        RosterComplete = rosterComplete;
        Participants = new ReadOnlyCollection<Participant>(copied);
        _participantsByKey = byKey;
    }

    public Participant? FindParticipant(string participantKey)
    {
        if (string.IsNullOrWhiteSpace(participantKey))
        {
            return null;
        }

        return _participantsByKey.TryGetValue(participantKey.Trim(), out Participant? participant)
            ? participant
            : null;
    }

    /// <summary>
    /// Same provider instance: a higher generation wins; within one generation a
    /// higher sequence wins. Different provider instances are separate scopes and
    /// are handled by <see cref="SessionRegistry"/>.
    /// </summary>
    public bool IsNewerThan(MatchSession other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(ProviderInstanceId, other.ProviderInstanceId, StringComparison.Ordinal))
        {
            return true;
        }

        if (Generation != other.Generation)
        {
            return Generation > other.Generation;
        }

        return Sequence > other.Sequence;
    }
}
