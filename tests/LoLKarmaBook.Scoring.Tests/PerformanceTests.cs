using System.Text.Json;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Scoring.Tests;

internal static class PerformanceTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("performance: positive-clean and ratio primitives", MathPrimitives);
        runner.Add("performance: one-game hand-computable parity", OneGameParity);
        runner.Add("performance: two-game hand-computable parity", TwoGameParity);
        runner.Add("performance: missing metrics stay null and weights shrink", MissingMetrics);
        runner.Add("performance: unavailable KDA is null, not zero", UnavailableKda);
        runner.Add("performance: zero deaths uses max(1,deaths)", ZeroDeaths);
        runner.Add("performance: equality/over-total shares are excluded", EqualityShares);
        runner.Add("performance: terms clamp at one and final stays in range", ClampTerms);
        runner.Add("performance: sample shrinkage toward 60", SampleShrinkage);
        runner.Add("performance: input order does not change results", PermutationInvariant);
        runner.Add("performance: invalid rows are rejected", InvalidRows);
        runner.Add("performance: source labels accept live/manual and retain validation", DatasetValidation);
        runner.Add("performance JSON: round-trip and demo fixture", JsonRoundTrip);
        runner.Add("performance JSON: missing-metrics fixture", JsonMissingFixture);
        runner.Add("performance JSON: malformed and required fields", JsonMalformed);
        runner.Add("performance JSON: source/schema/duplicates", JsonValidation);
        runner.Add("performance JSON: size guard", JsonSizeGuard);
        runner.Add("performance: tiny gold share does not overflow", TinyGoldShare);
        runner.Add("performance: clamp01 saturates infinity to one", ClampInfinity);
        runner.Add("performance JSON: duplicate properties rejected", JsonDuplicateProperties);
    }

    private static MatchStatRow Row(
        string id,
        bool win = true,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        int teamKills = 0,
        double damage = 0,
        double teamDamage = 0,
        double taken = 0,
        double teamTaken = 0,
        double gold = 0,
        double teamGold = 0)
        => new(id, win, kills, deaths, assists, teamKills, damage, teamDamage, taken, teamTaken, gold, teamGold);

    private static PerformanceDataset Dataset(params MatchStatRow[] rows)
        => new()
        {
            SchemaVersion = PerformanceJson.SchemaVersion,
            Source = PerformanceJson.UserOwnedSource,
            Matches = rows
        };

    private static void MathPrimitives()
    {
        TestAssert.EqualDouble(5, PerformanceMath.PositiveClean(5));
        TestAssert.EqualDouble(0, PerformanceMath.PositiveClean(0));
        TestAssert.EqualDouble(0, PerformanceMath.PositiveClean(-3));
        TestAssert.EqualDouble(0, PerformanceMath.PositiveClean(double.NaN));
        TestAssert.EqualDouble(0, PerformanceMath.PositiveClean(double.PositiveInfinity));

        TestAssert.Null(PerformanceMath.Ratio(0, 10));
        TestAssert.Null(PerformanceMath.Ratio(-1, 10));
        TestAssert.Null(PerformanceMath.Ratio(5, 5));
        TestAssert.Null(PerformanceMath.Ratio(6, 5));
        TestAssert.Null(PerformanceMath.Ratio(double.NaN, 10));
        TestAssert.EqualDouble(0.5, PerformanceMath.Ratio(5, 10)!.Value);
    }

    private static void TinyGoldShare()
    {
        // Gold 1e-300 / team gold 1e10 => goldShare = 1e-310 (a subnormal double).
        // Damage 5e9 / 1e10 = 0.5 => conversion = 0.5 / 1e-310 / 1.6 = ~3e309, which overflows
        // to +Infinity. Legacy clamp01(Infinity) == 0, so the conversion CONTRIBUTES 0 while its
        // 30-point weight stays active because the inputs were present.
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 1, 6, 20, 5e9, 1e10, 1000, 10000, 1e-300, 1e10)
        ]);

        TestAssert.EqualDouble(0.5, score.Components.DamageShare!.Value);
        TestAssert.EqualDouble(1e-310, score.Components.GoldShare!.Value);
        TestAssert.Null(score.Components.Conversion);
        TestAssert.EqualDouble(0, score.Contributions.Conversion);
        TestAssert.EqualDouble(30, score.Weights.Conversion);
        TestAssert.True(score.MissingMetrics.Contains("conversion-overflow"));
        TestAssert.False(score.MissingMetrics.Contains("conversion"));
        TestAssert.True(double.IsFinite(score.RawWeightedSum!.Value));
        TestAssert.True(double.IsFinite(score.NormalizedScore!.Value));
        TestAssert.True(double.IsFinite(score.FinalScore!.Value));

        // Gold 1 against a 1e308 team total => share 1e-308; conversion ~6.25e306 is finite but
        // far beyond the denominator, so it clamps to the same contribution.
        var normal = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 1, 6, 20, 1000, 10000, 1000, 10000, 1, 1e308)
        ]);
        TestAssert.True(normal.Components.GoldShare!.Value > 0);
        TestAssert.True(double.IsFinite(normal.Components.Conversion!.Value));
        TestAssert.EqualDouble(30, normal.Contributions.Conversion);
        TestAssert.True(double.IsFinite(normal.FinalScore!.Value));

        // A conversion that is merely enormous (but finite) clamps identically.
        var finite = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 1, 6, 20, 1000, 10000, 1000, 10000, 100000, 1e308)
        ]);
        TestAssert.True(double.IsFinite(finite.Components.Conversion!.Value));
        TestAssert.EqualDouble(30, finite.Contributions.Conversion);

        // The supervised reference case (84-case parity corpus). Its expected final score bakes in
        // "conversion contributes 0, 30-point weight stays active"; a 1.0 clamp would give 99.25.
        var reference = PerformanceCalculator.Calculate([
            Row("reference", true, 5, 3, 8, 50, 5e9, 1e10, 5000, 50000, 0.01, 1e308)
        ]);
        TestAssert.EqualDouble(59.07055555555556, reference.FinalScore!.Value, 1e-10);
        TestAssert.EqualDouble(0, reference.Contributions.Conversion);
        TestAssert.EqualDouble(30, reference.Weights.Conversion);
        TestAssert.Null(reference.Components.Conversion);
        TestAssert.True(reference.MissingMetrics.Contains("conversion-overflow"));

        // DTO must serialize with a stock serializer: no NaN/Infinity can appear anywhere.
        var json = JsonSerializer.Serialize(reference);
        TestAssert.False(json.Contains("Infinity", StringComparison.Ordinal), json);
        TestAssert.False(json.Contains("NaN", StringComparison.Ordinal), json);
        var roundTripped = JsonSerializer.Deserialize<PerformanceScore>(json);
        TestAssert.EqualDouble(reference.FinalScore!.Value, roundTripped!.FinalScore!.Value);
        TestAssert.Null(roundTripped.Components.Conversion);

        foreach (var s in new[] { score, normal, finite })
        {
            TestAssert.True(double.IsFinite(s.RawWeightedSum!.Value));
            TestAssert.True(double.IsFinite(s.NormalizedScore!.Value));
            TestAssert.True(double.IsFinite(s.FinalScore!.Value));
            TestAssert.True(s.FinalScore!.Value is >= 0 and <= 100);
            TestAssert.True(double.IsFinite(s.Components.KillParticipation!.Value));
        }
    }

    private static void ClampInfinity()
    {
        // Clamp01 is internal. Legacy semantics: any non-finite value (including +Infinity) maps to
        // 0, so an overflowed term contributes nothing but never leaks NaN/Infinity into the result.
        var clamped = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 1, 6, 20, double.MaxValue, double.MaxValue, 0, 0, 1, 1e300)
        ]);
        TestAssert.True(clamped.FinalScore is >= 0 and <= 100);
        TestAssert.True(double.IsFinite(clamped.RawWeightedSum!.Value));

        // A term at exactly the denominator reaches 1.0; slightly above stays at 1.0.
        var atLimit = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 1, 6, 20, 3500, 10000, 4000, 10000, 1000, 10000)
        ]);
        TestAssert.EqualDouble(20, atLimit.Contributions.Damage);
        TestAssert.EqualDouble(5, atLimit.Contributions.Taken);
        TestAssert.EqualDouble(30, atLimit.Contributions.Conversion);
    }

    private static void OneGameParity()
    {
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 10, 0, 5, 20, 30000, 60000, 20000, 100000, 12000, 60000)
        ]);

        TestAssert.True(score.IsAvailable);
        TestAssert.Equal(1, score.GameCount);
        TestAssert.Equal(1, score.WinCount);
        TestAssert.EqualDouble(1, score.WinRate);
        TestAssert.EqualDouble(15, score.AggregateKda!.Value);
        TestAssert.EqualDouble(0.75, score.Components.KillParticipation!.Value);
        TestAssert.EqualDouble(0.5, score.Components.DamageShare!.Value);
        TestAssert.EqualDouble(0.2, score.Components.GoldShare!.Value);
        TestAssert.EqualDouble(0.2, score.Components.TakenShare!.Value);
        TestAssert.EqualDouble(1.5625, score.Components.Conversion!.Value);
        TestAssert.EqualDouble(97.5, score.RawWeightedSum!.Value);
        TestAssert.EqualDouble(97.5, score.NormalizedScore!.Value);
        TestAssert.EqualDouble(66.25, score.FinalScore!.Value);
        TestAssert.EqualDouble(20, score.Contributions.WinRate);
        TestAssert.EqualDouble(8, score.Contributions.Kda);
        TestAssert.EqualDouble(17, score.Contributions.KillParticipation);
        TestAssert.EqualDouble(30, score.Contributions.Conversion);
        TestAssert.EqualDouble(20, score.Contributions.Damage);
        TestAssert.EqualDouble(2.5, score.Contributions.Taken);
        TestAssert.EqualDouble(100, score.Weights.Total);
        TestAssert.Equal(0, score.MissingMetrics.Count);
        TestAssert.Equal(PerformanceCalculator.AlgorithmVersion, score.AlgorithmVersion);
    }

    private static void TwoGameParity()
    {
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 2, 6, 20, 20000, 40000, 10000, 50000, 10000, 50000),
            Row("m2", false, 2, 3, 2, 15, 15000, 30000, 15000, 30000, 8000, 40000)
        ]);

        TestAssert.EqualDouble(0.5, score.WinRate);
        TestAssert.EqualDouble(2.8, score.AggregateKda!.Value);
        TestAssert.EqualDouble((0.5 + 4.0 / 15.0) / 2.0, score.Components.KillParticipation!.Value);
        TestAssert.EqualDouble(0.5, score.Components.DamageShare!.Value);
        TestAssert.EqualDouble(0.2, score.Components.GoldShare!.Value);
        TestAssert.EqualDouble(0.35, score.Components.TakenShare!.Value);
        TestAssert.EqualDouble(1.5625, score.Components.Conversion!.Value);
        TestAssert.EqualDouble(80.20833333333333, score.RawWeightedSum!.Value);
        TestAssert.EqualDouble(65.77380952380952, score.FinalScore!.Value);
        TestAssert.EqualDouble(100, score.Weights.Total);
    }

    private static void MissingMetrics()
    {
        var score = PerformanceCalculator.Calculate([Row("m1", true, 4, 0, 4)]);

        TestAssert.True(score.IsAvailable);
        TestAssert.EqualDouble(8, score.AggregateKda!.Value);
        TestAssert.EqualDouble(28, score.Weights.Total);
        TestAssert.EqualDouble(28, score.RawWeightedSum!.Value);
        TestAssert.EqualDouble(100, score.NormalizedScore!.Value);
        TestAssert.EqualDouble(66.66666666666667, score.FinalScore!.Value);
        TestAssert.SequenceEqual(
            ["kill-participation", "damage-share", "gold-share", "conversion", "taken-share"],
            score.MissingMetrics);
        TestAssert.Null(score.Components.KillParticipation);
        TestAssert.Null(score.Components.DamageShare);
        TestAssert.Null(score.Components.GoldShare);
        TestAssert.Null(score.Components.TakenShare);
        TestAssert.Null(score.Components.Conversion);
        TestAssert.EqualDouble(0, score.Contributions.KillParticipation);
        TestAssert.EqualDouble(0, score.Contributions.Conversion);
        TestAssert.EqualDouble(0, score.Contributions.Damage);
        TestAssert.EqualDouble(0, score.Contributions.Taken);
    }

    private static void UnavailableKda()
    {
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 0, 5, 0, 20, 1000, 10000, 1000, 10000, 1000, 10000)
        ]);

        TestAssert.False(score.IsAvailable);
        TestAssert.Null(score.AggregateKda);
        TestAssert.Null(score.RawWeightedSum);
        TestAssert.Null(score.NormalizedScore);
        TestAssert.Null(score.FinalScore);
        TestAssert.True(score.MissingMetrics.Contains("aggregate-kda"));
        TestAssert.EqualDouble(0, score.Contributions.Kda);
        TestAssert.EqualDouble(0.1, score.Components.DamageShare!.Value);
    }

    private static void ZeroDeaths()
    {
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 3, 0, 4, 20, 1000, 10000, 1000, 10000, 1000, 10000)
        ]);

        TestAssert.EqualDouble(7, score.AggregateKda!.Value);
        TestAssert.True(score.IsAvailable);
    }

    private static void EqualityShares()
    {
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 4, 1, 6, 10, 100, 100, 20, 20, 50, 50)
        ]);

        TestAssert.EqualDouble(10, score.AggregateKda!.Value);
        TestAssert.Null(score.Components.KillParticipation);
        TestAssert.Null(score.Components.DamageShare);
        TestAssert.Null(score.Components.GoldShare);
        TestAssert.Null(score.Components.TakenShare);
        TestAssert.Null(score.Components.Conversion);
        TestAssert.EqualDouble(28, score.Weights.Total);
        TestAssert.EqualDouble(28, score.RawWeightedSum!.Value);
        TestAssert.EqualDouble(100, score.NormalizedScore!.Value);
        TestAssert.EqualDouble(66.66666666666667, score.FinalScore!.Value);
        TestAssert.Equal(5, score.MissingMetrics.Count);
    }

    private static void ClampTerms()
    {
        // Damage share 10000/100000 = 0.1; gold 2500/100000 = 0.025;
        // conversion = 0.1 / 0.025 / 1.6 = 2.5, clamped to 1.
        // Taken share 0.28 => taken term 0.7; KDA 100 clamps to 1.
        var score = PerformanceCalculator.Calculate([
            Row("m1", true, 100, 0, 0, 500, 10000, 100000, 28000, 100000, 2500, 100000)
        ]);

        TestAssert.EqualDouble(100, score.AggregateKda!.Value);
        TestAssert.EqualDouble(8, score.Contributions.Kda);
        TestAssert.EqualDouble(0.1, score.Components.DamageShare!.Value);
        TestAssert.EqualDouble(0.025, score.Components.GoldShare!.Value);
        TestAssert.EqualDouble(0.28, score.Components.TakenShare!.Value);
        TestAssert.EqualDouble(2.5, score.Components.Conversion!.Value);
        TestAssert.EqualDouble(30, score.Contributions.Conversion);
        TestAssert.EqualDouble(5.714285714285715, score.Contributions.Damage);
        TestAssert.EqualDouble(3.5, score.Contributions.Taken);
        TestAssert.EqualDouble(100, score.Weights.Total);
        // KP 100/500 = 0.2, comfortably below the 0.5 denominator.
        TestAssert.EqualDouble(0.2, score.Components.KillParticipation!.Value);
        TestAssert.NotNull(score.Components.TakenShare);
        TestAssert.EqualDouble(100, score.Weights.Total);
        TestAssert.EqualDouble(74.0142857142857, score.RawWeightedSum!.Value);
        TestAssert.EqualDouble(74.0142857142857, score.NormalizedScore!.Value);
        TestAssert.EqualDouble((74.0142857142857 + 60 * 5) / 6.0, score.FinalScore!.Value);
        TestAssert.True(score.FinalScore is >= 0 and <= 100);
    }

    private static void SampleShrinkage()
    {
        static PerformanceDataset Dataset(int count) => new()
        {
            SchemaVersion = 1,
            Source = PerformanceJson.UserOwnedSource,
            Matches = Enumerable.Range(0, count)
                .Select(i => Row($"m{i:000}", true, 10, 0, 5, 20, 30000, 60000, 20000, 100000, 12000, 60000))
                .ToArray()
        };

        var one = PerformanceCalculator.Calculate(Dataset(1));
        var five = PerformanceCalculator.Calculate(Dataset(5));
        var hundred = PerformanceCalculator.Calculate(Dataset(100));

        TestAssert.EqualDouble(97.5, one.NormalizedScore!.Value);
        TestAssert.EqualDouble(66.25, one.FinalScore!.Value);
        TestAssert.EqualDouble(78.75, five.FinalScore!.Value);
        TestAssert.EqualDouble(95.71428571428571, hundred.FinalScore!.Value);
        TestAssert.True(one.FinalScore < five.FinalScore && five.FinalScore < hundred.FinalScore);
    }

    private static void PermutationInvariant()
    {
        var first = Row("a", true, 4, 2, 6, 20, 20000, 40000, 10000, 50000, 10000, 50000);
        var second = Row("b", false, 2, 3, 2, 15, 15000, 30000, 15000, 30000, 8000, 40000);

        var forward = PerformanceCalculator.Calculate([first, second]);
        var reverse = PerformanceCalculator.Calculate([second, first]);

        TestAssert.EqualDouble(forward.FinalScore!.Value, reverse.FinalScore!.Value, 0);
        TestAssert.EqualDouble(forward.Components.KillParticipation!.Value, reverse.Components.KillParticipation!.Value, 0);
    }

    private static void InvalidRows()
    {
        TestAssert.Throws("performance.empty", () => PerformanceCalculator.Calculate(Array.Empty<MatchStatRow>()));
        TestAssert.Throws("performance.invalid-count", () => PerformanceCalculator.Calculate([Row("m1", kills: -1)]));
        TestAssert.Throws("performance.invalid-number", () => PerformanceCalculator.Calculate([Row("m1", damage: -1)]));
        TestAssert.Throws("performance.invalid-number", () => PerformanceCalculator.Calculate([Row("m1", damage: double.NaN)]));
        TestAssert.Throws("performance.invalid-number", () => PerformanceCalculator.Calculate([Row("m1", taken: double.PositiveInfinity)]));
        TestAssert.Throws("performance.duplicate-match-id", () => PerformanceCalculator.Calculate([Row("m1"), Row("m1")]));
        TestAssert.Throws("performance.match-id", () => PerformanceCalculator.Calculate([Row("  ")]));
        TestAssert.Throws("performance.match-id", () => PerformanceCalculator.Calculate([Row(" m1")]));
        TestAssert.Throws("performance.null-row", () => PerformanceCalculator.Calculate(new List<MatchStatRow> { null! }));
    }

    private static void DatasetValidation()
    {
        var validRows = new[] { Row("m1", true, 1, 0, 1, 10) };

        var baseline = PerformanceCalculator.Calculate(Dataset(validRows));
        foreach (var source in new[] { "live", "manual", "synthetic", "other-import", "采集器", new string('s', 120) })
        {
            var dataset = new PerformanceDataset { Source = source, Matches = validRows };
            var score = PerformanceCalculator.Calculate(dataset);
            TestAssert.Equal(baseline.FinalScore, score.FinalScore);
            var imported = PerformanceJson.Parse(PerformanceJson.Export(dataset));
            TestAssert.Equal(source, imported.Source);
            TestAssert.Equal(score.FinalScore, PerformanceCalculator.Calculate(imported).FinalScore);
        }

        foreach (var source in new string?[] { null, "", " ", " live", "live ", "li\nve", new string('s', 121) })
        {
            var invalid = new PerformanceDataset { Source = source!, Matches = validRows };
            TestAssert.Throws("performance.source", () => PerformanceCalculator.Calculate(invalid));
            TestAssert.Throws("performance.source", () => PerformanceJson.Export(invalid));
            TestAssert.Throws("performance.source", () => PerformanceJson.Parse(
                "{\"schemaVersion\":1,\"source\":" + JsonSerializer.Serialize(source) + ",\"matches\":[]}"));
        }
        TestAssert.Throws("performance.source", () => PerformanceJson.Parse("{\"schemaVersion\":1,\"matches\":[]}"));

        TestAssert.Throws("performance.schema-version", () => PerformanceCalculator.Calculate(new PerformanceDataset
        {
            SchemaVersion = 2,
            Source = PerformanceJson.UserOwnedSource,
            Matches = validRows
        }));

        TestAssert.NotNull(baseline.FinalScore);
    }

    private static void JsonRoundTrip()
    {
        var demo = SyntheticScenarioGenerator.CreatePerformanceDemo();
        var json = PerformanceJson.Export(demo);
        var parsed = PerformanceJson.Parse(json);

        TestAssert.Equal(12, parsed.Matches.Count);
        TestAssert.Equal(PerformanceJson.UserOwnedSource, parsed.Source);
        TestAssert.Equal(1, parsed.SchemaVersion);
        TestAssert.EqualDouble(
            PerformanceCalculator.Calculate(demo).FinalScore!.Value,
            PerformanceCalculator.Calculate(parsed).FinalScore!.Value,
            1e-12);

        var fixture = PerformanceJson.LoadFile(TestPaths.Fixture("performance-demo.json"));
        TestAssert.Equal(12, fixture.Matches.Count);
        var score = PerformanceCalculator.Calculate(fixture);
        TestAssert.Equal(12, score.GameCount);
        TestAssert.Equal(8, score.WinCount);
        TestAssert.True(score.FinalScore is > 0 and <= 100);
        TestAssert.Equal(0, score.MissingMetrics.Count);
    }

    private static void JsonMissingFixture()
    {
        var parsed = PerformanceJson.LoadFile(TestPaths.Fixture("performance-missing-metrics.json"));
        TestAssert.Equal(4, parsed.Matches.Count);

        // Row 2 supplies all four shares at equality, so nothing is available.
        var equalityOnly = PerformanceCalculator.Calculate([parsed.Matches[1]]);
        TestAssert.Null(equalityOnly.Components.KillParticipation);
        TestAssert.Null(equalityOnly.Components.DamageShare);
        TestAssert.Null(equalityOnly.Components.GoldShare);
        TestAssert.Null(equalityOnly.Components.TakenShare);
        TestAssert.Equal(5, equalityOnly.MissingMetrics.Count);

        // Row 1 is all zero. Row 2 shares are all at equality, so they are excluded.
        // Row 3: damage 5k/40k = 0.125, gold 1k/40k = 0.025, taken 1k/40k = 0.025.
        // Row 4: damage 9k/45k = 0.2, gold 3k/45k = 1/15, taken 3k/45k = 1/15.
        // Row 2 is excluded (every share at equality) and row 1 is all zero.
        var whole = PerformanceCalculator.Calculate(parsed);
        TestAssert.True(whole.IsAvailable);
        TestAssert.Equal(4, whole.GameCount);
        var killParticipation = 8.0 / 11.0;
        var damage = (0.125 + 0.2) / 2;
        var gold = (0.025 + 1.0 / 15.0) / 2;
        var taken = (0.025 + 1.0 / 15.0) / 2;
        TestAssert.EqualDouble(killParticipation, whole.Components.KillParticipation!.Value);
        TestAssert.EqualDouble(0.1625, damage);
        TestAssert.EqualDouble(0.04583333333333334, gold);
        TestAssert.EqualDouble(0.04583333333333334, taken);
        TestAssert.EqualDouble(damage, whole.Components.DamageShare!.Value);
        TestAssert.EqualDouble(gold, whole.Components.GoldShare!.Value);
        TestAssert.EqualDouble(taken, whole.Components.TakenShare!.Value);
        TestAssert.EqualDouble(damage / gold / 1.6, whole.Components.Conversion!.Value);
        TestAssert.EqualDouble(28 + 17 + 30 + 20 + 5, whole.Weights.Total);
        TestAssert.SequenceEqual(Array.Empty<string>(), whole.MissingMetrics);
        TestAssert.NotNull(whole.FinalScore);
    }

    private static void JsonMalformed()
    {
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse("{"));
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse("null"));
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse("{\"schemaVersion\":1,\"source\":\"user-owned-completed-matches\",\"matches\":[],\"extra\":1}"));

        TestAssert.Throws("performance.structure", () => PerformanceJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"user-owned-completed-matches\",\"matches\":[{}]}"));

        TestAssert.Throws("performance.structure", () => PerformanceJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"user-owned-completed-matches\",\"matches\":[{\"matchId\":\"m1\",\"kills\":1,\"deaths\":0,\"assists\":0,\"teamTotalKills\":1,\"totalDamageDealt\":0,\"teamTotalDamageDealt\":0,\"totalDamageTaken\":0,\"teamTotalDamageTaken\":0,\"goldEarned\":0,\"teamTotalGoldEarned\":0}]}"));

        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"user-owned-completed-matches\",\"matches\":[{\"matchId\":\"m1\",\"win\":true,\"kills\":1.5,\"deaths\":0,\"assists\":0,\"teamTotalKills\":1,\"totalDamageDealt\":0,\"teamTotalDamageDealt\":0,\"totalDamageTaken\":0,\"teamTotalDamageTaken\":0,\"goldEarned\":0,\"teamTotalGoldEarned\":0}]}"));

        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"user-owned-completed-matches\",\"matches\":[{\"matchId\":\"m1\",\"win\":true,\"kills\":1,\"deaths\":0,\"assists\":0,\"teamTotalKills\":1,\"totalDamageDealt\":NaN,\"teamTotalDamageDealt\":0,\"totalDamageTaken\":0,\"teamTotalDamageTaken\":0,\"goldEarned\":0,\"teamTotalGoldEarned\":0}]}"));
    }

    private static void JsonValidation()
    {
        TestAssert.Throws("performance.schema-version", () => PerformanceJson.Parse(
            "{\"schemaVersion\":2,\"source\":\"user-owned-completed-matches\",\"matches\":[]}"));

        TestAssert.Throws("performance.source", () => PerformanceJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"\",\"matches\":[]}"));

        const string row = """
            {
              "matchId": "dup",
              "win": true,
              "kills": 1,
              "deaths": 0,
              "assists": 1,
              "teamTotalKills": 5,
              "totalDamageDealt": 100.0,
              "teamTotalDamageDealt": 500.0,
              "totalDamageTaken": 100.0,
              "teamTotalDamageTaken": 500.0,
              "goldEarned": 100.0,
              "teamTotalGoldEarned": 500.0
            }
            """;
        TestAssert.Throws("performance.duplicate-match-id", () => PerformanceJson.Parse(
            $"{{\"schemaVersion\":1,\"source\":\"user-owned-completed-matches\",\"matches\":[{row},{row}]}}"));

        var bad = Dataset(Row("m1", true, 1, 0, 1, 5, damage: 100, teamDamage: 500));
        bad = new PerformanceDataset { SchemaVersion = 1, Source = "", Matches = bad.Matches };
        TestAssert.Throws("performance.source", () => PerformanceJson.Export(bad));
    }

    private static void JsonDuplicateProperties()
    {
        const string row = """
              {
                "matchId": "m1",
                "win": true,
                "kills": 1,
                "deaths": 0,
                "assists": 1,
                "teamTotalKills": 5,
                "totalDamageDealt": 100.0,
                "teamTotalDamageDealt": 500.0,
                "totalDamageTaken": 100.0,
                "teamTotalDamageTaken": 500.0,
                "goldEarned": 100.0,
                "teamTotalGoldEarned": 500.0
              }
            """;

        // Control: the same document without duplication must still parse.
        TestAssert.NotNull(PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"source":"user-owned-completed-matches","matches":[{{row}}]}"""));

        // Duplicate top-level schemaVersion / source with disagreeing values. Both orders are
        // checked in case a serializer ever falls back to last-value-wins.
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"schemaVersion":2,"source":"user-owned-completed-matches","matches":[{{row}}]}"""));
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"source":"live","source":"user-owned-completed-matches","matches":[{{row}}]}"""));
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"source":"user-owned-completed-matches","source":"live","matches":[{{row}}]}"""));

        // Case-variant spelling binds to the same member, so it is a duplicate too.
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"Source":"live","source":"user-owned-completed-matches","matches":[{{row}}]}"""));

        // Duplicate nested match property (win with conflicting values, and a numeric field).
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"source":"user-owned-completed-matches","matches":[{{row.Replace("\"win\": true,", "\"win\": true,\"win\": false,")}}]}"""));
        TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"source":"user-owned-completed-matches","matches":[{{row.Replace("\"kills\": 1,", "\"kills\": 1,\"kills\": 2,")}}]}"""));

        // The error reports the JSON path so the UI can point at the problem.
        var exception = TestAssert.Throws("performance.json", () => PerformanceJson.Parse(
            $$"""{"schemaVersion":1,"source":"user-owned-completed-matches","matches":[{{row.Replace("\"win\": true,", "\"win\": true,\"win\": false,")}}]}"""));
        TestAssert.Contains("Duplicate JSON property", exception.Message);
        TestAssert.Contains("$.matches[0]", exception.Message);
    }

    private static void JsonSizeGuard()
    {
        TestAssert.Equal(5L * 1024 * 1024, PerformanceJson.MaxBytes);
        TestAssert.Throws("scoring.file-too-large", () => PerformanceJson.Parse(new string(' ', (int)PerformanceJson.MaxBytes + 1)));

        var path = Path.Combine(Path.GetTempPath(), $"lolkarmabook-performance-{Guid.NewGuid():N}.json");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(PerformanceJson.MaxBytes + 1);
            }

            TestAssert.Throws("scoring.file-too-large", () => PerformanceJson.LoadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
