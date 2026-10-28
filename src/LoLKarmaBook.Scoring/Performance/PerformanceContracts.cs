namespace LoLKarmaBook.Scoring.Performance;

/// <summary>One completed match stat row.</summary>
public sealed record MatchStatRow(
    string MatchId,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    int TeamTotalKills,
    double TotalDamageDealt,
    double TeamTotalDamageDealt,
    double TotalDamageTaken,
    double TeamTotalDamageTaken,
    double GoldEarned,
    double TeamTotalGoldEarned);

/// <summary>Versioned envelope for an imported completed-performance dataset.</summary>
public sealed class PerformanceDataset
{
    public int SchemaVersion { get; init; } = PerformanceJson.SchemaVersion;

    /// <summary>Non-empty source label, preserved during JSON import and export.</summary>
    public string Source { get; init; } = PerformanceJson.UserOwnedSource;

    public IReadOnlyList<MatchStatRow> Matches { get; init; } = [];

    /// <summary>
    /// Optional roster data for the estimated-Elo card. When present it must
    /// describe exactly the matches in <see cref="Matches"/>, in the same order;
    /// when absent the dataset is performance-only and no estimate is shown.
    /// </summary>
    public PerformanceRoster? Roster { get; init; }

    /// <summary>The tracked player inside <see cref="Roster"/>; defaults to its owner.</summary>
    public string? PlayerId { get; init; }
}

/// <summary>
/// Team-level half of a performance dataset: ten stable player IDs per match,
/// the tracked player's four teammates, and which side won. The tracked player
/// is always on the side of <see cref="Teammates"/>.
/// </summary>
public sealed record PerformanceRoster(
    string OwnerId,
    IReadOnlyList<PerformanceRosterMatch> Matches)
{
    /// <summary>
    /// Converts the roster to the estimator's input, checking that every match
    /// lines up with its stat row by ID. The owner's win/loss comes from the
    /// stat row so the two halves of the dataset can never disagree.
    /// </summary>
    public IReadOnlyList<LoLKarmaBook.Scoring.Rating.RatingMatchInput> ToRatingMatches(
        IReadOnlyList<MatchStatRow> statRows)
    {
        if (Matches.Count != statRows.Count)
        {
            throw new ScoringValidationException(
                "performance.roster",
                $"Roster has {Matches.Count} matches but there are {statRows.Count} stat rows.");
        }

        var converted = new List<LoLKarmaBook.Scoring.Rating.RatingMatchInput>(Matches.Count);
        for (var i = 0; i < Matches.Count; i++)
        {
            PerformanceRosterMatch roster = Matches[i]
                ?? throw new ScoringValidationException("performance.roster", $"Roster match {i} is null.");
            MatchStatRow row = statRows[i];

            if (!string.Equals(roster.MatchId, row.MatchId, StringComparison.Ordinal))
            {
                throw new ScoringValidationException(
                    "performance.roster",
                    $"Roster match {i} is \"{roster.MatchId}\" but stat row {i} is \"{row.MatchId}\".");
            }

            converted.Add(new LoLKarmaBook.Scoring.Rating.RatingMatchInput(
                roster.MatchId,
                roster.Timestamp,
                [.. roster.Teammates, OwnerId],
                roster.Opponents,
                row.Win ? LoLKarmaBook.Scoring.Rating.TeamSide.A : LoLKarmaBook.Scoring.Rating.TeamSide.B));
        }

        return converted;
    }
}

/// <summary>One roster row: the owner's four teammates and the five opponents.</summary>
public sealed record PerformanceRosterMatch(
    string MatchId,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> Teammates,
    IReadOnlyList<string> Opponents);
public sealed record PerformanceComponents(
    double? KillParticipation,
    double? DamageShare,
    double? GoldShare,
    double? TakenShare,
    double? Conversion);

/// <summary>Weighted points contributed by each component (already clamp01'd).</summary>
public sealed record PerformanceContributions(
    double WinRate,
    double Kda,
    double KillParticipation,
    double Conversion,
    double Damage,
    double Taken);

public sealed record PerformanceWeights(
    double WinRate,
    double Kda,
    double KillParticipation,
    double Conversion,
    double Damage,
    double Taken)
{
    public double Total => WinRate + Kda + KillParticipation + Conversion + Damage + Taken;
}

/// <summary>Result of the local recent-performance calculation.</summary>
public sealed record PerformanceScore(
    bool IsAvailable,
    int GameCount,
    int WinCount,
    double WinRate,
    double? AggregateKda,
    double? RawWeightedSum,
    double? NormalizedScore,
    double? FinalScore,
    PerformanceComponents Components,
    PerformanceContributions Contributions,
    PerformanceWeights Weights,
    IReadOnlyList<string> MissingMetrics,
    string AlgorithmVersion);
