using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

namespace LoLKarmaBook.Scoring.Tests;

internal static class ScenarioTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("scenario: rating demo is deterministic and useful", RatingDemo);
        runner.Add("scenario: performance demo is deterministic", PerformanceDemo);
        runner.Add("scenario: generator rejects invalid sizes", GeneratorRanges);
        runner.Add("scenario: fixtures match generated demos", FixtureParity);
    }

    private static void RatingDemo()
    {
        var first = SyntheticScenarioGenerator.CreateRatingDemo();
        var second = SyntheticScenarioGenerator.CreateRatingDemo();

        TestAssert.Equal(
            SyntheticRatingJson.Export(first),
            SyntheticRatingJson.Export(second));

        TestAssert.Equal(14, first.Matches.Count);
        var result = SyntheticRatingCalculator.Calculate(first, [SyntheticScenarioGenerator.DefaultTargetPlayerId]);
        var target = result.Players.Single();
        TestAssert.Equal(14, target.Games);
        TestAssert.False(target.IsProvisional);
        TestAssert.Equal(19, target.ConnectedPoolSize);

        var teammateSets = first.Matches
            .Select(match => match.TeamA.Contains(SyntheticScenarioGenerator.DefaultTargetPlayerId, StringComparer.Ordinal)
                ? match.TeamA
                : match.TeamB)
            .Where(team => team.Contains(SyntheticScenarioGenerator.DefaultTargetPlayerId, StringComparer.Ordinal))
            .Select(team => string.Join(
                "|",
                team.Where(id => id != SyntheticScenarioGenerator.DefaultTargetPlayerId).OrderBy(id => id, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Count();

        TestAssert.True(teammateSets > 1, "The demo target should have changing teams.");

        var targetSides = first.Matches
            .Select(match => match.TeamA.Contains(SyntheticScenarioGenerator.DefaultTargetPlayerId, StringComparer.Ordinal) ? "A" : "B")
            .Distinct(StringComparer.Ordinal)
            .Count();
        TestAssert.Equal(2, targetSides);
    }

    private static void PerformanceDemo()
    {
        var first = SyntheticScenarioGenerator.CreatePerformanceDemo();
        var second = SyntheticScenarioGenerator.CreatePerformanceDemo();

        TestAssert.Equal(PerformanceJson.Export(first), PerformanceJson.Export(second));
        TestAssert.Equal(12, first.Matches.Count);

        var score = PerformanceCalculator.Calculate(first);
        TestAssert.Equal(12, score.GameCount);
        TestAssert.Equal(8, score.WinCount);
        TestAssert.True(score.IsAvailable);
        TestAssert.NotNull(score.FinalScore);
        TestAssert.Equal(0, score.MissingMetrics.Count);
        TestAssert.True(score.FinalScore is > 0 and <= 100);
    }

    private static void GeneratorRanges()
    {
        TestAssert.ThrowsArgumentOutOfRange(() => SyntheticScenarioGenerator.CreateRatingDemo(0));
        TestAssert.ThrowsArgumentOutOfRange(() => SyntheticScenarioGenerator.CreateRatingDemo(5001));
        TestAssert.ThrowsArgumentOutOfRange(() => SyntheticScenarioGenerator.CreatePerformanceDemo(0));
        TestAssert.ThrowsArgumentOutOfRange(() => SyntheticScenarioGenerator.CreatePerformanceDemo(5001));
    }

    private static void FixtureParity()
    {
        var ratingFixture = SyntheticRatingJson.LoadFile(TestPaths.Fixture("synthetic-rating-demo.json"));
        var generatedRating = SyntheticScenarioGenerator.CreateRatingDemo();
        TestAssert.Equal(generatedRating.Scope, ratingFixture.Scope);
        TestAssert.Equal(generatedRating.Matches.Count, ratingFixture.Matches.Count);
        for (var i = 0; i < generatedRating.Matches.Count; i++)
        {
            TestAssert.Equal(generatedRating.Matches[i].MatchId, ratingFixture.Matches[i].MatchId);
            TestAssert.Equal(generatedRating.Matches[i].Timestamp, ratingFixture.Matches[i].Timestamp);
            TestAssert.Equal(generatedRating.Matches[i].Winner, ratingFixture.Matches[i].Winner);
            TestAssert.SequenceEqual(generatedRating.Matches[i].TeamA, ratingFixture.Matches[i].TeamA);
            TestAssert.SequenceEqual(generatedRating.Matches[i].TeamB, ratingFixture.Matches[i].TeamB);
        }

        var performanceFixture = PerformanceJson.LoadFile(TestPaths.Fixture("performance-demo.json"));
        var generatedPerformance = SyntheticScenarioGenerator.CreatePerformanceDemo();
        TestAssert.Equal(generatedPerformance.Matches.Count, performanceFixture.Matches.Count);
        for (var i = 0; i < generatedPerformance.Matches.Count; i++)
        {
            TestAssert.Equal(generatedPerformance.Matches[i], performanceFixture.Matches[i]);
        }

        // The committed fixture also carries the demo roster, so the estimate in
        // the docs and the desktop demo both stay reproducible from it.
        var (generatedEstimateDataset, generatedEstimate) =
            SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo();
        TestAssert.NotNull(performanceFixture.Roster);
        TestAssert.Equal(generatedEstimateDataset.PlayerId, performanceFixture.PlayerId);
        TestAssert.Equal(generatedEstimateDataset.Roster!.OwnerId, performanceFixture.Roster!.OwnerId);
        for (var i = 0; i < generatedEstimateDataset.Roster.Matches.Count; i++)
        {
            PerformanceRosterMatch expected = generatedEstimateDataset.Roster.Matches[i];
            PerformanceRosterMatch actual = performanceFixture.Roster.Matches[i];
            TestAssert.Equal(expected.MatchId, actual.MatchId);
            TestAssert.Equal(expected.Timestamp, actual.Timestamp);
            TestAssert.SequenceEqual(expected.Teammates, actual.Teammates);
            TestAssert.SequenceEqual(expected.Opponents, actual.Opponents);
        }

        EstimatedElo fromFixture = EstimatedEloCalculator.Calculate(
            performanceFixture.PlayerId!,
            performanceFixture.Roster.ToRatingMatches(performanceFixture.Matches),
            performanceFixture.Matches,
            performanceFixture.Source);
        TestAssert.Equal(generatedEstimate.Rating, fromFixture.Rating);
    }
}
