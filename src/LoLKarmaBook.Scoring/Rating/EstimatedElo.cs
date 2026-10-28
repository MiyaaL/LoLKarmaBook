using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Scoring.Rating;

/// <summary>
/// An "estimated Elo" for a player, derived from data the app already has.
/// It runs the transparent team-Elo calculator over the player's completed
/// matches and blends that rating with the recent-performance score, so a
/// player who wins a lot of close games and a player who hard-carries both
/// end up above the baseline. It is an estimate produced by this tool; it is
/// not Riot's MMR and not a copy of any third-party hidden-rating service.
/// </summary>
public sealed record EstimatedElo(
    string PlayerId,
    double Rating,
    double? TeamElo,
    double? PerformanceScore,
    int Wins,
    int Games,
    bool IsProvisional,
    string AlgorithmVersion);

/// <summary>
/// Pure calculator turning completed matches into an <see cref="EstimatedElo"/>.
/// The caller supplies each match twice: the team result (who played, who won)
/// and the player's own stat rows, index-aligned and in the same order.
/// </summary>
public static class EstimatedEloCalculator
{
    public const string AlgorithmVersion = "estimated-elo-1.0.0";

    /// <summary>Share of the estimate that comes from the team-Elo result.</summary>
    public const double TeamEloWeight = 0.75;

    /// <summary>Share of the estimate that comes from the performance score.</summary>
    public const double PerformanceWeight = 0.25;

    /// <summary>Performance points (0..100) are centred here before scaling.</summary>
    public const double PerformanceMidpoint = 50.0;

    /// <summary>Elo points per performance point above or below the midpoint.</summary>
    public const double EloPointsPerPerformancePoint = 4.0;

    public static EstimatedElo Calculate(
        string playerId,
        IReadOnlyList<RatingMatchInput> matches,
        IReadOnlyList<MatchStatRow> statRows,
        string scope)
    {
        if (string.IsNullOrWhiteSpace(playerId) || playerId != playerId.Trim())
        {
            throw new ScoringValidationException(
                "estimated-elo.player",
                "Player ID must be non-empty and have no surrounding whitespace.");
        }

        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(statRows);

        if (matches.Count == 0)
        {
            throw new ScoringValidationException(
                "estimated-elo.empty",
                "At least one completed match is required.");
        }

        if (statRows.Count != matches.Count)
        {
            throw new ScoringValidationException(
                "estimated-elo.mismatch",
                $"Expected one stat row per match ({matches.Count}), got {statRows.Count}.");
        }

        // Every match in an estimate is the player's own, so the result is the
        // player's own win or loss.
        for (var i = 0; i < matches.Count; i++)
        {
            RatingMatchInput match = matches[i];
            bool playerOnA = match.TeamA.Contains(playerId, StringComparer.Ordinal);
            bool playerOnB = match.TeamB.Contains(playerId, StringComparer.Ordinal);
            if (playerOnA == playerOnB)
            {
                throw new ScoringValidationException(
                    "estimated-elo.player-missing",
                    $"Player \"{playerId}\" must be on exactly one team in match \"{match.MatchId}\".");
            }

            bool won = (match.Winner == TeamSide.A) == playerOnA;
            if (won != statRows[i].Win)
            {
                throw new ScoringValidationException(
                    "estimated-elo.result-mismatch",
                    $"Match \"{match.MatchId}\" result disagrees between the roster and the stat row.");
            }
        }

        string scopeName = string.IsNullOrWhiteSpace(scope) ? "local" : scope.Trim();
        var dataset = new SyntheticRatingDataset
        {
            Scope = new SyntheticScope(scopeName, scopeName, scopeName, scopeName),
            Matches = matches.Select(m => new SyntheticRatingMatch(
                m.MatchId,
                m.Timestamp,
                Scope: null,
                TeamA: m.TeamA,
                TeamB: m.TeamB,
                Winner: m.Winner == TeamSide.A ? "A" : "B")).ToArray()
        };

        SyntheticRatingResult rating = SyntheticRatingCalculator.Calculate(dataset, [playerId]);
        PlayerRating player = rating.Players.Single();

        // A match set the performance calculator cannot score (e.g. zero kills
        // and assists throughout) must not fail the estimate; the team Elo then
        // stands for the whole of it.
        double? performance;
        try
        {
            performance = PerformanceCalculator.Calculate(statRows).FinalScore;
        }
        catch (ScoringValidationException)
        {
            performance = null;
        }

        int wins = statRows.Count(row => row.Win);
        double rounded = Math.Round(Blend(player.Rating, performance) * 20.0) / 20.0;

        return new EstimatedElo(
            playerId,
            rounded,
            player.Rating,
            performance,
            wins,
            player.Games,
            player.IsProvisional,
            AlgorithmVersion);
    }

    /// <summary>
    /// Team Elo plus a bounded performance adjustment: every 10 performance
    /// points above the midpoint adds 13.3 Elo, and below subtracts the same.
    /// Without a usable performance score the team Elo stands on its own.
    /// </summary>
    public static double Blend(double teamElo, double? performanceScore)
    {
        if (performanceScore is not { } score || !double.IsFinite(score))
        {
            return teamElo;
        }

        double adjustment = (Math.Clamp(score, 0.0, 100.0) - PerformanceMidpoint)
            * EloPointsPerPerformancePoint
            * (PerformanceWeight / TeamEloWeight);
        return teamElo + adjustment;
    }
}

/// <summary>The side of a match, from the perspective of the player being estimated.</summary>
public enum TeamSide
{
    A,
    B
}

/// <summary>The roster-level half of a match: who played, on which side, and who won.</summary>
public sealed record RatingMatchInput(
    string MatchId,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> TeamA,
    IReadOnlyList<string> TeamB,
    TeamSide Winner);
