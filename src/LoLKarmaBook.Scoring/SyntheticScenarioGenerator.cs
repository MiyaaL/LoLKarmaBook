using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

namespace LoLKarmaBook.Scoring;

/// <summary>
/// Deterministic fabricated datasets for Desktop/CLI demos. No random source, file I/O or
/// network access is used, so repeated calls produce byte-identical data.
/// </summary>
public static class SyntheticScenarioGenerator
{
    public const string DefaultTargetPlayerId = "synthetic:target";
    public const string DefaultOperator = "synthetic";
    public const string DefaultRealm = "demo-realm";
    public const string DefaultQueue = "synthetic-queue";
    public const string DefaultPatch = "demo-patch-1";

    public static SyntheticRatingDataset CreateRatingDemo(int targetMatchCount = 14)
    {
        if (targetMatchCount <= 0 || targetMatchCount > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetMatchCount),
                "Target match count must be between 1 and 5000.");
        }

        var scope = new SyntheticScope(DefaultOperator, DefaultRealm, DefaultQueue, DefaultPatch);
        var others = Enumerable.Range(0, 18)
            .Select(index => $"synthetic:p{index:00}")
            .ToArray();

        var matches = new List<SyntheticRatingMatch>(targetMatchCount);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < targetMatchCount; i++)
        {
            var offset = (i * 3) % others.Length;
            var selected = Enumerable.Range(0, 9)
                .Select(k => others[(offset + k) % others.Length])
                .ToArray();

            var teammates = selected[..4];
            var opponents = selected[4..];
            var targetOnA = i % 2 == 0;

            var teamA = targetOnA
                ? new[] { DefaultTargetPlayerId }.Concat(teammates).ToArray()
                : opponents.ToArray();
            var teamB = targetOnA
                ? opponents.ToArray()
                : new[] { DefaultTargetPlayerId }.Concat(teammates).ToArray();

            matches.Add(new SyntheticRatingMatch(
                $"demo-rating-{i + 1:000}",
                start.AddHours(i),
                Scope: null,
                teamA,
                teamB,
                Winner: i % 3 == 0 ? "B" : "A"));
        }

        return new SyntheticRatingDataset
        {
            SchemaVersion = SyntheticRatingJson.SchemaVersion,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = scope,
            Matches = matches
        };
    }

    /// <summary>
    /// One synthetic player with both halves of an estimate, split out of the
    /// dataset for callers that only need the calculator's inputs.
    /// </summary>
    public static (IReadOnlyList<RatingMatchInput> Matches, IReadOnlyList<MatchStatRow> StatRows) CreateEstimatedEloDemo(
        int matchCount = 12)
    {
        (PerformanceDataset dataset, _) = CreateEstimatedEloPerformanceDemo(matchCount);
        return (dataset.Roster!.ToRatingMatches(dataset.Matches), dataset.Matches);
    }

    /// <summary>
    /// The performance demo with an attached roster, so the desktop's estimate
    /// card has something to work with. Match IDs, results and stat rows are the
    /// same as <see cref="CreatePerformanceDemo"/>; only the optional roster is
    /// added.
    /// </summary>
    public static (PerformanceDataset Dataset, EstimatedElo Estimate) CreateEstimatedEloPerformanceDemo(
        int matchCount = 12)
    {
        PerformanceDataset performance = CreatePerformanceDemo(matchCount);
        SyntheticRatingDataset rating = CreateRatingDemo(matchCount);

        var rosterMatches = new List<PerformanceRosterMatch>(matchCount);
        for (var i = 0; i < matchCount; i++)
        {
            SyntheticRatingMatch source = rating.Matches[i];
            string[] own = source.TeamA.Contains(DefaultTargetPlayerId, StringComparer.Ordinal)
                ? source.TeamA.ToArray()
                : source.TeamB.ToArray();
            string[] other = source.TeamA.Contains(DefaultTargetPlayerId, StringComparer.Ordinal)
                ? source.TeamB.ToArray()
                : source.TeamA.ToArray();

            rosterMatches.Add(new PerformanceRosterMatch(
                performance.Matches[i].MatchId,
                source.Timestamp,
                own.Where(id => id != DefaultTargetPlayerId).ToArray(),
                other));
        }

        var dataset = new PerformanceDataset
        {
            SchemaVersion = PerformanceJson.SchemaVersion,
            Source = PerformanceJson.UserOwnedSource,
            Matches = performance.Matches,
            PlayerId = DefaultTargetPlayerId,
            Roster = new PerformanceRoster(DefaultTargetPlayerId, rosterMatches)
        };

        EstimatedElo estimate = EstimatedEloCalculator.Calculate(
            DefaultTargetPlayerId,
            dataset.Roster.ToRatingMatches(dataset.Matches),
            dataset.Matches,
            dataset.Source);

        return (dataset, estimate);
    }

    public static PerformanceDataset CreatePerformanceDemo(int matchCount = 12)
    {
        if (matchCount <= 0 || matchCount > 5000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(matchCount),
                "Match count must be between 1 and 5000.");
        }

        var matches = new List<MatchStatRow>(matchCount);

        for (var i = 0; i < matchCount; i++)
        {
            var kills = 5 + (i * 3) % 8;
            var assists = 8 + (i * 5) % 12;
            var deaths = 1 + (i * 2) % 6;
            var damage = 18000 + (i * 1700) % 12000;
            var taken = 9000 + (i * 1300) % 10000;
            var gold = 9000 + (i * 700) % 6000;

            matches.Add(new MatchStatRow(
                $"demo-performance-{i + 1:000}",
                Win: i % 3 != 2,
                kills,
                deaths,
                assists,
                TeamTotalKills: kills + assists + 12 + i % 4,
                TotalDamageDealt: damage,
                TeamTotalDamageDealt: damage + 45000 + (i % 5) * 1000,
                TotalDamageTaken: taken,
                TeamTotalDamageTaken: taken + 38000 + (i % 4) * 1500,
                GoldEarned: gold,
                TeamTotalGoldEarned: gold + 42000 + (i % 3) * 1200));
        }

        return new PerformanceDataset
        {
            SchemaVersion = PerformanceJson.SchemaVersion,
            Source = PerformanceJson.UserOwnedSource,
            Matches = matches
        };
    }
}
