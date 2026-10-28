using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

namespace LoLKarmaBook.Scoring.Tests;

internal static class EstimatedEloTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Target = "synthetic:target";

    private static readonly string[] TargetsTeam = [Target, "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"];
    private static readonly string[] OtherTeam = ["synthetic:b1", "synthetic:b2", "synthetic:b3", "synthetic:b4", "synthetic:b5"];

    public static void Register(TestRunner runner)
    {
        runner.Add("estimated-elo: demo dataset produces a plausible rating", DemoRuns);
        runner.Add("estimated-elo: equal rosters, one win, no performance score", EqualRostersAWin);
        runner.Add("estimated-elo: blend is bounded and symmetric around the midpoint", BlendBounds);
        runner.Add("estimated-elo: missing performance score falls back to team Elo", BlendWithoutPerformance);
        runner.Add("estimated-elo: result disagreement between roster and stat row rejects", ResultMismatch);
        runner.Add("estimated-elo: player missing from every roster rejects", PlayerMissing);
        runner.Add("estimated-elo: stat row count mismatch rejects", RowCountMismatch);
        runner.Add("estimated-elo: API is the same as the reference team-elo", MatchesTeamElo);
    }

    private static void DemoRuns()
    {
        var (matches, stats) = SyntheticScenarioGenerator.CreateEstimatedEloDemo();
        EstimatedElo result = EstimatedEloCalculator.Calculate(Target, matches, stats, "synthetic");

        TestAssert.True(result.Games == matches.Count, "game count");
        TestAssert.True(result.Wins == stats.Count(s => s.Win), "win count");
        TestAssert.True(result.PerformanceScore is not null, "performance score");
        TestAssert.True(result.Rating is > 1000 and < 2000, "rating within a sane band: " + result.Rating);
        TestAssert.True(Math.Abs(result.Rating * 20 - Math.Round(result.Rating * 20)) < 1e-9, "rating rounded to 0.05");
        TestAssert.Equal(EstimatedEloCalculator.AlgorithmVersion, result.AlgorithmVersion);
    }

    private static void EqualRostersAWin()
    {
        var matches = new[]
        {
            new RatingMatchInput("m1", T0, TargetsTeam, OtherTeam, TeamSide.A)
        };
        MatchStatRow stats = Row("m1", win: true);

        EstimatedElo result = EstimatedEloCalculator.Calculate(Target, matches, [stats], "synthetic");
        // Equal ratings give E = 0.5, so one win is +12 before the performance blend.
        TestAssert.EqualDouble(1512.0, result.TeamElo!.Value);
        TestAssert.EqualDouble(Math.Round(EstimatedEloCalculator.Blend(1512.0, result.PerformanceScore!.Value) * 20.0) / 20.0, result.Rating);
        TestAssert.True(result.IsProvisional, "one game is provisional");
    }

    private static void BlendBounds()
    {
        TestAssert.EqualDouble(1500.0, EstimatedEloCalculator.Blend(1500.0, 50.0));

        // 50 points of performance is worth 50 * 4 * (0.25 / 0.75) = 66.67 Elo.
        TestAssert.EqualDouble(1500.0 + 50.0 * 4.0 / 3.0, EstimatedEloCalculator.Blend(1500.0, 100.0));
        TestAssert.EqualDouble(1500.0 - 50.0 * 4.0 / 3.0, EstimatedEloCalculator.Blend(1500.0, 0.0));

        // Values outside 0..100 clamp rather than run away.
        TestAssert.EqualDouble(EstimatedEloCalculator.Blend(1500.0, 100.0), EstimatedEloCalculator.Blend(1500.0, 500.0));
        TestAssert.EqualDouble(EstimatedEloCalculator.Blend(1500.0, 0.0), EstimatedEloCalculator.Blend(1500.0, -80.0));
    }

    private static void BlendWithoutPerformance()
    {
        TestAssert.EqualDouble(1620.0, EstimatedEloCalculator.Blend(1620.0, null));
        TestAssert.EqualDouble(1620.0, EstimatedEloCalculator.Blend(1620.0, double.NaN));
    }

    private static void ResultMismatch()
    {
        var matches = new[] { new RatingMatchInput("m1", T0, TargetsTeam, OtherTeam, TeamSide.A) };
        TestAssert.Throws("estimated-elo.result-mismatch",
            () => EstimatedEloCalculator.Calculate(Target, matches, [Row("m1", win: false)], "synthetic"));
    }

    private static void PlayerMissing()
    {
        var matches = new[]
        {
            new RatingMatchInput("m1", T0,
                ["synthetic:a1", "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"],
                OtherTeam, TeamSide.A)
        };
        TestAssert.Throws("estimated-elo.player-missing",
            () => EstimatedEloCalculator.Calculate(Target, matches, [Row("m1", win: true)], "synthetic"));
    }

    private static void RowCountMismatch()
    {
        var matches = new[]
        {
            new RatingMatchInput("m1", T0, TargetsTeam, OtherTeam, TeamSide.A),
            new RatingMatchInput("m2", T0.AddHours(1), TargetsTeam, OtherTeam, TeamSide.A)
        };
        TestAssert.Throws("estimated-elo.mismatch",
            () => EstimatedEloCalculator.Calculate(Target, matches, [Row("m1", win: true)], "synthetic"));
    }

    private static void MatchesTeamElo()
    {
        var (matches, stats) = SyntheticScenarioGenerator.CreateEstimatedEloDemo();
        EstimatedElo result = EstimatedEloCalculator.Calculate(Target, matches, stats, "synthetic");

        var dataset = new SyntheticRatingDataset
        {
            Scope = new SyntheticScope("synthetic", "synthetic", "synthetic", "synthetic"),
            Matches = matches.Select(m => new SyntheticRatingMatch(
                m.MatchId, m.Timestamp, null, m.TeamA, m.TeamB,
                m.Winner == TeamSide.A ? "A" : "B")).ToArray()
        };
        double direct = SyntheticRatingCalculator.Calculate(dataset, [Target]).Players.Single().Rating;

        TestAssert.EqualDouble(direct, result.TeamElo!.Value);
    }

    private static MatchStatRow Row(string matchId, bool win) => new(
        matchId, win,
        Kills: 6, Deaths: 3, Assists: 9, TeamTotalKills: 25,
        TotalDamageDealt: 24000, TeamTotalDamageDealt: 70000,
        TotalDamageTaken: 18000, TeamTotalDamageTaken: 65000,
        GoldEarned: 12000, TeamTotalGoldEarned: 55000);
}
