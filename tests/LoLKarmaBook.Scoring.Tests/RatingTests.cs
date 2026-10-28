using System.Text.Json;
using LoLKarmaBook.Scoring.Rating;

namespace LoLKarmaBook.Scoring.Tests;

internal static class RatingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly SyntheticScope Scope = new("synthetic", "realm", "queue", "patch");
    private static readonly string[] TeamA = ["synthetic:a1", "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"];
    private static readonly string[] TeamB = ["synthetic:b1", "synthetic:b2", "synthetic:b3", "synthetic:b4", "synthetic:b5"];

    public static void Register(TestRunner runner)
    {
        runner.Add("rating: equal teams, A win is +12/-12", EqualTeamsAWin);
        runner.Add("rating: second match hand-computable update", TwoMatchesHandComputed);
        runner.Add("rating: B win mirrors the update", BWin);
        runner.Add("rating: zero-sum across a generated scenario", ZeroSum);
        runner.Add("rating: determinism and match/team permutation", Determinism);
        runner.Add("rating: identical duplicates dedupe and report", DuplicateDedupe);
        runner.Add("rating: conflicting duplicate IDs reject", DuplicateConflict);
        runner.Add("rating: malformed teams reject", InvalidTeams);
        runner.Add("rating: source labels accept live/manual and retain validation", SourceLabels);
        runner.Add("rating: scope must be full and consistent", ScopeGating);
        runner.Add("rating: timestamp-then-id ordering", Ordering);
        runner.Add("rating: provisional status below 10 games", ProvisionalStatus);
        runner.Add("rating: connected-pool reporting and isolation", ConnectedPools);
        runner.Add("rating: target selection and validation", TargetSelection);
        runner.Add("rating: structure and schema validation", StructureValidation);
        runner.Add("rating JSON: round-trip and demo fixtures", JsonRoundTrip);
        runner.Add("rating JSON: source/schema/required fields/teams", JsonValidation);
        runner.Add("rating JSON: size guard", JsonSizeGuard);
        runner.Add("rating JSON: duplicate properties rejected", JsonDuplicateProperties);
    }

    private static SyntheticRatingMatch Match(
        string id,
        DateTimeOffset timestamp,
        string winner = "A",
        IReadOnlyList<string>? teamA = null,
        IReadOnlyList<string>? teamB = null,
        SyntheticScope? scope = null)
        => new(id, timestamp, scope, teamA ?? TeamA, teamB ?? TeamB, winner);

    private static SyntheticRatingDataset Dataset(params SyntheticRatingMatch[] matches)
        => new()
        {
            SchemaVersion = SyntheticRatingJson.SchemaVersion,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = Scope,
            Matches = matches
        };

    private static PlayerRating Player(SyntheticRatingResult result, string id)
        => result.Players.Single(p => p.PlayerId == id);

    private static void EqualTeamsAWin()
    {
        var result = SyntheticRatingCalculator.Calculate(Dataset(Match("m1", T0)));

        TestAssert.Equal(10, result.PlayerCount);
        TestAssert.Equal(1, result.MatchCount);
        TestAssert.Equal(0, result.DuplicateMatchCount);
        TestAssert.Equal(1, result.ConnectedPoolCount);
        TestAssert.Equal(10, result.LargestConnectedPoolSize);
        TestAssert.Equal(SyntheticRatingCalculator.AlgorithmVersion, result.AlgorithmVersion);

        var a1 = Player(result, "synthetic:a1");
        var b1 = Player(result, "synthetic:b1");
        TestAssert.EqualDouble(1512, a1.Rating);
        TestAssert.EqualDouble(1488, b1.Rating);
        TestAssert.Equal(1, a1.Games);
        TestAssert.True(a1.IsProvisional);
        TestAssert.Equal("synthetic:a1", a1.ConnectedPoolId);
        TestAssert.Equal(10, a1.ConnectedPoolSize);

        var history = result.History.Single();
        TestAssert.EqualDouble(0.5, history.ExpectedTeamA);
        TestAssert.EqualDouble(12, history.Delta);
        TestAssert.EqualDouble(1500, history.TeamAAverageBefore);
        TestAssert.EqualDouble(1500, history.TeamBAverageBefore);
        TestAssert.Equal(10, history.Changes.Count);
        TestAssert.EqualDouble(0, history.Changes.Sum(c => c.RatingAfter - c.RatingBefore));
    }

    private static void TwoMatchesHandComputed()
    {
        var result = SyntheticRatingCalculator.Calculate(Dataset(
            Match("m1", T0),
            Match("m2", T0.AddHours(1))));

        TestAssert.EqualDouble(1523.172385326556, Player(result, "synthetic:a1").Rating);
        TestAssert.EqualDouble(1476.827614673444, Player(result, "synthetic:b1").Rating);
        TestAssert.EqualDouble(0.5344839447268315, result.History[1].ExpectedTeamA);
        TestAssert.EqualDouble(11.172385326556045, result.History[1].Delta);
        TestAssert.Equal(2, Player(result, "synthetic:a1").Games);
        TestAssert.True(Player(result, "synthetic:a1").IsProvisional);
    }

    private static void BWin()
    {
        var result = SyntheticRatingCalculator.Calculate(Dataset(Match("m1", T0, "B")));

        TestAssert.EqualDouble(1488, Player(result, "synthetic:a1").Rating);
        TestAssert.EqualDouble(1512, Player(result, "synthetic:b1").Rating);
        TestAssert.EqualDouble(-12, result.History.Single().Delta);
        TestAssert.EqualDouble(0.5, result.History.Single().ExpectedTeamA);
        TestAssert.EqualDouble(1512, result.History.Single().Changes.Single(c => c.PlayerId == "synthetic:b1").RatingAfter);
    }

    private static void ZeroSum()
    {
        var result = SyntheticRatingCalculator.Calculate(SyntheticScenarioGenerator.CreateRatingDemo());

        var net = result.Players.Sum(p => p.Rating - SyntheticRatingCalculator.InitialRating);
        TestAssert.EqualDouble(0, net, 1e-8);
        TestAssert.Equal(19, result.PlayerCount);

        foreach (var match in result.History)
        {
            var matchNet = match.Changes.Sum(c => c.RatingAfter - c.RatingBefore);
            TestAssert.EqualDouble(0, matchNet, 1e-9);
        }
    }

    private static void Determinism()
    {
        var demo = SyntheticScenarioGenerator.CreateRatingDemo();
        var first = SyntheticRatingCalculator.Calculate(demo);
        var second = SyntheticRatingCalculator.Calculate(demo);
        EqualRatings(first, second);

        var reversed = new SyntheticRatingDataset
        {
            SchemaVersion = 1,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = demo.Scope,
            Matches = demo.Matches
                .Reverse()
                .Select(m => new SyntheticRatingMatch(
                    m.MatchId,
                    m.Timestamp,
                    m.Scope,
                    m.TeamA.Reverse().ToArray(),
                    m.TeamB.Reverse().ToArray(),
                    m.Winner))
                .ToArray()
        };

        EqualRatings(first, SyntheticRatingCalculator.Calculate(reversed));
    }

    private static void DuplicateDedupe()
    {
        var m1 = Match("m1", T0);
        var m2 = Match("m2", T0.AddHours(1), "B");
        // A duplicate is the same game: players are on the same side, only list order differs.
        var duplicate = new SyntheticRatingMatch(
            "m2",
            m2.Timestamp,
            null,
            m2.TeamA.Reverse().ToArray(),
            m2.TeamB.Reverse().ToArray(),
            m2.Winner);

        var deduped = SyntheticRatingCalculator.Calculate(Dataset(m1, m2, duplicate));
        var plain = SyntheticRatingCalculator.Calculate(Dataset(m1, m2));

        TestAssert.Equal(2, deduped.MatchCount);
        TestAssert.Equal(1, deduped.DuplicateMatchCount);
        TestAssert.Equal(2, deduped.History.Count);
        TestAssert.Equal(0, plain.DuplicateMatchCount);
        EqualRatings(plain, deduped, compareDuplicateCount: false);
    }

    private static void DuplicateConflict()
    {
        var m1 = Match("same", T0);
        var conflictWinner = Match("same", T0, "B");
        var conflictTime = Match("same", T0.AddMinutes(1));

        TestAssert.Throws("rating.duplicate-id-conflict", () => SyntheticRatingCalculator.Calculate(Dataset(m1, conflictWinner)));
        TestAssert.Throws("rating.duplicate-id-conflict", () => SyntheticRatingCalculator.Calculate(Dataset(m1, conflictTime)));
    }

    private static void InvalidTeams()
    {
        static SyntheticRatingMatch WithTeams(string id, IReadOnlyList<string> a, IReadOnlyList<string> b)
            => new(id, T0, null, a, b, "A");

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(WithTeams("m1", TeamA[..4], TeamB))));

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(WithTeams("m1", [.. TeamA, "synthetic:a6"], TeamB))));

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(WithTeams("m1", ["synthetic:a1", "synthetic:a1", "synthetic:a2", "synthetic:a3", "synthetic:a4"], TeamB))));

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(WithTeams("m1", TeamA, ["synthetic:a1", "synthetic:b2", "synthetic:b3", "synthetic:b4", "synthetic:b5"]))));

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(WithTeams("m1", ["", "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"], TeamB))));

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(WithTeams("m1", [" synthetic:a1", "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"], TeamB))));

        TestAssert.Throws("rating.teams", () => SyntheticRatingCalculator.Calculate(
            Dataset(new SyntheticRatingMatch("m1", T0, null, null!, TeamB, "A"))));
    }

    private static void SourceLabels()
    {
        var baseline = SyntheticRatingCalculator.Calculate(Dataset(Match("m1", T0)));
        foreach (var source in new[] { "live", "manual", "manual-real", "user-owned-completed-matches", "Synthetic", "采集器", new string('s', 120) })
        {
            var dataset = new SyntheticRatingDataset
            {
                SchemaVersion = 1,
                Source = source,
                Scope = Scope,
                Matches = [Match("m1", T0)]
            };
            var result = SyntheticRatingCalculator.Calculate(dataset);
            TestAssert.EqualDouble(Player(baseline, "synthetic:a1").Rating, Player(result, "synthetic:a1").Rating);
            var imported = SyntheticRatingJson.Parse(SyntheticRatingJson.Export(dataset));
            TestAssert.Equal(source, imported.Source);
            TestAssert.EqualDouble(Player(result, "synthetic:a1").Rating,
                Player(SyntheticRatingCalculator.Calculate(imported), "synthetic:a1").Rating);
        }

        foreach (var source in new string?[] { null, "", " ", " live", "live ", "li\nve", new string('s', 121) })
        {
            var invalid = new SyntheticRatingDataset
            {
                Source = source!,
                Scope = Scope,
                Matches = [Match("m1", T0)]
            };
            TestAssert.Throws("rating.source", () => SyntheticRatingCalculator.Calculate(invalid));
            TestAssert.Throws("rating.source", () => SyntheticRatingJson.Export(invalid));
            TestAssert.Throws("rating.source", () => SyntheticRatingJson.Parse(
                "{\"schemaVersion\":1,\"source\":" + JsonSerializer.Serialize(source)
                + ",\"scope\":{\"operator\":\"synthetic\",\"realm\":\"r\",\"queue\":\"q\",\"patch\":\"p\"},\"matches\":[]}"));
        }

        TestAssert.Throws("rating.source", () => SyntheticRatingJson.Parse(
            "{\"schemaVersion\":1,\"scope\":{\"operator\":\"synthetic\",\"realm\":\"r\",\"queue\":\"q\",\"patch\":\"p\"},\"matches\":[]}"));
    }

    private static void ScopeGating()
    {
        var other = new SyntheticScope("synthetic", "other-realm", "queue", "patch");
        TestAssert.Throws("rating.scope", () => SyntheticRatingCalculator.Calculate(
            Dataset(Match("m1", T0, scope: other))));

        TestAssert.Throws("rating.scope", () => SyntheticRatingCalculator.Calculate(new SyntheticRatingDataset
        {
            SchemaVersion = 1,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = new SyntheticScope("", "realm", "queue", "patch"),
            Matches = [Match("m1", T0)]
        }));

        TestAssert.Throws("rating.scope", () => SyntheticRatingCalculator.Calculate(new SyntheticRatingDataset
        {
            SchemaVersion = 1,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = null!,
            Matches = [Match("m1", T0)]
        }));

        var same = SyntheticRatingCalculator.Calculate(Dataset(Match("m1", T0, scope: Scope)));
        TestAssert.EqualDouble(1512, Player(same, "synthetic:a1").Rating);
    }

    private static void Ordering()
    {
        var early = Match("early", T0.AddHours(1));
        var late = Match("late", T0.AddHours(3));
        var result = SyntheticRatingCalculator.Calculate(Dataset(late, early));

        TestAssert.SequenceEqual(["early", "late"], result.History.Select(h => h.MatchId));

        var firstAtSameTime = Match("z", T0);
        var secondAtSameTime = Match("a", T0);
        var tied = SyntheticRatingCalculator.Calculate(Dataset(firstAtSameTime, secondAtSameTime));
        TestAssert.SequenceEqual(["a", "z"], tied.History.Select(h => h.MatchId));
    }

    private static void ProvisionalStatus()
    {
        var nine = SyntheticRatingCalculator.Calculate(
            SyntheticScenarioGenerator.CreateRatingDemo(9),
            [SyntheticScenarioGenerator.DefaultTargetPlayerId]);
        TestAssert.Equal(9, nine.Players.Single().Games);
        TestAssert.True(nine.Players.Single().IsProvisional);

        var ten = SyntheticRatingCalculator.Calculate(
            SyntheticScenarioGenerator.CreateRatingDemo(10),
            [SyntheticScenarioGenerator.DefaultTargetPlayerId]);
        TestAssert.Equal(10, ten.Players.Single().Games);
        TestAssert.False(ten.Players.Single().IsProvisional);

        var fourteen = SyntheticRatingCalculator.Calculate(
            SyntheticScenarioGenerator.CreateRatingDemo(),
            [SyntheticScenarioGenerator.DefaultTargetPlayerId]);
        TestAssert.Equal(14, fourteen.Players.Single().Games);
        TestAssert.False(fourteen.Players.Single().IsProvisional);
    }

    private static void ConnectedPools()
    {
        var poolA = Match(
            "pool-a",
            T0,
            teamA: ["synthetic:a1", "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"],
            teamB: ["synthetic:b1", "synthetic:b2", "synthetic:b3", "synthetic:b4", "synthetic:b5"]);
        var poolB = Match(
            "pool-b",
            T0.AddHours(1),
            teamA: ["synthetic:c1", "synthetic:c2", "synthetic:c3", "synthetic:c4", "synthetic:c5"],
            teamB: ["synthetic:d1", "synthetic:d2", "synthetic:d3", "synthetic:d4", "synthetic:d5"]);

        var both = SyntheticRatingCalculator.Calculate(Dataset(poolA, poolB));
        TestAssert.Equal(2, both.ConnectedPoolCount);
        TestAssert.Equal(10, both.LargestConnectedPoolSize);
        TestAssert.Equal(20, both.PlayerCount);

        var a1 = Player(both, "synthetic:a1");
        var c1 = Player(both, "synthetic:c1");
        TestAssert.Equal("synthetic:a1", a1.ConnectedPoolId);
        TestAssert.Equal(10, a1.ConnectedPoolSize);
        TestAssert.Equal("synthetic:c1", c1.ConnectedPoolId);
        TestAssert.Equal(10, c1.ConnectedPoolSize);
        TestAssert.EqualDouble(1512, a1.Rating);
        TestAssert.EqualDouble(1512, c1.Rating);

        var onlyA = SyntheticRatingCalculator.Calculate(Dataset(poolA));
        TestAssert.EqualDouble(Player(onlyA, "synthetic:a1").Rating, a1.Rating);
    }

    private static void TargetSelection()
    {
        var demo = SyntheticScenarioGenerator.CreateRatingDemo();
        var all = SyntheticRatingCalculator.Calculate(demo);
        var target = SyntheticRatingCalculator.Calculate(demo, [SyntheticScenarioGenerator.DefaultTargetPlayerId]);

        TestAssert.Equal(1, target.Players.Count);
        TestAssert.Equal(19, target.PlayerCount);
        TestAssert.EqualDouble(
            all.Players.Single(p => p.PlayerId == SyntheticScenarioGenerator.DefaultTargetPlayerId).Rating,
            target.Players.Single().Rating,
            0);

        TestAssert.Throws("rating.unknown-target", () => SyntheticRatingCalculator.Calculate(demo, ["synthetic:missing"]));
        TestAssert.Throws("rating.target", () => SyntheticRatingCalculator.Calculate(demo, [" bad"]));
    }

    private static void StructureValidation()
    {
        TestAssert.Throws("rating.empty", () => SyntheticRatingCalculator.Calculate(new SyntheticRatingDataset
        {
            SchemaVersion = 1,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = Scope,
            Matches = []
        }));

        TestAssert.Throws("rating.schema-version", () => SyntheticRatingCalculator.Calculate(new SyntheticRatingDataset
        {
            SchemaVersion = 2,
            Source = SyntheticRatingCalculator.SyntheticSource,
            Scope = Scope,
            Matches = [Match("m1", T0)]
        }));

        TestAssert.Throws("rating.structure", () => SyntheticRatingCalculator.Calculate(
            Dataset(new SyntheticRatingMatch[] { null! })));
        TestAssert.Throws("rating.winner", () => SyntheticRatingCalculator.Calculate(Dataset(Match("m1", T0, "C"))));
        TestAssert.Throws("rating.timestamp", () => SyntheticRatingCalculator.Calculate(Dataset(Match("m1", default))));
        TestAssert.Throws("rating.match-id", () => SyntheticRatingCalculator.Calculate(Dataset(Match(" ", T0))));
    }

    private static void JsonRoundTrip()
    {
        var demo = SyntheticScenarioGenerator.CreateRatingDemo();
        var json = SyntheticRatingJson.Export(demo);
        var parsed = SyntheticRatingJson.Parse(json);

        TestAssert.Equal(14, parsed.Matches.Count);
        TestAssert.Equal(SyntheticRatingCalculator.SyntheticSource, parsed.Source);
        EqualRatings(
            SyntheticRatingCalculator.Calculate(demo),
            SyntheticRatingCalculator.Calculate(parsed));

        var fixture = SyntheticRatingJson.LoadFile(TestPaths.Fixture("synthetic-rating-demo.json"));
        var result = SyntheticRatingCalculator.Calculate(fixture, [SyntheticScenarioGenerator.DefaultTargetPlayerId]);
        TestAssert.Equal(14, fixture.Matches.Count);
        TestAssert.Equal(14, result.Players.Single().Games);
        TestAssert.False(result.Players.Single().IsProvisional);
        TestAssert.True(result.LargestConnectedPoolSize > 10);

        var disconnected = SyntheticRatingJson.LoadFile(TestPaths.Fixture("synthetic-rating-disconnected.json"));
        var disconnectedResult = SyntheticRatingCalculator.Calculate(disconnected);
        TestAssert.Equal(2, disconnectedResult.MatchCount);
        TestAssert.Equal(1, disconnectedResult.DuplicateMatchCount);
        TestAssert.Equal(2, disconnectedResult.ConnectedPoolCount);
        TestAssert.Equal(10, disconnectedResult.LargestConnectedPoolSize);
        TestAssert.Equal(20, disconnectedResult.PlayerCount);
    }

    private static void JsonValidation()
    {
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse("{"));
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse("null"));
        TestAssert.Throws("rating.schema-version", () => SyntheticRatingJson.Parse(
            "{\"schemaVersion\":2,\"source\":\"synthetic\",\"scope\":{\"operator\":\"synthetic\",\"realm\":\"r\",\"queue\":\"q\",\"patch\":\"p\"},\"matches\":[]}"));
        TestAssert.Throws("rating.structure", () => SyntheticRatingJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"synthetic\",\"scope\":{\"operator\":\"synthetic\",\"realm\":\"r\",\"queue\":\"q\",\"patch\":\"p\"},\"matches\":[{}]}"));
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"synthetic\",\"scope\":{\"operator\":\"synthetic\",\"realm\":\"r\",\"queue\":\"q\",\"patch\":\"p\"},\"matches\":[],\"extra\":1}"));
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            "{\"schemaVersion\":1,\"source\":\"synthetic\",\"scope\":{\"operator\":\"synthetic\",\"realm\":\"r\",\"queue\":\"q\",\"patch\":\"p\"},\"matches\":[{\"matchId\":\"m1\",\"timestamp\":\"not-a-date\",\"teamA\":[],\"teamB\":[],\"winner\":\"A\"}]}"));

        var invalidTeams = """
            {
              "schemaVersion": 1,
              "source": "synthetic",
              "scope": {"operator":"synthetic","realm":"r","queue":"q","patch":"p"},
              "matches": [{
                "matchId":"m1",
                "timestamp":"2026-01-01T00:00:00+00:00",
                "teamA":["a1","a2","a3","a4"],
                "teamB":["b1","b2","b3","b4","b5"],
                "winner":"A"
              }]
            }
            """;
        TestAssert.Throws("rating.teams", () => SyntheticRatingJson.Parse(invalidTeams));

        var invalidScope = """
            {
              "schemaVersion": 1,
              "source": "synthetic",
              "scope": {"operator":"synthetic","realm":"r","queue":"q","patch":"p"},
              "matches": [{
                "matchId":"m1",
                "timestamp":"2026-01-01T00:00:00+00:00",
                "scope": {"operator":"synthetic","realm":"OTHER","queue":"q","patch":"p"},
                "teamA":["a1","a2","a3","a4","a5"],
                "teamB":["b1","b2","b3","b4","b5"],
                "winner":"A"
              }]
            }
            """;
        TestAssert.Throws("rating.scope", () => SyntheticRatingJson.Parse(invalidScope));

        var bad = new SyntheticRatingDataset
        {
            SchemaVersion = 1,
            Source = "",
            Scope = Scope,
            Matches = [Match("m1", T0)]
        };
        TestAssert.Throws("rating.source", () => SyntheticRatingJson.Export(bad));
    }

    private static void JsonSizeGuard()
    {
        TestAssert.Equal(5L * 1024 * 1024, SyntheticRatingJson.MaxBytes);
        TestAssert.Throws("scoring.file-too-large", () => SyntheticRatingJson.Parse(new string(' ', (int)SyntheticRatingJson.MaxBytes + 1)));
    }

    private static void JsonDuplicateProperties()
    {
        const string scope = """{"operator":"synthetic","realm":"r","queue":"q","patch":"p"}""";
        const string match = """
              {
                "matchId": "m1",
                "timestamp": "2026-01-01T00:00:00+00:00",
                "teamA": ["a1","a2","a3","a4","a5"],
                "teamB": ["b1","b2","b3","b4","b5"],
                "winner": "A"
              }
            """;

        // Control: no duplication still parses.
        TestAssert.NotNull(SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"source":"synthetic","scope":{{scope}},"matches":[{{match}}]}"""));

        // Duplicate top-level schemaVersion / source with disagreeing values.
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"schemaVersion":2,"source":"synthetic","scope":{{scope}},"matches":[{{match}}]}"""));
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"source":"live","source":"synthetic","scope":{{scope}},"matches":[{{match}}]}"""));
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"source":"synthetic","source":"live","scope":{{scope}},"matches":[{{match}}]}"""));

        // Duplicate nested "winner" with conflicting values; without protection this would
        // silently pick the last value and skip the malformed-team checks.
        var conflictingWinner = match.Replace("\"winner\": \"A\"", "\"winner\": \"B\",\"winner\": \"A\"");
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"source":"synthetic","scope":{{scope}},"matches":[{{conflictingWinner}}]}"""));

        // Duplicate nested "teamA" array property.
        var duplicateTeam = match.Replace(
            "\"teamA\": [\"a1\",\"a2\",\"a3\",\"a4\",\"a5\"],",
            "\"teamA\": [\"a1\",\"a2\",\"a3\",\"a4\",\"a5\"],\"teamA\": [\"b1\",\"b2\",\"b3\",\"b4\",\"b5\"],");
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"source":"synthetic","scope":{{scope}},"matches":[{{duplicateTeam}}]}"""));

        // Duplicate inside the nested scope object.
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            """{"schemaVersion":1,"source":"synthetic","scope":{"operator":"synthetic","operator":"live","realm":"r","queue":"q","patch":"p"},"matches":[]}"""));

        // Case-variant duplicate top-level key.
        TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"Source":"live","source":"synthetic","scope":{{scope}},"matches":[{{match}}]}"""));

        // The failure is a ScoringValidationException with a path, never a raw serializer error.
        var exception = TestAssert.Throws("rating.json", () => SyntheticRatingJson.Parse(
            $$"""{"schemaVersion":1,"source":"synthetic","scope":{{scope}},"matches":[{{conflictingWinner}}]}"""));
        TestAssert.Contains("Duplicate JSON property", exception.Message);
        TestAssert.Contains("$.matches[0]", exception.Message);
    }

    private static void EqualRatings(
        SyntheticRatingResult expected,
        SyntheticRatingResult actual,
        bool compareDuplicateCount = true)
    {
        TestAssert.Equal(expected.Players.Count, actual.Players.Count);
        TestAssert.Equal(expected.PlayerCount, actual.PlayerCount);
        TestAssert.Equal(expected.MatchCount, actual.MatchCount);
        if (compareDuplicateCount)
        {
            TestAssert.Equal(expected.DuplicateMatchCount, actual.DuplicateMatchCount);
        }
        TestAssert.Equal(expected.ConnectedPoolCount, actual.ConnectedPoolCount);
        TestAssert.Equal(expected.LargestConnectedPoolSize, actual.LargestConnectedPoolSize);

        for (var i = 0; i < expected.Players.Count; i++)
        {
            var left = expected.Players[i];
            var right = actual.Players[i];
            TestAssert.Equal(left.PlayerId, right.PlayerId);
            TestAssert.EqualDouble(left.Rating, right.Rating, 0);
            TestAssert.Equal(left.Games, right.Games);
            TestAssert.Equal(left.IsProvisional, right.IsProvisional);
            TestAssert.Equal(left.ConnectedPoolId, right.ConnectedPoolId);
            TestAssert.Equal(left.ConnectedPoolSize, right.ConnectedPoolSize);
        }

        TestAssert.Equal(expected.History.Count, actual.History.Count);
        for (var i = 0; i < expected.History.Count; i++)
        {
            TestAssert.Equal(expected.History[i].MatchId, actual.History[i].MatchId);
            TestAssert.EqualDouble(expected.History[i].ExpectedTeamA, actual.History[i].ExpectedTeamA, 0);
            TestAssert.EqualDouble(expected.History[i].Delta, actual.History[i].Delta, 0);
            TestAssert.SequenceEqual(
                expected.History[i].Changes.Select(c => $"{c.PlayerId}:{c.Team}:{c.RatingAfter:R}"),
                actual.History[i].Changes.Select(c => $"{c.PlayerId}:{c.Team}:{c.RatingAfter:R}"));
        }
    }
}
