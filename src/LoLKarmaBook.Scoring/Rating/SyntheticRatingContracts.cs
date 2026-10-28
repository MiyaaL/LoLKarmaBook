namespace LoLKarmaBook.Scoring.Rating;

/// <summary>Dataset/team scope used to keep match pools isolated.</summary>
public sealed record SyntheticScope(string Operator, string Realm, string Queue, string Patch)
{
    public SyntheticScope()
        : this(string.Empty, string.Empty, string.Empty, string.Empty)
    {
    }
}

/// <summary>One completed match. Teams are unordered sets of exactly five stable IDs.</summary>
public sealed record SyntheticRatingMatch(
    string MatchId,
    DateTimeOffset Timestamp,
    SyntheticScope? Scope,
    IReadOnlyList<string> TeamA,
    IReadOnlyList<string> TeamB,
    string Winner);

/// <summary>Versioned envelope for a rating dataset.</summary>
public sealed class SyntheticRatingDataset
{
    public int SchemaVersion { get; init; } = SyntheticRatingJson.SchemaVersion;

    /// <summary>Non-empty source label, preserved during JSON import and export.</summary>
    public string Source { get; init; } = SyntheticRatingCalculator.SyntheticSource;

    public SyntheticScope Scope { get; init; } = new();

    public IReadOnlyList<SyntheticRatingMatch> Matches { get; init; } = [];
}

public sealed record PlayerRating(
    string PlayerId,
    double Rating,
    int Games,
    bool IsProvisional,
    string ConnectedPoolId,
    int ConnectedPoolSize);

public sealed record RatingChange(
    string PlayerId,
    string Team,
    double RatingBefore,
    double RatingAfter);

public sealed record SyntheticMatchRating(
    string MatchId,
    DateTimeOffset Timestamp,
    double TeamAAverageBefore,
    double TeamBAverageBefore,
    double ExpectedTeamA,
    double Delta,
    string Winner,
    IReadOnlyList<RatingChange> Changes);

/// <summary>
/// Result of the team-Elo calculation.
/// </summary>
public sealed record SyntheticRatingResult(
    IReadOnlyList<PlayerRating> Players,
    IReadOnlyList<SyntheticMatchRating> History,
    int PlayerCount,
    int ConnectedPoolCount,
    int LargestConnectedPoolSize,
    int MatchCount,
    int DuplicateMatchCount,
    string AlgorithmVersion);
